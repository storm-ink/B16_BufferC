using System.Net.Http;
using System.Net.Http.Json;
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
        // 现场布局（2026-08-17）：MAGV03B01_P01 命名 + 8 站裁剪——物理 9~16 槽寄存器变化不产生幻影事件（2026-08-20 映射：无逻辑对应）；SVID29 只 8 个单位
        var plc = new PlcSim(1, stations: 8);   // 8 站机台：行为 API 参数=逻辑号，内部按 StationMap 换算物理槽
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

            // 物理槽 9 寄存器（状态 10 号地址，8 站机台无逻辑对应）变化 → 无幻影事件
            plc.SetReg(RegisterMap.RegStationState + 8, 1);
            Assert.Equal(0, await mcs.WaitForEventCountAsync(204, 1, 1000));

            // 1 号站放入 → 等 ID → 补填 → 204 位置 = MAGV03B01_P01；台账 UnitId/DeviceNo 同步
            plc.SetCarrierId(1, "FIELD01");
            plc.SetStationState(1, 1);
            Assert.Null(await mcs.WaitForEventAsync(204, 500));
            var (ok, _) = svc.FillCarrierPending(1, 1, "FIELD01");
            Assert.True(ok);
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
        // 位置解析：MAGV03Bxx_Pyy（站口=逻辑号，2026-08-20 映射口径）；旧 BUFFER01_P5 格式已废弃；
        // 8 站机台的 P09 越界；未配置名称的机台回退 BUFFER{index:00} 名称解析
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
            Assert.True(svc.ManualInstall("X1", "MAGV03B03_P16"));   // B03 16 站 → plc=3 逻辑站 16
            Assert.Contains(svc.Ledger, r => r.PlcIndex == 3 && r.Station == 16 && r.CmdState == 1);
            Assert.True(svc.ManualInstall("X2", "MAGV03B01_P08"));   // B01 8 站边界（逻辑 8=物理 8）
            Assert.Contains(svc.Ledger, r => r.PlcIndex == 1 && r.Station == 8 && r.CmdState == 1);
            Assert.False(svc.ManualInstall("X3", "MAGV03B01_P09"));  // 8 站机台 P09 越界（逻辑站只有 1~8）
            Assert.False(svc.ManualInstall("X4", "MAGV03B99_P01"));  // 未知机台名
            Assert.False(svc.ManualInstall("X5", "BUFFER01_P5"));  // 旧格式已废弃（2026-08-20）
            Assert.False(svc.ManualInstall("X6", "MAGV03B01_Pxx"));  // 非数字站口
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
            var hcack = await mcs.SendS2F41Async("CarrierDataInstall", ("CARRIERID", "BOX-9001"), ("CARRIERLOC", "BUFFER01_P2"));
            Assert.Equal(4, hcack);
            var deadline = Environment.TickCount64 + 15000;
            while (Environment.TickCount64 < deadline)
            {
                var alarms = await http.GetStringAsync("/api/alarms");
                if (alarms.Contains("9001")) break;
                await Task.Delay(200);
            }
            Assert.Contains("9001", await http.GetStringAsync("/api/alarms"));
            // Q7（现场口径）：SVID3 数据源（service.Alarms）不含内部 9001；Web 面板（AllAlarms）含
            Assert.DoesNotContain(svc.Alarms, a => a.AlarmId == BufferCService.AlarmCmdFailed);
            Assert.Contains(svc.AllAlarms, a => a.AlarmId == BufferCService.AlarmCmdFailed);
            Assert.NotNull(await mcs.WaitForEventAsync(0, 5000));   // S5F1 SET

            // 恢复 → 下条命令成功 → 9001 自动清除 + S5F1 CLEAR
            plc.CommandHang = false;
            hcack = await mcs.SendS2F41Async("CarrierDataInstall", ("CARRIERID", "BOX-9002"), ("CARRIERLOC", "BUFFER01_P2"));
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

    // ---------- 载具确认机制（2026-08-19）：数据完整才上报 MCS ----------

    /// <summary>等 9 台 PLC 全部轮询就绪 + 目标行可见（poll 条件替代固定 delay）</summary>
    private static async Task WaitReadyAsync(BufferCService svc, Func<BufferCService, bool> cond, string what, int ms = 15000)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline && !cond(svc)) await Task.Delay(100);
        Assert.True(cond(svc), $"{what} 未在 {ms}ms 内就绪");
    }

    [Fact]
    public async Task Carrier204_WaitsForId_AgvcQuery_ReportsWithId()
    {
        // ①204 等 ID：0→2→1 先登记中间态（不立即上报）→ AGVC queryMachines 回 ID → 写 PLC + 台账 + 带 ID 上报 204 → 中间态清理（pending_id 留痕）
        var plc = new PlcSim(1);
        var plcPort = plc.Start();
        using var agvc = new FakeAgvcServer { QueryCarrierId = "AGV2001" };
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5130 },
            PollIntervalMs = 100,
            Agvc = new AgvcConfig { BaseUrl = $"http://127.0.0.1:{agvc.Port}", ArrivalGraceMs = 1000 },
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        try
        {
            await WaitReadyAsync(svc, s => s.AllPlcsReady, "PLC 基线就绪");
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5130);
            await mcs.EstablishAsync();

            plc.SetStationState(1, 2);
            await Task.Delay(300);
            plc.SetStationState(1, 1);
            Assert.Null(await mcs.WaitForEventAsync(204, 500));                    // 等 ID：不立即上报
            Assert.Contains(svc.GetInventoryView(), r => r.Station == 1 && r.PendingCeid == 204);

            var ev = await mcs.WaitForEventAsync(204);                             // AGVC 查询 → 写 PLC → 上报
            Assert.NotNull(ev);
            var rpt = ev!.Items()[0].Children![2].Children![0].Children![1];
            Assert.Equal("AGV2001", rpt.Children![0].AsString());
            Assert.Equal("BUFFER01_01", rpt.Children![1].AsString());
            Assert.Equal("AGV2001", plc.GetCarrierId(1));                          // 已写 PLC
            Assert.Contains(svc.Carriers, c => c.CarrierId == "AGV2001");
            var row = svc.GetInventoryView().Single(r => r.Station == 1);
            Assert.Equal(0, row.PendingCeid);                                      // 中间态已清理
            Assert.Equal("AGV2001", svc.Ledger.Single(r => r.Station == 1).PendingId);   // 留痕
            mcs.Dispose();
        }
        finally
        {
            svc.Dispose();
            plc.Dispose();
        }
    }

    [Fact]
    public async Task PendingFillApi_WritesPlc_ReportsEvent()
    {
        // ②补填 API：待补列表可见 → POST fill → 命令1 写 PLC + 台账 + 带 ID 上报 204 → 列表清空
        var plc = new PlcSim(1);
        var plcPort = plc.Start();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5131 },
            PollIntervalMs = 100,
            WebPort = 5132,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5132);
        try
        {
            await WaitReadyAsync(svc, s => s.AllPlcsReady, "PLC 基线就绪");
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5131);
            await mcs.EstablishAsync();
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5132") };

            plc.SetStationState(1, 2);
            await Task.Delay(300);
            plc.SetStationState(1, 1);
            Assert.Null(await mcs.WaitForEventAsync(204, 500));
            await WaitReadyAsync(svc, s => s.GetInventoryView().Any(r => r.PendingCeid == 204), "中间态登记");
            var list = await http.GetStringAsync("/api/pending-carrier-events");
            Assert.Contains("BUFFER01_01", list);

            var fill = await http.PostAsJsonAsync("/api/pending-carrier-events/fill", new { plc = 1, station = 1, carrierId = "MAN2001" });
            Assert.True(fill.IsSuccessStatusCode);
            var ev = await mcs.WaitForEventAsync(204);
            Assert.NotNull(ev);
            Assert.Equal("MAN2001", ev!.Items()[0].Children![2].Children![0].Children![1].Children![0].AsString());
            Assert.Equal("MAN2001", plc.GetCarrierId(1));                          // 已写 PLC
            await WaitReadyAsync(svc, s => !s.GetInventoryView().Any(r => r.PendingCeid != 0), "补填后中间态清理");

            // 补填成功后重复提交 → 400（无等待事件）
            var again = await http.PostAsJsonAsync("/api/pending-carrier-events/fill", new { plc = 1, station = 1, carrierId = "MAN2002" });
            Assert.False(again.IsSuccessStatusCode);
            mcs.Dispose();
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
            plc.Dispose();
        }
    }

    [Fact]
    public async Task Pending_AbortedByStateChange_Revoked()
    {
        // ③中断撤销：等待期间状态离开有货态（1→0）→ 中间态撤销、异常取走不上报 203/202
        var plc = new PlcSim(1);
        var plcPort = plc.Start();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5133 },
            PollIntervalMs = 100,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        try
        {
            await WaitReadyAsync(svc, s => s.AllPlcsReady, "PLC 基线就绪");
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5133);
            await mcs.EstablishAsync();

            plc.SetStationState(1, 2);
            await Task.Delay(300);
            plc.SetStationState(1, 1);
            Assert.Null(await mcs.WaitForEventAsync(204, 500));
            Assert.Contains(svc.GetInventoryView(), r => r.Station == 1 && r.PendingCeid == 204);

            plc.SetStationState(1, 0);                                            // 等 ID 期间被取走
            Assert.Equal(0, await mcs.WaitForEventCountAsync(203, 1, 800));       // 未确认载具 → 不上报
            Assert.Equal(0, await mcs.WaitForEventCountAsync(202, 1, 400));
            await WaitReadyAsync(svc, s => !s.GetInventoryView().Any(r => r.PendingCeid != 0), "中间态撤销");
            var row = svc.Ledger.Single(r => r.Station == 1);
            Assert.Equal(RegisterMap.StEmpty, row.State);
            Assert.Equal("", row.CarrierId);
            mcs.Dispose();
        }
        finally
        {
            svc.Dispose();
            plc.Dispose();
        }
    }

    [Fact]
    public async Task Remove_UsesLedgerId_ClearsLedgerAndPlc()
    {
        // ④取走：203 参数取主表 ID → 上报后主表清空 → 命令2 清 PLC ID 区 → SVID15 不再含
        var plc = new PlcSim(1);
        var plcPort = plc.Start();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5134 },
            PollIntervalMs = 100,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        try
        {
            await WaitReadyAsync(svc, s => s.AllPlcsReady, "PLC 基线就绪");
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5134);
            await mcs.EstablishAsync();

            plc.SetStationState(1, 2);
            await Task.Delay(300);
            plc.SetStationState(1, 1);
            Assert.Null(await mcs.WaitForEventAsync(204, 500));
            var (ok, _) = svc.FillCarrierPending(1, 1, "CARRIER2041");
            Assert.True(ok);
            Assert.NotNull(await mcs.WaitForEventAsync(204));
            Assert.Equal("CARRIER2041", plc.GetCarrierId(1));

            plc.SetStationState(1, 0);                                            // AGV 取走（1→0）
            var ev = await mcs.WaitForEventAsync(203);
            Assert.NotNull(ev);
            var rpt = ev!.Items()[0].Children![2].Children![0].Children![1];
            Assert.Equal("CARRIER2041", rpt.Children![0].AsString());             // 参数取主表 ID
            Assert.Equal("BUFFER01_01", rpt.Children![1].AsString());

            await WaitReadyAsync(svc, s =>
            {
                var r = s.Ledger.Single(x => x.Station == 1);
                return r.State == RegisterMap.StEmpty && r.CarrierId == "";       // 主表清空（取走清 ID 新口径）
            }, "主表清空");
            var deadline = Environment.TickCount64 + 10000;
            while (plc.GetCarrierId(1) != "" && Environment.TickCount64 < deadline) await Task.Delay(50);   // 命令2 清 PLC ID 区
            Assert.Equal("", plc.GetCarrierId(1));
            var carriers = (await mcs.QuerySvidAsync(15))[0].Children!;
            Assert.Empty(carriers);
            mcs.Dispose();
        }
        finally
        {
            svc.Dispose();
            plc.Dispose();
        }
    }

    [Fact]
    public async Task Remove_UnconfirmedCarrier_NoEvent_LogsOnly()
    {
        // ⑤异常取走（入货未完整就被取走）：不上报 203/202，只日志 + 审计；中间态清理、主表无货
        var plc = new PlcSim(1);
        var plcPort = plc.Start();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5135 },
            PollIntervalMs = 100,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        try
        {
            await WaitReadyAsync(svc, s => s.AllPlcsReady, "PLC 基线就绪");
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5135);
            await mcs.EstablishAsync();

            plc.SetStationState(1, 2);
            await Task.Delay(300);
            plc.SetStationState(1, 1);                                            // 等 ID（永不到达）
            Assert.Null(await mcs.WaitForEventAsync(204, 500));
            plc.SetStationState(1, 0);                                            // 未确认就被取走
            Assert.Equal(0, await mcs.WaitForEventCountAsync(203, 1, 800));       // 不上报
            Assert.Equal(0, await mcs.WaitForEventCountAsync(202, 1, 400));
            await WaitReadyAsync(svc, s => !s.GetInventoryView().Any(r => r.PendingCeid != 0), "中间态清理");
            Assert.Empty(svc.Carriers);
            mcs.Dispose();
        }
        finally
        {
            svc.Dispose();
            plc.Dispose();
        }
    }

    [Fact]
    public async Task Restart_PendingRecovered_ContinuesWaiting_OrRevoked()
    {
        // ⑥重启恢复：中间态持久化 → 重启后继续等（补填 → 204）；另一站状态已离开 → 首轮轮询撤销（无事件）
        var db = Path.Combine(Path.GetTempPath(), $"bufferc-pending-{Guid.NewGuid():N}.db");
        var plc = new PlcSim(1);
        var plcPort = plc.Start();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5136 },
            PollIntervalMs = 100,
            DbPath = db,
            LogFile = "test-bufferc.log",
        };
        var svc1 = new BufferCService(cfg);
        svc1.Start();
        try
        {
            await WaitReadyAsync(svc1, s => s.AllPlcsReady, "PLC 基线就绪");
            plc.SetStationState(1, 2);
            await Task.Delay(300);
            plc.SetStationState(1, 1);                                            // 站 1：等 ID（重启后继续等）
            plc.SetStationState(2, 2);
            await Task.Delay(300);
            plc.SetStationState(2, 1);                                            // 站 2：等 ID（重启前状态离开 → 重启后撤销）
            await WaitReadyAsync(svc1, s => s.GetInventoryView().Count(r => r.PendingCeid == 204) == 2, "两站中间态登记");
            plc.SetStationState(2, 0);                                            // 站 2 重启前已取走
            await WaitReadyAsync(svc1, s => s.GetInventoryView().Count(r => r.PendingCeid == 204) == 1, "站 2 撤销");
            svc1.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(300);                                                 // 等 HSMS 端口释放

            var svc2 = new BufferCService(cfg);                                    // 重启
            svc2.Start();
            try
            {
                await WaitReadyAsync(svc2, s => s.AllPlcsReady, "重启后基线就绪");
                var mcs = new McsSim();
                mcs.Connect("127.0.0.1", 5136);
                await mcs.EstablishAsync();
                Assert.Contains(svc2.GetInventoryView(), r => r.Station == 1 && r.PendingCeid == 204);   // 继续等
                Assert.Equal(0, await mcs.WaitForEventCountAsync(203, 1, 400));   // 重启过程不补报

                var (ok, _) = svc2.FillCarrierPending(1, 1, "CARRIER2042");       // 继续等的站可补填
                Assert.True(ok);
                // 命令异步执行（重启前站 2 的清除命令可能残留触发值，命令重试耗时较长）→ 等台账确认再等事件
                var d2 = Environment.TickCount64 + 10000;
                while (Environment.TickCount64 < d2 && svc2.Ledger.Single(x => x.Station == 1).CarrierId != "CARRIER2042")
                    await Task.Delay(100);
                Assert.NotNull(await mcs.WaitForEventAsync(204));
                Assert.Equal("CARRIER2042", plc.GetCarrierId(1));
                mcs.Dispose();
            }
            finally { svc2.Dispose(); }
        }
        finally
        {
            svc1.Dispose();
            plc.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(db)) File.Delete(db);
        }
    }

    [Fact]
    public void PendingId_TraceOnReady_ClearedOnNextRegistration()
    {
        // ⑦pending_id 留痕：就绪清 pending 时写入实际 ID；下一次入货登记清空重来；取走全清
        var inv = new Inventory(null, 1);
        inv.MarkCarrierPending(1, 1, 204);
        Assert.Equal("", inv.Ledger.Single(r => r.Station == 1).PendingId);       // 数据没来 = 空
        inv.SetCarrier(1, 1, "TRACE001", "BUFFER01_01", "AGV");
        inv.ClearCarrierPending(1, 1, "TRACE001");
        var row = inv.Ledger.Single(r => r.Station == 1);
        Assert.Equal(0, row.PendingCeid);
        Assert.Equal("TRACE001", row.PendingId);                                  // 就绪留痕
        Assert.Equal(1, row.CarrierConfirmed);

        inv.RemoveCarrier(1, 1, out _);
        Assert.Equal("", inv.Ledger.Single(r => r.Station == 1).PendingId);       // 取走全清

        inv.SetCarrier(1, 1, "TRACE002", "BUFFER01_01", "MANUAL");
        inv.MarkCarrierPending(1, 1, 201);                                        // 下一次登记（异常回退到等待）
        var row2 = inv.Ledger.Single(r => r.Station == 1);
        Assert.Equal(201, row2.PendingCeid);
        Assert.Equal("", row2.PendingId);                                         // 清空重来
        Assert.Equal(0, row2.CarrierConfirmed);
    }
}
