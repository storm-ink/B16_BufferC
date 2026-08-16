using System.Net.Sockets;
using BufferC.Core;
using BufferC.Core.Config;
using BufferC.Core.Core;
using BufferC.Core.Modbus;
using BufferC.Core.Secs;
using BufferC.Harness;
using Xunit;

namespace BufferC.Tests;

/// <summary>A1-A7 缺陷修复回归测试（2026-08-17 复盘）：T3Ms 生效+T3 监控存活 / 快照深拷贝 / cmd_seq 回写 / 9001 自动清除</summary>
public class FixRegressionTests
{
    [Fact]
    public async Task T3Timeout_UsesConfigT3Ms_AndMonitorKeepsRunning()
    {
        // A1+A2：T3 死线用 config t3Ms（800ms 快速超时）；两次超时证明 T3 监控循环未静默死亡
        var plc = new PlcSim(1);
        var plcPort = plc.Start();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5118, T3Ms = 800 },
            PollIntervalMs = 100,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        try
        {
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);

            // 裸 TCP 客户端（不回 S6F12 ACK）：S1F15 → S1F16 + S6F11 CEID1(W) → T3 超时
            using var tcp = new TcpClient();
            tcp.Connect("127.0.0.1", 5118);
            var stream = tcp.GetStream();
            var s1f15 = new HsmsMessage { Stream = 1, Function = 15, WBit = false, SystemBytes = 1, Body = SecsEncode.L() };
            stream.Write(s1f15.ToBytes());
            await Task.Delay(2500);
            Assert.True(svc.GetDebugInfo().Mcs.T3Timeout >= 1, $"T3Ms=800 应已超时，实际 {svc.GetDebugInfo().Mcs.T3Timeout}");

            // 第二次：监控循环应继续工作（防静默死亡回归）
            var s1f15b = new HsmsMessage { Stream = 1, Function = 15, WBit = false, SystemBytes = 2, Body = SecsEncode.L() };
            stream.Write(s1f15b.ToBytes());
            await Task.Delay(2500);
            Assert.True(svc.GetDebugInfo().Mcs.T3Timeout >= 2, $"第二次超时未发生（监控循环死亡？），实际 {svc.GetDebugInfo().Mcs.T3Timeout}");
        }
        finally
        {
            svc.Dispose();
            plc.Dispose();
        }
    }

    [Fact]
    public void PlcSnapshot_Clone_IsIndependent()
    {
        // A4：发布深拷贝——修改拷贝不得影响原对象（轮询器每轮就地覆写原对象）
        var s = new PlcSnapshot { PlcIndex = 1 };
        s.StationState[0] = 1;
        s.CarrierId[1] = "X";
        s.ScanCode = "S";
        var c = s.Clone();
        c.StationState[0] = 5;
        c.CarrierId[1] = "Y";
        c.ScanCode = "T";
        c.EchoStation[0] = 9;
        Assert.Equal(1, s.StationState[0]);
        Assert.Equal("X", s.CarrierId[1]);
        Assert.Equal("S", s.ScanCode);
        Assert.Equal(0, s.EchoStation[0]);
        Assert.Equal(5, c.StationState[0]);
        Assert.Equal(9, c.EchoStation[0]);
    }

    [Fact]
    public void WriteCommandSeq_RecordsLastSeqAfterFailure()
    {
        // A5：失败命令也要记录最后一次 400 触发值（排障对账用）
        var inv = new Inventory(null, 1);
        inv.QueueCommand(1, 2, RegisterMap.CmdWrite, "C1", "MCS");
        inv.FailCommand(1, 2);
        inv.WriteCommandSeq(1, 2, 77);
        var row = inv.Ledger.Single(r => r.Station == 2);
        Assert.Equal(3, row.CmdState);
        Assert.Equal(77u, row.CmdSeq);
    }

    [Fact]
    public async Task Alarm9001_AutoClearsOnNextCommandSuccess()
    {
        // A6：命令失败置起 9001（S5F1 SET）→ 下条命令成功自动发 S5F1 CLEAR 并删行
        var plc = new PlcSim(1);
        var plcPort = plc.Start();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5119 },
            PollIntervalMs = 100,
            EchoTimeoutMs = 500,
            EchoRetryCount = 1,
            WebPort = 5051,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5051);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5051") };
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5119);
            await mcs.EstablishAsync();

            // 命令挂起 → 安装失败 → 9001 SET
            plc.CommandHang = true;
            var hcack = await mcs.SendS2F41Async("CarrierDataInstall", ("CARRIERID", "BOX-9001"), ("CARRIERLOC", "Buffer1_Port2"));
            Assert.Equal(4, hcack);
            var deadline = Environment.TickCount64 + 15000;
            while (Environment.TickCount64 < deadline)
            {
                var alarms = await http.GetStringAsync("/api/alarms");
                if (alarms.Contains("9001")) break;
                await Task.Delay(200);
            }
            Assert.Contains("9001", await http.GetStringAsync("/api/alarms"));
            Assert.NotNull(await mcs.WaitForEventAsync(0, 5000));   // S5F1 SET

            // 恢复 → 下条命令成功 → 9001 自动清除 + S5F1 CLEAR
            plc.CommandHang = false;
            hcack = await mcs.SendS2F41Async("CarrierDataInstall", ("CARRIERID", "BOX-9002"), ("CARRIERLOC", "Buffer1_Port2"));
            Assert.Equal(4, hcack);
            deadline = Environment.TickCount64 + 15000;
            var cleared = false;
            while (Environment.TickCount64 < deadline && !cleared)
            {
                await Task.Delay(300);
                var alarms = await http.GetStringAsync("/api/alarms");
                if (!alarms.Contains("9001")) cleared = true;
            }
            Assert.True(cleared, "9001 应随下条命令成功自动清除");
            Assert.Contains("CLEAR", await http.GetStringAsync("/api/alarm-history?tail=50"));
            Assert.NotNull(await mcs.WaitForEventAsync(0, 5000));   // S5F1 CLEAR
            mcs.Dispose();
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
            plc.Dispose();
        }
    }
}
