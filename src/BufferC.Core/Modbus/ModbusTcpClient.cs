using System.Net.Sockets;

namespace BufferC.Core.Modbus;

/// <summary>
/// 极简 Modbus TCP 客户端（FC03 读保持寄存器 / FC06 写单寄存器 / FC16 写多寄存器）。
/// 自研实现，保证联调工具可控；正式开发阶段可切换 NModbus。
/// </summary>
public sealed class ModbusTcpClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte _unit;
    private readonly int _timeoutMs;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _txId = 0;

    public bool Connected => _tcp?.Connected == true;

    /// <summary>帧级日志钩子（isTx=true 发送 / false 接收；参数为完整 MBAP+PDU 帧）；联调 trace 级日志用</summary>
    public event Action<bool, byte[]>? Frame;

    public ModbusTcpClient(string host, int port, byte unit, int timeoutMs = 3000)
    {
        _host = host;
        _port = port;
        _unit = unit;
        _timeoutMs = timeoutMs;
    }

    public void Connect()
    {
        _tcp?.Dispose();
        _tcp = new TcpClient();
        try
        {
            var task = _tcp.ConnectAsync(_host, _port);
            if (!task.Wait(_timeoutMs)) throw new TimeoutException($"连接超时 {_host}:{_port}");
            _stream = _tcp.GetStream();
            _stream.ReadTimeout = _timeoutMs;
            _stream.WriteTimeout = _timeoutMs;
        }
        catch
        {
            _tcp.Dispose();
            _tcp = null;
            throw;
        }
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public ushort[] ReadHoldingRegisters(ushort address, ushort count)
    {
        var resp = Transact(new byte[] { 0x03,
            (byte)(address >> 8), (byte)address,
            (byte)(count >> 8), (byte)count });
        if (resp[0] == 0x83)
            throw new ModbusException($"FC03 异常码 {resp[1]} @ {address}");
        int byteCount = resp[1];
        var regs = new ushort[byteCount / 2];
        for (int i = 0; i < regs.Length; i++)
            regs[i] = (ushort)((resp[2 + i * 2] << 8) | resp[3 + i * 2]);
        return regs;
    }

    public void WriteSingleRegister(ushort address, ushort value)
    {
        var resp = Transact(new byte[] { 0x06,
            (byte)(address >> 8), (byte)address,
            (byte)(value >> 8), (byte)value });
        if (resp[0] == 0x86)
            throw new ModbusException($"FC06 异常码 {resp[1]} @ {address}");
    }

    public void WriteMultipleRegisters(ushort address, ushort[] values)
    {
        // FC16 单帧上限 123 字（Modbus 规范，真 PLC 超限拒收）：自动分段逐帧发送
        const int maxWords = 123;
        for (int off = 0; off < values.Length; off += maxWords)
        {
            int count = Math.Min(maxWords, values.Length - off);
            var pdu = new byte[6 + count * 2];
            pdu[0] = 0x10;
            pdu[1] = (byte)((address + off) >> 8); pdu[2] = (byte)(address + off);
            pdu[3] = (byte)(count >> 8); pdu[4] = (byte)count;
            pdu[5] = (byte)(count * 2);
            for (int i = 0; i < count; i++)
            {
                pdu[6 + i * 2] = (byte)(values[off + i] >> 8);
                pdu[7 + i * 2] = (byte)values[off + i];
            }
            var resp = Transact(pdu);
            if (resp[0] == 0x90)
                throw new ModbusException($"FC16 异常码 {resp[1]} @ {address + off}");
        }
    }

    private readonly object _ioLock = new();

    private byte[] Transact(byte[] pdu)
    {
        // 线程安全：轮询器与命令通道（HSMS 线程）并发访问同一 socket，必须串行化
        lock (_ioLock)
        {
            return TransactCore(pdu);
        }
    }

    private byte[] TransactCore(byte[] pdu)
    {
        if (_stream == null) throw new ModbusException("未连接");
        _txId++;
        var mbap = new byte[7];
        mbap[0] = (byte)(_txId >> 8); mbap[1] = (byte)_txId;
        mbap[2] = 0; mbap[3] = 0;                              // 协议标识 0
        mbap[4] = (byte)((pdu.Length + 1) >> 8); mbap[5] = (byte)(pdu.Length + 1);
        mbap[6] = _unit;

        var frame = new byte[mbap.Length + pdu.Length];
        Array.Copy(mbap, frame, 7);
        Array.Copy(pdu, 0, frame, 7, pdu.Length);
        _stream.Write(frame, 0, frame.Length);
        Frame?.Invoke(true, frame);

        // 响应: 7 字节 MBAP（含 unit）+ PDU。length 字段 = unit(1) + PDU，unit 已在 MBAP 内
        var head = ReadExactly(7);
        int respLen = (head[4] << 8) | head[5];
        if (respLen < 2) throw new ModbusException("响应过短");
        var pduResp = ReadExactly(respLen - 1);                // PDU = length - 1
        var respFrame = new byte[7 + pduResp.Length];
        Array.Copy(head, respFrame, 7);
        Array.Copy(pduResp, 0, respFrame, 7, pduResp.Length);
        Frame?.Invoke(false, respFrame);
        return pduResp;
    }

    private byte[] ReadExactly(int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = _stream!.Read(buf, got, n - got);
            if (r <= 0) throw new IOException("连接中断");
            got += r;
        }
        return buf;
    }

    public void Dispose() => Disconnect();
}

public sealed class ModbusException : Exception
{
    public ModbusException(string msg) : base(msg) { }
}
