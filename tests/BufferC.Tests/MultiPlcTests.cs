using System.Diagnostics;
using BufferC.Core;
using BufferC.Core.Config;
using BufferC.Harness;
using Xunit;

namespace BufferC.Tests;

/// <summary>
/// 15 台 PLC 并发集成验证（开发文档 §3 架构核心假设）：
/// 全量事件路由、单台断线隔离、S1F3 全量查询性能。
/// </summary>
public sealed class MultiPlcTests : IAsyncLifetime
{
    private const int PlcCount = 15;
    private readonly List<PlcSim> _plcs = new();
    private BufferCService _svc = null!;
    private McsSim _mcs = null!;

    public async Task InitializeAsync()
    {
        var cfg = new BufferCConfig { Hsms = new HsmsConfig { ListenPort = 5010 }, PollIntervalMs = 100, LogFile = "test-bufferc.log" };
        for (int i = 1; i <= PlcCount; i++)
        {
            var plc = new PlcSim(index: i, unitId: 1);
            int port = plc.Start();
            _plcs.Add(plc);
            cfg.Plcs.Add(new PlcConfig { Index = i, Ip = "127.0.0.1", Port = port, UnitId = 1, ByteOrder = "high" });
        }
        _svc = new BufferCService(cfg);
        _svc.Start();
        _mcs = new McsSim();
        _mcs.Connect("127.0.0.1", 5010);
        await _mcs.EstablishAsync();
        await Task.Delay(500);   // 等 15 台全部基线
    }

    public Task DisposeAsync()
    {
        _mcs.Dispose();
        _svc.Dispose();
        foreach (var p in _plcs) p.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FifteenPlcs_AllEvents_RoutedCorrectly()
    {
        // 每台 PLC 站口 1 放入 → 15 个 204，载具 ID 各自正确
        for (int i = 0; i < PlcCount; i++)
        {
            _plcs[i].SetCarrierId(1, $"C{i + 1:000}");
            _plcs[i].SetStationState(1, 2);
        }
        await Task.Delay(400);
        for (int i = 0; i < PlcCount; i++) _plcs[i].SetStationState(1, 1);

        Assert.Equal(PlcCount, await _mcs.WaitForEventCountAsync(204, PlcCount, 10000));

        // S1F3 全量查询：EnhancedCarriers 反映 15 台实况
        var items = await _mcs.QuerySvidAsync(15);
        Assert.Equal(PlcCount, items[0].Children!.Count);
        Assert.Contains(items[0].Children!, c => c.Children![0].AsString() == "C007");
    }

    [Fact]
    public async Task SinglePlcDisconnect_OthersUnaffected()
    {
        // 掐断 5 号 PLC → 其余 14 台事件照常
        _plcs[4].DropConnection();
        await Task.Delay(1500);   // 5 号走重连退避
        for (int i = 0; i < PlcCount; i++)
        {
            if (i == 4) continue;
            _plcs[i].SetCarrierId(2, $"D{i + 1:000}");
            _plcs[i].SetStationState(2, 2);
        }
        await Task.Delay(400);
        for (int i = 0; i < PlcCount; i++)
        {
            if (i == 4) continue;
            _plcs[i].SetStationState(2, 1);
        }
        Assert.Equal(PlcCount - 1, await _mcs.WaitForEventCountAsync(204, PlcCount - 1, 10000));
    }

    [Fact]
    public async Task S1F3_FullQuery_Performance()
    {
        // 全量查询（5 个 SVID，含 240 站口视图组装）性能：目标 < 3s（联调口径），实测应 < 300ms
        var sw = Stopwatch.StartNew();
        var items = await _mcs.QuerySvidAsync(14, 21, 15, 29, 3);
        sw.Stop();
        Assert.Equal(5, items.Count);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"S1F3 全量查询耗时 {sw.ElapsedMilliseconds}ms");
    }
}
