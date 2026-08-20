using System.Net;
using System.Net.Sockets;
using BufferC.Core.Modbus;

namespace BufferC.Harness;

/// <summary>
/// PLC 仿真器（Modbus TCP Server）：模拟 V8 寄存器空间 + 命令执行回显 + 扫码握手。
/// 测试用行为 API：SetStationState / SetCarrierId / SetStationAlarm / TriggerScan / DropConnection。
/// </summary>
public sealed class PlcSim : IDisposable
{
    private readonly int _index;
    private readonly byte _unitId;
    private readonly string _byteOrder;
    private readonly int _stations;   // 该机台站口数（8/16）：行为 API 参数=逻辑号，内部按 StationMap 换算物理寄存器
    private readonly ushort[] _regs = new ushort[1024];
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private ushort _lastExecSeq;
    private readonly List<TcpClient> _clients = new();

    public PlcSim(int index, byte unitId = 1, string byteOrder = "high", int stations = 16)
    {
        _index = index;
        _unitId = unitId;
        _byteOrder = byteOrder;
        _stations = stations;
        _regs[RegisterMap.RegBufferNo] = (ushort)index;
    }

    public int Port { get; private set; }

    /// <summary>当前已连接的 Modbus 客户端数（联调预演用：等 BufferC 连入）</summary>
    public int ClientCount { get { lock (_clients) return _clients.Count; } }

    /// <summary>启动监听（随机端口，Loopback），返回端口号</summary>
    public int Start() => Start(IPAddress.Loopback, 0);

    /// <summary>启动监听（指定地址与端口）；联调预演用 Any + 固定端口，让 WSL 里的 BufferC 经网关 IP 接入</summary>
    public int Start(IPAddress ip, int port)
    {
        _listener = new TcpListener(ip, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        lock (_clients) foreach (var c in _clients) c.Dispose();
    }

    /// <summary>注入记录（调试台可观测性）</summary>
    private void Note(string s) => Console.WriteLine($"[PlcSim{_index}] 注入: {s}");

    /// <summary>断线注入：掐断所有连接（BufferC 将走重连+重同步）</summary>
    public void DropConnection()
    {
        Note($"断连注入（{_clients.Count} 个连接）");
        lock (_clients) foreach (var c in _clients) c.Dispose();
    }

    /// <summary>命令挂起模式：收到命令不回显（模拟 PLC 卡死 → 命令超时重试）</summary>
    public volatile bool CommandHang;
    /// <summary>回显延迟（毫秒）：模拟 PLC 执行慢（迟到回显）</summary>
    public volatile int EchoDelayMs;
    /// <summary>回显丢弃计数：前 N 次命令执行但回显丢失（模拟丢回显）</summary>
    public volatile int EchoDropCount;
    /// <summary>写请求失败注入：第 N 次 FC06/FC16 返回异常码 0x04 且不生效（模拟命令序列中途写失败）；0=关</summary>
    public volatile int FailOnWriteNo;
    private long _writeCount;

    // ---------- 行为 API（测试驱动；station 参数=逻辑号，内部按 StationMap 换算物理寄存器） ----------
    public ushort GetReg(int addr) { lock (_lock) return _regs[addr]; }
    public void SetReg(int addr, ushort v) { lock (_lock) _regs[addr] = v; }   // 物理直写（幻影测试/reg 命令用）
    public void SetStationState(int station, ushort state) =>
        SetReg(RegisterMap.RegStationState + PhysOf(station) - 1, state);
    public void SetStationAlarm(int station, ushort alarm) =>
        SetReg(RegisterMap.RegStationAlarm + PhysOf(station) - 1, alarm);
    public void SetStationAvail(int station, ushort avail) =>
        SetReg(RegisterMap.RegStationAvail + PhysOf(station) - 1, avail);
    public void SetCarrierId(int station, string id)
    {
        int phys = PhysOf(station);
        var words = RegisterMap.PackAscii(id, _byteOrder);
        lock (_lock) Array.Copy(words, 0, _regs, RegisterMap.RegCarrierId + (phys - 1) * 16, 16);
    }
    public string GetCarrierId(int station)
    {
        int phys = PhysOf(station);
        lock (_lock)
            return RegisterMap.UnpackAscii(_regs[(RegisterMap.RegCarrierId + (phys - 1) * 16)..(RegisterMap.RegCarrierId + phys * 16)], _byteOrder);
    }
    public ushort GetEchoNo() => GetReg(RegisterMap.RegEchoNo);
    public ushort GetEchoStation(int station) => GetReg(RegisterMap.RegEchoStation + PhysOf(station) - 1);

    /// <summary>扫码器触发：写 323=物理站口/324~339，置 340=1（PLC 侧 323 是物理号）</summary>
    public void TriggerScan(int station, string code)
    {
        int phys = PhysOf(station);
        Note($"扫码握手 站{station}（物理{phys}）码=\"{code}\"（置 340=1）");
        var words = RegisterMap.PackAscii(code, _byteOrder);
        lock (_lock)
        {
            _regs[RegisterMap.RegScanStation] = (ushort)phys;
            Array.Copy(words, 0, _regs, RegisterMap.RegScanCode, 16);
            _regs[RegisterMap.RegHandshake] = 1;
        }
    }

    private int PhysOf(int station) => BufferC.Core.Core.StationMap.PhysicalOf(station, _stations);

    // ---------- 服务端 ----------
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                lock (_clients) _clients.Add(client);
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(200, ct); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var head = await ReadExactlyAsync(stream, 7, ct);
                if (head == null) break;
                int pduLen = (head[4] << 8) | head[5];
                var pdu = await ReadExactlyAsync(stream, pduLen - 1, ct);  // length 含 unit，PDU = length - 1
                if (pdu == null) break;
                var resp = HandlePdu(pdu);
                if (resp == null) continue;
                var frame = new byte[7 + resp.Length];
                Array.Copy(head, frame, 6);
                frame[4] = (byte)((resp.Length + 1) >> 8); frame[5] = (byte)(resp.Length + 1);
                frame[6] = head[6];   // unit 原样回显
                Array.Copy(resp, 0, frame, 7, resp.Length);
                stream.Write(frame);
            }
        }
        catch (Exception e) { Console.WriteLine($"[PlcSim{_index}] 连接异常: {e.Message}"); }
        finally { lock (_clients) _clients.Remove(client); }
    }

    private byte[]? HandlePdu(byte[] pdu)
    {
        switch (pdu[0])
        {
            case 0x03: // 读保持寄存器
            {
                int addr = (pdu[1] << 8) | pdu[2];
                int count = (pdu[3] << 8) | pdu[4];
                if (count < 1 || count > 125 || addr + count > 1024)
                    return new byte[] { 0x83, 0x02 };   // IllegalDataAddress（真实 PLC 行为）
                lock (_lock)
                {
                    var resp = new byte[2 + count * 2];
                    resp[0] = 0x03; resp[1] = (byte)(count * 2);
                    for (int i = 0; i < count; i++)
                    {
                        resp[2 + i * 2] = (byte)(_regs[addr + i] >> 8);
                        resp[3 + i * 2] = (byte)_regs[addr + i];
                    }
                    return resp;
                }
            }
            case 0x06: // 写单寄存器
            {
                if (TryFailWrite()) return new byte[] { 0x86, 0x04 };   // DeviceFailure：不生效
                int addr = (pdu[1] << 8) | pdu[2];
                ushort val = (ushort)((pdu[3] << 8) | pdu[4]);
                lock (_lock) _regs[addr] = val;
                CheckCommand();
                return pdu;   // 回显请求
            }
            case 0x10: // 写多寄存器
            {
                if (TryFailWrite()) return new byte[] { 0x90, 0x04 };   // DeviceFailure：不生效
                int addr = (pdu[1] << 8) | pdu[2];
                int count = (pdu[3] << 8) | pdu[4];
                lock (_lock)
                    for (int i = 0; i < count; i++)
                        _regs[addr + i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
                CheckCommand();
                return new byte[] { 0x10, pdu[1], pdu[2], pdu[3], pdu[4] };   // 回 addr+count
            }
            default:
                return new byte[] { (byte)(pdu[0] | 0x80), 0x01 };  // 非法功能码
        }
    }

    /// <summary>写请求失败注入：计数到 FailOnWriteNo 的那一次写返回异常码（调用方见 ModbusException），随后关闭注入</summary>
    private bool TryFailWrite()
    {
        long n = Interlocked.Increment(ref _writeCount);
        if (FailOnWriteNo == 0 || n != FailOnWriteNo) return false;
        FailOnWriteNo = 0;
        Note($"写请求失败注入（第 {n} 次写）");
        return true;
    }

    /// <summary>命令执行仿真：400 编号变化 → 应用 401~416 命令 → 回显 306/307~322</summary>
    private void CheckCommand()
    {
        if (CommandHang) { Note("命令挂起中: 不回显"); return; }   // 命令挂起：不回显
        if (_regs[RegisterMap.RegCmdNo] == _lastExecSeq) return;
        if (EchoDropCount > 0)                               // 执行但丢弃回显
        {
            EchoDropCount--;
            _lastExecSeq = _regs[RegisterMap.RegCmdNo];
            Note($"回显丢弃: 执行 seq={_lastExecSeq}，剩余丢弃次数 {EchoDropCount}");
            return;
        }
        _lastExecSeq = _regs[RegisterMap.RegCmdNo];
        ApplyCommand();
        Note($"命令执行 seq={_lastExecSeq}（{(EchoDelayMs > 0 ? $"迟到回显 {EchoDelayMs}ms" : "立即回显")}）");
        if (EchoDelayMs > 0)                                 // 迟到回显：执行快、306 延迟写（与响应分离）
        {
            ushort seq = _lastExecSeq;
            var cmdRegs = _regs[RegisterMap.RegCmdStation..(RegisterMap.RegCmdStation + 16)].ToArray();
            _ = Task.Run(async () =>
            {
                await Task.Delay(EchoDelayMs);
                lock (_lock)
                {
                    _regs[RegisterMap.RegEchoNo] = seq;
                    Array.Copy(cmdRegs, 0, _regs, RegisterMap.RegEchoStation, 16);
                }
            });
            return;
        }
        _regs[RegisterMap.RegEchoNo] = _lastExecSeq;
        Array.Copy(_regs, RegisterMap.RegCmdStation, _regs, RegisterMap.RegEchoStation, 16);
    }

    private void ApplyCommand()
    {
        for (int st = 1; st <= 16; st++)
        {
            ushort cmd = _regs[RegisterMap.RegCmdStation + st - 1];
            if (cmd == RegisterMap.CmdWrite)
                Array.Copy(_regs, RegisterMap.RegCmdCarrierId + (st - 1) * 16,
                           _regs, RegisterMap.RegCarrierId + (st - 1) * 16, 16);
            else if (cmd == RegisterMap.CmdClear)
                Array.Clear(_regs, RegisterMap.RegCarrierId + (st - 1) * 16, 16);
        }
    }

    private static async Task<byte[]?> ReadExactlyAsync(NetworkStream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = await s.ReadAsync(buf.AsMemory(got, n - got), ct);
            if (r <= 0) return null;
            got += r;
        }
        return buf;
    }
}
