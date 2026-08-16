using BufferC.Core.Modbus;

namespace BufferC.Core.Core;

/// <summary>PLC 变化推导出的事件（CEID 对齐开发文档 §2.6/§3.3）</summary>
public enum EventKind
{
    CarrierInstalled = 204,        // AGV 自动放入
    CarrierRemoved = 203,          // AGV 自动取走
    CarrierInstallCompleted = 201, // 手动放入/UNK 完成（扫码路径）
    CarrierRemoveCompleted = 202,  // 手动取走
    UnitInService = 301,
    UnitOutOfService = 302,
    AlarmSet = 102,
    AlarmCleared = 101,
    UnitAlarmSet = 402,
    UnitAlarmCleared = 401,
    CarrierIdRead = 501,
}

/// <summary>一个待上报事件</summary>
public sealed record PlcEvent(EventKind Kind, Dictionary<string, string> Params);

/// <summary>单个 PLC 的一次轮询快照（发布给 UI/SVID/推送线程时用 Clone()：轮询器每轮就地覆写，活对象发布会读到半轮混合状态）</summary>
public sealed class PlcSnapshot
{
    public int PlcIndex;
    public ushort BufferNo;                          // 0
    public ushort AlarmSummary;                      // 1
    public ushort[] StationState = new ushort[16];   // 2~17
    public ushort[] StationAlarm = new ushort[16];   // 18~33
    public ushort[] StationAvail = new ushort[16];   // 34~49
    public string[] CarrierId = new string[16];      // 50~305（仅状态变化时读取更新）
    public ushort EchoNo;                            // 306
    public ushort[] EchoStation = new ushort[16];    // 307~322
    public ushort Handshake;                         // 340
    public ushort ScanStation;                       // 323
    public string ScanCode = "";                     // 324~339

    public PlcSnapshot Clone() => new()
    {
        PlcIndex = PlcIndex,
        BufferNo = BufferNo,
        AlarmSummary = AlarmSummary,
        StationState = (ushort[])StationState.Clone(),
        StationAlarm = (ushort[])StationAlarm.Clone(),
        StationAvail = (ushort[])StationAvail.Clone(),
        CarrierId = (string[])CarrierId.Clone(),
        EchoNo = EchoNo,
        EchoStation = (ushort[])EchoStation.Clone(),
        Handshake = Handshake,
        ScanStation = ScanStation,
        ScanCode = ScanCode,
    };
}

/// <summary>
/// 事件推导引擎：快照对比 → 事件列表（多 PLC 实例共享，按 PlcIndex 隔离）。
/// 首轮快照仅做基线，不产生事件。
/// </summary>
public sealed class EventEngine
{
    private readonly object _lock = new();           // 15 个 poller 线程并发调用 Diff/Reset/MarkScanInstalled：统一串行化（推导极轻，锁开销可忽略）
    private readonly Dictionary<int, PlcSnapshot> _base = new();
    // 扫码路径完成写入后抑制 204（手动放入已由 501+201 上报）；取出时清除
    private readonly HashSet<string> _suppress204 = new();
    // 已上报的 SC 级告警（"plc:alid"）：同告警多单位时 102/101 只报一次，402/401 逐单位（spec 2.2.2/2.2.3）
    private readonly HashSet<string> _scAlarms = new();

    public void MarkScanInstalled(int plcIndex, int station)
    {
        lock (_lock) _suppress204.Add($"{plcIndex}:{station}");
    }

    /// <summary>AGV 放入完成（状态 2→1）回调：BufferC 用于启动「等扫码窗口 → 无 ID 则找 AGVC 要货物 ID」（方向1）</summary>
    public Action<int, int>? OnAgvPutCompleted;

    /// <summary>异常状态跳变回调（1→5 / 5→1：货未动却换类型标记）：仅记日志不上报 MCS</summary>
    public Action<int, int, string>? OnAbnormalTransition;

    /// <summary>重连重同步时清除基线（断线期间的变化不补报，只做状态同步）</summary>
    public void Reset(int plcIndex)
    {
        lock (_lock) _base.Remove(plcIndex);
    }

    /// <summary>对比新快照，返回应上报的事件（基线必须深拷贝：调用方会复用同一快照对象）；内部加锁串行化 15 线程并发推导</summary>
    public List<PlcEvent> Diff(PlcSnapshot cur)
    {
        lock (_lock) return DiffCore(cur);
    }

    private List<PlcEvent> DiffCore(PlcSnapshot cur)
    {
        var events = new List<PlcEvent>();
        if (!_base.TryGetValue(cur.PlcIndex, out var prev))
        {
            _base[cur.PlcIndex] = cur.Clone();
            return events;   // 基线
        }

        // 站口状态 2~17
        for (int i = 0; i < 16; i++)
        {
            ushort a = prev.StationState[i], b = cur.StationState[i];
            if (a == b) continue;
            string key = $"{cur.PlcIndex}:{i + 1}";
            if (b == 1 && a != 1)      // 放入完成（AGV 路径 0→2→1，终点 2→1）
            {
                if (a == RegisterMap.StManualCarrier)   // 异常跳变 5→1：只记日志不上报
                {
                    OnAbnormalTransition?.Invoke(cur.PlcIndex, i + 1, "5→1");
                }
                else
                {
                    if (a == RegisterMap.StPutting)     // 2→1 = AGV 放入完成（人工 0→5 不触发）
                        OnAgvPutCompleted?.Invoke(cur.PlcIndex, i + 1);
                    if (!_suppress204.Remove(key))      // 扫码路径已上报则跳过
                        events.Add(new PlcEvent(EventKind.CarrierInstalled, P(
                            "CarrierID", cur.CarrierId[i], "CarrierLoc", Loc(cur.PlcIndex, i + 1), "Station", (i + 1).ToString())));
                }
            }
            else if (b == RegisterMap.StManualCarrier && a != RegisterMap.StManualCarrier) // 人工放入完成（放入即置5，ID 后写→此处可能为空）
            {
                if (a == RegisterMap.StHasCarrier)      // 异常跳变 1→5：只记日志不上报
                    OnAbnormalTransition?.Invoke(cur.PlcIndex, i + 1, "1→5");
                else
                    events.Add(new PlcEvent(EventKind.CarrierInstallCompleted, P(
                        "CarrierID", cur.CarrierId[i], "CarrierLoc", Loc(cur.PlcIndex, i + 1), "Station", (i + 1).ToString())));
            }
            else if (b == 0 && a != 0) // 取出（AGV 路径 1/5→3→0，终点 3→0；手动 1/5→0 直跳）
            {
                _suppress204.Remove(key);
                events.Add(new PlcEvent(EventKind.CarrierRemoved, P(
                    "CarrierID", cur.CarrierId[i], "HandoffType", a == RegisterMap.StTaking ? "AGV" : "MANUAL", "Station", (i + 1).ToString())));
            }
        }

        // 告警码 18~33（SC 级 102/101 按告警码去重，单位级 402/401 逐站上报）
        for (int i = 0; i < 16; i++)
        {
            ushort a = prev.StationAlarm[i], b = cur.StationAlarm[i];
            if (a == b) continue;
            if (a == 0 && b != 0)
            {
                bool first = _scAlarms.Add($"{cur.PlcIndex}:{b}");
                if (first)
                    events.Add(new PlcEvent(EventKind.AlarmSet, P("AlarmId", b.ToString(), "UnitId", Unit(cur.PlcIndex, i + 1), "AlarmText", $"Station{i + 1} Alarm {b}")));
                events.Add(new PlcEvent(EventKind.UnitAlarmSet, P("AlarmId", b.ToString(), "UnitId", Unit(cur.PlcIndex, i + 1), "AlarmText", $"Station{i + 1} Alarm {b}")));
            }
            else if (a != 0 && b == 0)
            {
                events.Add(new PlcEvent(EventKind.UnitAlarmCleared, P("AlarmId", a.ToString(), "UnitId", Unit(cur.PlcIndex, i + 1), "AlarmText", $"Station{i + 1} Alarm {a}")));
                bool anyLeft = cur.StationAlarm.Any(x => x == a);
                if (!anyLeft)
                {
                    _scAlarms.Remove($"{cur.PlcIndex}:{a}");
                    events.Add(new PlcEvent(EventKind.AlarmCleared, P("AlarmId", a.ToString(), "UnitId", Unit(cur.PlcIndex, i + 1), "AlarmText", $"Station{i + 1} Alarm {a}")));
                }
            }
        }

        // 可用状态 34~49
        for (int i = 0; i < 16; i++)
        {
            ushort a = prev.StationAvail[i], b = cur.StationAvail[i];
            if (a == b) continue;
            if (a == 0 && b == 1) events.Add(new PlcEvent(EventKind.UnitOutOfService, P("UnitId", Unit(cur.PlcIndex, i + 1), "Station", (i + 1).ToString())));
            if (a == 1 && b == 0) events.Add(new PlcEvent(EventKind.UnitInService, P("UnitId", Unit(cur.PlcIndex, i + 1), "Station", (i + 1).ToString())));
        }

        _base[cur.PlcIndex] = cur.Clone();
        return events;
    }

    private static Dictionary<string, string> P(params string[] kv)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
        return d;
    }

    // MCS 现场命名规则（2026-08-15）：单位/位置统一 = BUFFER{机台号2位}_{站口2位}（Buffer1_Port2 → BUFFER01_02）
    public static string Loc(int plcIndex, int station) => $"BUFFER{plcIndex:00}_{station:00}";
    public static string Unit(int plcIndex, int station) => $"BUFFER{plcIndex:00}_{station:00}";
}
