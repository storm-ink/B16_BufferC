using BufferC.Core;
using BufferC.Core.Config;
using BufferC.Harness;
using Xunit;

namespace BufferC.Tests;

/// <summary>
/// 压力冒烟（开发文档 §7 故障注入/长稳的短时版）：
/// 事件风暴 / 告警风暴去重 / 迷你长稳（连续轮询 + 随机变化）。
/// logFrames=false 验证开关生效并加速测试。
/// </summary>
public sealed class StressTests : IAsyncLifetime
{
    private PlcSim _plc = null!;
    private BufferCService _svc = null!;
    private McsSim _mcs = null!;

    public async Task InitializeAsync()
    {
        _plc = new PlcSim(index: 1, unitId: 1);
        int port = _plc.Start();
        var cfg = new BufferCConfig
        {
            Plcs = { new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = port, UnitId = 1, ByteOrder = "high" } },
            Hsms = new HsmsConfig { ListenPort = 5011, LogFrames = false },
            PollIntervalMs = 100,
            LogFile = "test-bufferc.log",
        };
        _svc = new BufferCService(cfg);
        _svc.Start();
        _mcs = new McsSim();
        _mcs.Connect("127.0.0.1", 5011);
        await _mcs.EstablishAsync();
        var deadline = Environment.TickCount64 + 10000;
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
    public async Task EventStorm_30Rounds_StateConsistent()
    {
        // 16 站口 × 30 轮 放入/取出 风暴（含中间态被轮询跳过的极端时序）
        for (int round = 0; round < 30; round++)
        {
            for (int st = 1; st <= 16; st++) _plc.SetStationState(st, 2);
            await Task.Delay(30);
            for (int st = 1; st <= 16; st++) _plc.SetStationState(st, 1);
            await Task.Delay(30);
            for (int st = 1; st <= 16; st++) _plc.SetStationState(st, 0);
            await Task.Delay(30);
        }
        // 风暴结束：最终状态一致（S1F3 查询 EnhancedCarriers 为空）
        var items = await _mcs.QuerySvidAsync(15);
        Assert.Empty(items[0].Children!);
    }

    [Fact]
    public async Task AlarmStorm_16Stations_Dedup()
    {
        // 16 站口同时同告警码 → SC 级 102 只报 1 次，402 逐单位
        for (int st = 1; st <= 16; st++) _plc.SetStationAlarm(st, 42);
        Assert.Equal(1, await _mcs.WaitForEventCountAsync(102, 1, 5000));
        // 全部清除 → 101 只报 1 次
        for (int st = 1; st <= 16; st++) _plc.SetStationAlarm(st, 0);
        Assert.Equal(1, await _mcs.WaitForEventCountAsync(101, 1, 5000));
    }

    [Fact]
    public async Task ContinuousRandomChanges_15s_NoException()
    {
        // 迷你长稳：15s 连续轮询 + 随机状态变化，服务保持响应
        var rand = new Random(42);
        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline)
        {
            int st = rand.Next(1, 17);
            _plc.SetStationState(st, (ushort)rand.Next(0, 4));
            _plc.SetStationAlarm(st, (ushort)(rand.Next(0, 2) == 0 ? 0 : rand.Next(1, 10)));
            await Task.Delay(50);
        }
        var items = await _mcs.QuerySvidAsync(14);   // 服务仍正常响应
        Assert.Single(items);
    }
}
