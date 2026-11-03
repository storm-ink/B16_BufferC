using System.Net;
using System.Text;
using BufferC.Core;
using BufferC.Core.Config;
using BufferC.Core.Core;
using BufferC.Core.Modbus;
using BufferC.Harness;
using BufferC.Host;
using Microsoft.Data.Sqlite;

namespace BufferC.Tests;

/// <summary>AGV 货格加解锁（V9）：入站接口 → 命令通道 3/4 → 341 回读 → 镜像/超时/对账。</summary>
public sealed class CellLockTests
{
    // ---------- 夹具 ----------
    private static BufferCConfig Cfg(int plcPort, int hsmsPort, string? dbPath = null, int timeoutMinutes = 30, int stations = 16) => new()
    {
        Plcs = new List<PlcConfig>
        {
            new() { Index = 1, Ip = "127.0.0.1", Port = plcPort, UnitId = 1, ByteOrder = "high", Stations = stations, Name = "MAGV03B01" },
        },
        Hsms = new HsmsConfig { ListenPort = hsmsPort },
        PollIntervalMs = 50,
        EchoTimeoutMs = 500,
        EchoRetryCount = 0,
        EchoPollIntervalMs = 20,
        CellLockTimeoutMinutes = timeoutMinutes,
        DbPath = dbPath,
        WebPort = 0,
        LogLevel = "debug",
    };

    private static string Req(string guid, string cms, string action, string? time = null) =>
        "{\"time\":\"" + (time ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) +
        "\",\"guid\":\"" + guid + "\",\"cmsIndex\":\"" + cms + "\",\"action\":\"" + action + "\"}";

    private static async Task WaitReady(BufferCService svc, int ms = 8000)
    {
        var deadline = Environment.TickCount64 + ms;
        while (!svc.AllPlcsReady && Environment.TickCount64 < deadline) await Task.Delay(20);
        Assert.True(svc.AllPlcsReady, "PLC 首轮快照未就绪");
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 8000)
    {
        var deadline = Environment.TickCount64 + ms;
        while (!cond() && Environment.TickCount64 < deadline) await Task.Delay(30);
        Assert.True(cond());
    }

    private static CellLockManager.CellLockView View(BufferCService svc, int station) =>
        svc.GetCellLocks().Locks.Single(l => l.Plc == 1 && l.Station == station);

    // ---------- 正常路径 ----------
    [Fact]
    public async Task Lock_Success_SetsReg341_RecordsStateAndEvent()
    {
        using var plc = new PlcSim(index: 1);
        int port = plc.Start();
        using var svc = new BufferCService(Cfg(port, FakeAgvcServer.FreePort()));
        svc.Start();
        await WaitReady(svc);

        var r = svc.HandleCellLock(Req("G1", "10001", "lock"));

        Assert.Equal("0", r.Code);
        Assert.Equal("加锁成功", r.Message);
        Assert.Equal(1, plc.GetStationLock(1));
        var v = View(svc, 1);
        Assert.True(v.Locked);
        Assert.Equal("AGV", v.Source);
        Assert.Equal("G1", v.Guid);
        var (_, recent) = svc.GetCellLocks();
        Assert.Contains(recent, e => e.Guid == "G1" && e.Result == "OK" && e.Action == "lock" && e.CmdSeq != 0);
    }

    [Fact]
    public async Task Unlock_Success_ClearsReg341()
    {
        using var plc = new PlcSim(index: 1);
        int port = plc.Start();
        using var svc = new BufferCService(Cfg(port, FakeAgvcServer.FreePort()));
        svc.Start();
        await WaitReady(svc);

        Assert.Equal("0", svc.HandleCellLock(Req("G1", "10001", "lock")).Code);
        var r = svc.HandleCellLock(Req("G2", "10001", "unlock"));

        Assert.Equal("0", r.Code);
        Assert.Equal("解锁成功", r.Message);
        Assert.Equal(0, plc.GetStationLock(1));
        Assert.False(View(svc, 1).Locked);
    }

    [Fact]
    public async Task DuplicateGuid_ReturnsIdempotent_WithoutSecondCommand()
    {
        using var plc = new PlcSim(index: 1);
        int port = plc.Start();
        using var svc = new BufferCService(Cfg(port, FakeAgvcServer.FreePort()));
        svc.Start();
        await WaitReady(svc);

        Assert.Equal("0", svc.HandleCellLock(Req("G1", "10001", "lock")).Code);
        ushort seq = plc.GetEchoNo();
        var r = svc.HandleCellLock(Req("G1", "10001", "lock"));

        Assert.Equal("0", r.Code);
        Assert.Equal("重复消息（幂等）", r.Message);
        Assert.Equal(seq, plc.GetEchoNo());   // 400 未被再次触发
    }

    [Fact]
    public async Task AlreadyLocked_NewGuid_NoCommand_AndRefreshesOwner()
    {
        using var plc = new PlcSim(index: 1);
        int port = plc.Start();
        using var svc = new BufferCService(Cfg(port, FakeAgvcServer.FreePort()));
        svc.Start();
        await WaitReady(svc);

        Assert.Equal("0", svc.HandleCellLock(Req("G1", "10001", "lock")).Code);
        ushort seq = plc.GetEchoNo();
        var r = svc.HandleCellLock(Req("G2", "10001", "lock"));

        Assert.Equal("0", r.Code);
        Assert.Equal("加锁成功", r.Message);
        Assert.Equal(seq, plc.GetEchoNo());
        Assert.Equal("G2", View(svc, 1).Guid);   // 新任务持有，超时计时刷新
    }

    [Fact]
    public async Task Unlock_WhenAlreadyUnlocked_NoCommand()
    {
        using var plc = new PlcSim(index: 1);
        int port = plc.Start();
        using var svc = new BufferCService(Cfg(port, FakeAgvcServer.FreePort()));
        svc.Start();
        await WaitReady(svc);

        ushort seq = plc.GetEchoNo();
        var r = svc.HandleCellLock(Req("G1", "10002", "unlock"));

        Assert.Equal("0", r.Code);
        Assert.Equal("解锁成功", r.Message);
        Assert.Equal(seq, plc.GetEchoNo());
    }

    [Fact]
    public async Task Lock_On8StationPlc_UsesPhysicalSlot()
    {
        using var plc = new PlcSim(index: 1, stations: 8);
        int port = plc.Start();
        using var svc = new BufferCService(Cfg(port, FakeAgvcServer.FreePort(), stations: 8));
        svc.Start();
        await WaitReady(svc);

        var r = svc.HandleCellLock(Req("G1", "10008", "lock"));   // 逻辑 8 → 物理 8

        Assert.Equal("0", r.Code);
        Assert.Equal(1, plc.GetReg(RegisterMap.RegStationLock + 7));
        Assert.True(View(svc, 8).Locked);
    }

    // ---------- 参数/失败路径 ----------
    [Theory]
    [InlineData("{\"guid\":\"G\",\"cmsIndex\":\"10001\",\"action\":\"lock\"}", "缺少 time")]
    [InlineData("{\"time\":\"2026-10-23 10:00:00\",\"cmsIndex\":\"10001\",\"action\":\"lock\"}", "缺少 guid")]
    [InlineData("{\"time\":\"2026-10-23 10:00:00\",\"guid\":\"G\",\"action\":\"lock\"}", "缺少 cmsIndex")]
    [InlineData("{\"time\":\"2026-10-23 10:00:00\",\"guid\":\"G\",\"cmsIndex\":\"10000\",\"action\":\"lock\"}", "cmsIndex 非法")]
    [InlineData("{\"time\":\"2026-10-23 10:00:00\",\"guid\":\"G\",\"cmsIndex\":\"10001\",\"action\":\"open\"}", "action 非法")]
    [InlineData("{\"time\":\"not-a-time\",\"guid\":\"G\",\"cmsIndex\":\"10001\",\"action\":\"lock\"}", "time 格式非法")]
    [InlineData("{", "JSON 格式错误")]
    public async Task InvalidRequest_ReturnsCode1(string body, string expected)
    {
        using var plc = new PlcSim(index: 1);
        int port = plc.Start();
        using var svc = new BufferCService(Cfg(port, FakeAgvcServer.FreePort()));
        svc.Start();
        await WaitReady(svc);

        var r = svc.HandleCellLock(body);

        Assert.Equal("1", r.Code);
        Assert.Contains(expected, r.Message);
    }

    [Fact]
    public void PlcOffline_ReturnsCode1()
    {
        int deadPort = FakeAgvcServer.FreePort();   // 有探测后立即释放，无监听
        using var svc = new BufferCService(Cfg(deadPort, FakeAgvcServer.FreePort()));
        svc.Start();

        var r = svc.HandleCellLock(Req("G1", "10001", "lock"));

        Assert.Equal("1", r.Code);
        Assert.Equal("PLC 离线", r.Message);
    }

    [Fact]
    public async Task CommandFailure_ReturnsCode1_AndDoesNotRecordLock()
    {
        using var plc = new PlcSim(index: 1);
        int port = plc.Start();
        using var svc = new BufferCService(Cfg(port, FakeAgvcServer.FreePort()));
        svc.Start();
        await WaitReady(svc);
        plc.CommandHang = true;   // 不回显 → 命令通道超时失败

        var r = svc.HandleCellLock(Req("G1", "10001", "lock"));

        Assert.Equal("1", r.Code);
        Assert.Equal("PLC 转发失败", r.Message);
        Assert.Equal(0, plc.GetStationLock(1));
        Assert.False(View(svc, 1).Locked);
        var (_, recent) = svc.GetCellLocks();
        Assert.Contains(recent, e => e.Guid == "G1" && e.Result == "FAIL");
    }

    // ---------- 超时 / 对账 ----------
    [Fact]
    public async Task Timeout_AutoUnlocks_AndRecordsTimeoutEvent()
    {
        using var plc = new PlcSim(index: 1);
        int port = plc.Start();
        using var svc = new BufferCService(Cfg(port, FakeAgvcServer.FreePort(), timeoutMinutes: 1));
        svc.Start();
        await WaitReady(svc);

        Assert.Equal("0", svc.HandleCellLock(Req("G1", "10001", "lock")).Code);
        Assert.Equal(1, plc.GetStationLock(1));

        svc.Clock = () => DateTime.Now.AddMinutes(2);   // 拨过超时点（时钟晚绑定）
        svc.CheckCellLockTimeouts();

        Assert.Equal(0, plc.GetStationLock(1));
        Assert.False(View(svc, 1).Locked);
        var (_, recent) = svc.GetCellLocks();
        Assert.Contains(recent, e => e.Guid == "G1" && e.Result == "TIMEOUT");
    }

    [Fact]
    public async Task Reconcile_AdoptsExternalLock_AndClearsOnExternalUnlock()
    {
        using var plc = new PlcSim(index: 1);
        int port = plc.Start();
        using var svc = new BufferCService(Cfg(port, FakeAgvcServer.FreePort()));
        svc.Start();
        await WaitReady(svc);

        plc.SetStationLock(2, 1);   // PLC 侧外部加锁（BufferC 无记录）
        await WaitUntil(() => View(svc, 2).Locked);
        Assert.Equal("PLC", View(svc, 2).Source);

        await Task.Delay(3200);     // 越过对账保护窗口
        plc.SetStationLock(2, 0);   // PLC 上电清零
        await WaitUntil(() => !View(svc, 2).Locked);
    }

    // ---------- HTTP 端点 ----------
    [Fact]
    public async Task WebApi_CellLock_Always200_AndLocksQuery()
    {
        using var plc = new PlcSim(index: 1);
        int port = plc.Start();
        using var svc = new BufferCService(Cfg(port, FakeAgvcServer.FreePort()));
        svc.Start();
        await WaitReady(svc);
        int webPort = FakeAgvcServer.FreePort();
        var web = WebUi.Start(svc, webPort);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{webPort}") };

            // 坏 JSON 也必须 HTTP 200 + code=1
            using var bad = await http.PostAsync("/api/agv/cellLock",
                new StringContent("{", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
            Assert.Contains("\"code\":\"1\"", await bad.Content.ReadAsStringAsync());

            using var ok = await http.PostAsync("/api/agv/cellLock",
                new StringContent(Req("G1", "10001", "lock"), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var body = await ok.Content.ReadAsStringAsync();
            Assert.Contains("\"code\":\"0\"", body);
            Assert.Contains("加锁成功", body);

            string locks = await http.GetStringAsync("/api/agv/locks");
            Assert.Contains("\"locked\":true", locks);
            Assert.Contains("\"guid\":\"G1\"", locks);
        }
        finally
        {
            await web.StopAsync();
        }
    }

    // ---------- 持久化 ----------
    [Fact]
    public void CellLocks_PersistAcrossRestart()
    {
        string db = Path.Combine(Path.GetTempPath(), $"celllock-test-{Guid.NewGuid():N}.db");
        try
        {
            var at = new DateTime(2026, 10, 23, 10, 0, 0);
            using (var inv = new Inventory(db, 1))
            {
                inv.SetCellLock(1, 1, "AGV", "G1", at, at.AddSeconds(-1));
                inv.AddCellLockEvent(at, "G1", 1, 1, "lock", "OK", "加锁成功", 7, at.AddSeconds(-1));
            }
            SqliteConnection.ClearAllPools();

            using (var inv2 = new Inventory(db, 1))
            {
                var row = inv2.FindCellLock(1, 1);
                Assert.NotNull(row);
                Assert.True(row!.Locked);
                Assert.Equal("G1", row.Guid);
                Assert.Equal(at, row.LockedAt);

                var ev = inv2.LastCellLockEvent("G1");
                Assert.NotNull(ev);
                Assert.Equal("OK", ev!.Result);
                Assert.Equal(7u, ev.CmdSeq);
                Assert.Single(inv2.LoadCellLockEvents(10));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch { }
        }
    }
}
