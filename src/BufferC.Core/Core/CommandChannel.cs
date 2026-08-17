using System.Diagnostics;
using System.IO;
using BufferC.Core.Config;
using BufferC.Core.Modbus;

namespace BufferC.Core.Core;

/// <summary>
/// 统一命令通道（开发文档 §2.4）：整段清零 401~672 → 写 ID/命令 → 写 400 编号触发 → 回显匹配。
/// 成功与否以回显匹配为唯一标准；同 PLC 内串行化；5s 超时重试；命令编号 1~65535 自增。
/// 重试策略：区域未就位（清零/写内容中途失败）→ 完整重写；区域已就位（400 响应丢失/回显丢失）→ 只重写 400 新编号再触发。
/// </summary>
public sealed class CommandChannel
{
    private readonly ModbusTcpClient _client;
    private readonly PlcConfig _cfg;
    private readonly BufferCConfig _app;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string, string, LogLevel>? _log;
    private uint _seq;

    public CommandChannel(ModbusTcpClient client, PlcConfig cfg, BufferCConfig app,
        Action<string, string, LogLevel>? log = null)
    {
        _client = client;
        _cfg = cfg;
        _app = app;
        _log = log;
        _seq = cfg.LastSeq;
    }

    public uint LastSeq => _seq;

    /// <summary>命令统计（界面/联调用）</summary>
    public long CommandCount;
    public long CommandFailCount;

    /// <summary>最近 100 次命令总耗时（含重试与排队，与 CmdEntry.ElapsedMs 语义一致）</summary>
    public readonly RingBuffer<long> LatencyRing = new(100);

    public sealed record LatencyStats(long Count, long MinMs, long MaxMs, long AvgMs);

    public LatencyStats GetLatencyStats()
    {
        var all = LatencyRing.GetTail(100);
        if (all.Count == 0) return new LatencyStats(0, 0, 0, 0);
        return new LatencyStats(all.Count, all.Min(), all.Max(), (long)all.Average());
    }

    /// <summary>执行一条站口命令；成功返回 true。
    /// 日志口径（保场景脚本断言力）：首试与成功为 Debug（默认 info 不可见）→ [Cmd] 行只在「重试(Warn)/最终失败(Error)」时出现。</summary>
    public bool Execute(int station, ushort cmd, string? carrierId)
    {
        var sw = Stopwatch.StartNew();
        _gate.Wait();
        try
        {
            bool areaReady = false;   // 401~672 已写入本次内容：此后失败只重写 400 新编号触发
            for (int attempt = 0; attempt <= _app.EchoRetryCount; attempt++)
            {
                _seq = _seq % 65535 + 1;                      // 1~65535 回绕（每次尝试新编号：PLC 靠 400 新值触发）
                if (attempt > 0)
                    _log?.Invoke("Cmd", $"PLC{_cfg.Index} 站口{station} 重试 {attempt}…（上次回显超时/传输异常，seq={_seq}）", LogLevel.Warn);
                else
                    _log?.Invoke("Cmd", $"PLC{_cfg.Index} 站口{station} 命令{cmd} seq={_seq} 开始执行（尝试 1/{_app.EchoRetryCount + 1}，等待回显 ≤{_app.EchoTimeoutMs}ms）", LogLevel.Debug);
                try
                {
                    if (!areaReady)
                    {
                        // PLC 协议（现场确认）：400 出现新值才扫描 401 以上执行。
                        // ① 整段清零 401~672（清掉旧命令/旧 ID，防非幂等重复执行）
                        _client.WriteMultipleRegisters((ushort)RegisterMap.RegCmdStation,
                            new ushort[RegisterMap.RegCmdAreaWords]);
                        // ② 写【各站操作命令】→ ③ 写【货物ID写入】（现场确认写序：命令码在前、ID 在后）
                        _client.WriteSingleRegister((ushort)(RegisterMap.RegCmdStation + station - 1), cmd);
                        if (cmd == RegisterMap.CmdWrite)
                            _client.WriteMultipleRegisters((ushort)(RegisterMap.RegCmdCarrierId + (station - 1) * 16),
                                RegisterMap.PackAscii(carrierId ?? "", _cfg.ByteOrder));
                        areaReady = true;                     // 区域就位——此后失败只重写 400 新编号
                    }
                    // ③ 写 400（新编号）触发执行；成功与否以回显匹配为唯一标准
                    _client.WriteSingleRegister(RegisterMap.RegCmdNo, (ushort)_seq);
                    if (WaitEcho((ushort)_seq, station, cmd))
                    {
                        CommandCount++;
                        _log?.Invoke("Cmd", $"PLC{_cfg.Index} 站口{station} 命令{cmd} 成功 seq={_seq}（{sw.ElapsedMilliseconds}ms）", LogLevel.Debug);
                        return true;
                    }
                    // 回显超时（400 已发出、PLC 可能已执行）：区域仍有效，重试只重写 400 新编号再触发（命令幂等，重复执行无害）
                }
                catch (Exception e) when (e is ModbusException or IOException)
                {
                    // 传输异常：区域未就位 → 下次完整重写；已就位（失败在 400/回显阶段）→ 下次只重写 400
                    _log?.Invoke("Cmd", $"PLC{_cfg.Index} 站口{station} 尝试 {attempt + 1} 传输异常: {e.Message}", LogLevel.Debug);
                }
            }
            CommandCount++;
            CommandFailCount++;
            _log?.Invoke("Cmd", $"PLC{_cfg.Index} 站口{station} 命令{cmd} 失败（{_app.EchoRetryCount + 1} 次尝试，耗时 {sw.ElapsedMilliseconds}ms）", LogLevel.Error);
            return false;
        }
        finally
        {
            _gate.Release();
            // 亚毫秒完成（本机回环仿真）ElapsedMilliseconds 截断为 0，与"无命令"语义混淆 → 下限 1ms
            LatencyRing.Add(Math.Max(1, sw.ElapsedMilliseconds));
        }
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
            Thread.Sleep(_app.EchoPollIntervalMs);   // 轮询粒度（D6 配置化，默认 100ms），避免错过窗口内的迟到回显
        }
        return false;
    }
}
