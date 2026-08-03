using BufferC.Core.Config;
using BufferC.Core.Modbus;

namespace BufferC.Core.Core;

/// <summary>事件出口（由 BufferCService 实现：路由到 HSMS + 日志）</summary>
public interface IEventSink
{
    void Emit(int plcIndex, IReadOnlyList<PlcEvent> events);
    void Log(string category, string msg);
}

/// <summary>
/// 单台 PLC 轮询器：快速轮询 + 变化触发读 ID + 扫码握手 + 事件推导。
/// 断线重连（指数退避）+ 重连后全量重同步（基线重建，不补报事件）。
/// </summary>
public sealed class PlcPoller
{
    private readonly PlcConfig _cfg;
    private readonly BufferCConfig _app;
    private readonly EventEngine _engine;
    private readonly IEventSink _sink;
    private readonly ModbusTcpClient _client;
    private readonly CommandChannel _channel;
    private readonly PlcSnapshot _snap = new();
    private PlcSnapshot? _snapCopy;                    // 供 SVID 查询读取的最近快照（跨线程）
    private ushort[]? _prevStates;

    public PlcPoller(PlcConfig cfg, BufferCConfig app, EventEngine engine, IEventSink sink)
    {
        _cfg = cfg;
        _app = app;
        _engine = engine;
        _sink = sink;
        _client = new ModbusTcpClient(cfg.Ip, cfg.Port, cfg.UnitId, cfg.TimeoutMs);
        _channel = new CommandChannel(_client, cfg, app);
    }

    public CommandChannel Channel => _channel;
    public PlcConfig Config => _cfg;
    public PlcSnapshot? Snapshot => _snapCopy;

    private string ReadStationId(int station) =>
        RegisterMap.UnpackAscii(_client.ReadHoldingRegisters(
            (ushort)(RegisterMap.RegCarrierId + (station - 1) * RegisterMap.CarrierIdWords), RegisterMap.CarrierIdWords),
            _cfg.ByteOrder);

    public async Task RunAsync(CancellationToken ct)
    {
        var backoffMs = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _client.Connect();
                _sink.Log($"PLC{_cfg.Index}", $"已连接 {_cfg.Ip}:{_cfg.Port}");
                backoffMs = 1000;
                await PollLoopAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _sink.Log($"PLC{_cfg.Index}", $"连接/轮询异常: {e.Message}");
                _client.Disconnect();
                _prevStates = null;                       // 重连后重新基线（含事件引擎，不补报）
                _engine.Reset(_cfg.Index);
                try { await Task.Delay(backoffMs, ct); } catch (OperationCanceledException) { break; }
                backoffMs = Math.Min(backoffMs * 2, 30_000);
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PollOnce();
            await Task.Delay((int)_app.PollIntervalMs, ct);
        }
    }

    private void PollOnce()
    {
        _snap.PlcIndex = _cfg.Index;

        // 1. 快速区：0~49 + 306~340
        var baseRegs = _client.ReadHoldingRegisters(0, 50);
        var fastRegs = _client.ReadHoldingRegisters(306, 35);
        for (int i = 0; i < 16; i++)
        {
            _snap.StationState[i] = baseRegs[RegisterMap.RegStationState + i];
            _snap.StationAlarm[i] = baseRegs[RegisterMap.RegStationAlarm + i];
            _snap.StationAvail[i] = baseRegs[RegisterMap.RegStationAvail + i];
        }
        _snap.ScanStation = fastRegs[RegisterMap.RegScanStation - RegisterMap.RegEchoNo];
        _snap.ScanCode = RegisterMap.UnpackAscii(fastRegs[(RegisterMap.RegScanCode - RegisterMap.RegEchoNo)..(RegisterMap.RegScanCode - RegisterMap.RegEchoNo + 16)], _cfg.ByteOrder);
        _snap.Handshake = fastRegs[RegisterMap.RegHandshake - RegisterMap.RegEchoNo];

        // 2. 站口状态变化 → 按站口读货物ID（每站 16 寄存器；FC03 单次上限 125）
        var curStates = _snap.StationState;
        if (_prevStates == null)
        {
            for (int s = 0; s < 16; s++) _snap.CarrierId[s] = ReadStationId(s + 1);
        }
        else
        {
            for (int s = 0; s < 16; s++)
                if (curStates[s] != _prevStates[s])
                    _snap.CarrierId[s] = ReadStationId(s + 1);
        }
        _prevStates = (ushort[])curStates.Clone();

        // 3. 扫码握手：340=1 → 应答 340=0 → 命令路径写入 → 501 + 201
        var extra = new List<PlcEvent>();
        if (_snap.Handshake == 1)
        {
            int st = _snap.ScanStation;
            bool failed = _snap.ScanCode.StartsWith("UNK");
            _client.WriteSingleRegister(RegisterMap.RegHandshake, 0);   // 应答
            bool ok = _channel.Execute(st, RegisterMap.CmdWrite, _snap.ScanCode);
            _sink.Log($"PLC{_cfg.Index}", $"扫码握手: 站口{st} 码=\"{_snap.ScanCode}\" 写入={(ok ? "OK" : "FAIL")}");
            if (ok)
            {
                _engine.MarkScanInstalled(_cfg.Index, st);
                extra.Add(new PlcEvent(EventKind.CarrierIdRead, new Dictionary<string, string>
                {
                    ["CarrierID"] = _snap.ScanCode,
                    ["CarrierLoc"] = EventEngine.Loc(_cfg.Index, st),
                    ["IDReadStatus"] = failed ? "1" : "0",
                    ["Station"] = st.ToString(),
                }));
                extra.Add(new PlcEvent(EventKind.CarrierInstallCompleted, new Dictionary<string, string>
                {
                    ["CarrierID"] = _snap.ScanCode,
                    ["CarrierLoc"] = EventEngine.Loc(_cfg.Index, st),
                    ["Station"] = st.ToString(),
                }));
            }
        }

        // 4. 事件推导 + 上报（快照先发布，供 SVID 查询）
        _snapCopy = _snap;
        var events = _engine.Diff(_snap);
        if (extra.Count > 0) events.AddRange(extra);
        if (events.Count > 0) _sink.Emit(_cfg.Index, events);
    }
}
