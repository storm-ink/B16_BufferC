using System.Net.Http.Json;
using BufferC.Core;
using BufferC.Core.Config;
using BufferC.Core.Modbus;
using BufferC.Core.Secs;
using BufferC.Harness;
using Xunit;

namespace BufferC.Tests;

/// <summary>
/// 场景回归（开发文档 §3.6 映射）：PlcSim + BufferCService + McsSim 全链路。
/// 覆盖：初始化建连/查询、AGV 放入(204)、S2F41 命令(201)、扫码握手(501+201)、告警(102/402)。
/// </summary>
public sealed class ScenarioTests : IAsyncLifetime
{
    private PlcSim _plc = null!;
    private BufferCService _svc = null!;
    private McsSim _mcs = null!;

    public async Task InitializeAsync()
    {
        _plc = new PlcSim(index: 1, unitId: 1);
        int plcPort = _plc.Start();

        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5000 },
            PollIntervalMs = 100,
            LogFile = "test-bufferc.log",
        };
        _svc = new BufferCService(cfg);
        _svc.Start();

        _mcs = new McsSim();
        _mcs.Connect("127.0.0.1", 5000);
        await _mcs.EstablishAsync();
        // 等基线轮询就绪（CI 负载下固定 delay 不可靠）
        var deadline = Environment.TickCount64 + 15000;
        while (!_svc.AllPlcsReady && Environment.TickCount64 < deadline) await Task.Delay(100);
    }

    public Task DisposeAsync()
    {
        _mcs.Dispose();
        _svc.Dispose();
        _plc.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Scenario01_Init_QuerySvid()
    {
        var items = await _mcs.QuerySvidAsync(14, 21, 15, 29, 3);
        Assert.Equal(5, items.Count);
        Assert.Equal((uint)5, items[0].AsUInt());    // ControlState = ONLINE REMOTE
        Assert.Equal((uint)3, items[1].AsUInt());    // SCState = AUTO
        Assert.Equal(SecsFormat.L, items[2].Format); // EnhancedCarriers（L）
        Assert.Equal(SecsFormat.L, items[4].Format); // AlarmSet（L）
    }

    [Fact]
    public async Task Scenario02_AgvInstall_Raises204()
    {
        _plc.SetCarrierId(3, "CARRIER001");
        _plc.SetStationState(3, 2);                  // AGV 正放
        await Task.Delay(300);
        _plc.SetStationState(3, 1);                  // 放完
        var ev = await _mcs.WaitForEventAsync(204);
        Assert.NotNull(ev);
        var l = ev!.Items()[0].Children!;                     // S6F11 = L3[...]
        var rpt = l[2].Children![0].Children![1];             // L1[L2[U2 rptid, RPT body]]
        Assert.Equal("CARRIER001", rpt.Children![0].AsString());
        Assert.Equal("Buffer1_Port3", rpt.Children![1].AsString());
    }

    [Fact]
    public async Task Scenario03_S2F41_CarrierDataInstall()
    {
        var hcack = await _mcs.SendS2F41Async("CarrierDataInstall",
            ("CARRIERID", "CARRIER002"), ("CARRIERLOC", "Buffer1_Port5"));
        Assert.Equal(4, hcack);                      // 确认将执行
        var ev = await _mcs.WaitForEventAsync(201);
        Assert.NotNull(ev);
        Assert.Equal("CARRIER002", _plc.GetCarrierId(5));   // PLC 侧已写入
        Assert.Equal((ushort)1, _plc.GetEchoStation(5));    // 回显 cmd=1
        Assert.True(_plc.GetEchoNo() > 0);
    }

    [Fact]
    public async Task Scenario04_S2F41_Pause_Rejected_HCACK2()
    {
        var hcack = await _mcs.SendS2F41Async("PAUSE");
        Assert.Equal(2, hcack);                      // MCS 已确认不发；异常收到拒收
    }

    [Fact]
    public async Task Scenario05_ScanHandshake_Reports501_201()
    {
        _plc.TriggerScan(2, "SCANCODE01");
        var ev501 = await _mcs.WaitForEventAsync(501);
        var ev201 = await _mcs.WaitForEventAsync(201);
        Assert.NotNull(ev501);
        Assert.NotNull(ev201);
        Assert.Equal((ushort)0, _plc.GetReg(340));           // 握手已应答
        Assert.Equal("SCANCODE01", _plc.GetCarrierId(2));    // 已写入 PLC 存储区
    }

    [Fact]
    public async Task Scenario06_Alarm_SetClear()
    {
        _plc.SetStationAlarm(1, 5);
        var s5 = await _mcs.WaitForEventAsync(0);            // 任意 S5F1
        var e102 = await _mcs.WaitForEventAsync(102);
        var e402 = await _mcs.WaitForEventAsync(402);
        Assert.NotNull(s5);
        Assert.NotNull(e102);
        Assert.NotNull(e402);

        _plc.SetStationAlarm(1, 0);
        var c101 = await _mcs.WaitForEventAsync(101);
        var c401 = await _mcs.WaitForEventAsync(401);
        Assert.NotNull(c101);
        Assert.NotNull(c401);
    }

    [Fact]
    public async Task Scenario07_PlcDisconnect_Reconnect_NoEventBackfill()
    {
        _plc.SetCarrierId(4, "CARRIER004");
        _plc.DropConnection();                         // 断线注入
        _plc.SetStationState(4, 1);                    // 断线期间发生变化（重连后仅状态同步，不补报）
        await Task.Delay(2500);                        // 等重连（1s 退避）+ 基线重建
        var ev = await _mcs.WaitForEventAsync(204, 1000);
        Assert.Null(ev);                               // 断线期间的事件不补报
        // 状态已同步：S1F3 查询能反映 PLC 实况（快照合并），但无 204 事件
        var items = await _mcs.QuerySvidAsync(15);
        var carriers = items[0].Children!;
        Assert.Single(carriers);
        Assert.Equal("CARRIER004", carriers[0].Children![0].AsString());
    }

    [Fact]
    public async Task Scenario08_OnlineToOffline_CEID1()
    {
        var r16 = await _mcs.PrimaryAsync(1, 15, Array.Empty<byte>());   // S1F15 Request OFF-LINE
        Assert.NotNull(r16);
        Assert.Equal(16, r16!.Function);                                 // S1F16
        Assert.Equal((uint)0, r16.Items()[0].AsUInt());                  // OFLACK=0
        var ev = await _mcs.WaitForEventAsync(1);                        // Offline(1)
        Assert.NotNull(ev);
    }

    [Fact]
    public async Task Scenario09_OnlineRemote_CEID3()
    {
        var r18 = await _mcs.PrimaryAsync(1, 17, Array.Empty<byte>());   // S1F17 Request ON-LINE
        Assert.NotNull(r18);
        Assert.Equal(18, r18!.Function);                                 // S1F18
        Assert.Equal((uint)0, r18.Items()[0].AsUInt());                  // ONLACK=0
        var ev = await _mcs.WaitForEventAsync(3);                        // OnlineRemote(3)
        Assert.NotNull(ev);
    }

    [Fact]
    public async Task Scenario10_BCR_NG_501_IDReadStatus1()
    {
        _plc.TriggerScan(2, "UNK-20260804120000000-Buffer1_Port2");      // 扫码失败 → UNK 载具
        var ev501 = await _mcs.WaitForEventAsync(501);
        var ev201 = await _mcs.WaitForEventAsync(201);
        Assert.NotNull(ev501);
        Assert.NotNull(ev201);
        var l = ev501!.Items()[0].Children!;
        var rpt = l[2].Children![0].Children![1];                        // RPT5: A id, A loc, U2 status
        Assert.StartsWith("UNK-", rpt.Children![0].AsString());
        Assert.Equal((uint)1, rpt.Children![2].AsUInt());                // IDReadStatus=1
    }

    [Fact]
    public async Task Scenario11_UnitInOutOfService_301_302()
    {
        _plc.SetStationAvail(1, 1);                                      // 不可用
        var e302 = await _mcs.WaitForEventAsync(302);
        Assert.NotNull(e302);
        _plc.SetStationAvail(1, 0);                                      // 恢复可用
        var e301 = await _mcs.WaitForEventAsync(301);
        Assert.NotNull(e301);
        var l = e302!.Items()[0].Children!;
        var rpt = l[2].Children![0].Children![1];                        // RPT3: A UnitID
        Assert.Equal("Buffer1_AI01", rpt.Children![0].AsString());
    }

    [Fact]
    public async Task Scenario12_CarrierRemoved_203()
    {
        // 202（手动取出）依赖 A1（AGV/人工区分）确认后实现；当前按 203 上报
        _plc.SetCarrierId(2, "CARRIER002");
        _plc.SetStationState(2, 2);
        await Task.Delay(200);
        _plc.SetStationState(2, 1);                                      // 放入 → 204
        var e204 = await _mcs.WaitForEventAsync(204);
        Assert.NotNull(e204);
        _plc.SetStationState(2, 0);                                      // 取出 → 203
        var e203 = await _mcs.WaitForEventAsync(203);
        Assert.NotNull(e203);
        var l = e203!.Items()[0].Children!;
        var rpt = l[2].Children![0].Children![1];                        // RPT2: A id, A HandoffType
        Assert.Equal("AGV", rpt.Children![1].AsString());
    }

    [Fact]
    public async Task Scenario13_MultiUnitSameAlarm_Dedup()
    {
        // spec 2.2.2：同告警多单位 → 102 一次 + 402 逐单位
        _plc.SetStationAlarm(1, 5);
        await Task.Delay(200);
        _plc.SetStationAlarm(2, 5);
        Assert.Equal(1, await _mcs.WaitForEventCountAsync(102, 1));
        Assert.Equal(2, await _mcs.WaitForEventCountAsync(402, 2));
        // 清除：最后一个单位清除时才发 101
        _plc.SetStationAlarm(1, 0);
        await Task.Delay(200);
        _plc.SetStationAlarm(2, 0);
        Assert.Equal(1, await _mcs.WaitForEventCountAsync(101, 1));
        Assert.Equal(2, await _mcs.WaitForEventCountAsync(401, 2));
    }

    [Fact]
    public async Task Scenario14_InventoryDataSend_Defensive()
    {
        // 已裁剪（2026-08-04）：异常收到时记录并忽略，回 HCACK=4，不产生事件
        var hcack = await _mcs.SendS2F41Async("InventoryDataSend",
            ("CARRIERID", "INV001"), ("CARRIERLOC", "Buffer1_Port1"));
        Assert.Equal(4, hcack);
        var ev = await _mcs.WaitForEventAsync(201, 1000);
        Assert.Null(ev);
    }

    [Fact]
    public async Task Scenario15_HsmsDisconnect_Recover()
    {
        _mcs.Dispose();                                                  // HSMS 断开
        await Task.Delay(500);                                           // 服务端检测断开
        var mcs2 = new McsSim();
        mcs2.Connect("127.0.0.1", 5000);
        await mcs2.EstablishAsync();
        _plc.SetStationState(1, 2);
        await Task.Delay(200);
        _plc.SetStationState(1, 1);                                      // 重连后新事件正常
        var ev = await mcs2.WaitForEventAsync(204);
        Assert.NotNull(ev);
        mcs2.Dispose();
    }

    [Fact]
    public async Task Scenario16_Restart_RecoverFromPlc()
    {
        // 重启恢复：台账为空时 SVID 15 反映 PLC 实况（快照合并）
        _plc.SetCarrierId(5, "CARRIER005");
        _plc.SetStationState(5, 1);
        await Task.Delay(400);
        _mcs.Dispose();
        _svc.Dispose();                                                  // 模拟 BufferC 重启
        await Task.Delay(300);

        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = _plc.Port, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5000 },
            PollIntervalMs = 100,
            LogFile = "test-bufferc.log",
        };
        var svc2 = new BufferCService(cfg);
        svc2.Start();
        try
        {
            var mcs2 = new McsSim();
            mcs2.Connect("127.0.0.1", 5000);
            await mcs2.EstablishAsync();
            var ready = Environment.TickCount64 + 15000;                 // 等轮询基线就绪
            while (!svc2.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);
            var items = await mcs2.QuerySvidAsync(15);
            var carriers = items[0].Children!;
            Assert.Single(carriers);                                     // PLC 实况已反映
            Assert.Equal("CARRIER005", carriers[0].Children![0].AsString());
            mcs2.Dispose();
        }
        finally
        {
            svc2.Dispose();
        }
    }

    [Fact]
    public async Task Scenario17_InvalidMessage_S9()
    {
        var r9f5 = await _mcs.PrimaryAsync(2, 99, SecsEncode.L());       // 未知函数
        Assert.NotNull(r9f5);
        Assert.Equal(9, r9f5!.Stream);
        Assert.Equal(5, r9f5.Function);                                  // S9F5
        var r9f3 = await _mcs.PrimaryAsync(99, 1, SecsEncode.L());       // 未知流
        Assert.NotNull(r9f3);
        Assert.Equal(9, r9f3!.Stream);
        Assert.Equal(3, r9f3.Function);                                  // S9F3
    }

    [Fact]
    public async Task Scenario18_CarrierDataRemove()
    {
        var hcack = await _mcs.SendS2F41Async("CarrierDataInstall",
            ("CARRIERID", "CARRIER003"), ("CARRIERLOC", "Buffer1_Port3"));
        Assert.Equal(4, hcack);
        Assert.NotNull(await _mcs.WaitForEventAsync(201));
        Assert.Equal("CARRIER003", _plc.GetCarrierId(3));

        var hcack2 = await _mcs.SendS2F41Async("CarrierDataRemove", ("CARRIERID", "CARRIER003"));
        Assert.Equal(4, hcack2);
        Assert.NotNull(await _mcs.WaitForEventAsync(202));
        Assert.Equal("", _plc.GetCarrierId(3));                          // PLC 存储区已清除
        Assert.Equal((ushort)RegisterMap.CmdClear, _plc.GetEchoStation(3));
    }

    [Fact]
    public async Task Scenario19_S5F6_ListAlarm()
    {
        _plc.SetStationAlarm(1, 7);
        Assert.NotNull(await _mcs.WaitForEventAsync(102));
        var r = await _mcs.PrimaryAsync(5, 5, SecsEncode.L());           // S5F5 List Alarm
        Assert.NotNull(r);
        Assert.Equal(6, r!.Function);                                    // S5F6
        var list = r.Items()[0].Children!;                               // L of L3[B, U4, A]
        Assert.Contains(list, item => item.Children is { Count: 3 } && item.Children[1].AsUInt() == 7);
    }

    [Fact]
    public async Task Scenario21_RegisterInvalidValue_NoCrash()
    {
        _plc.SetReg(RegisterMap.RegStationState, 99);                    // 站口 1 非法状态码
        await Task.Delay(300);                                           // 轮询处理非法值不崩溃
        _plc.SetCarrierId(1, "CARRIER099");
        _plc.SetStationState(1, 1);                                      // 99→1 仍应触发放入事件
        var ev = await _mcs.WaitForEventAsync(204);
        Assert.NotNull(ev);
    }

    [Fact]
    public async Task Scenario22_SqliteRestore_AfterRestart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"bufferc-test-{Guid.NewGuid():N}.db");
        try
        {
            var cfg = new BufferCConfig
            {
                Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = _plc.Port, UnitId = 1, ByteOrder = "high" } },
                Hsms = new HsmsConfig { ListenPort = 5002 },             // 避开并行测试端口
                PollIntervalMs = 100,
                DbPath = db,
                LogFile = "test-bufferc.log",
            };
            var svc1 = new BufferCService(cfg);
            svc1.Start();
            var mcs1 = new McsSim();
            mcs1.Connect("127.0.0.1", 5002);
            await mcs1.EstablishAsync();
            var ready = Environment.TickCount64 + 15000;
            while (!svc1.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);

            _plc.SetCarrierId(6, "CARRIER006");                          // 正常 204 事件 → 台账入库
            _plc.SetStationState(6, 2);
            await Task.Delay(200);
            _plc.SetStationState(6, 1);
            Assert.NotNull(await mcs1.WaitForEventAsync(204));
            mcs1.Dispose();
            svc1.Dispose();
            await Task.Delay(300);

            var svc2 = new BufferCService(cfg);                          // 重启：从 SQLite 恢复台账
            svc2.Start();
            try
            {
                var mcs2 = new McsSim();
                mcs2.Connect("127.0.0.1", 5002);
                await mcs2.EstablishAsync();
                var items = await mcs2.QuerySvidAsync(15);
                var carriers = items[0].Children!;
                Assert.Contains(carriers, c => c.Children![0].AsString() == "CARRIER006");
                mcs2.Dispose();
            }
            finally
            {
                svc2.Dispose();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();   // 释放连接池文件句柄
            if (File.Exists(db)) File.Delete(db);
        }
    }

    [Fact]
    public async Task Scenario23_WebApi_StatusAndCommand()
    {
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = _plc.Port, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5004 },             // 避开并行测试端口
            PollIntervalMs = 100,
            WebPort = 5003,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5003);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5003") };
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);

            // 状态视图包含 PLC 与 16 站口
            var status = await http.GetStringAsync("/api/status");
            Assert.Contains("Buffer1_Port1", status);
            Assert.Contains("hsmsConnected", status);

            // 手动命令（Web → 命令路径 → 201 事件）
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5004);
            await mcs.EstablishAsync();
            var resp = await http.PostAsJsonAsync("/api/command",
                new { cmd = "install", carrierId = "WEB001", carrierLoc = "Buffer1_Port7" });
            Assert.True(resp.IsSuccessStatusCode);
            Assert.NotNull(await mcs.WaitForEventAsync(201));

            // 状态视图反映新载具 + PLC 存储区已写入
            await Task.Delay(200);
            var status2 = await http.GetStringAsync("/api/status");
            Assert.Contains("WEB001", status2);
            Assert.Equal("WEB001", _plc.GetCarrierId(7));
            mcs.Dispose();
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Scenario25_AgvcDelivery_And_FailedCommandList()
    {
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = _plc.Port, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5006 },
            PollIntervalMs = 100,
            WebPort = 5005,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5005);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5005") };
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5006);
            await mcs.EstablishAsync();

            // AGVC 送达 → 命令路径 → 201 + PLC 写入
            var resp = await http.PostAsJsonAsync("/api/agvc/delivery",
                new { cmd = "install", carrierId = "AGV001", carrierLoc = "Buffer1_Port9" });
            Assert.True(resp.IsSuccessStatusCode);
            Assert.NotNull(await mcs.WaitForEventAsync(201));
            Assert.Equal("AGV001", _plc.GetCarrierId(9));

            // 命令失败 → 悬空记录（C1）出现在 /api/commands，来源 AGVC
            _plc.CommandHang = true;
            await http.PostAsJsonAsync("/api/agvc/delivery",
                new { cmd = "install", carrierId = "AGV002", carrierLoc = "Buffer1_Port10" });
            await Task.Delay(1500);                          // 等超时+重试失败
            var cmds = await http.GetStringAsync("/api/commands");
            Assert.Contains("AGV002", cmds);
            Assert.Contains("AGVC", cmds);
            _plc.CommandHang = false;
            mcs.Dispose();
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Scenario26_WebInfo_LogsStatsActivity()
    {
        // 信息增强：日志 API / 统计字段 / 事件流 / 命令历史 / 告警历史（开发文档 §2 界面服务）
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = _plc.Port, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5008 },
            PollIntervalMs = 100,
            WebPort = 5007,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5007);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5007") };
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5008);
            await mcs.EstablishAsync();

            // 1) 事件流 + 日志 API
            _plc.SetCarrierId(3, "INFO001");
            _plc.SetStationState(3, 2);
            await Task.Delay(200);
            _plc.SetStationState(3, 1);
            Assert.NotNull(await mcs.WaitForEventAsync(204));
            await Task.Delay(200);

            var events = await http.GetStringAsync("/api/events?tail=50");
            Assert.Contains("204", events);
            Assert.Contains("INFO001", events);

            var logs = await http.GetStringAsync("/api/logs?tail=200");
            Assert.Contains("PLC1", logs);
            var logsPlc = await http.GetStringAsync("/api/logs?tail=200&category=PLC1");
            Assert.Contains("已连接", logsPlc);

            // 2) 状态统计与寄存器视图字段
            var status = await http.GetStringAsync("/api/status");
            Assert.Contains("\"connected\":true", status);       // PLC 在线
            Assert.Contains("pollCount", status);
            Assert.Contains("bufferNo", status);                 // 寄存器视图
            Assert.Contains("messagesOut", status);              // MCS 统计
            Assert.Contains("startedAt", status);                // 系统信息

            // 3) 命令历史 + 告警历史
            await http.PostAsJsonAsync("/api/command",
                new { cmd = "install", carrierId = "INFO002", carrierLoc = "Buffer1_Port4" });
            Assert.NotNull(await mcs.WaitForEventAsync(201));
            var cmds = await http.GetStringAsync("/api/commands");
            Assert.Contains("INFO002", cmds);
            Assert.Contains("MANUAL", cmds);                     // 来源

            _plc.SetStationAlarm(2, 9);
            Assert.NotNull(await mcs.WaitForEventAsync(102));
            _plc.SetStationAlarm(2, 0);
            Assert.NotNull(await mcs.WaitForEventAsync(101));
            var hist = await http.GetStringAsync("/api/alarm-history");
            Assert.Contains("SET", hist);
            Assert.Contains("CLEAR", hist);
            mcs.Dispose();
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
        }
    }
}
