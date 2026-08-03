using BufferC.Core.Config;
using BufferC.Core.Modbus;

namespace BufferC.Core.Core;

/// <summary>
/// 统一命令通道（开发文档 §2.4）：写ID → 写命令 → 写编号 → 回显匹配。
/// 同 PLC 内串行化；5s 超时重试 1 次；命令编号 1~65535 自增。
/// </summary>
public sealed class CommandChannel
{
    private readonly ModbusTcpClient _client;
    private readonly PlcConfig _cfg;
    private readonly BufferCConfig _app;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private uint _seq;

    public CommandChannel(ModbusTcpClient client, PlcConfig cfg, BufferCConfig app)
    {
        _client = client;
        _cfg = cfg;
        _app = app;
        _seq = cfg.LastSeq;
    }

    public uint LastSeq => _seq;

    /// <summary>命令统计（界面/联调用）</summary>
    public long CommandCount;
    public long CommandFailCount;

    /// <summary>执行一条站口命令；成功返回 true</summary>
    public bool Execute(int station, ushort cmd, string? carrierId)
    {
        _gate.Wait();
        try
        {
            for (int attempt = 0; attempt <= _app.EchoRetryCount; attempt++)
            {
                if (attempt > 0) Console.WriteLine($"[Cmd] 站口{station} 重试 {attempt}…");
                _seq = _seq % 65535 + 1;                      // 1~65535 回绕
                if (cmd == RegisterMap.CmdWrite)
                    _client.WriteMultipleRegisters((ushort)(RegisterMap.RegCmdCarrierId + (station - 1) * 16),
                        RegisterMap.PackAscii(carrierId ?? "", _cfg.ByteOrder));
                _client.WriteSingleRegister((ushort)(RegisterMap.RegCmdStation + station - 1), cmd);
                _client.WriteSingleRegister(RegisterMap.RegCmdNo, (ushort)_seq);
                if (WaitEcho((ushort)_seq, station, cmd))
                {
                    CommandCount++;
                    return true;
                }
            }
            CommandCount++;
            CommandFailCount++;
            return false;
        }
        finally { _gate.Release(); }
    }

    private bool WaitEcho(ushort seq, int station, ushort cmd)
    {
        var deadline = Environment.TickCount64 + _app.EchoTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var echo = _client.ReadHoldingRegisters(RegisterMap.RegEchoNo, 17);
                if (echo[0] == seq && echo[station] == cmd) return true;
            }
            catch (ModbusException)
            {
                // 轮询异常继续等待
            }
            Thread.Sleep(100);   // 100ms 轮询粒度，避免错过窗口内的迟到回显
        }
        return false;
    }
}
