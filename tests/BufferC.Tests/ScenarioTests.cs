using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
        // 取走路径区分（A1 已确认）：1→0 直跳 = 人工取走 → HandoffType=MANUAL；经 3→0 才报 AGV
        _plc.SetCarrierId(2, "CARRIER002");
        _plc.SetStationState(2, 2);
        await Task.Delay(200);
        _plc.SetStationState(2, 1);                                      // 放入 → 204
        var e204 = await _mcs.WaitForEventAsync(204);
        Assert.NotNull(e204);
        _plc.SetStationState(2, 0);                                      // 直跳取出 → 203 MANUAL
        var e203 = await _mcs.WaitForEventAsync(203);
        Assert.NotNull(e203);
        var l = e203!.Items()[0].Children!;
        var rpt = l[2].Children![0].Children![1];                        // RPT2: A id, A HandoffType
        Assert.Equal("MANUAL", rpt.Children![1].AsString());
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
    public async Task Scenario25_ManualInstall_And_FailedCommandList()
    {
        // 入站 AGVC 送达端点已移除（2026-08-14）：本场景改用 /api/command 验证同一命令路径与悬空记录（C1）
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

            // 手动安装 → 命令路径 → 201 + PLC 写入
            var resp = await http.PostAsJsonAsync("/api/command",
                new { cmd = "install", carrierId = "AGV001", carrierLoc = "Buffer1_Port9" });
            Assert.True(resp.IsSuccessStatusCode);
            Assert.NotNull(await mcs.WaitForEventAsync(201));
            Assert.Equal("AGV001", _plc.GetCarrierId(9));

            // 命令失败 → 悬空记录（C1）出现在 /api/commands，来源 MANUAL
            _plc.CommandHang = true;
            await http.PostAsJsonAsync("/api/command",
                new { cmd = "install", carrierId = "AGV002", carrierLoc = "Buffer1_Port10" });
            await Task.Delay(1500);                          // 等超时+重试失败
            var cmds = await http.GetStringAsync("/api/commands");
            Assert.Contains("AGV002", cmds);
            Assert.Contains("MANUAL", cmds);
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

    [Fact]
    public async Task Scenario27_LogLevel_FilterTraceAndModbusFrames()
    {
        // 日志分级（调试功能 A1/A2）：默认 info 过滤 trace 级；运行时切 trace 后 Modbus 帧日志可见
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = _plc.Port, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5015 },
            PollIntervalMs = 100,
            LogModbusFrames = true,
            WebPort = 5012,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5012);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5012") };
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);

            // 默认 info：trace 级 Modbus 帧被过滤（/api/logs 无 MODBUS 帧）
            var logs = await http.GetStringAsync("/api/logs?tail=100");
            Assert.DoesNotContain("MODBUS", logs);
            Assert.DoesNotContain("\"level\":\"Trace\"", logs);

            // 切 trace → Modbus 帧可见（显式等待 poller 产生新帧）
            var r = await http.PostAsJsonAsync("/api/debug/loglevel", new { level = "trace" });
            Assert.True(r.IsSuccessStatusCode);
            var deadline = Environment.TickCount64 + 10000;
            var seen = false;
            while (Environment.TickCount64 < deadline && !seen)
            {
                await Task.Delay(300);
                logs = await http.GetStringAsync("/api/logs?tail=200");
                seen = logs.Contains("MODBUS TX") || logs.Contains("MODBUS RX");
            }
            Assert.True(seen, "切 trace 后应出现 Modbus 帧日志");

            // 非法级别 → 400
            var bad = await http.PostAsJsonAsync("/api/debug/loglevel", new { level = "bogus" });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Scenario28_DebugApi_ToggleFrames()
    {
        // 帧日志运行时切换（A4）：/api/debug/logframes 关 → 无新帧日志；开 → 恢复
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = _plc.Port, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5016, LogFrames = true },
            PollIntervalMs = 100,
            WebPort = 5013,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5013);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5013") };
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5016);
            await mcs.EstablishAsync();

            // 关帧日志 → 发 S1F3 → 窗口内无新 S1F3 帧（建连帧 S1F13/1/17 不含 "S1F3" 字样）
            var r = await http.PostAsJsonAsync("/api/debug/logframes", new { enabled = false });
            Assert.True(r.IsSuccessStatusCode);
            await mcs.QuerySvidAsync(14);
            await Task.Delay(500);
            var logs = await http.GetStringAsync("/api/logs?tail=200&category=MCS");
            Assert.DoesNotContain("S1F3", logs);

            // 开帧日志 → 发 S1F3 → 帧日志恢复
            await http.PostAsJsonAsync("/api/debug/logframes", new { enabled = true });
            await mcs.QuerySvidAsync(14);
            var deadline = Environment.TickCount64 + 10000;
            var seen = false;
            while (Environment.TickCount64 < deadline && !seen)
            {
                await Task.Delay(300);
                logs = await http.GetStringAsync("/api/logs?tail=200&category=MCS");
                seen = logs.Contains("S1F3");
            }
            Assert.True(seen, "开帧日志后应出现 S1F3 帧日志");
            mcs.Dispose();
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Scenario29_CommandLatency_ReportedViaDebugApi()
    {
        // 命令耗时统计（A5）：Web 命令后 /api/debug 报告 latency（count ≥ 1 且 avgMs > 0）
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = _plc.Port, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5017 },
            PollIntervalMs = 100,
            WebPort = 5014,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5014);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5014") };
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);

            var r = await http.PostAsJsonAsync("/api/command",
                new { cmd = "install", carrierId = "LAT001", carrierLoc = "Buffer1_Port5" });
            Assert.True(r.IsSuccessStatusCode);

            var deadline = Environment.TickCount64 + 15000;
            var ok = false;
            while (Environment.TickCount64 < deadline && !ok)
            {
                await Task.Delay(300);
                var debug = await http.GetStringAsync("/api/debug");
                // PlcDebug.latency（camelCase）：count ≥ 1 且 avgMs > 0（JSON 解析，避免子串误判 count=2~9）
                using var doc = System.Text.Json.JsonDocument.Parse(debug);
                var latency = doc.RootElement.GetProperty("plcs")[0].GetProperty("latency");
                ok = latency.GetProperty("count").GetInt64() >= 1 && latency.GetProperty("avgMs").GetInt64() > 0;
            }
            Assert.True(ok, "/api/debug 应报告命令耗时（latency count≥1 且 avgMs>0）");
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Scenario30_CommandWhilePlcDown_NoHsmsCrash()
    {
        // 缺陷回归（故障注入调试台实测）：PLC 断线时发 S2F41 命令 → 按失败处理（S5F1 9001），HSMS 连接不得崩溃
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = _plc.Port, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5018 },
            PollIntervalMs = 100,
            WebPort = 0,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        try
        {
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5018);
            await mcs.EstablishAsync();

            _plc.DropConnection();                       // PLC 断线（重连退避 1s，命令在窗口内发出）
            await Task.Delay(300);
            var hc = await mcs.SendS2F41Async("CarrierDataInstall", ("CARRIERID", "DOWN001"), ("CARRIERLOC", "Buffer1_Port6"));
            Assert.Equal((byte)4, hc);                   // HCACK=4 异步执行确认（设计行为）

            // 命令失败 → S5F1 告警 9001；且 HSMS 连接保持可用（后续查询成功 = 连接未崩）
            Assert.NotNull(await mcs.WaitForEventAsync(0, 5000));   // 任意 S5F1（9001）
            var items = await mcs.QuerySvidAsync(14);
            Assert.Single(items);
            mcs.Dispose();
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Scenario31_DebugPlcPanel_RegReadWriteAndDirectCmd()
    {
        // PLC 调试面板（现场联调）：寄存器直读直写 + 站口命令直发——绕过业务（不动台账、不发 MCS 事件）
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = _plc.Port, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5020 },
            PollIntervalMs = 100,
            WebPort = 5019,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5019);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5019") };
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);

            // 直读寄存器：0~49 快速区可读且含 Buffer 编号
            var rr = await http.GetStringAsync("/api/debug/regread?plc=1&addr=0&count=50");
            Assert.Contains("\"addr\":0", rr);
            Assert.Contains("\"values\"", rr);

            // 直发命令 1=写入货物ID：PLC 存储区写入 + 回显区匹配（306=编号 / 307+站口=1）
            var r1 = await http.PostAsJsonAsync("/api/debug/cmd",
                new { plc = 1, station = 5, cmd = 1, carrierId = "DBG001" });
            Assert.True(r1.IsSuccessStatusCode);
            Assert.Equal("DBG001", _plc.GetCarrierId(5));
            Assert.Equal((ushort)1, _plc.GetEchoStation(5));
            Assert.Equal(_plc.GetEchoNo(), _plc.GetReg(RegisterMap.RegCmdNo));

            // 直发命令 2=清除货物ID：ID 区清零
            var r2 = await http.PostAsJsonAsync("/api/debug/cmd",
                new { plc = 1, station = 5, cmd = 2 });
            Assert.True(r2.IsSuccessStatusCode);
            Assert.Equal("", _plc.GetCarrierId(5));

            // 命令码放开（任意 0~65535 直发，仅 1 写 ID 区）：cmd=9 成功执行且回显
            var r3 = await http.PostAsJsonAsync("/api/debug/cmd",
                new { plc = 1, station = 6, cmd = 9 });
            Assert.True(r3.IsSuccessStatusCode);
            Assert.Equal((ushort)9, _plc.GetEchoStation(6));

            // 手动命令位置格式放宽：取前两个数字（"1号Buffer_6号站口" == Buffer1_Port6）
            var r4 = await http.PostAsJsonAsync("/api/command",
                new { cmd = "install", carrierId = "LOOSE1", carrierLoc = "1号Buffer_6号站口" });
            Assert.True(r4.IsSuccessStatusCode);
            Assert.Equal("LOOSE1", _plc.GetCarrierId(6));

            // 直写寄存器（FC16）→ API 回读一致
            var wr = await http.PostAsJsonAsync("/api/debug/regwrite",
                new { plc = 1, addr = 700, values = new[] { 11, 22 } });
            Assert.True(wr.IsSuccessStatusCode);
            var back = await http.GetStringAsync("/api/debug/regread?plc=1&addr=700&count=2");
            Assert.Contains("\"values\":[11,22]", back);

            // 参数校验：非法站口/位置/PLC → 400
            var bad1 = await http.PostAsJsonAsync("/api/debug/cmd", new { plc = 1, station = 0, cmd = 1, carrierId = "X" });
            Assert.False(bad1.IsSuccessStatusCode);
            var bad2 = await http.PostAsJsonAsync("/api/debug/cmd", new { plc = 1, station = 17, cmd = 1, carrierId = "X" });
            Assert.False(bad2.IsSuccessStatusCode);
            var bad3resp = await http.GetAsync("/api/debug/regread?plc=99&addr=0&count=10");
            Assert.False(bad3resp.IsSuccessStatusCode);
            var bad4 = await http.PostAsJsonAsync("/api/command",
                new { cmd = "install", carrierId = "X", carrierLoc = "Buffer1_Port17" });
            Assert.False(bad4.IsSuccessStatusCode);                 // 站口越界
            var bad5 = await http.PostAsJsonAsync("/api/command",
                new { cmd = "install", carrierId = "X", carrierLoc = "1号Buffer" });
            Assert.False(bad5.IsSuccessStatusCode);                 // 只有一个数字无法定位站口
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
        }
    }

    // ---------- AGVC HTTP 集成（§D1：方向1 出站到货要 ID；方向2 入站站口服务状态） ----------
    // 新场景全部用独立 PlcSim（不复用类级 _plc）：避免与 fixture 服务的扫码握手/命令写入竞态
    private static AgvcConfig AgvcCfg(string baseUrl, int graceMs = 0, int retryCount = 0, int retryIntervalMs = 100) => new()
    {
        BaseUrl = baseUrl,
        ArrivalGraceMs = graceMs,
        RetryCount = retryCount,
        RetryIntervalMs = retryIntervalMs,
    };

    [Fact]
    public async Task Scenario32_AgvcQueryMachines_OnAgvArrival_WritesIdDirectly()
    {
        using var plc = new PlcSim(index: 1);
        int plcPort = plc.Start();
        using var agvc = new FakeAgvcServer { QueryCarrierId = "AGV001" };
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5021 },
            PollIntervalMs = 100,
            WebPort = 5022,
            Agvc = AgvcCfg($"http://127.0.0.1:{agvc.Port}"),
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5022);
        try
        {
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5021);
            await mcs.EstablishAsync();

            // AGV 放入（0→2→1）→ 出站查询 AGVC queryMachines 要货物 ID（伴随 204 事件的状态推送会一并到达）
            plc.SetStationState(1, 2);
            await Task.Delay(300);
            plc.SetStationState(1, 1);
            var deadline = Environment.TickCount64 + 10000;
            while (!agvc.Requests.Any(r => r.Path == "/api/hsmsRpc/queryMachines") && Environment.TickCount64 < deadline) await Task.Delay(50);
            var q = agvc.Requests.First(r => r.Path == "/api/hsmsRpc/queryMachines");
            using (var doc = JsonDocument.Parse(q.Body))
                Assert.Equal("10001", doc.RootElement.GetProperty("cmsIndex").GetString());

            // 响应 data[0].carrierId 直接回 ID → BufferC 自己写 PLC（命令1）+ 台账 + 201
            var deadline2 = Environment.TickCount64 + 10000;
            while (plc.GetCarrierId(1) == "" && Environment.TickCount64 < deadline2) await Task.Delay(50);
            Assert.Equal("AGV001", plc.GetCarrierId(1));
            Assert.NotNull(await mcs.WaitForEventAsync(201));
            Assert.Contains(svc.Carriers, c => c.CarrierLoc == "Buffer1_Port1" && c.CarrierId == "AGV001");

            // 变化即推：201（带 ID）触发 pushDeviceStatusInfo，trayId 为刚写入的货物 ID
            var deadline3 = Environment.TickCount64 + 10000;
            while (!agvc.Requests.Any(r => r.Path == "/api/hsmsRpc/pushDeviceStatusInfo"
                && JsonDocument.Parse(r.Body).RootElement.GetProperty("status")[0].GetProperty("trayId").GetString() == "AGV001")
                && Environment.TickCount64 < deadline3) await Task.Delay(50);
            Assert.Contains(agvc.Requests, r => r.Path == "/api/hsmsRpc/pushDeviceStatusInfo"
                && JsonDocument.Parse(r.Body).RootElement.GetProperty("status")[0].GetProperty("trayId").GetString() == "AGV001");
            mcs.Dispose();
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Scenario33_AgvcPushGraceWindow_HandshakeCancels()
    {
        using var plc = new PlcSim(index: 1);
        int plcPort = plc.Start();
        using var agvc = new FakeAgvcServer();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5023 },
            PollIntervalMs = 100,
            WebPort = 5024,
            Agvc = AgvcCfg($"http://127.0.0.1:{agvc.Port}", graceMs: 800),
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5024);
        try
        {
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);

            // 变体A：2→1 后窗口内扫码握手到达 → 取消出站调用（扫码路径写入 ID）
            plc.SetStationState(1, 2);
            await Task.Delay(300);
            plc.SetStationState(1, 1);
            await Task.Delay(200);
            plc.TriggerScan(1, "SCANWIN1");
            var deadline = Environment.TickCount64 + 5000;
            while (plc.GetCarrierId(1) == "" && Environment.TickCount64 < deadline) await Task.Delay(50);
            Assert.Equal("SCANWIN1", plc.GetCarrierId(1));
            await Task.Delay(1500);                              // 越过窗口期
            Assert.Empty(agvc.Requests.Where(r => r.Path == "/api/hsmsRpc/queryMachines"));  // 扫码路径接管，未查询（204/501 的状态推送仍会发生）

            // 变体B：无握手的站口 → 窗口到期后出站查询 queryMachines
            plc.SetStationState(2, 2);
            await Task.Delay(300);
            plc.SetStationState(2, 1);
            var deadline2 = Environment.TickCount64 + 5000;
            while (!agvc.Requests.Any(r => r.Path == "/api/hsmsRpc/queryMachines") && Environment.TickCount64 < deadline2) await Task.Delay(50);
            var qs = agvc.Requests.Where(r => r.Path == "/api/hsmsRpc/queryMachines").ToList();
            Assert.Single(qs);
            using (var doc = JsonDocument.Parse(qs[0].Body))
                Assert.Equal("10002", doc.RootElement.GetProperty("cmsIndex").GetString());
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Scenario34_AgvcQueryRetry_And_NoAlarmOnFinalFailure()
    {
        // 变体A：前 2 次应答 code=1 → 重试共 3 次后成功（第 3 次带 carrierId）
        using (var plc = new PlcSim(index: 1))
        {
            int plcPort = plc.Start();
            using var agvc = new FakeAgvcServer { FailCount = 2, QueryCarrierId = "AGVRTY1" };
            var cfg = new BufferCConfig
            {
                Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
                Hsms = new HsmsConfig { ListenPort = 5029 },
                PollIntervalMs = 100,
                Agvc = AgvcCfg($"http://127.0.0.1:{agvc.Port}", retryCount: 2, retryIntervalMs: 100),
                LogFile = "test-bufferc.log",
            };
            var svc = new BufferCService(cfg);
            svc.Start();
            try
            {
                var ready = Environment.TickCount64 + 15000;
                while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);
                plc.SetStationState(1, 2);
                await Task.Delay(300);
                plc.SetStationState(1, 1);
                var deadline = Environment.TickCount64 + 8000;
                while (agvc.Requests.Count(r => r.Path == "/api/hsmsRpc/queryMachines") < 3 && Environment.TickCount64 < deadline) await Task.Delay(50);
                Assert.Equal(3, agvc.Requests.Count(r => r.Path == "/api/hsmsRpc/queryMachines"));  // 1 次 + 2 次重试
                var deadline2 = Environment.TickCount64 + 5000;
                while (plc.GetCarrierId(1) == "" && Environment.TickCount64 < deadline2) await Task.Delay(50);
                Assert.Equal("AGVRTY1", plc.GetCarrierId(1));    // 第 3 次成功 → 写 PLC
            }
            finally { svc.Dispose(); }
        }

        // 变体B：服务器不可达 → 重试后最终失败，仅日志不告警（无 9001）
        using (var plc2 = new PlcSim(index: 1))
        {
            int plcPort = plc2.Start();
            var cfg = new BufferCConfig
            {
                Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
                Hsms = new HsmsConfig { ListenPort = 5030 },
                PollIntervalMs = 100,
                Agvc = AgvcCfg($"http://127.0.0.1:{FakeAgvcServer.FreePort()}", retryCount: 1, retryIntervalMs: 100),
                LogFile = "test-bufferc.log",
            };
            var svc = new BufferCService(cfg);
            svc.Start();
            try
            {
                var ready = Environment.TickCount64 + 15000;
                while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);
                plc2.SetStationState(1, 2);
                await Task.Delay(300);
                plc2.SetStationState(1, 1);
                await Task.Delay(800);                           // 等 1+1 次尝试完成
                Assert.Empty(svc.Alarms);                        // 不告警（用户确认：仅日志）
            }
            finally { svc.Dispose(); }
        }

        // 变体C：code=0 但 data 为空（AGVC 暂不知道 ID）→ 重试耗尽 → ID 未写、无告警
        using (var plc3 = new PlcSim(index: 1))
        {
            int plcPort = plc3.Start();
            using var agvc = new FakeAgvcServer();               // QueryCarrierId 默认 null → data 空
            var cfg = new BufferCConfig
            {
                Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
                Hsms = new HsmsConfig { ListenPort = 5033 },
                PollIntervalMs = 100,
                Agvc = AgvcCfg($"http://127.0.0.1:{agvc.Port}", retryCount: 1, retryIntervalMs: 100),
                LogFile = "test-bufferc.log",
            };
            var svc = new BufferCService(cfg);
            svc.Start();
            try
            {
                var ready = Environment.TickCount64 + 15000;
                while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);
                plc3.SetStationState(1, 2);
                await Task.Delay(300);
                plc3.SetStationState(1, 1);
                var deadline = Environment.TickCount64 + 5000;
                while (agvc.Requests.Count(r => r.Path == "/api/hsmsRpc/queryMachines") < 2 && Environment.TickCount64 < deadline) await Task.Delay(50);
                await Task.Delay(300);                           // 等写入窗口（若有）
                Assert.Equal(2, agvc.Requests.Count(r => r.Path == "/api/hsmsRpc/queryMachines"));  // 1 次 + 1 次重试
                Assert.Equal("", plc3.GetCarrierId(1));          // 无 ID 可写
                Assert.Empty(svc.Alarms);                        // 不告警
            }
            finally { svc.Dispose(); }
        }
    }
    [Fact]
    public async Task Scenario37_ManualState5_EventsAndAbnormalTransitions()
    {
        // 状态区新增 5=（人工）有货已存储：0→5 报 201（空 ID，ID 后写）→ 扫码补 501+201；
        // 取走按路径：5→0 直跳报 MANUAL、5→3→0 经 AGV 报 AGV；异常跳变 1→5/5→1 只记日志不上报
        using var plc = new PlcSim(index: 1);
        int plcPort = plc.Start();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5031 },
            PollIntervalMs = 100,
            WebPort = 5032,
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5032);
        try
        {
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5031);
            await mcs.EstablishAsync();

            // 1) 人工放入 0→5：报 201（ID 空）+ 台账写入
            plc.SetStationState(1, 5);
            var e201 = await mcs.WaitForEventAsync(201);
            Assert.NotNull(e201);
            var rpt = e201!.Items()[0].Children![2].Children![0].Children![1];
            Assert.Equal("", rpt.Children![0].AsString());                       // CarrierID 空
            Assert.Equal("Buffer1_Port1", rpt.Children![1].AsString());
            Assert.Contains(svc.Carriers, c => c.CarrierLoc == "Buffer1_Port1");

            // 2) 扫码补 ID：501 + 201（带 ID）→ PLC 已写入
            plc.TriggerScan(1, "MAN5001");
            Assert.NotNull(await mcs.WaitForEventAsync(501));
            Assert.NotNull(await mcs.WaitForEventAsync(201));
            Assert.Equal("MAN5001", plc.GetCarrierId(1));

            // 3) 人工取走 5→0 直跳：203 HandoffType=MANUAL
            plc.SetStationState(1, 0);
            var e203m = await mcs.WaitForEventAsync(203);
            var rptM = e203m!.Items()[0].Children![2].Children![0].Children![1];
            Assert.Equal("MANUAL", rptM.Children![1].AsString());

            // 4) 人工放 + AGV 取：0→5 → 5→3（无事件）→ 3→0：203 HandoffType=AGV
            plc.SetStationState(2, 5);
            Assert.NotNull(await mcs.WaitForEventAsync(201));
            plc.SetStationState(2, 3);
            await Task.Delay(200);
            plc.SetStationState(2, 0);
            var e203a = await mcs.WaitForEventAsync(203);
            var rptA = e203a!.Items()[0].Children![2].Children![0].Children![1];
            Assert.Equal("AGV", rptA.Children![1].AsString());

            // 5) 异常跳变只记日志不上报：0→2→1（204 正常）→ 1→5 无 201 → 5→1 无 204 → 1→0 收尾
            plc.SetStationState(3, 2);
            await Task.Delay(200);
            plc.SetStationState(3, 1);
            Assert.NotNull(await mcs.WaitForEventAsync(204));
            plc.SetStationState(3, 5);
            Assert.Equal(0, await mcs.WaitForEventCountAsync(201, 1, 400));      // 异常 1→5 不上报
            plc.SetStationState(3, 1);
            Assert.Equal(0, await mcs.WaitForEventCountAsync(204, 1, 400));      // 异常 5→1 不上报
            plc.SetStationState(3, 0);
            Assert.NotNull(await mcs.WaitForEventAsync(203));
            mcs.Dispose();
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Scenario38_AgvcPushStatus_OnStationChanges()
    {
        // 变化即推：204/203/201/501/302/301 事件触发 pushDeviceStatusInfo（单站口一条）；
        // service=34~49 原始寄存器值、present=状态区 1/5、trayId 有货时取事件/台账/快照、无货恒空、input/outputRequest 恒 0
        using var plc = new PlcSim(index: 1);
        int plcPort = plc.Start();
        using var agvc = new FakeAgvcServer();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5034 },
            PollIntervalMs = 100,
            WebPort = 5035,
            Agvc = AgvcCfg($"http://127.0.0.1:{agvc.Port}"),
            LogFile = "test-bufferc.log",
        };
        var svc = new BufferCService(cfg);
        svc.Start();
        var web = BufferC.Host.WebUi.Start(svc, 5035);
        try
        {
            var ready = Environment.TickCount64 + 15000;
            while (!svc.AllPlcsReady && Environment.TickCount64 < ready) await Task.Delay(100);
            var mcs = new McsSim();
            mcs.Connect("127.0.0.1", 5034);
            await mcs.EstablishAsync();

            // 1) 人工放入 0→5 → 201 → 推 (service=0, present=1, trayId="")
            plc.SetStationState(1, 5);
            Assert.NotNull(await mcs.WaitForEventAsync(201));
            // 2) 扫码补 ID → 501 + 201 → 推 trayId=SCANP1
            plc.TriggerScan(1, "SCANP1");
            Assert.NotNull(await mcs.WaitForEventAsync(501));
            Assert.NotNull(await mcs.WaitForEventAsync(201));
            // 3) 控制状态变化（34~49）→ 302 → 推 service=1（原始寄存器值）
            plc.SetStationAvail(1, 1);
            Assert.NotNull(await mcs.WaitForEventAsync(302));
            // 4) 人工取走 5→0 → 203 → 推 present=0（无货 trayId 恒空）
            plc.SetStationState(1, 0);
            Assert.NotNull(await mcs.WaitForEventAsync(203));

            // 等推送落库（推送为 Task.Run 异步）：5 个事件 → 5 条推送
            var deadline = Environment.TickCount64 + 10000;
            while (agvc.Requests.Count < 5 && Environment.TickCount64 < deadline) await Task.Delay(50);
            var pushes = agvc.Requests.Where(r => r.Path == "/api/hsmsRpc/pushDeviceStatusInfo").ToList();
            Assert.Equal(5, pushes.Count);

            var sigs = pushes.Select(p =>
            {
                using var d = JsonDocument.Parse(p.Body);
                var st = d.RootElement.GetProperty("status")[0];
                return (st.GetProperty("service").GetString()!, st.GetProperty("present").GetString()!, st.GetProperty("trayId").GetString()!);
            }).ToList();
            Assert.Contains(("0", "1", ""), sigs);           // 0→5 入货（ID 后写，此刻空）
            Assert.Contains(("0", "1", "SCANP1"), sigs);     // 扫码补 ID
            Assert.Contains(("1", "1", "SCANP1"), sigs);     // 控制状态=1（原始寄存器值）
            Assert.Contains(("1", "0", ""), sigs);           // 取走（无货 trayId 恒空）
            foreach (var p in pushes)
            {
                using var d = JsonDocument.Parse(p.Body);
                var st = d.RootElement.GetProperty("status")[0];
                Assert.Equal("10001", st.GetProperty("cmsIndex").GetString());
                Assert.Equal("0", st.GetProperty("inputRequest").GetString());   // 恒 0
                Assert.Equal("0", st.GetProperty("outputRequest").GetString());
            }
            mcs.Dispose();
        }
        finally
        {
            await web.StopAsync();
            svc.Dispose();
        }
    }

    /// <summary>
    /// AGVC 假服务器（HttpListener）：记录请求路径+body；应答 queryMachines 规格
    /// {code, message, reqCode, interrupt, data:[{carrierId,...}]}。
    /// 注入：前 FailCount 次应答 code="1"（无 data）；QueryCode=正常 code；QueryCarrierId=null 时 data 空数组。
    /// FreePort()：探测空闲端口（另用于模拟"服务器不可达"）。
    /// </summary>
    private sealed class FakeAgvcServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<(string Path, string Body)> _requests = new();
        private readonly object _lock = new();
        private volatile int _failCount;

        public volatile string QueryCode = "0";          // 正常应答的 code 字段
        public volatile string? QueryCarrierId;          // null → data 空数组
        public int Port { get; private set; }
        public int FailCount { set => _failCount = value; }
        public IReadOnlyList<(string Path, string Body)> Requests { get { lock (_lock) return _requests.ToList(); } }

        public FakeAgvcServer()
        {
            Port = FreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }                                 // Stop() 时退出
                _ = Task.Run(async () =>
                {
                    string body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    lock (_lock) _requests.Add((ctx.Request.Url!.AbsolutePath, body));
                    // FailCount 只作用于 queryMachines（推送请求不消耗失败计数，保证重试断言确定性）
                    bool fail = _failCount > 0 && ctx.Request.Url!.AbsolutePath == "/api/hsmsRpc/queryMachines";
                    if (fail) Interlocked.Decrement(ref _failCount);
                    string code = fail ? "1" : QueryCode;
                    string resp;
                    if (ctx.Request.Url!.AbsolutePath == "/api/hsmsRpc/pushDeviceStatusInfo")
                        resp = $"{{\"code\":\"{code}\",\"data\":\"\",\"interrupt\":false,\"message\":\"{(fail ? "busy" : "ok")}\",\"reqCode\":\"REQ001\"}}";
                    else
                    {
                        string data = (fail || QueryCarrierId == null) ? "[]" : $"[{{\"carrierId\":\"{QueryCarrierId}\"}}]";
                        resp = $"{{\"code\":\"{code}\",\"message\":\"{(fail ? "busy" : "ok")}\",\"reqCode\":\"REQ001\",\"interrupt\":false,\"data\":{data}}}";
                    }
                    var buf = Encoding.UTF8.GetBytes(resp);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = buf.Length;
                    await ctx.Response.OutputStream.WriteAsync(buf, ct);
                    ctx.Response.OutputStream.Close();
                }, ct);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }
    }
}
