using BufferC.Core.Config;
using BufferC.Core.Core;
using BufferC.Core.Modbus;
using Xunit;

namespace BufferC.Tests;

/// <summary>旁路收编单测（2026-08-17 日志梳理）：CommandChannel/Inventory 日志注入入统一管道</summary>
public class BypassLoggingTests
{
    [Fact]
    public void CommandChannel_LogInjection_RetryWarn_FailureError_CmdCategory()
    {
        // 未连接的 client → 每步写都抛 ModbusException("未连接")：首试 Debug、重试 Warn、最终失败 Error
        var captured = new List<(string Category, string Msg, LogLevel Level)>();
        var client = new ModbusTcpClient("127.0.0.1", 1, 1, 200);
        var app = new BufferCConfig { EchoRetryCount = 1, EchoTimeoutMs = 200 };
        var cfg = new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = 1, ByteOrder = "high" };
        var ch = new CommandChannel(client, cfg, app, (c, m, lv) => captured.Add((c, m, lv)));

        var ok = ch.Execute(4, RegisterMap.CmdWrite, "BOX001");
        Assert.False(ok);

        // category 恰为 "Cmd"（场景脚本 grep "[Cmd]" 锚点；首试 Debug → 默认 info 下 [Cmd] 只出现在重试/失败时）
        Assert.All(captured, x => Assert.Equal("Cmd", x.Category));
        Assert.Contains(captured, x => x.Msg.Contains("开始执行") && x.Msg.Contains("PLC1") && x.Level == LogLevel.Debug);
        Assert.Contains(captured, x => x.Msg.Contains("重试 1") && x.Msg.Contains("PLC1") && x.Level == LogLevel.Warn);
        Assert.Contains(captured, x => x.Msg.Contains("传输异常") && x.Level == LogLevel.Debug);
        Assert.Contains(captured, x => x.Msg.Contains("失败") && x.Level == LogLevel.Error);
    }

    [Fact]
    public void Inventory_LogInjection_LoadFailureWarn_WriteThrottle()
    {
        var captured = new List<(string Category, string Msg, LogLevel Level)>();
        // dbPath 指向一个目录 → SQLite 无法当文件打开：加载失败（WARN）+ 每次写失败（首次 ERROR、后续节流 Debug）
        var dir = Path.Combine(Path.GetTempPath(), $"invlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var inv = new Inventory(dir, 1, (c, m, lv) => captured.Add((c, m, lv)));
            Assert.Contains(captured, x => x.Category == "Inventory" && x.Msg.Contains("SQLite 加载失败") && x.Level == LogLevel.Warn);
            // 建满 16 行时 INSERT 连续失败：第 1 次 ERROR、第 10 次 Debug（节流）
            Assert.Contains(captured, x => x.Msg.Contains("SQLite 写入失败") && x.Msg.Contains("连续第 1 次") && x.Level == LogLevel.Error);
            Assert.Contains(captured, x => x.Msg.Contains("SQLite 写入失败") && x.Msg.Contains("连续第 10 次") && x.Level == LogLevel.Debug);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
