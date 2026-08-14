using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly EventEngine _engine = new();
    private readonly List<PlcPoller> _pollers = new();
    private readonly List<AlarmStatus> _alarms = new();
    private readonly CancellationTokenSource _cts = new();
    private HsmsServer? _hsms;
    private AgvcClient? _agvc;                              // 出站 AGVC 客户端（baseUrl 空=不启用）
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _agvcArrivalTimers = new();
    private readonly object _alarmLock = new();
    private LogLevel _minLevel = LogLevel.Info;             // 启动按 cfg.LogLevel 解析；/api/debug 可运行时切换
    private long _logSeq;

    public BufferCService(BufferCConfig cfg)
    {
        _cfg = cfg;
        _inv = new Inventory(cfg.DbPath);
        _minLevel = Enum.TryParse<LogLevel>(cfg.LogLevel, true, out var lv) ? lv : LogLevel.Info;
    }

    /// <summary>全部 PLC 已完成首次轮询（基线就绪，测试/联调用）</summary>
    public bool AllPlcsReady => _pollers.Count > 0 && _pollers.All(p => p.Snapshot != null);

    // ---------- IStatusProvider ----------
    // SVID 15/29：台账 + 轮询快照合并（重启恢复后台账为空，查询须反映 PLC 实况）
    public IReadOnlyList<CarrierStatus> Carriers
    {
        get
        {
            var list = _inv.Carriers.ToList();
            foreach (var p in _pollers)
            {
                var s = p.Snapshot;
                if (s == null) continue;
                for (int i = 0; i < 16; i++)
                {
                    if (s.StationState[i] is not (RegisterMap.StHasCarrier or RegisterMap.StManualCarrier)) continue;
                    var loc = EventEngine.Loc(p.Config.Index, i + 1);
                    if (list.All(c => c.CarrierLoc != loc))
                        list.Add(new CarrierStatus(s.CarrierId[i] ?? "", loc, "", 1));
                }
            }
            return list;
        }
    }

    public IReadOnlyList<UnitStatus> Units
    {
        get
        {
            var dict = _inv.Units.ToDictionary(u => u.UnitId, u => u.State);
            foreach (var p in _pollers)
            {
                var s = p.Snapshot;
                if (s == null) continue;
                for (int i = 0; i < 16; i++)
                    dict[EventEngine.Unit(p.Config.Index, i + 1)] = s.StationAvail[i] == 0 ? (ushort)2 : (ushort)1;
            }
            return dict.Select(kv => new UnitStatus(kv.Key, kv.Value)).ToList();
        }
    }

    public IReadOnlyList<AlarmStatus> Alarms { get { lock (_alarmLock) return _alarms.ToList(); } }

    // ---------- 生命周期 ----------
    public void Start()
    {
        StartedAt = DateTime.Now;
        _hsms = new HsmsServer(_cfg.Hsms, this);
        _hsms.Log += (c, m) => Log(c, m);
        _hsms.OnHostCommand += HandleHostCommand;
        _hsms.Start();

        foreach (var plc in _cfg.Plcs)
        {
            var poller = new PlcPoller(plc, _cfg, _engine, this);
            _pollers.Add(poller);
            _ = Task.Run(() => poller.RunAsync(_cts.Token));
        }

        // AGVC 集成（方向1 出站）：baseUrl 配置后订阅 AGV 放入完成回调（2→1）
        if (!string.IsNullOrWhiteSpace(_cfg.Agvc.BaseUrl))
        {
            _agvc = new AgvcClient(_cfg.Agvc, Log);
            _engine.OnAgvPutCompleted += StartAgvcArrivalTimer;
        }
        // 异常状态跳变（1→5 / 5→1）：只记日志不上报 MCS
        _engine.OnAbnormalTransition += (plc, st, desc) =>
            Log("PLC", $"PLC{plc} 站口{st} 异常状态跳变 {desc}（仅记录不上报）", LogLevel.Info);
        Log("SVC", $"BufferC 启动: {_cfg.Plcs.Count} 台 PLC, HSMS 端口 {_cfg.Hsms.ListenPort}, AGVC {( _agvc != null ? _cfg.Agvc.BaseUrl : "未配置（出站禁用）")}");
    }

    public void Stop()
    {
        _cts.Cancel();
        foreach (var t in _agvcArrivalTimers.Values) { t.Cancel(); t.Dispose(); }
        _agvcArrivalTimers.Clear();
        _agvc?.Dispose();
        _hsms?.Dispose();
        lock (_logLock)
        {
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }

    public void Dispose() => Stop();

    // ---------- IEventSink ----------
    private readonly object _logLock = new();
    private StreamWriter? _logWriter;

    // ---------- 活动记录（界面/联调用，环形缓冲） ----------
    public sealed record LogEntry(DateTime Time, string Category, LogLevel Level, long Seq, string Message);
    public sealed record EventEntry(DateTime Time, ushort Ceid, string Description);
    public sealed record CmdEntry(DateTime Time, string Source, int Station, string Cmd, bool Ok, long ElapsedMs, string CarrierId);
    public sealed record AlarmHistoryEntry(DateTime Time, string UnitId, uint AlarmId, string Text, string Action);

    private readonly RingBuffer<LogEntry> _logs = new(500);
    private readonly RingBuffer<EventEntry> _events = new(100);
    private readonly RingBuffer<CmdEntry> _cmds = new(100);
    private readonly RingBuffer<AlarmHistoryEntry> _alarmHistory = new(100);
    public DateTime StartedAt;

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

    /// <summary>日志（比启动/运行时级别更详细的整条跳过——info 最粗、trace 最细；三通道统一）；默认 Info 保持既有调用语义</summary>
    public void Log(string category, string msg, LogLevel level = LogLevel.Info)
    {
        if (level > _minLevel) return;
        var line = $"[{DateTime.Now:HH:mm:ss.fff}][{category}] {msg}";
        Console.WriteLine(line);
        _logs.Add(new LogEntry(DateTime.Now, category, level, Interlocked.Increment(ref _logSeq), msg));
        try
        {
            // 单例 writer（高频事件下避免反复开关文件句柄）；UTF-8 带 BOM 兼容 PowerShell 5.1
            lock (_logLock)
            {
                _logWriter ??= new StreamWriter(_cfg.LogFile, append: true,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
                _logWriter.WriteLine(line);
            }
        }
        catch { /* 日志文件不可写时仅控制台 */ }
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
            _events.Add(new EventEntry(DateTime.Now, (ushort)ev.Kind, DescribeEvent(ev)));
            HandleEvent(plcIndex, ev);
        }
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
                _inv.SetCarrier(plcIndex, st, p.GetValueOrDefault("CarrierID", ""), p.GetValueOrDefault("CarrierLoc", ""));
                hsms.SendS6F11(204, 2, Rpt2(p));
                PushStationStatus(plcIndex, st);
                break;
            case EventKind.CarrierRemoved:
                _inv.RemoveCarrier(plcIndex, st, out _);
                hsms.SendS6F11(203, 2, Rpt2(p));   // RPT2: CarrierID + HandoffType
                PushStationStatus(plcIndex, st);
                break;
            case EventKind.CarrierInstallCompleted:
                _inv.SetCarrier(plcIndex, st, p.GetValueOrDefault("CarrierID", ""), p.GetValueOrDefault("CarrierLoc", ""));
                hsms.SendS6F11(201, 2, Rpt2(p));
                PushStationStatus(plcIndex, st, p.GetValueOrDefault("CarrierID", ""));
                break;
            case EventKind.CarrierRemoveCompleted:
                _inv.RemoveCarrier(plcIndex, st, out _);
                hsms.SendS6F11(202, 2, Rpt2(p));
                break;
            case EventKind.CarrierIdRead:
                CancelAgvcArrivalTimer(plcIndex, st);   // 扫码路径已写入 ID，取消「找 AGVC 要 ID」等待计时
                hsms.SendS6F11(501, 5, SecsEncode.L(
                    SecsEncode.A(p.GetValueOrDefault("CarrierID", "")),
                    SecsEncode.A(p.GetValueOrDefault("CarrierLoc", "")),
                    SecsEncode.U2(ushort.Parse(p.GetValueOrDefault("IDReadStatus", "0")))));
                PushStationStatus(plcIndex, st, p.GetValueOrDefault("CarrierID", ""));
                break;
            case EventKind.UnitInService:
                _inv.UpdateUnit(plcIndex, st, 0);
                hsms.SendS6F11(301, 3, Rpt3(p));
                PushStationStatus(plcIndex, st);
                break;
            case EventKind.UnitOutOfService:
                _inv.UpdateUnit(plcIndex, st, 1);
                hsms.SendS6F11(302, 3, Rpt3(p));
                PushStationStatus(plcIndex, st);
                break;
            case EventKind.AlarmSet:
            {
                // SC 级：S5F1 + 102（同告警多单位时引擎已去重，仅首个单位触发）
                uint alid = uint.Parse(p.GetValueOrDefault("AlarmId", "0"));
                string text = p.GetValueOrDefault("AlarmText", "Alarm");
                string unit = p.GetValueOrDefault("UnitId", "");
                hsms.SendS5F1(true, alid, text);
                hsms.SendS6F11(102, 1, Rpt1(p));
                _alarmHistory.Add(new AlarmHistoryEntry(DateTime.Now, unit, alid, text, "SET"));
                lock (_alarmLock)
                    if (_alarms.All(a => a.AlarmId != alid)) _alarms.Add(new AlarmStatus(unit, alid, text));
                break;
            }
            case EventKind.UnitAlarmSet:
                hsms.SendS6F11(402, 4, Rpt4(p));
                break;
            case EventKind.AlarmCleared:
            {
                // SC 级：S5F1(清除) + 101（仅最后一个单位清除时触发）
                uint alid = uint.Parse(p.GetValueOrDefault("AlarmId", "0"));
                string text = p.GetValueOrDefault("AlarmText", "Alarm");
                string unit = p.GetValueOrDefault("UnitId", "");
                hsms.SendS5F1(false, alid, text);
                hsms.SendS6F11(101, 1, Rpt1(p));
                _alarmHistory.Add(new AlarmHistoryEntry(DateTime.Now, unit, alid, text, "CLEAR"));
                lock (_alarmLock) _alarms.RemoveAll(a => a.AlarmId == alid);
                break;
            }
            case EventKind.UnitAlarmCleared:
                hsms.SendS6F11(401, 4, Rpt4(p));
                break;
        }
    }

    // ---------- RPT 组装 ----------
    private static byte[] Rpt2(Dictionary<string, string> p) => SecsEncode.L(
        SecsEncode.A(p.GetValueOrDefault("CarrierID", "")),
        SecsEncode.A(p.GetValueOrDefault("CarrierLoc", p.GetValueOrDefault("HandoffType", ""))));

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

    /// <summary>站口定位解析：优先 AGVC cmsIndex 格式（纯数字，如 10001）→ 回落宽松文本格式</summary>
    private bool TryParseLoc(string loc, out int plc, out int station)
    {
        if (TryParseCmsIndex(loc, out plc, out station)) return true;
        var m = LocRegex.Match(loc);
        plc = 0; station = 0;
        return m.Success && int.TryParse(m.Groups[1].Value, out plc)
            && int.TryParse(m.Groups[2].Value, out station);
    }

    // 悬空命令补偿（C1）：HCACK=4 已确认后命令失败 → 记录 + 内部告警 9001（告警码表 A9 确认后调整）
    public const uint AlarmCmdFailed = 9001;
    private readonly List<FailedCommand> _failedCommands = new();
    private readonly object _failLock = new();

    public sealed record FailedCommand(string Source, string Cmd, string CarrierId, string CarrierLoc, DateTime Time);
    public IReadOnlyList<FailedCommand> FailedCommands { get { lock (_failLock) return _failedCommands.ToList(); } }

    private void RecordFailedCommand(string source, string cmd, string carrierId, string loc)
    {
        lock (_failLock)
        {
            _failedCommands.Add(new FailedCommand(source, cmd, carrierId, loc, DateTime.Now));
            if (_failedCommands.Count > 100) _failedCommands.RemoveAt(0);
        }
        Log("CMD", $"悬空命令: {source} {cmd} {carrierId} {loc}（HCACK=4 已确认，执行失败）");
        _hsms?.SendS5F1(true, AlarmCmdFailed, $"命令执行失败: {cmd} {carrierId} {loc}");
        lock (_alarmLock)
            if (_alarms.All(a => a.AlarmId != AlarmCmdFailed))
                _alarms.Add(new AlarmStatus("SYSTEM", AlarmCmdFailed, "命令执行失败（悬空命令）"));
    }

    /// <summary>安装载具（source: MCS/AGVC/MANUAL；成功返回 true 并上报 201）</summary>
    public bool ManualInstall(string carrierId, string loc, string source = "MANUAL")
    {
        var sw = Stopwatch.StartNew();
        if (!TryParseLoc(loc, out int plc, out int st) || carrierId.Length == 0)
        {
            Log("CMD", $"参数无效: CARRIERID={carrierId} CARRIERLOC={loc}");
            return false;
        }
        if (st < 1 || st > RegisterMap.StationsPerPlc) { Log("CMD", $"站口越界: {loc}"); return false; }
        var poller = _pollers.FirstOrDefault(x => x.Config.Index == plc);
        if (poller == null) { Log("CMD", $"PLC{plc} 不存在"); return false; }
        // 台账/事件统一用标准 loc（输入兼容 cmsIndex 纯数字与文本格式，如 "10001" → "Buffer1_Port1"）
        var locNorm = EventEngine.Loc(plc, st);
        // PLC 断线时 Execute 会抛传输异常（调试台实测）：一律按失败处理，不得让异常崩掉 HSMS 连接
        bool ok;
        try { ok = poller.Channel.Execute(st, RegisterMap.CmdWrite, carrierId); }
        catch (Exception e) { Log("CMD", $"安装 站口{st} 命令异常: {e.Message}"); ok = false; }
        sw.Stop();
        _cmds.Add(new CmdEntry(DateTime.Now, source, st, "CarrierDataInstall", ok, sw.ElapsedMilliseconds, carrierId));
        Log("CMD", $"安装 站口{st} 写入 {(ok ? "OK" : "FAIL")}（{sw.ElapsedMilliseconds}ms）");
        if (ok)
        {
            CancelAgvcArrivalTimer(plc, st);   // 命令路径已写入 ID，取消「找 AGVC 要 ID」计时
            _inv.SetCarrier(plc, st, carrierId, locNorm);
            _hsms?.SendS6F11(201, 2, SecsEncode.L(SecsEncode.A(carrierId), SecsEncode.A(locNorm)));
            PushStationStatus(plc, st, carrierId);   // 命令路径不经过 HandleEvent，直接推状态（trayId 用已知 ID）
        }
        else
        {
            RecordFailedCommand(source, "CarrierDataInstall", carrierId, loc);
        }
        return ok;
    }

    /// <summary>移除载具（成功返回 true 并上报 202）</summary>
    public bool ManualRemove(string carrierId, string source = "MANUAL")
    {
        var sw = Stopwatch.StartNew();
        if (!_inv.FindCarrier(carrierId, out int plc, out int st))
        {
            Log("CMD", $"移除找不到 {carrierId}（库存无此载具）");
            return false;
        }
        var poller = _pollers.FirstOrDefault(x => x.Config.Index == plc);
        // PLC 断线时 Execute 会抛传输异常（同 ManualInstall）：按失败处理，不得崩掉 HSMS 连接
        bool ok;
        try { ok = poller != null && poller.Channel.Execute(st, RegisterMap.CmdClear, null); }
        catch (Exception e) { Log("CMD", $"移除 {carrierId} 命令异常: {e.Message}"); ok = false; }
        sw.Stop();
        _cmds.Add(new CmdEntry(DateTime.Now, source, st, "CarrierDataRemove", ok, sw.ElapsedMilliseconds, carrierId));
        Log("CMD", $"移除 {carrierId} 清除 {(ok ? "OK" : "FAIL")}（{sw.ElapsedMilliseconds}ms）");
        if (ok)
        {
            _inv.RemoveCarrier(plc, st, out _);
            _hsms?.SendS6F11(202, 2, SecsEncode.L(SecsEncode.A(carrierId), SecsEncode.A(EventEngine.Loc(plc, st))));
        }
        else
        {
            RecordFailedCommand(source, "CarrierDataRemove", carrierId, EventEngine.Loc(plc, st));
        }
        return ok;
    }

    /// <summary>告警确认（本地标记，不影响 SVID 3 内容）</summary>
    public bool AckAlarm(uint alid)
    {
        lock (_alarmLock)
        {
            int idx = _alarms.FindIndex(a => a.AlarmId == alid);
            if (idx < 0) return false;
            _alarms[idx] = _alarms[idx] with { Acked = true };
            return true;
        }
    }

    // ---------- PLC 调试（Web「PLC 调试」面板：寄存器直读直写 + 站口命令直发，绕过业务副作用） ----------
    private PlcPoller? FindPoller(int plcIndex) => _pollers.FirstOrDefault(x => x.Config.Index == plcIndex);

    /// <summary>读任意保持寄存器（FC03，1~125 个）；PLC 不存在/断线/越界返回 null</summary>
    public ushort[]? ReadPlcRegs(int plcIndex, int addr, int count)
    {
        var p = FindPoller(plcIndex);
        if (p == null || addr < 0 || addr > ushort.MaxValue || count < 1 || count > 125) return null;
        try { return p.ReadRegs(addr, count); }
        catch (Exception e) { Log("DBG", $"读 PLC{plcIndex} 寄存器 {addr}+{count} 失败: {e.Message}"); return null; }
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
        catch (Exception e) { Log("DBG", $"写 PLC{plcIndex} 寄存器 {addr} 失败: {e.Message}"); return false; }
    }

    /// <summary>直发站口命令（1=写入ID/2=清除），仅走 PLC 命令协议（含回显等待/重试），不动台账不发 MCS 事件；返回 成功+耗时</summary>
    public (bool Ok, long ElapsedMs) DebugSendCmd(int plcIndex, int station, ushort cmd, string? carrierId)
    {
        var p = FindPoller(plcIndex);
        if (p == null) return (false, 0);
        var sw = Stopwatch.StartNew();
        bool ok;
        try { ok = p.Channel.Execute(station, cmd, carrierId); }
        catch (Exception e) { Log("DBG", $"直发命令 PLC{plcIndex} 站口{station} 命令{cmd} 异常: {e.Message}"); ok = false; }
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
        return plc is >= 1 and <= 15 && station is >= 1 and <= RegisterMap.StationsPerPlc;
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
                    Log("AGVC", $"站口{station} AGV 到货无 ID 但 agvc.baseUrl 未配置，跳过");
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
        // trayId：无货恒空（取走后 PLC ID 区可能未清，快照陈旧）；有货时事件已知 ID 优先 → 台账（快照 ID 仅在状态变化时重读，扫码/命令写入后是陈旧的）→ 快照
        string trayId = present
            ? knownId
                ?? _inv.Carriers.FirstOrDefault(c => c.CarrierLoc == EventEngine.Loc(plcIndex, station))?.CarrierId
                ?? snap.CarrierId[station - 1] ?? ""
            : "";
        ushort avail = snap.StationAvail[station - 1];
        _ = Task.Run(() => _agvc!.PushStatusAsync(plcIndex, station, avail, present, trayId, _cts.Token));
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
        PlcRegisters Registers, List<StationView> Stations);
    public sealed record PlcRegisters(ushort BufferNo, ushort AlarmSummary, ushort EchoNo, ushort[] EchoStation,
        ushort ScanStation, string ScanCode, ushort Handshake, string ByteOrder);
    public sealed record StationView(int Station, int State, int Alarm, int Avail, string CarrierId, bool Truncated,
        string CarrierLoc, string InstallTime);
    public sealed record SystemInfo(DateTime StartedAt, string Mdln, string SoftRev, string PlcSummary,
        string ByteOrderSummary, double PollIntervalMs, int HsmsPort, int WebPort);

    public StatusView GetStatusView()
    {
        var plcs = new List<PlcView>();
        foreach (var p in _pollers)
        {
            var s = p.Snapshot;
            var stations = new List<StationView>();
            for (int i = 0; i < 16; i++)
            {
                var loc = EventEngine.Loc(p.Config.Index, i + 1);
                // 台账为权威（含手动安装/命令安装）；快照兜底 AGV 实况
                var carrier = _inv.Carriers.FirstOrDefault(c => c.CarrierLoc == loc);
                var id = carrier?.CarrierId ?? (s != null ? s.CarrierId[i] ?? "" : "");
                stations.Add(new StationView(i + 1,
                    s != null ? s.StationState[i] : 0,
                    s != null ? s.StationAlarm[i] : 0,
                    s != null ? s.StationAvail[i] : 0,
                    id, id.Length >= RegisterMap.CarrierIdChars,
                    loc, carrier?.InstallTime ?? ""));
            }
            plcs.Add(new PlcView(p.Config.Index, p.Config.Ip, p.IsConnected, p.GetStats(), new PlcRegisters(
                s != null ? s.BufferNo : (ushort)0,
                s != null ? s.AlarmSummary : (ushort)0,
                s != null ? s.EchoNo : (ushort)0,
                s != null ? s.EchoStation : new ushort[16],
                s != null ? s.ScanStation : (ushort)0,
                s != null ? s.ScanCode : "",
                s != null ? s.Handshake : (ushort)0,
                p.Config.ByteOrder), stations));
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

    // ---------- S2F41 主机命令 ----------
    private void HandleHostCommand(string rcmd, Dictionary<string, string> pars)
    {
        Log("S2F41", $"RCMD={rcmd}");
        switch (rcmd)
        {
            case "CarrierDataInstall":
                ManualInstall(pars.GetValueOrDefault("CARRIERID", ""), pars.GetValueOrDefault("CARRIERLOC", ""), "MCS");
                break;
            case "CarrierDataRemove":
                ManualRemove(pars.GetValueOrDefault("CARRIERID", ""), "MCS");
                break;
            case "InventoryDataSend":
                // 已裁剪（2026-08-04）：异常收到时记录并忽略
                Log("S2F41", $"InventoryDataSend 收到（已裁剪，忽略）");
                break;
            default:
                Log("S2F41", $"未知命令 {rcmd}");
                break;
        }
    }
}
