using BufferC.Core.Config;
using BufferC.Core.Modbus;

namespace BufferC.Core.Core;

/// <summary>事件出口（由 BufferCService 实现：路由到 HSMS + 日志 + 表同步）</summary>
public interface IEventSink
{
    void Emit(int plcIndex, IReadOnlyList<PlcEvent> events);
    void SyncSnapshot(PlcSnapshot snap);   // 每轮轮询后落表（站口主表：状态/告警/可用/ID 兜底）
    void Log(string category, string msg, LogLevel level = LogLevel.Info);
    void EnqueueScan(int plcIndex, int station, string scanCode);   // 扫码握手命令投递（D3：poller 不阻塞，异步命令路径执行）
    void OnPlcDisconnected(int plcIndex);   // 断线回调（BufferCService：该机台台账置停服；底层变化才写，重复回调静默）
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
    private long _lastIdVerifyTick;                    // 空站口残留 ID 验证节流（2026-08-19 现场：PLC 清区后快照旧 ID 残留页面）

    public PlcPoller(PlcConfig cfg, BufferCConfig app, EventEngine engine, IEventSink sink)
    {
        _cfg = cfg;
        _app = app;
        _engine = engine;
        _sink = sink;
        _snap.StationCount = Math.Min(RegisterMap.StationsPerPlc, cfg.Stations);   // 逻辑站口数（寄存器布局恒 16 站空间）
        _client = new ModbusTcpClient(cfg.Ip, cfg.Port, cfg.UnitId, cfg.TimeoutMs);
        // Modbus 帧级日志（trace 级，logModbusFrames 配置控制；命令通道共用同一 client，命令帧自动覆盖）
        _client.Frame += (isTx, data) =>
        {
            if (_app.LogModbusFrames)
                _sink.Log($"PLC{cfg.Index}", $"MODBUS {(isTx ? "TX" : "RX")} {Convert.ToHexString(data)}", LogLevel.Trace);
        };
        _channel = new CommandChannel(_client, cfg, app, _sink.Log);   // 命令全生命周期日志收编入统一管道（category=Cmd）
    }

    public CommandChannel Channel => _channel;
    public PlcConfig Config => _cfg;
    public PlcSnapshot? Snapshot => _snapCopy;

    /// <summary>取走后清快照 ID（事件在轮询线程同步派发，无并发）：PLC 清区在命令2 回显后才生效，
    /// 快照在 1/5→0 时读到的旧 ID 若不即时清除会永久残留（状态不再变化即不再重读，状态视图兜底显示旧 ID）</summary>
    public void ClearSnapshotCarrier(int station) => _snap.CarrierId[station - 1] = "";

    // ---------- 寄存器直读写（Web「PLC 调试」面板联调用；断线抛 ModbusException/IOException） ----------
    public ushort[] ReadRegs(int addr, int count) =>
        _client.ReadHoldingRegisters((ushort)addr, (ushort)count, _app.DebugReadChunkWords);   // Web 调试读：默认 16 字循环短读（现场长帧读慢/异常）
    public void WriteReg(int addr, ushort value) => _client.WriteSingleRegister((ushort)addr, value);
    public void WriteRegs(int addr, ushort[] values) => _client.WriteMultipleRegisters((ushort)addr, values, _app.CmdWriteChunkWords);   // Web 调试写同命令通道小片策略

    // ---------- 运行统计（界面/联调用） ----------
    public bool IsConnected;
    public long LastPollAtMs;
    public long PollCount;
    public long ErrorCount;
    public long ReconnectCount;
    public string LastError = "";
    private string? _lastFailMsg;      // 断线节流：同 streak 首条 ERROR、重复异常降 Debug
    private int _failStreak;

    public sealed record PlcStats(bool Connected, DateTime LastPollAt, long PollCount, long ErrorCount,
        long ReconnectCount, string LastError, long CommandCount, long CommandFailCount);

    public PlcStats GetStats() => new(IsConnected,
        LastPollAtMs == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(LastPollAtMs).LocalDateTime,
        PollCount, ErrorCount, ReconnectCount, LastError,
        _channel.CommandCount, _channel.CommandFailCount);

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
                IsConnected = true;
                ReconnectCount++;
                _failStreak = 0;
                _lastFailMsg = null;
                _sink.Log($"PLC{_cfg.Index}", $"已连接 {_cfg.Ip}:{_cfg.Port}（第 {ReconnectCount} 次连接）");
                backoffMs = 1000;
                await PollLoopAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                IsConnected = false;
                ErrorCount++;
                LastError = e.Message;
                // 断线节流：同 streak 首条 ERROR（保留完整异常原文，场景脚本锚点），重复异常降 Debug 带计数
                _failStreak = e.Message == _lastFailMsg ? _failStreak + 1 : 1;
                _lastFailMsg = e.Message;
                bool repeated = _failStreak > 1;
                _sink.Log($"PLC{_cfg.Index}",
                    $"连接/轮询异常: {e.Message}（{(repeated ? $"连续第 {_failStreak} 次" : "首次")}，退避 {backoffMs}ms 后重试）",
                    repeated ? LogLevel.Debug : LogLevel.Error);
                _client.Disconnect();
                _prevStates = null;                       // 重连后重新基线（含事件引擎，不补报）
                _engine.Reset(_cfg.Index);
                _sink.OnPlcDisconnected(_cfg.Index);      // 断线核对：台账置停服（幂等，重复 catch 静默）
                try { await Task.Delay(backoffMs, ct); } catch (OperationCanceledException) { break; }
                backoffMs = Math.Min(backoffMs * 2, _app.ReconnectMaxBackoffMs);
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
        PollCount++;
        LastPollAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 1. 快速区：0~49 + 306~340
        var baseRegs = _client.ReadHoldingRegisters(0, 50);
        var fastRegs = _client.ReadHoldingRegisters(306, 35);
        _snap.BufferNo = baseRegs[RegisterMap.RegBufferNo];
        _snap.AlarmSummary = baseRegs[RegisterMap.RegAlarmSummary];
        for (int i = 0; i < 16; i++)
        {
            _snap.StationState[i] = baseRegs[RegisterMap.RegStationState + i];
            _snap.StationAlarm[i] = baseRegs[RegisterMap.RegStationAlarm + i];
            _snap.StationAvail[i] = baseRegs[RegisterMap.RegStationAvail + i];
        }
        _snap.EchoNo = fastRegs[0];
        Array.Copy(fastRegs, 1, _snap.EchoStation, 0, 16);
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

        // 2b. 空站口残留 ID 兜底验证（2026-08-19 现场：PLC 清区后快照旧 ID 永久残留页面）：
        // 状态 0 且快照仍有 ID → 每 2s 重读该站 ID 区直到确认空（16 字小片，真 PLC 已验证可承受；覆盖 PLC 侧自行清区等外部路径）
        if (Environment.TickCount64 - _lastIdVerifyTick >= 2000)
        {
            for (int s = 0; s < _snap.StationCount; s++)
                if (curStates[s] == RegisterMap.StEmpty && _snap.CarrierId[s] != "")
                    _snap.CarrierId[s] = ReadStationId(s + 1);
            _lastIdVerifyTick = Environment.TickCount64;
        }

        // 3. 扫码握手：340=1 → 应答 340=0 → 投递异步命令（D3：不再阻塞轮询——
        // 命令执行/501/201/台账由命令工作线程完成，poller 立即恢复轮询，消除 ≤10s 盲区）
        if (_snap.Handshake == 1)
        {
            int st = _snap.ScanStation;
            _client.WriteSingleRegister(RegisterMap.RegHandshake, 0);   // 应答
            _sink.EnqueueScan(_cfg.Index, st, _snap.ScanCode);
        }

        // 4. 事件推导 + 上报（快照先发布，供 SVID 查询；发布深拷贝：本对象每轮就地覆写，活对象会读到半轮混合状态）；随后同步站口主表（变化才写）
        _snapCopy = _snap.Clone();
        var events = _engine.Diff(_snap);
        if (events.Count > 0) _sink.Emit(_cfg.Index, events);
        _sink.SyncSnapshot(_snap);
    }
}
