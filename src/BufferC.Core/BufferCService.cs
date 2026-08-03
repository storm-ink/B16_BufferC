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
    private readonly object _alarmLock = new();

    public BufferCService(BufferCConfig cfg)
    {
        _cfg = cfg;
        _inv = new Inventory(cfg.DbPath);
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
                    if (s.StationState[i] != RegisterMap.StHasCarrier) continue;
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
        Log("SVC", $"BufferC 启动: {_cfg.Plcs.Count} 台 PLC, HSMS 端口 {_cfg.Hsms.ListenPort}");
    }

    public void Stop()
    {
        _cts.Cancel();
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
    public sealed record LogEntry(DateTime Time, string Category, string Message);
    public sealed record EventEntry(DateTime Time, ushort Ceid, string Description);
    public sealed record CmdEntry(DateTime Time, string Source, int Station, string Cmd, bool Ok, long ElapsedMs, string CarrierId);
    public sealed record AlarmHistoryEntry(DateTime Time, string UnitId, uint AlarmId, string Text, string Action);

    private readonly RingBuffer<LogEntry> _logs = new(500);
    private readonly RingBuffer<EventEntry> _events = new(100);
    private readonly RingBuffer<CmdEntry> _cmds = new(100);
    private readonly RingBuffer<AlarmHistoryEntry> _alarmHistory = new(100);
    public DateTime StartedAt;

    public List<LogEntry> GetLogs(int tail, string? category = null)
    {
        var all = _logs.GetTail(tail * 2);
        return category == null ? all.TakeLast(tail).ToList()
            : all.Where(l => l.Category.Contains(category, StringComparison.OrdinalIgnoreCase)).TakeLast(tail).ToList();
    }
    public List<EventEntry> GetEvents(int tail = 50) => _events.GetTail(tail);
    public List<CmdEntry> GetCommands(int tail = 50) => _cmds.GetTail(tail);
    public List<AlarmHistoryEntry> GetAlarmHistory(int tail = 50) => _alarmHistory.GetTail(tail);

    public void Log(string category, string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}][{category}] {msg}";
        Console.WriteLine(line);
        _logs.Add(new LogEntry(DateTime.Now, category, msg));
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
                break;
            case EventKind.CarrierRemoved:
                _inv.RemoveCarrier(plcIndex, st, out _);
                hsms.SendS6F11(203, 2, Rpt2(p));   // RPT2: CarrierID + HandoffType
                break;
            case EventKind.CarrierInstallCompleted:
                _inv.SetCarrier(plcIndex, st, p.GetValueOrDefault("CarrierID", ""), p.GetValueOrDefault("CarrierLoc", ""));
                hsms.SendS6F11(201, 2, Rpt2(p));
                break;
            case EventKind.CarrierRemoveCompleted:
                _inv.RemoveCarrier(plcIndex, st, out _);
                hsms.SendS6F11(202, 2, Rpt2(p));
                break;
            case EventKind.CarrierIdRead:
                hsms.SendS6F11(501, 5, SecsEncode.L(
                    SecsEncode.A(p.GetValueOrDefault("CarrierID", "")),
                    SecsEncode.A(p.GetValueOrDefault("CarrierLoc", "")),
                    SecsEncode.U2(ushort.Parse(p.GetValueOrDefault("IDReadStatus", "0")))));
                break;
            case EventKind.UnitInService:
                _inv.UpdateUnit(plcIndex, st, 0);
                hsms.SendS6F11(301, 3, Rpt3(p));
                break;
            case EventKind.UnitOutOfService:
                _inv.UpdateUnit(plcIndex, st, 1);
                hsms.SendS6F11(302, 3, Rpt3(p));
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
    private static readonly Regex LocRegex = new(@"^Buffer(\d+)_Port(\d+)$", RegexOptions.Compiled);

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
        var m = LocRegex.Match(loc);
        if (!m.Success || carrierId.Length == 0)
        {
            Log("CMD", $"参数无效: CARRIERID={carrierId} CARRIERLOC={loc}");
            return false;
        }
        int plc = int.Parse(m.Groups[1].Value), st = int.Parse(m.Groups[2].Value);
        var poller = _pollers.FirstOrDefault(x => x.Config.Index == plc);
        if (poller == null) { Log("CMD", $"PLC{plc} 不存在"); return false; }
        bool ok = poller.Channel.Execute(st, RegisterMap.CmdWrite, carrierId);
        sw.Stop();
        _cmds.Add(new CmdEntry(DateTime.Now, source, st, "CarrierDataInstall", ok, sw.ElapsedMilliseconds, carrierId));
        Log("CMD", $"安装 站口{st} 写入 {(ok ? "OK" : "FAIL")}（{sw.ElapsedMilliseconds}ms）");
        if (ok)
        {
            _inv.SetCarrier(plc, st, carrierId, loc);
            _hsms?.SendS6F11(201, 2, SecsEncode.L(SecsEncode.A(carrierId), SecsEncode.A(loc)));
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
        bool ok = poller != null && poller.Channel.Execute(st, RegisterMap.CmdClear, null);
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
