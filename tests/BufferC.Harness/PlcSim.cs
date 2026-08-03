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
    private readonly ushort[] _regs = new ushort[1024];
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private ushort _lastExecSeq;
    private readonly List<TcpClient> _clients = new();

    public PlcSim(int index, byte unitId = 1, string byteOrder = "high")
    {
        _index = index;
        _unitId = unitId;
        _byteOrder = byteOrder;
        _regs[RegisterMap.RegBufferNo] = (ushort)index;
    }

    public int Port { get; private set; }

    /// <summary>启动监听（随机端口），返回端口号</summary>
    public int Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
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

    /// <summary>断线注入：掐断所有连接（BufferC 将走重连+重同步）</summary>
    public void DropConnection()
    {
        lock (_clients) foreach (var c in _clients) c.Dispose();
    }

    /// <summary>命令挂起模式：收到命令不回显（模拟 PLC 卡死 → 命令超时重试）</summary>
    public volatile bool CommandHang;
    /// <summary>回显延迟（毫秒）：模拟 PLC 执行慢（迟到回显）</summary>
    public volatile int EchoDelayMs;
    /// <summary>回显丢弃计数：前 N 次命令执行但回显丢失（模拟丢回显）</summary>
    public volatile int EchoDropCount;

    // ---------- 行为 API（测试驱动） ----------
    public ushort GetReg(int addr) { lock (_lock) return _regs[addr]; }
    public void SetReg(int addr, ushort v) { lock (_lock) _regs[addr] = v; }
    public void SetStationState(int station, ushort state) => SetReg(RegisterMap.RegStationState + station - 1, state);
    public void SetStationAlarm(int station, ushort alarm) => SetReg(RegisterMap.RegStationAlarm + station - 1, alarm);
    public void SetStationAvail(int station, ushort avail) => SetReg(RegisterMap.RegStationAvail + station - 1, avail);
    public void SetCarrierId(int station, string id)
    {
        var words = RegisterMap.PackAscii(id, _byteOrder);
        lock (_lock) Array.Copy(words, 0, _regs, RegisterMap.RegCarrierId + (station - 1) * 16, 16);
    }
    public string GetCarrierId(int station)
    {
        lock (_lock)
            return RegisterMap.UnpackAscii(_regs[(RegisterMap.RegCarrierId + (station - 1) * 16)..(RegisterMap.RegCarrierId + station * 16)], _byteOrder);
    }
    public ushort GetEchoNo() => GetReg(RegisterMap.RegEchoNo);
    public ushort GetEchoStation(int station) => GetReg(RegisterMap.RegEchoStation + station - 1);

    /// <summary>扫码器触发：写 323/324~339，置 340=1</summary>
    public void TriggerScan(int station, string code)
    {
        var words = RegisterMap.PackAscii(code, _byteOrder);
        lock (_lock)
        {
            _regs[RegisterMap.RegScanStation] = (ushort)station;
            Array.Copy(words, 0, _regs, RegisterMap.RegScanCode, 16);
            _regs[RegisterMap.RegHandshake] = 1;
        }
    }

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
                int addr = (pdu[1] << 8) | pdu[2];
                ushort val = (ushort)((pdu[3] << 8) | pdu[4]);
                lock (_lock) _regs[addr] = val;
                CheckCommand();
                return pdu;   // 回显请求
            }
            case 0x10: // 写多寄存器
            {
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

    /// <summary>命令执行仿真：400 编号变化 → 应用 401~416 命令 → 回显 306/307~322</summary>
    private void CheckCommand()
    {
        if (CommandHang) return;                             // 命令挂起：不回显
        if (_regs[RegisterMap.RegCmdNo] == _lastExecSeq) return;
        if (EchoDropCount > 0)                               // 执行但丢弃回显
        {
            EchoDropCount--;
            _lastExecSeq = _regs[RegisterMap.RegCmdNo];
            return;
        }
        _lastExecSeq = _regs[RegisterMap.RegCmdNo];
        ApplyCommand();
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
