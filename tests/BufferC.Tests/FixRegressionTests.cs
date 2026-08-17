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
            var deadline = Environment.TickCount64 + 8000;
            while (svc.GetDebugInfo().Mcs.T3Timeout < 1 && Environment.TickCount64 < deadline) await Task.Delay(200);
            Assert.True(svc.GetDebugInfo().Mcs.T3Timeout >= 1, $"T3Ms=800 应已超时，实际 {svc.GetDebugInfo().Mcs.T3Timeout}");

            // 第二次：监控循环应继续工作（防静默死亡回归）
            var s1f15b = new HsmsMessage { Stream = 1, Function = 15, WBit = false, SystemBytes = 2, Body = SecsEncode.L() };
            stream.Write(s1f15b.ToBytes());
            deadline = Environment.TickCount64 + 8000;
            while (svc.GetDebugInfo().Mcs.T3Timeout < 2 && Environment.TickCount64 < deadline) await Task.Delay(200);
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
    public async Task SecondMcsConnection_Rejected_FirstStaysConnected()
    {
        // 现场主备（2026-08-17）：先连者胜——第二台连接被拒绝（服务器直接关闭 socket），第一台不受影响
        var plc = new PlcSim(1);
        var plcPort = plc.Start();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5120 },
            PollIntervalMs = 100,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        try
        {
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);

            var mcs1 = new McsSim();
            mcs1.Connect("127.0.0.1", 5120);
            await mcs1.EstablishAsync();

            // 第二台：TCP 能连上，但服务器立即关闭 → S1F14 永远等不到（建连超时抛异常）
            var mcs2 = new McsSim();
            mcs2.Connect("127.0.0.1", 5120);
            await Assert.ThrowsAnyAsync<Exception>(() => mcs2.EstablishAsync());

            // 第一台完全不受影响：查询照常
            Assert.NotNull(await mcs1.QuerySvidAsync(14));
            mcs1.Dispose();
            mcs2.Dispose();
        }
        finally
        {
            svc.Dispose();
            plc.Dispose();
        }
    }

    [Fact]
    public async Task FieldNaming_DeviceNamePStation_AndStationCount()
    {
        // 现场布局（2026-08-17）：MAGV03B01_P01 命名 + 8 站裁剪——9~16 寄存器变化不产生幻影事件；SVID29 只 8 个单位
        var plc = new PlcSim(1);
        var plcPort = plc.Start();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high", Name = "MAGV03B01", Stations = 8 } },
            Hsms = new HsmsConfig { ListenPort = 5121 },
            PollIntervalMs = 100,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        try
        {
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5121);
            await mcs.EstablishAsync();

            // 9 号站（超出 8 站裁剪）变化 → 无幻影事件
            plc.SetStationState(9, 1);
            Assert.Equal(0, await mcs.WaitForEventCountAsync(204, 1, 1000));

            // 1 号站放入 → 204 位置 = MAGV03B01_P01；台账 UnitId/DeviceNo 同步
            plc.SetCarrierId(1, "FIELD01");
            plc.SetStationState(1, 1);
            var ev = await mcs.WaitForEventAsync(204, 5000);
            Assert.NotNull(ev);
            var rpt = ev!.Items()[0].Children![2].Children![0].Children![1];
            Assert.Equal("MAGV03B01_P01", rpt.Children![1].AsString());
            Assert.Contains(svc.Ledger, r => r.UnitId == "MAGV03B01_P01");
            Assert.All(svc.Ledger, r => Assert.True(r.Station <= 8));   // 台账只 8 行
            Assert.Equal(8, svc.Ledger.Count);

            // SVID29 单位列表 = 8 个，命名新格式
            var units = (await mcs.QuerySvidAsync(29))[0].Children!;
            Assert.Equal(8, units.Count);
            Assert.Equal("MAGV03B01_P01", units[0].Children![0].AsString());

            // 前端视图：PlcView 带 name 与 stationCount
            var view = svc.GetStatusView().Plcs[0];
            Assert.Equal("MAGV03B01", view.Name);
            Assert.Equal(8, view.StationCount);
            Assert.Equal(8, view.Stations.Count);
            mcs.Dispose();
        }
        finally
        {
            svc.Dispose();
            plc.Dispose();
        }
    }

    [Fact]
    public void FieldNaming_TryParseLoc_NewAndLegacyFormats()
    {
        // 位置解析：MAGV03B03_P16（16 站机台）与旧 Buffer1_Port5 均可用；8 站机台的 P09 越界
        var cfg = new BufferCConfig
        {
            Plcs =
            {
                new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = 1, Name = "MAGV03B01", Stations = 8 },
                new PlcConfig { Index = 2, Ip = "127.0.0.1", Port = 2, Name = "MAGV03B02", Stations = 8 },
                new PlcConfig { Index = 3, Ip = "127.0.0.1", Port = 3, Name = "MAGV03B03", Stations = 16 },
            },
            Hsms = new HsmsConfig { ListenPort = 5122 },
        };
        var svc = new BufferCService(cfg);
        svc.Start();   // 建 poller（ManualInstall 需 FindPoller；仿真端口无服务，连接失败走退避，无碍）
        try
        {
            Assert.True(svc.ManualInstall("X1", "MAGV03B03_P16"));   // B03 16 站 → plc=3 st=16
            Assert.Contains(svc.Ledger, r => r.PlcIndex == 3 && r.Station == 16 && r.CmdState == 1);
            Assert.True(svc.ManualInstall("X2", "MAGV03B01_P08"));   // B01 8 站边界
            Assert.Contains(svc.Ledger, r => r.PlcIndex == 1 && r.Station == 8 && r.CmdState == 1);
            Assert.False(svc.ManualInstall("X3", "MAGV03B01_P09"));  // 8 站机台 P09 越界
            Assert.False(svc.ManualInstall("X4", "MAGV03B99_P01"));  // 未知机台名
            Assert.True(svc.ManualInstall("X5", "Buffer1_Port5"));   // 旧格式兼容
            Assert.Contains(svc.Ledger, r => r.PlcIndex == 1 && r.Station == 5 && r.CmdState == 1);
        }
        finally
        {
            svc.Dispose();
        }
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
