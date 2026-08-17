using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BufferC.Core.Config;
using BufferC.Core.Core;
using BufferC.Core.Modbus;
using BufferC.Core.Secs;

namespace BufferC.Core;

/// <summary>
/// BufferC 服务编排：HSMS 服务 + 15 台 PLC 轮询器 + 事件路由 + 库存台账。
/// 实现 IStatusProvider（S1F3/S1F4 数据源）与 IEventSink（事件出口）。
/// </summary>
public sealed class BufferCService : IStatusProvider, IEventSink, IDisposable
{
    private readonly BufferCConfig _cfg;
    private readonly Inventory _inv;
    private readonly EventEngine _engine;
    private readonly List<PlcPoller> _pollers = new();
    private readonly CancellationTokenSource _cts = new();
    private HsmsServer? _hsms;
    private AgvcClient? _agvc;                              // 出站 AGVC 客户端（baseUrl 空=不启用）
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _agvcArrivalTimers = new();
    private readonly SemaphoreSlim[] _cmdSignals;   // D2：每 PLC 命令入队信号（替代 200ms 忙轮询；长度=配置 PLC 台数）
    private readonly object _alarmSendLock = new();   // 告警组三帧连发（S5F1+102+402 / S5F1+101+401）组间互不交错（现场要求 2026-08-17）
    private LogLevel _minLevel = LogLevel.Info;             // 启动按 cfg.LogLevel 解析；/api/debug 可运行时切换
    private long _logSeq;

    public BufferCService(BufferCConfig cfg)
    {
        _cfg = cfg;
        _cmdSignals = Enumerable.Range(0, cfg.Plcs.Count).Select(_ => new SemaphoreSlim(0)).ToArray();
        _engine = new EventEngine(i => cfg.Plcs.FirstOrDefault(p => p.Index == i)?.Name);   // 机台名注入（缺省回退 BUFFER 旧格式）
        _inv = new Inventory(cfg.DbPath, cfg.Plcs.Count, Log, cfg.HistoryRetentionRows,
            (p, s) => _engine.Unit(p, s), cfg.Plcs.Select(p => p.Stations).ToList());       // 站口主表：每台按 stations 建行；日志收编入统一管道
        _minLevel = Enum.TryParse<LogLevel>(cfg.LogLevel, true, out var lv) ? lv : LogLevel.Info;
    }

    /// <summary>全部 PLC 已完成首次轮询（基线就绪，测试/联调用）</summary>
    public bool AllPlcsReady => _pollers.Count > 0 && _pollers.All(p => p.Snapshot != null);

    // ---------- IStatusProvider（表为中心：全部从 Inventory 表读，轮询器负责落表） ----------
    public IReadOnlyList<CarrierStatus> Carriers => _inv.Carriers;

    public IReadOnlyList<UnitStatus> Units => _inv.Units;

    public IReadOnlyList<AlarmStatus> Alarms => _inv.Alarms;

    public ushort ControlState => _inv.ControlState;   // SVID 14
    public ushort ScState => _inv.ScState;             // SVID 21

    /// <summary>站口主表全量（含已取走历史；前端台账/测试用）</summary>
    public IReadOnlyList<Inventory.StationRow> Ledger => _inv.Ledger;

    // ---------- 生命周期 ----------
    public void Start()
    {
        StartedAt = DateTime.Now;
        var now = Clock();
        CleanupOldLogs(_cfg.LogFile, now);
        if (!string.IsNullOrWhiteSpace(_cfg.AuditFile)) CleanupOldLogs(_cfg.AuditFile!, now);
        _hsms = new HsmsServer(_cfg.Hsms, this);
        _hsms.Log += (c, m, lv) => Log(c, m, lv);
        _hsms.Audit += (c, m) => Audit(c, m);
        _hsms.OnHostCommand += HandleHostCommand;
        _hsms.Start();

        foreach (var plc in _cfg.Plcs)
        {
            var poller = new PlcPoller(plc, _cfg, _engine, this);
            _pollers.Add(poller);
            _ = Task.Run(() => poller.RunAsync(_cts.Token));
            // 第二期异步命令：每台 PLC 一个工作线程（互不阻塞；单命令挂起只影响本机台）
            _ = Task.Run(() => CommandWorkerLoopAsync(plc.Index, _cts.Token));
        }

        // 重启恢复（未完成命令标记失败）
        RecoverStaleCommands();
        SeedHistoryFromDb();   // B3：历史回填环形缓冲（Web 面板重启后可查）

        // AGVC 集成（方向1 出站）：baseUrl 配置后订阅 AGV 放入完成回调（2→1）
        if (!string.IsNullOrWhiteSpace(_cfg.Agvc.BaseUrl))
        {
            _agvc = new AgvcClient(_cfg.Agvc, Log,
                (type, cmsIndex, req, resp, ok) => _agvcTraffic.Add(new AgvcTrafficEntry(DateTime.Now, type, cmsIndex, req, resp, ok)));
            _engine.OnAgvPutCompleted += StartAgvcArrivalTimer;
        }
        // 异常状态跳变（1→5 / 5→1）：只记日志不上报 MCS
        _engine.OnAbnormalTransition += (plc, st, desc) =>
            Log("PLC", $"PLC{plc} 站口{st} 异常状态跳变 {desc}（仅记录不上报）", LogLevel.Warn);
        Log("SVC", $"BufferC 启动: {_cfg.Plcs.Count} 台 PLC, HSMS 端口 {_cfg.Hsms.ListenPort}, AGVC {( _agvc != null ? _cfg.Agvc.BaseUrl : "未配置（出站禁用）")}");
    }

    public void Stop()
    {
        _cts.Cancel();
        foreach (var t in _agvcArrivalTimers.Values) { t.Cancel(); t.Dispose(); }
        _agvcArrivalTimers.Clear();
        _agvc?.Dispose();
        _hsms?.Dispose();
        _inv.Dispose();
        lock (_logLock)
        {
            _logWriter?.Dispose();
            _logWriter = null;
            _auditWriter?.Dispose();
            _auditWriter = null;
        }
    }

    public void Dispose() => Stop();

    // ---------- IEventSink ----------
    private readonly object _logLock = new();
    private StreamWriter? _logWriter;
    private string? _logDate;                               // 主日志当前日期（yyyyMMdd），变化即滚动切新文件
    private StreamWriter? _auditWriter;
    private string? _auditDate;                             // 审计日志当前日期

    /// <summary>可注入时钟（日志行/滚动/过期清理全经此取时；测试拨动日期用）</summary>
    public Func<DateTime> Clock = () => DateTime.Now;

    // ---------- 活动记录（界面/联调用，环形缓冲） ----------
    public sealed record LogEntry(DateTime Time, string Category, LogLevel Level, long Seq, string Message, bool IsAudit = false);
    public sealed record EventEntry(DateTime Time, ushort Ceid, string Description);
    public sealed record CmdEntry(DateTime Time, string Source, int Station, string Cmd, bool Ok, long ElapsedMs, string CarrierId);
    public sealed record AlarmHistoryEntry(DateTime Time, string UnitId, uint AlarmId, string Text, string Action);

    private readonly RingBuffer<LogEntry> _logs = new(500);
    private readonly RingBuffer<EventEntry> _events = new(100);
    private readonly RingBuffer<CmdEntry> _cmds = new(100);
    private readonly RingBuffer<AlarmHistoryEntry> _alarmHistory = new(100);
    private readonly RingBuffer<AgvcTrafficEntry> _agvcTraffic = new(100);
    public DateTime StartedAt;

    /// <summary>AGVC 出站交互记录（联调面板）：接口类型/cmsIndex/请求体/最终响应/是否成功</summary>
    public sealed record AgvcTrafficEntry(DateTime Time, string Type, string CmsIndex, string Request, string Response, bool Ok);
    public List<AgvcTrafficEntry> GetAgvcTraffic(int tail = 50) => _agvcTraffic.GetTail(tail);

    /// <summary>HSMS 收发记录（MCS 联调页：收=对方发来/发=回给对方，含 SML 与 HEX 原文）</summary>
    public List<Secs.HsmsServer.HsmsTrafficEntry> GetHsmsTraffic(int tail = 50) =>
        _hsms?.Traffic.GetTail(tail) ?? new List<Secs.HsmsServer.HsmsTrafficEntry>();

    public List<LogEntry> GetLogs(int tail, string? category = null, LogLevel? maxLevel = null)
    {
        var all = _logs.GetTail(tail * 4);
        if (category != null)
            all = all.Where(l => l.Category.Contains(category, StringComparison.OrdinalIgnoreCase)).ToList();
        if (maxLevel != null)
            all = all.Where(l => l.Level <= maxLevel).ToList();
        return all.TakeLast(tail).ToList();
    }
    public List<EventEntry> GetEvents(int tail = 50) => _events.GetTail(tail);
    public List<CmdEntry> GetCommands(int tail = 50) => _cmds.GetTail(tail);
    public List<AlarmHistoryEntry> GetAlarmHistory(int tail = 50) => _alarmHistory.GetTail(tail);

    /// <summary>日志（三通道：控制台 + 按天滚动文件 + 内存环形缓冲）；级别高于 _minLevel 的整条跳过（error 最粗、trace 最细）。
    /// 行格式：<c>[yyyy-MM-dd HH:mm:ss.fff][级别5字符][category][#seq] msg</c>；多行消息折叠为一行。</summary>
    public void Log(string category, string msg, LogLevel level = LogLevel.Info)
    {
        if (level > _minLevel) return;
        var now = Clock();
        var seq = Interlocked.Increment(ref _logSeq);
        var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}][{LevelTag(level)}][{category}][#{seq:D6}] {OneLine(msg)}";
        Console.WriteLine(line);
        _logs.Add(new LogEntry(now, category, level, seq, msg));
        WriteToRolling(_cfg.LogFile, ref _logWriter, ref _logDate, now, line);
    }

    /// <summary>
    /// 审计日志（SECS 收发摘要/命令全生命周期/告警/对账——现场留档证据）：
    /// 控制台（journald/场景脚本）+ audit 按天滚动文件 + 内存环形（Web 面板可见）；不写主日志文件、不过级别过滤。
    /// auditFile 配置为 null/空时整通道关闭。与普通日志共用 seq 计数器 → 两个文件可互相关联。
    /// </summary>
    public void Audit(string category, string msg)
    {
        if (string.IsNullOrWhiteSpace(_cfg.AuditFile)) return;
        var now = Clock();
        var seq = Interlocked.Increment(ref _logSeq);
        var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}][AUDIT][{category}][#{seq:D6}] {OneLine(msg)}";
        Console.WriteLine(line);
        _logs.Add(new LogEntry(now, category, LogLevel.Info, seq, msg, IsAudit: true));   // 前端日志面板据此显示 [AUDIT] 级别位
        WriteToRolling(_cfg.AuditFile!, ref _auditWriter, ref _auditDate, now, line);
    }

    private static string LevelTag(LogLevel lv) => lv switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN ",
        LogLevel.Info => "INFO ",
        LogLevel.Debug => "DEBUG",
        _ => "TRACE",
    };

    /// <summary>多行消息折叠为一行（保证一行一条，日志可被 grep/脚本可靠解析）</summary>
    private static string OneLine(string s) => s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>异常记录：lv 级 Message 行 + Debug 级完整堆栈（现场定位保留上下文）</summary>
    private void LogException(string category, string context, Exception e, LogLevel level)
    {
        Log(category, $"{context}: {e.Message}", level);
        Log(category, $"{context} 详情: {e}", LogLevel.Debug);
    }

    /// <summary>按天滚动追加写：日期变化 → dispose 旧 writer、清理过期文件、append 打开当日文件（同日重启不覆盖）。
    /// UTF-8 带 BOM + AutoFlush（PowerShell 5.1 现场查看兼容）；文件不可写时静默降级（仅控制台）。</summary>
    private void WriteToRolling(string basePath, ref StreamWriter? writer, ref string? date, DateTime now, string line)
    {
        try
        {
            lock (_logLock)
            {
                var today = now.ToString("yyyyMMdd");
                if (writer == null || date != today)
                {
                    writer?.Dispose();
                    writer = null;
                    CleanupOldLogs(basePath, now);
                    var path = DatedPath(basePath, now);
                    // BOM 只在新建/空文件时写一次（同日重启 append 不再写，避免 BOM 夹进文件中间）
                    var enc = File.Exists(path) && new FileInfo(path).Length > 0
                        ? new UTF8Encoding(false)
                        : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                    writer = new StreamWriter(path, append: true, enc) { AutoFlush = true };
                    date = today;
                }
                writer.WriteLine(line);
            }
        }
        catch { /* 日志文件不可写时仅控制台 */ }
    }

    /// <summary>当日文件名：基名 bufferc.log → bufferc-20260817.log（扩展名前插 -yyyyMMdd）</summary>
    private static string DatedPath(string basePath, DateTime now)
    {
        var dir = Path.GetDirectoryName(basePath);
        var dated = $"{Path.GetFileNameWithoutExtension(basePath)}-{now:yyyyMMdd}{Path.GetExtension(basePath)}";
        return string.IsNullOrEmpty(dir) ? dated : Path.Combine(dir, dated);
    }

    /// <summary>清理过期滚动文件：只删匹配「{基名}-8位日期.ext」且早于保留天数的文件（防御误删其他文件）</summary>
    private void CleanupOldLogs(string basePath, DateTime now)
    {
        try
        {
            var dir = string.IsNullOrEmpty(Path.GetDirectoryName(basePath)) ? "." : Path.GetDirectoryName(basePath)!;
            var pattern = $"{Path.GetFileNameWithoutExtension(basePath)}-*{Path.GetExtension(basePath)}";
            var rx = new Regex($@"^{Regex.Escape(Path.GetFileNameWithoutExtension(basePath))}-(\d{{8}}){Regex.Escape(Path.GetExtension(basePath))}$");
            var cutoff = now.Date.AddDays(-_cfg.LogRetentionDays);
            foreach (var f in Directory.GetFiles(dir, pattern))
            {
                var m = rx.Match(Path.GetFileName(f));
                if (!m.Success) continue;
                if (DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd", null, DateTimeStyles.None, out var d) && d < cutoff)
                    try { File.Delete(f); } catch { }
            }
        }
        catch { /* 目录不可读时跳过清理 */ }
    }

    // ---------- 运行时调试开关（联调 Web /api/debug） ----------
    public LogLevel CurrentMinLevel => _minLevel;

    /// <summary>运行时切换日志级别（info/debug/trace），返回是否成功</summary>
    public bool SetRuntimeLogLevel(string level)
    {
        if (!Enum.TryParse<LogLevel>(level, true, out var lv)) return false;
        _minLevel = lv;
        _cfg.LogLevel = level.ToLowerInvariant();
        Log("SVC", $"日志级别切换: {level}");
        return true;
    }

    /// <summary>运行时切换 HSMS 帧日志开关（不落盘，仅运行时）</summary>
    public void SetRuntimeLogFrames(bool on)
    {
        if (_hsms == null) return;
        _hsms.LogFramesEnabled = on;
        Log("SVC", $"帧日志切换: {(on ? "开" : "关")}");
    }

    /// <summary>联调诊断快照（/api/debug）：运行参数 + 统计 + 开关状态</summary>
    public DebugInfo GetDebugInfo() => new(
        StartedAt, _cfg.Hsms.Mdln, _cfg.Hsms.SoftRev,
        _cfg.PollIntervalMs, _cfg.EchoTimeoutMs, _cfg.EchoRetryCount,
        _cfg.LogLevel, _hsms?.LogFramesEnabled ?? _cfg.Hsms.LogFrames, _cfg.LogModbusFrames,
        _cfg.Hsms.ListenPort, _cfg.Hsms.T3Ms, _cfg.Hsms.SvidCarrierFormat, _cfg.WebPort,
        _pollers.Select(p => new PlcDebug(p.Config.Index, p.Config.Ip, p.Config.Port, p.Config.UnitId,
            p.Config.ByteOrder, p.Config.TimeoutMs, p.GetStats(), p.Channel.GetLatencyStats())).ToList(),
        _hsms?.GetStats() ?? new Secs.HsmsServer.McsStats(false, null, "", 0, 0, 0, 0, "", ""));

    public sealed record DebugInfo(DateTime StartedAt, string Mdln, string SoftRev,
        double PollIntervalMs, int EchoTimeoutMs, int EchoRetryCount,
        string LogLevel, bool LogFrames, bool LogModbusFrames,
        int HsmsListenPort, int T3Ms, string SvidCarrierFormat, int WebPort,
        List<PlcDebug> Plcs, Secs.HsmsServer.McsStats Mcs);
    public sealed record PlcDebug(int Index, string Ip, int Port, byte UnitId, string ByteOrder, int TimeoutMs,
        PlcPoller.PlcStats Stats, CommandChannel.LatencyStats Latency);

    public void Emit(int plcIndex, IReadOnlyList<PlcEvent> events)
    {
        foreach (var ev in events)
        {
            var now = DateTime.Now;
            _events.Add(new EventEntry(now, (ushort)ev.Kind, DescribeEvent(ev)));
            _inv.AddEventHistory(now, (ushort)ev.Kind, DescribeEvent(ev));   // B3：事件历史持久化
            HandleEvent(plcIndex, ev);
        }
    }

    /// <summary>每轮轮询后同步站口主表（状态/告警/可用/ID 兜底；Inventory 内部变化才写）</summary>
    public void SyncSnapshot(PlcSnapshot snap)
    {
        for (int i = 0; i < snap.StationCount; i++)
            _inv.UpdateStationState(snap.PlcIndex, i + 1, snap.StationState[i], snap.StationAlarm[i], snap.StationAvail[i], snap.CarrierId[i]);
    }

    private static string DescribeEvent(PlcEvent ev)
    {
        var parts = ev.Params
            .Where(kv => kv.Key is "CarrierID" or "CarrierLoc" or "UnitId" or "AlarmId")
            .Select(kv => $"{kv.Key}={kv.Value}");
        return $"{ev.Kind} {string.Join(" ", parts)}".Trim();
    }

    private void HandleEvent(int plcIndex, PlcEvent ev)
    {
        var hsms = _hsms;
        if (hsms == null) return;
        var p = ev.Params;
        int st = p.TryGetValue("Station", out var s) ? int.Parse(s) : 0;

        switch (ev.Kind)
        {
            case EventKind.CarrierInstalled:
                _inv.SetCarrier(plcIndex, st, p.GetValueOrDefault("CarrierID", ""), p.GetValueOrDefault("CarrierLoc", ""), "AGV");   // 204 自动放入
                SendEvent(204, 2, Rpt2(p), $"PLC{plcIndex} 站口{st} {p.GetValueOrDefault("CarrierID", "")}");
                PushStationStatus(plcIndex, st);
                break;
            case EventKind.CarrierRemoved:
                _inv.RemoveCarrier(plcIndex, st, out _);
                SendEvent(203, 2, Rpt2(p), $"PLC{plcIndex} 站口{st} {p.GetValueOrDefault("CarrierID", "")}");   // RPT2: CarrierID + CarrierLoc
                PushStationStatus(plcIndex, st);
                break;
            case EventKind.CarrierInstallCompleted:
                _inv.SetCarrier(plcIndex, st, p.GetValueOrDefault("CarrierID", ""), p.GetValueOrDefault("CarrierLoc", ""), "MANUAL");   // 201 手动（含扫码）
                SendEvent(201, 2, Rpt2(p), $"PLC{plcIndex} 站口{st} {p.GetValueOrDefault("CarrierID", "")}");
                PushStationStatus(plcIndex, st, p.GetValueOrDefault("CarrierID", ""));
                break;
            case EventKind.CarrierRemoveCompleted:
                _inv.RemoveCarrier(plcIndex, st, out _);
                SendEvent(202, 2, Rpt2(p), $"PLC{plcIndex} 站口{st} {p.GetValueOrDefault("CarrierID", "")}");
                PushStationStatus(plcIndex, st);   // 与 203 一致：变化即推 AGVC
                break;
            case EventKind.UnitInService:
                _inv.UpdateUnit(plcIndex, st, 0);
                SendEvent(301, 3, Rpt3(p), $"PLC{plcIndex} 站口{st} 在服");
                PushStationStatus(plcIndex, st);
                break;
            case EventKind.UnitOutOfService:
                _inv.UpdateUnit(plcIndex, st, 1);
                SendEvent(302, 3, Rpt3(p), $"PLC{plcIndex} 站口{st} 停服");
                PushStationStatus(plcIndex, st);
                break;
            case EventKind.AlarmSet:
            {
                // 每单位告警组（2.2.1，现场要求 2026-08-17）：S5F1+102+402 三帧在组锁内连发，组间互不交错；告警表只存当前置起
                uint alid = uint.Parse(p.GetValueOrDefault("AlarmId", "0"));
                string text = p.GetValueOrDefault("AlarmText", "Alarm");
                string unit = p.GetValueOrDefault("UnitId", "");
                lock (_alarmSendLock)
                {
                    Audit("告警", $"S5F1 SET {alid} {text}");
                    if (!hsms.SendS5F1(true, alid, text))
                        Log("告警", $"S5F1 SET {alid} 发送失败（MCS 未连接）", LogLevel.Error);
                    SendEvent(102, 1, Rpt1(p), $"PLC{plcIndex} 告警 {alid}");
                    SendEvent(402, 4, Rpt4(p), $"PLC{plcIndex} 站口{st} 告警 {alid}");
                }
                _alarmHistory.Add(new AlarmHistoryEntry(DateTime.Now, unit, alid, text, "SET"));
                _inv.AddAlarmHistory(DateTime.Now, unit, alid, text, "SET");   // B3：告警历史持久化
                _inv.AlarmSet(alid, unit, text);
                break;
            }
            case EventKind.AlarmCleared:
            {
                // 每单位清除组：S5F1+101+401 三帧在组锁内连发；清除即删行（方案 A）
                uint alid = uint.Parse(p.GetValueOrDefault("AlarmId", "0"));
                string text = p.GetValueOrDefault("AlarmText", "Alarm");
                string unit = p.GetValueOrDefault("UnitId", "");
                lock (_alarmSendLock)
                {
                    Audit("告警", $"S5F1 CLEAR {alid} {text}");
                    if (!hsms.SendS5F1(false, alid, text))
                        Log("告警", $"S5F1 CLEAR {alid} 发送失败（MCS 未连接）", LogLevel.Error);
                    SendEvent(101, 1, Rpt1(p), $"PLC{plcIndex} 告警 {alid}");
                    SendEvent(401, 4, Rpt4(p), $"PLC{plcIndex} 站口{st} 告警 {alid}");
                }
                _alarmHistory.Add(new AlarmHistoryEntry(DateTime.Now, unit, alid, text, "CLEAR"));
                _inv.AddAlarmHistory(DateTime.Now, unit, alid, text, "CLEAR");   // B3：告警历史持久化
                _inv.AlarmClear(alid);
                break;
            }
        }
    }

    /// <summary>S6F11 事件发送（带失败上下文日志：MCS 未连接时事件丢失可见。
    /// 断线不补报为既定决策（重连后 S1F3 全量同步）——此处只让失败留痕，不改变不补报。）</summary>
    private void SendEvent(ushort ceid, ushort rptId, byte[] body, string desc)
    {
        if (_hsms == null || !_hsms.SendS6F11(ceid, rptId, body))
            Log("HSMS", $"事件 CEID {ceid} 发送失败（MCS 未连接）: {desc}", LogLevel.Warn);
    }

    // ---------- RPT 组装 ----------
    /// <summary>RPT2 = 载具ID + 位置（MCS 现场约定 2026-08-17；原 HandoffType 区分已由状态区 5=人工有货承担，不再上报）</summary>
    private static byte[] Rpt2(Dictionary<string, string> p) => SecsEncode.L(
        SecsEncode.A(p.GetValueOrDefault("CarrierID", "")),
        SecsEncode.A(p.GetValueOrDefault("CarrierLoc", "")));

    private static byte[] Rpt3(Dictionary<string, string> p) => SecsEncode.L(
        SecsEncode.A(p.GetValueOrDefault("UnitId", "")));

    private static byte[] Rpt4(Dictionary<string, string> p) => SecsEncode.L(
        SecsEncode.A(p.GetValueOrDefault("UnitId", "")),
        SecsEncode.U4(uint.Parse(p.GetValueOrDefault("AlarmId", "0"))),
        SecsEncode.A(p.GetValueOrDefault("AlarmText", "Alarm")));

    private static byte[] Rpt1(Dictionary<string, string> p) => SecsEncode.L(
        SecsEncode.A(p.GetValueOrDefault("AlarmText", "Alarm")),
        SecsEncode.L(SecsEncode.A(p.GetValueOrDefault("UnitId", "")), SecsEncode.U2(0)),
        SecsEncode.U4(uint.Parse(p.GetValueOrDefault("AlarmId", "0"))));

    // ---------- 手动命令（Web/AGVC 与 S2F41 共用同一命令路径） ----------
    // 宽松解析：取前两个数字作为 Buffer 号与站口（支持 Buffer1_Port3 / 1号Buffer_3号站口 / Buffer1-3 等写法）
    private static readonly Regex LocRegex = new(@"(\d+)[^\d]*(\d+)", RegexOptions.Compiled);

    /// <summary>站口定位解析：① AGVC cmsIndex 纯数字 ② 现场新格式 机台名_P站口号（MAGV03B01_P05，按名称表精确解析）
    /// ③ 回落旧宽松文本格式（Buffer1_Port3 等，兼容历史输入）</summary>
    private bool TryParseLoc(string loc, out int plc, out int station)
    {
        if (TryParseCmsIndex(loc, out plc, out station)) return true;
        int us = loc.IndexOf('_');
        if (us > 0)
        {
            string dev = loc[..us];
            var m = Regex.Match(loc[(us + 1)..], @"^P(\d{1,2})$");
            var plcCfg = _cfg.Plcs.FirstOrDefault(p => (p.Name ?? $"BUFFER{p.Index:00}") == dev);
            if (plcCfg != null && m.Success && int.TryParse(m.Groups[1].Value, out station)
                && station >= 1 && station <= plcCfg.Stations)
            {
                plc = plcCfg.Index;
                return true;
            }
            // 「设备名_Pxx」形态但机台未知/站口越界：直接失败，不回落到宽松正则（防误解析成其他机台）
            if (m.Success) { plc = 0; station = 0; return false; }
        }
        var lm = LocRegex.Match(loc);
        plc = 0; station = 0;
        return lm.Success && int.TryParse(lm.Groups[1].Value, out plc)
            && int.TryParse(lm.Groups[2].Value, out station);
    }

    // 悬空命令补偿（C1）：HCACK=4 已确认后命令失败 → 记录 + 内部告警 9001（告警码表 A9 确认后调整）
    public const uint AlarmCmdFailed = 9001;
    private readonly List<FailedCommand> _failedCommands = new();
    private readonly object _failLock = new();

    public sealed record FailedCommand(string Source, string Cmd, string CarrierId, string CarrierLoc, DateTime Time);
    public IReadOnlyList<FailedCommand> FailedCommands { get { lock (_failLock) return _failedCommands.ToList(); } }

    private void RecordFailedCommand(string source, string cmd, string carrierId, string loc)
    {
        var now = DateTime.Now;
        lock (_failLock)
        {
            _failedCommands.Add(new FailedCommand(source, cmd, carrierId, loc, now));
            if (_failedCommands.Count > 100) _failedCommands.RemoveAt(0);
        }
        _inv.AddFailedHistory(now, source, cmd, carrierId, loc);   // B3：悬空命令持久化（重启后 Web 面板仍可查）
        string tag = TryParseLoc(loc, out int plc, out int st) ? $"PLC{plc} 站口{st}" : loc;
        Log("CMD", $"悬空命令: {source} {cmd} {carrierId} {loc}（HCACK=4 已确认，执行失败）", LogLevel.Error);
        Audit("CMD", $"悬空命令: {source} {cmd} {carrierId} {loc}（HCACK=4 已确认，执行失败）");
        Audit("告警", $"S5F1 SET {AlarmCmdFailed} {cmd} {carrierId} {tag}");
        lock (_alarmSendLock)   // 与告警组共用锁：9001 不得插进其他单位的告警组中间
        {
            if (_hsms != null && !_hsms.SendS5F1(true, AlarmCmdFailed, $"命令执行失败: {cmd} {carrierId} {loc}"))
                Log("告警", $"S5F1 SET {AlarmCmdFailed} 发送失败（MCS 未连接）", LogLevel.Error);
        }
        _inv.AlarmSet(AlarmCmdFailed, "SYSTEM", "命令执行失败（悬空命令）");
    }

    /// <summary>CIM 状态事件（1.1.4 OFFLINE→ONLINEREMOTE By CIM 自测）：false→CEID 1（Offline），true→CEID 3（OnlineRemote）</summary>
    public bool TriggerCimState(bool online)
    {
        if (_hsms == null || !_hsms.Connected)
        {
            Log("CIM", $"CIM 状态事件未发（MCS 未连接）: {(online ? "ONLINE" : "OFFLINE")}", LogLevel.Warn);
            return false;
        }
        Audit("CIM", $"CIM 状态事件: {(online ? "ONLINEREMOTE(3)" : "OFFLINE(1)")}");
        bool sent = _hsms.SendS6F11(online ? (ushort)3 : (ushort)1, 0, null);   // L3[U4, U2, L0]（MCS 方言）
        if (!sent)
            Log("HSMS", $"事件 CEID {(online ? 3 : 1)} 发送失败（MCS 未连接）: CIM 状态事件", LogLevel.Warn);
        return sent;
    }

    /// <summary>
    /// 安装载具（source: MCS/AGVC/MANUAL）——异步命令（第二期）：
    /// 先写表（cmd_state=1 待执行）返回 true，后台工作线程执行 PLC，成功后刷表终态 + 发 201。
    /// </summary>
    public bool ManualInstall(string carrierId, string loc, string source = "MANUAL")
    {
        if (!TryParseLoc(loc, out int plc, out int st) || carrierId.Length == 0)
        {
            Log("CMD", $"参数无效: CARRIERID={carrierId} CARRIERLOC={loc}", LogLevel.Warn);
            return false;
        }
        var poller = FindPoller(plc);
        if (poller == null) { Log("CMD", $"PLC{plc} 不存在", LogLevel.Warn); return false; }
        if (st < 1 || st > poller.Config.Stations) { Log("CMD", $"站口越界: {loc}（该机台共 {poller.Config.Stations} 站）", LogLevel.Warn); return false; }
        var srcNorm = source == "AGVC" ? "AGV" : source;   // 台账来源归一（AGVC→AGV）
        _inv.QueueCommand(plc, st, RegisterMap.CmdWrite, carrierId, srcNorm);
        _cmdSignals[plc - 1].Release();   // D2：唤醒该 PLC 的工作线程
        Audit("CMD", $"安装已入队（异步执行）: PLC{plc} 站口{st} {carrierId} 来源={srcNorm}");
        return true;
    }

    /// <summary>扫码握手命令投递（D3）：poller 只投递不阻塞（消除 ≤10s 轮询盲区）；执行/501/201 由命令工作线程完成。
    /// 扫码开始即 MarkScanInstalled（抑制并发 0→1 的 204；命令失败残留键由取出路径清理）。</summary>
    public void EnqueueScan(int plcIndex, int station, string scanCode)
    {
        _engine.MarkScanInstalled(plcIndex, station);
        Log("PLC", $"扫码握手: PLC{plcIndex} 站口{station} 码=\"{scanCode}\" 已入队（异步执行）", LogLevel.Info);
        ManualInstall(scanCode, _engine.Loc(plcIndex, station), "SCAN");
    }

    /// <summary>移除载具——异步命令：先写表（cmd_state=1）返回 true，后台执行成功后刷表 + 发 202</summary>
    public bool ManualRemove(string carrierId, string source = "MANUAL")
    {
        if (!_inv.FindCarrier(carrierId, out int plc, out int st))
        {
            Log("CMD", $"移除找不到 {carrierId}（库存无此载具）", LogLevel.Warn);
            return false;
        }
        _inv.QueueCommand(plc, st, RegisterMap.CmdClear, carrierId, source);
        _cmdSignals[plc - 1].Release();   // D2：唤醒该 PLC 的工作线程
        Audit("CMD", $"移除已入队（异步执行）: PLC{plc} 站口{st} {carrierId} 来源={source}");
        return true;
    }

    /// <summary>命令工作线程（每台 PLC 一条）：扫描本机台待执行行（200ms），执行命令并刷表终态/失败回滚</summary>
    private async Task CommandWorkerLoopAsync(int plcIndex, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var row in _inv.PendingCommands.Where(r => r.PlcIndex == plcIndex))
                {
                    var poller = FindPoller(row.PlcIndex);
                    if (poller == null || !poller.IsConnected) continue;   // PLC 断线：保持待执行，重连后继续
                    var st = row.Station;
                    string cmdName = row.CmdType == RegisterMap.CmdWrite ? "CarrierDataInstall" : "CarrierDataRemove";
                    if (row.CmdState == 4)
                    {
                        // 恢复待校验（B4 防误清）：比对站口当前载具 ID——货被换过则标记失败人工介入；一致（或站口已空）→ 转 1 正常执行
                        var snap = poller.Snapshot;
                        if (snap == null) continue;   // 快照未就绪，下轮再校验
                        string curId = snap.CarrierId[st - 1];
                        if (curId != "" && curId != row.CmdCarrierId)
                        {
                            _inv.FailCommand(row.PlcIndex, st);
                            RecordFailedCommand(row.CmdSource, cmdName, row.CmdCarrierId, _engine.Loc(row.PlcIndex, st));
                            Audit("CMD", $"恢复校验不通过（站口当前载具 {curId} ≠ 命令 {row.CmdCarrierId}，货已更换需人工介入）: PLC{row.PlcIndex} 站口{st}");
                            continue;
                        }
                        _inv.RequeueCommand(row.PlcIndex, st);
                        Audit("CMD", $"恢复校验通过: PLC{row.PlcIndex} 站口{st} {cmdName} {row.CmdCarrierId}");
                    }
                    Audit("CMD", $"执行站口命令: PLC{row.PlcIndex} 站口{st} {cmdName} {row.CmdCarrierId}");
                    _inv.MarkCommandRunning(row.PlcIndex, st, 0);
                    var sw = Stopwatch.StartNew();
                    bool ok;
                    try
                    {
                        ok = poller.Channel.Execute(st, row.CmdType,
                            row.CmdType == RegisterMap.CmdWrite ? row.CmdCarrierId : null);
                    }
                    catch (Exception e) { LogException("CMD", $"PLC{row.PlcIndex} 站口{st} 命令异常", e, LogLevel.Error); ok = false; }
                    sw.Stop();
                    _cmds.Add(new CmdEntry(DateTime.Now, row.CmdSource, st, cmdName, ok, sw.ElapsedMilliseconds, row.CmdCarrierId));
                    _inv.AddCommandHistory(DateTime.Now, row.CmdSource, row.PlcIndex, st, cmdName, ok, sw.ElapsedMilliseconds, row.CmdCarrierId);   // B3：命令历史持久化
                    if (ok)
                    {
                        var locNorm = _engine.Loc(row.PlcIndex, st);
                        if (row.CmdType == RegisterMap.CmdWrite)
                        {
                            CancelAgvcArrivalTimer(row.PlcIndex, st);   // 命令已写入 ID，取消「找 AGVC 要 ID」计时
                            if (row.CmdSource == "SCAN")
                            {
                                // 扫码路径（D3 异步化）：先报 501 CarrierIDRead（IDReadStatus=0 恒成功——UNK 失败路径已随 BCR NG 取消），再报 201
                                var now = DateTime.Now;
                                string desc501 = $"CarrierIdRead CarrierID={row.CmdCarrierId} CarrierLoc={locNorm}";
                                _events.Add(new EventEntry(now, 501, desc501));
                                _inv.AddEventHistory(now, 501, desc501);
                                SendEvent(501, 5, SecsEncode.L(SecsEncode.A(row.CmdCarrierId), SecsEncode.A(locNorm), SecsEncode.U2(0)),
                                    $"PLC{row.PlcIndex} 站口{st} {row.CmdCarrierId}");
                            }
                            _inv.SetCarrier(row.PlcIndex, st, row.CmdCarrierId, locNorm, row.CmdSource == "SCAN" ? "MANUAL" : row.CmdSource);
                            _inv.CompleteCommand(row.PlcIndex, st, poller.Channel.LastSeq);
                            SendEvent(201, 2, SecsEncode.L(SecsEncode.A(row.CmdCarrierId), SecsEncode.A(locNorm)),
                                $"PLC{row.PlcIndex} 站口{st} {row.CmdCarrierId}");
                            PushStationStatus(row.PlcIndex, st, row.CmdCarrierId);
                        }
                        else
                        {
                            _inv.RemoveCarrier(row.PlcIndex, st, out _);
                            _inv.CompleteCommand(row.PlcIndex, st, poller.Channel.LastSeq);
                            SendEvent(202, 2, SecsEncode.L(SecsEncode.A(row.CmdCarrierId), SecsEncode.A(locNorm)),
                                $"PLC{row.PlcIndex} 站口{st} {row.CmdCarrierId}");
                        }
                        // 9001 清除口径（2026-08-17 用户确认）：下条命令成功即自动清（与 PLC 告警「恢复即消」语义一致；失败事实留痕审计日志/台账）
                        if (_inv.AlarmClear(AlarmCmdFailed))
                        {
                            _alarmHistory.Add(new AlarmHistoryEntry(DateTime.Now, "SYSTEM", AlarmCmdFailed, "命令执行失败（悬空命令）", "CLEAR"));
                            _inv.AddAlarmHistory(DateTime.Now, "SYSTEM", AlarmCmdFailed, "命令执行失败（悬空命令）", "CLEAR");   // B3：告警历史持久化
                            Audit("告警", $"S5F1 CLEAR {AlarmCmdFailed}（命令恢复成功自动清除）");
                            lock (_alarmSendLock)   // 与告警组共用锁：不得插进其他单位的告警组中间
                            {
                                if (_hsms != null && !_hsms.SendS5F1(false, AlarmCmdFailed, "命令执行失败（悬空命令）"))
                                    Log("告警", $"S5F1 CLEAR {AlarmCmdFailed} 发送失败（MCS 未连接）", LogLevel.Error);
                            }
                        }
                        Audit("CMD", $"命令完成: PLC{row.PlcIndex} 站口{st} {cmdName} {row.CmdCarrierId} seq={poller.Channel.LastSeq}（{sw.ElapsedMilliseconds}ms）");
                    }
                    else
                    {
                        _inv.FailCommand(row.PlcIndex, st);
                        _inv.WriteCommandSeq(row.PlcIndex, st, poller.Channel.LastSeq);   // 失败也记录最后一次 400 触发值（排障对账用）
                        RecordFailedCommand(row.CmdSource, cmdName, row.CmdCarrierId, _engine.Loc(row.PlcIndex, st));
                    }
                }
            }
            catch (Exception e) { LogException("CMD", $"PLC{plcIndex} 命令工作线程异常", e, LogLevel.Error); }
            // D2：信号驱动（入队即唤醒）；2s 超时兜底（覆盖断线保持待执行后的重连场景等边缘遗漏）
            await _cmdSignals[plcIndex - 1].WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>B3：启动回填——历史记录从 SQLite 载入环形缓冲（Web 面板重启后可查）</summary>
    private void SeedHistoryFromDb()
    {
        foreach (var e in _inv.LoadEvents(100)) _events.Add(new EventEntry(e.Time, e.Ceid, e.Description));
        foreach (var c in _inv.LoadCommands(100)) _cmds.Add(new CmdEntry(c.Time, c.Source, c.Station, c.Cmd, c.Ok, c.ElapsedMs, c.CarrierId));
        foreach (var a in _inv.LoadAlarms(100)) _alarmHistory.Add(new AlarmHistoryEntry(a.Time, a.UnitId, a.AlarmId, a.Text, a.Action));
        lock (_failLock)
            foreach (var f in _inv.LoadFailed(100)) _failedCommands.Add(new FailedCommand(f.Source, f.Cmd, f.CarrierId, f.Loc, f.Time));
    }

    /// <summary>重启恢复（B4 用户确认 2026-08-17：全部重执行）：
    /// 未完成命令 → 4=恢复待校验（worker 比对站口当前载具 ID 防误清后执行；不再直接 9001）</summary>
    private void RecoverStaleCommands()
    {
        foreach (var row in _inv.RequeueStaleCommands())
        {
            Audit("CMD", $"重启恢复: PLC{row.PlcIndex} 站口{row.Station} 未完成命令重执行（恢复待校验） {row.CmdType} {row.CmdCarrierId} @ {row.UnitId}");
            _cmdSignals[row.PlcIndex - 1].Release();   // D2：唤醒对应 PLC 工作线程处理恢复行
        }
    }

    /// <summary>告警确认（本地标记，不影响 SVID 3 内容）</summary>
    public bool AckAlarm(uint alid) => _inv.MarkAlarmAcked(alid);

    // ---------- PLC 调试（Web「PLC 调试」面板：寄存器直读直写 + 站口命令直发，绕过业务副作用） ----------
    private PlcPoller? FindPoller(int plcIndex) => _pollers.FirstOrDefault(x => x.Config.Index == plcIndex);

    /// <summary>读任意保持寄存器（FC03，客户端自动按 ≤125 分段）；PLC 不存在/断线/越界返回 null</summary>
    public ushort[]? ReadPlcRegs(int plcIndex, int addr, int count)
    {
        var p = FindPoller(plcIndex);
        if (p == null || addr < 0 || addr > ushort.MaxValue || count < 1 || count > 512) return null;
        try { return p.ReadRegs(addr, count); }
        catch (Exception e) { LogException("DBG", $"读 PLC{plcIndex} 寄存器 {addr}+{count} 失败", e, LogLevel.Warn); return null; }
    }

    /// <summary>写保持寄存器（单/多自动选 FC06/FC16；联调危险操作，面板二次确认）；返回是否成功</summary>
    public bool WritePlcRegs(int plcIndex, int addr, ushort[] values)
    {
        var p = FindPoller(plcIndex);
        if (p == null || addr < 0 || addr > ushort.MaxValue || values.Length == 0) return false;
        try
        {
            if (values.Length == 1) p.WriteReg(addr, values[0]);
            else p.WriteRegs(addr, values);
            Log("DBG", $"写 PLC{plcIndex} 寄存器 {addr}+{values.Length}: {string.Join(",", values)}");
            return true;
        }
        catch (Exception e) { LogException("DBG", $"写 PLC{plcIndex} 寄存器 {addr} 失败", e, LogLevel.Warn); return false; }
    }

    /// <summary>直发站口命令（1=写入ID/2=清除），仅走 PLC 命令协议（含回显等待/重试），不动台账不发 MCS 事件；返回 成功+耗时</summary>
    public (bool Ok, long ElapsedMs) DebugSendCmd(int plcIndex, int station, ushort cmd, string? carrierId)
    {
        var p = FindPoller(plcIndex);
        if (p == null || station < 1 || station > p.Config.Stations) return (false, 0);   // 站口按该机台站口数校验
        var sw = Stopwatch.StartNew();
        bool ok;
        try { ok = p.Channel.Execute(station, cmd, carrierId); }
        catch (Exception e) { LogException("DBG", $"直发命令 PLC{plcIndex} 站口{station} 命令{cmd} 异常", e, LogLevel.Warn); ok = false; }
        sw.Stop();
        Log("DBG", $"直发命令 PLC{plcIndex} 站口{station} 命令{cmd} {(ok ? "OK" : "FAIL")}（{sw.ElapsedMilliseconds}ms）");
        return (ok, sw.ElapsedMilliseconds);
    }

    // ---------- AGVC HTTP 集成（§D1：仅出站 queryMachines 到货要 ID；入站接口已按现场要求移除 2026-08-14） ----------
    /// <summary>cmsIndex 解析：机台号 = idx / base、站口 = idx % base（10001=1号机台1号站口）</summary>
    private bool TryParseCmsIndex(string? s, out int plc, out int station)
    {
        plc = 0; station = 0;
        if (!int.TryParse(s, out int idx)) return false;
        int b = _cfg.Agvc.CmsIndexBase;
        plc = idx / b; station = idx % b;
        int pi = plc;
        var plcCfg = _cfg.Plcs.FirstOrDefault(p => p.Index == pi);
        return plcCfg != null && station >= 1 && station <= plcCfg.Stations;
    }

    /// <summary>AGV 放入完成（2→1）：起等待窗口计时，窗口内扫码握手可取消；到期仍无 ID → 出站找 AGVC 要货物 ID</summary>
    private void StartAgvcArrivalTimer(int plcIndex, int station)
    {
        var key = $"{plcIndex}:{station}";
        if (_agvcArrivalTimers.ContainsKey(key)) return;   // 同站口已有计时（防重复触发）
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        if (!_agvcArrivalTimers.TryAdd(key, cts)) { cts.Dispose(); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                if (_cfg.Agvc.ArrivalGraceMs > 0)
                    await Task.Delay(_cfg.Agvc.ArrivalGraceMs, cts.Token);
                // 窗口到期复检：PLC 在线 + 站口仍为有货 + ID 区为空 → 才找 AGVC 要 ID
                var poller = FindPoller(plcIndex);
                var snap = poller?.Snapshot;
                if (poller == null || !poller.IsConnected || snap == null
                    || snap.StationState[station - 1] != RegisterMap.StHasCarrier
                    || !string.IsNullOrEmpty(snap.CarrierId[station - 1]))
                {
                    Log("AGVC", $"站口{station} 等待窗口结束，无需索取 ID（已扫码/命令写入或状态变化）", LogLevel.Debug);
                    return;
                }
                if (_agvc == null)
                {
                    Log("AGVC", $"PLC{plcIndex} 站口{station} AGV 到货无 ID 但 agvc.baseUrl 未配置，跳过", LogLevel.Warn);
                    return;
                }
                // 查询 AGVC 拿货物 ID（queryMachines 直接回 ID）→ 拿到后走命令路径写 PLC + 台账 + 201
                var carrierId = await _agvc.QueryCarrierIdAsync(plcIndex, station, cts.Token);
                if (!string.IsNullOrWhiteSpace(carrierId))
                    ManualInstall(carrierId, (plcIndex * _cfg.Agvc.CmsIndexBase + station).ToString(), "AGVC");
            }
            catch (OperationCanceledException) { /* 扫码握手接管 / 停机 */ }
            finally
            {
                _agvcArrivalTimers.TryRemove(key, out _);
                cts.Dispose();
            }
        }, cts.Token);
    }

    /// <summary>推该站口当前状态给 AGVC（变化即推：service=34~49 原始寄存器值、present=状态区 1/5、trayId 优先事件已知 ID 其次快照）；离线不推（快照陈旧）</summary>
    private void PushStationStatus(int plcIndex, int station, string? knownId = null)
    {
        if (_agvc == null) return;
        var poller = FindPoller(plcIndex);
        var snap = poller?.Snapshot;
        if (poller == null || snap == null || !poller.IsConnected) return;
        bool present = snap.StationState[station - 1] is RegisterMap.StHasCarrier or RegisterMap.StManualCarrier;
        ushort avail = snap.StationAvail[station - 1];
        _ = Task.Run(() => _agvc!.PushStatusAsync(plcIndex, station, avail, present,
            DeriveTrayId(plcIndex, station, present, knownId), _cts.Token));
    }

    /// <summary>trayId 三级兜底：无货恒空；有货事件已知 ID → 台账 → 快照（快照 ID 仅状态变化时重读，扫码/命令写入后是陈旧的）</summary>
    private string DeriveTrayId(int plcIndex, int station, bool present, string? knownId = null) =>
        !present ? ""
        : knownId
            ?? _inv.Carriers.FirstOrDefault(c => c.CarrierLoc == _engine.Loc(plcIndex, station))?.CarrierId
            ?? FindPoller(plcIndex)?.Snapshot?.CarrierId[station - 1] ?? "";

    /// <summary>手动触发 queryMachines（AGVC 联调页）：与自动路径一致——查询 ID → 写 PLC + 台账 + 201</summary>
    public async Task<(bool Ok, string Message)> ManualQueryMachines(string cmsIndex)
    {
        if (_agvc == null) return (false, "agvc.baseUrl 未配置，出站禁用");
        if (!TryParseCmsIndex(cmsIndex, out int plc, out int st)) return (false, $"cmsIndex 无效: {cmsIndex}");
        if (FindPoller(plc) == null) return (false, $"PLC{plc} 未配置");
        var carrierId = await _agvc.QueryCarrierIdAsync(plc, st, _cts.Token);
        if (string.IsNullOrWhiteSpace(carrierId)) return (false, $"查询无结果（{cmsIndex}）——详见下方发送记录");
        bool ok = ManualInstall(carrierId, cmsIndex, "AGVC");
        return ok ? (true, $"已获取货物 ID \"{carrierId}\" 并写入 PLC") : (false, $"货物 ID \"{carrierId}\" 写 PLC 失败");
    }

    /// <summary>手动触发 pushDeviceStatusInfo（AGVC 联调页）：字段留空按当前实况推导（与自动推送同口径）</summary>
    public async Task<(bool Ok, string Message)> ManualPushStatus(string cmsIndex, string? service, string? present, string? trayId)
    {
        if (_agvc == null) return (false, "agvc.baseUrl 未配置，出站禁用");
        if (!TryParseCmsIndex(cmsIndex, out int plc, out int st)) return (false, $"cmsIndex 无效: {cmsIndex}");
        var poller = FindPoller(plc);
        var snap = poller?.Snapshot;
        if (poller == null || snap == null || !poller.IsConnected) return (false, $"PLC{plc} 未配置或未连接");
        ushort avail = ushort.TryParse(service, out var av) ? av : snap.StationAvail[st - 1];
        bool pres = present == "1" || (present != "0"
            && snap.StationState[st - 1] is RegisterMap.StHasCarrier or RegisterMap.StManualCarrier);
        string tid = trayId ?? DeriveTrayId(plc, st, pres);
        bool ok = await _agvc.PushStatusAsync(plc, st, avail, pres, tid, _cts.Token);
        return ok ? (true, $"已推送 {cmsIndex}（service={avail} present={(pres ? 1 : 0)} trayId=\"{tid}\"）")
                  : (false, $"推送失败（{cmsIndex}）——详见下方发送记录");
    }

    /// <summary>取消站口的「找 AGVC 要 ID」等待计时（扫码握手/命令路径已写入 ID）</summary>
    private void CancelAgvcArrivalTimer(int plcIndex, int station)
    {
        if (_agvcArrivalTimers.TryRemove($"{plcIndex}:{station}", out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            Log("AGVC", $"站口{station} 已写入 ID（扫码/命令路径），取消索取计时", LogLevel.Debug);
        }
    }

    // ---------- Web 界面状态视图 ----------
    public sealed record StatusView(int ControlState, int ScState, bool HsmsConnected, List<PlcView> Plcs,
        List<AlarmStatus> Alarms, Secs.HsmsServer.McsStats Mcs, SystemInfo System);
    public sealed record PlcView(int Index, string Ip, bool Connected, PlcPoller.PlcStats Stats,
        PlcRegisters Registers, List<StationView> Stations, string Name, int StationCount);
    public sealed record PlcRegisters(ushort BufferNo, ushort AlarmSummary, ushort EchoNo, ushort[] EchoStation,
        ushort ScanStation, string ScanCode, ushort Handshake, string ByteOrder);
    public sealed record StationView(int Station, int State, int Alarm, int Avail, string CarrierId, bool Truncated,
        string CarrierLoc, string UpdatedAt);
    public sealed record SystemInfo(DateTime StartedAt, string Mdln, string SoftRev, string PlcSummary,
        string ByteOrderSummary, double PollIntervalMs, int HsmsPort, int WebPort);

    public StatusView GetStatusView()
    {
        var plcs = new List<PlcView>();
        foreach (var p in _pollers)
        {
            var s = p.Snapshot;
            var stations = new List<StationView>();
            for (int i = 0; i < p.Config.Stations; i++)
            {
                var loc = _engine.Loc(p.Config.Index, i + 1);
                // 台账为权威（含手动安装/命令安装）；快照兜底 AGV 实况
                var carrier = _inv.Carriers.FirstOrDefault(c => c.CarrierLoc == loc);
                var id = carrier?.CarrierId ?? (s != null ? s.CarrierId[i] ?? "" : "");
                stations.Add(new StationView(i + 1,
                    s != null ? s.StationState[i] : 0,
                    s != null ? s.StationAlarm[i] : 0,
                    s != null ? s.StationAvail[i] : 0,
                    id, id.Length >= RegisterMap.CarrierIdChars,
                    loc, carrier?.UpdatedAt ?? ""));
            }
            plcs.Add(new PlcView(p.Config.Index, p.Config.Ip, p.IsConnected, p.GetStats(), new PlcRegisters(
                s != null ? s.BufferNo : (ushort)0,
                s != null ? s.AlarmSummary : (ushort)0,
                s != null ? s.EchoNo : (ushort)0,
                s != null ? s.EchoStation : new ushort[16],
                s != null ? s.ScanStation : (ushort)0,
                s != null ? s.ScanCode : "",
                s != null ? s.Handshake : (ushort)0,
                p.Config.ByteOrder), stations, DeviceNameOf(p.Config.Index), p.Config.Stations));
        }
        bool connected = _hsms?.Connected == true;
        return new StatusView(connected ? 5 : 1, 3, connected, plcs, Alarms.ToList(),
            _hsms?.GetStats() ?? new Secs.HsmsServer.McsStats(false, null, "", 0, 0, 0, 0, "", ""),
            GetSystemInfo());
    }

    public SystemInfo GetSystemInfo() => new(StartedAt, _cfg.Hsms.Mdln, _cfg.Hsms.SoftRev,
        string.Join(", ", _cfg.Plcs.Select(p => $"{p.Index}:{p.Ip}")),
        string.Join(", ", _cfg.Plcs.Select(p => p.ByteOrder).Distinct()),
        _cfg.PollIntervalMs, _cfg.Hsms.ListenPort, _cfg.WebPort);

    /// <summary>机台名（现场 2026-08-17：MAGV03B01；缺省回退 BUFFER{index:00}）</summary>
    private string DeviceNameOf(int plcIndex) => _cfg.Plcs.FirstOrDefault(p => p.Index == plcIndex)?.Name ?? $"BUFFER{plcIndex:00}";

    // ---------- 台账合并视图（站口主表直出，服务端合成中文标签） ----------
    public sealed record StationLedgerRow(int PlcIndex, int Station, string DeviceNo, string UnitId,
        int StationState, string StateLabel, int Avail, string UnitStateLabel,
        int AlarmCode, string CarrierId, string InstallSource, string UpdatedAt,
        int CmdState, string CmdStateLabel, int CmdType, string CmdCarrierId, string CmdTime, uint CmdSeq, string CmdSource);

    private static string StateLabelOf(ushort s) => s switch
    {
        RegisterMap.StEmpty => "空",
        RegisterMap.StHasCarrier => "有货",
        RegisterMap.StPutting => "正放",
        RegisterMap.StTaking => "正取",
        RegisterMap.StFault => "其他",   // 状态 4 不再表示报警（2026-08-17 现场约定：报警唯一来源=18~33 报警码非 0；非 0→0 即消除）
        RegisterMap.StManualCarrier => "人工有货",
        _ => $"未知({s})",
    };

    private static string CmdStateLabelOf(ushort s) => s switch
    {
        1 => "待执行",
        2 => "执行中",
        3 => "失败",
        4 => "恢复校验",
        _ => "—",
    };

    public List<StationLedgerRow> GetInventoryView() =>
        _inv.Ledger.Select(r => new StationLedgerRow(
            r.PlcIndex, r.Station,
            DeviceNameOf(r.PlcIndex), r.UnitId,
            r.State, StateLabelOf(r.State),
            r.Avail, r.Avail == 0 ? "在服" : "停服",
            r.AlarmCode, r.CarrierId, r.InstallSource, r.UpdatedAt,
            r.CmdState, CmdStateLabelOf(r.CmdState),
            r.CmdType, r.CmdCarrierId, r.CmdTime, r.CmdSeq, r.CmdSource)).ToList();

    // ---------- S2F41 主机命令 ----------
    private void HandleHostCommand(string rcmd, Dictionary<string, string> pars, List<SecsItem> entries)
    {
        Log("S2F41", $"RCMD={rcmd}", LogLevel.Debug);   // 审计明细已由 HSMS 层 Audit 记录（含参数）
        switch (rcmd)
        {
            case "CarrierDataInstall":
                ManualInstall(pars.GetValueOrDefault("CARRIERID", ""), pars.GetValueOrDefault("CARRIERLOC", ""), "MCS");
                break;
            case "CarrierDataRemove":
                ManualRemove(pars.GetValueOrDefault("CARRIERID", ""), "MCS");
                break;
            case "InventoryDataSend":
                ReconcileInventory(entries);   // 1.2.6：以 MCS 库存为准对账并更新台账
                break;
            case "PAUSE" or "RESUME":
                // HSMS 层已回 HCACK=0（RESUME 另发 CEID 103）；业务层无动作，不再记「未知命令」
                break;
            default:
                Log("S2F41", $"未知命令 {rcmd}", LogLevel.Warn);
                break;
        }
    }

    /// <summary>
    /// InventoryDataSend 对账（Scenario 1.2.6）：以 MCS 列表为准更新台账，所有修改记日志。
    /// MCS 未包含的本地条目只记差异日志、不删除（保守策略，避免联调期丢数据）。
    /// 兼容两种载荷形状：spec（每条 = L2[L2[名,值]...]）与平铺（L2[名,值] 连续成行，CARRIERID 开新行）。
    /// </summary>
    private void ReconcileInventory(List<SecsItem> entries)
    {
        var rows = new List<Dictionary<string, string>>();
        Dictionary<string, string>? cur = null;
        foreach (var e in entries)
        {
            List<(string Name, string Value)> pairs = new();
            try
            {
                var ch = e.Children ?? new List<SecsItem>();
                if (ch.Count >= 2 && ch.All(x => x.Children == null) && ch[0].Format == SecsFormat.A && ch[1].Format == SecsFormat.A)
                {
                    // 平铺条目：L2[A 名, A 值]
                    pairs.Add((ch[0].AsString().Trim(), ch[1].AsString().Trim()));
                }
                else
                {
                    foreach (var pair in ch)
                        if (pair.Children is { Count: >= 2 })
                            pairs.Add((pair.Children[0].AsString().Trim(), pair.Children[1].AsString().Trim()));
                }
            }
            catch (Exception ex) { Log("对账", $"条目解析失败（跳过）: {ex.Message}", LogLevel.Debug); }
            if (pairs.Count == 0) continue;
            if (pairs.Count > 1)
            {
                rows.Add(pairs.ToDictionary(p => p.Name, p => p.Value));
                cur = null;
                continue;
            }
            var (n, v) = pairs[0];
            if (n == "CARRIERID") { cur = new Dictionary<string, string>(); rows.Add(cur); }
            if (cur != null && !cur.ContainsKey(n)) cur[n] = v;
        }

        int updated = 0, unchanged = 0, skipped = 0;
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            var id = row.GetValueOrDefault("CARRIERID", "");
            var loc = row.GetValueOrDefault("CARRIERLOC", "");
            if (id.Length == 0 || !TryParseLoc(loc, out int plc, out int st)) { skipped++; continue; }
            seen.Add(id);
            var locNorm = _engine.Loc(plc, st);
            var c = _inv.Carriers.FirstOrDefault(x => x.CarrierId == id);
            if (c != null && c.CarrierLoc == locNorm) { unchanged++; continue; }
            // 搬站修复：旧位置标记离开（保留历史），再在新站安装——避免 SVID15/合并表双行显示
            if (c != null && TryParseLoc(c.CarrierLoc, out int oldPlc, out int oldSt))
                _inv.RemoveCarrier(oldPlc, oldSt, out _);
            _inv.SetCarrier(plc, st, id, locNorm, "MCS");
            Audit("对账", $"台账更新: {id} @ {locNorm}" + (c != null ? $"（原位置: {c.CarrierLoc}）" : "（新增）"));
            updated++;
        }
        foreach (var c in _inv.Carriers)
        {
            if (seen.Contains(c.CarrierId)) continue;
            if (_cfg.ReconcileStrict)   // 严格模式：MCS 列表即真理，未包含的本地条目删除
            {
                if (TryParseLoc(c.CarrierLoc, out int dPlc, out int dSt))
                    _inv.RemoveCarrier(dPlc, dSt, out _);
                Audit("对账", $"严格模式删除: {c.CarrierId} @ {c.CarrierLoc}（MCS 列表未包含）");
            }
            else
                Audit("对账", $"差异: 本地台账有 {c.CarrierId} @ {c.CarrierLoc}，MCS 列表未包含（未删除）");
        }
        Audit("对账", $"InventoryDataSend 对账完成: 共 {rows.Count} 条，更新 {updated}，一致 {unchanged}，跳过 {skipped}");
    }

    /// <summary>库存更新请求（Scenario 1.2.6 第一步）：S6F11 CEID 502，请求 MCS 下发 InventoryDataSend</summary>
    public bool TriggerInventoryUpdateRequest()
    {
        if (_hsms == null || !_hsms.Connected)
        {
            Audit("对账", "CEID 502 未发（MCS 未连接）");
            return false;
        }
        Audit("对账", "发送 InventoryUpdateRequest（CEID 502）");
        bool sent = _hsms.SendS6F11(502, 0, null);   // L3[U4, U2, L0]（MCS 方言）
        if (!sent)
            Log("HSMS", "事件 CEID 502 发送失败（MCS 未连接）: InventoryUpdateRequest", LogLevel.Warn);
        return sent;
    }
}
