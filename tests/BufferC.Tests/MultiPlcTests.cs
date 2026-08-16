using System.Diagnostics;
using BufferC.Core;
using BufferC.Core.Config;
using BufferC.Harness;
using Xunit;

namespace BufferC.Tests;

/// <summary>15 台 PLC 类级共享 fixture（一次启动，3 个测试共享——避免 CI 上连续启停 15 台仿真的端口冲突）</summary>
public sealed class MultiPlcFixture : IDisposable
{
    public const int HsmsPort = 5010;
    public const int PlcCount = 15;
    public readonly List<PlcSim> Plcs = new();
    public readonly BufferCService Svc;

    public MultiPlcFixture()
    {
        var cfg = new BufferCConfig { Hsms = new HsmsConfig { ListenPort = HsmsPort }, PollIntervalMs = 100, LogFile = "test-bufferc.log" };
        for (int i = 1; i <= PlcCount; i++)
        {
            var plc = new PlcSim(index: i, unitId: 1);
            int port = plc.Start();
            Plcs.Add(plc);
            cfg.Plcs.Add(new PlcConfig { Index = i, Ip = "127.0.0.1", Port = port, UnitId = 1, ByteOrder = "high" });
        }
        Svc = new BufferCService(cfg);
        Svc.Start();
        // 阻塞等 15 台基线就绪
        var deadline = Environment.TickCount64 + 20000;
        while (!Svc.AllPlcsReady && Environment.TickCount64 < deadline) Thread.Sleep(100);
        if (!Svc.AllPlcsReady) throw new Exception("15 台 PLC 未在 20s 内完成基线轮询");
    }

    public void Dispose()
    {
        Svc.Dispose();
        foreach (var p in Plcs) p.Dispose();
    }
}

/// <summary>
/// 15 台 PLC 并发集成验证（开发文档 §3 架构核心假设）：
/// 全量事件路由、单台断线隔离、S1F3 全量查询性能。
/// </summary>
public sealed class MultiPlcTests : IClassFixture<MultiPlcFixture>
{
    private readonly MultiPlcFixture _fx;

    public MultiPlcTests(MultiPlcFixture fx) => _fx = fx;

    private async Task<McsSim> ConnectMcsAsync()
    {
        var mcs = new McsSim();
        mcs.Connect("127.0.0.1", MultiPlcFixture.HsmsPort);
        await mcs.EstablishAsync();
        return mcs;
    }

    [Fact]
    public async Task FifteenPlcs_AllEvents_RoutedCorrectly()
    {
        using var mcs = await ConnectMcsAsync();
        // 每台 PLC 站口 1 放入 → 15 个 204，载具 ID 各自正确。
        // 不设中间停留：轮询器看到 0→1 直跳同样触发 204（ID 在状态变化时读），事件语义不变——消除 400ms pacing 的时序依赖
        for (int i = 0; i < MultiPlcFixture.PlcCount; i++)
        {
            _fx.Plcs[i].SetCarrierId(1, $"C{i + 1:000}");
            _fx.Plcs[i].SetStationState(1, 1);
        }

        Assert.Equal(MultiPlcFixture.PlcCount, await mcs.WaitForEventCountAsync(204, MultiPlcFixture.PlcCount, 10000));

        // S1F3 全量查询：EnhancedCarriers 反映 15 台实况
        var items = await mcs.QuerySvidAsync(15);
        Assert.Equal(MultiPlcFixture.PlcCount, items[0].Children!.Count);
        Assert.Contains(items[0].Children!, c => c.Children![0].AsString() == "C007");
    }

    [Fact]
    public async Task SinglePlcDisconnect_OthersUnaffected()
    {
        using var mcs = await ConnectMcsAsync();
        // 掐断 5 号 PLC → 其余 14 台事件照常
        _fx.Plcs[4].DropConnection();
        var dropDeadline = Environment.TickCount64 + 5000;
        while (_fx.Plcs[4].ClientCount > 0 && Environment.TickCount64 < dropDeadline) await Task.Delay(50);   // 等 BufferC 侧连接断开（条件替代 1500ms 固定等）
        for (int i = 0; i < MultiPlcFixture.PlcCount; i++)
        {
            if (i == 4) continue;
            _fx.Plcs[i].SetCarrierId(2, $"D{i + 1:000}");
            _fx.Plcs[i].SetStationState(2, 1);   // 0→1 直跳同样触发 204（与 FifteenPlcs 同理，去 pacing）
        }
        Assert.Equal(MultiPlcFixture.PlcCount - 1, await mcs.WaitForEventCountAsync(204, MultiPlcFixture.PlcCount - 1, 10000));
    }

    [Fact]
    public async Task S1F3_FullQuery_Performance()
    {
        using var mcs = await ConnectMcsAsync();
        // 全量查询（5 个 SVID，含 240 站口视图组装）性能：目标 < 3s（联调口径），实测应 < 300ms
        var sw = Stopwatch.StartNew();
        var items = await mcs.QuerySvidAsync(14, 21, 15, 29, 3);
        sw.Stop();
        Assert.Equal(5, items.Count);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"S1F3 全量查询耗时 {sw.ElapsedMilliseconds}ms");
    }
}
