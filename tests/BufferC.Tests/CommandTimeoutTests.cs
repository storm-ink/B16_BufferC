using BufferC.Core;
using BufferC.Core.Config;
using BufferC.Harness;
using Xunit;

namespace BufferC.Tests;

/// <summary>
/// 命令超时重试（开发文档 §7.3 ⑱ + 故障注入）：
/// PLC 挂起不回显 → 命令通道 5s 超时（此处 500ms 加速）→ 重试 1 次 → 失败 → 无 201 上报。
/// </summary>
public sealed class CommandTimeoutTests : IAsyncLifetime
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
            Hsms = new HsmsConfig { ListenPort = 5001 },     // 独立端口，避免与场景测试冲突
            PollIntervalMs = 100,
            EchoTimeoutMs = 500,                             // 加速：500ms 超时
            EchoRetryCount = 1,
            LogFile = "test-bufferc.log",
        };
        _svc = new BufferCService(cfg);
        _svc.Start();
        _mcs = new McsSim();
        _mcs.Connect("127.0.0.1", 5001);
        await _mcs.EstablishAsync();
        await Task.Delay(300);
    }

    public Task DisposeAsync()
    {
        _mcs.Dispose();
        _svc.Dispose();
        _plc.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Command_Timeout_Retry_Fail_NoEvent_ThenRecover()
    {
        _plc.CommandHang = true;                            // PLC 挂起：不回显
        var hcack = await _mcs.SendS2F41Async("CarrierDataInstall",
            ("CARRIERID", "T001"), ("CARRIERLOC", "Buffer1_Port3"));
        Assert.Equal(4, hcack);                             // HCACK=4 已确认（异步完成语义）
        var ev = await _mcs.WaitForEventAsync(201, 2000);
        Assert.Null(ev);                                    // 超时+重试失败 → 无 201 上报
        Assert.Equal((ushort)0, _plc.GetEchoNo());          // PLC 从未回显

        _plc.CommandHang = false;                           // 恢复
        var hcack2 = await _mcs.SendS2F41Async("CarrierDataInstall",
            ("CARRIERID", "T002"), ("CARRIERLOC", "Buffer1_Port3"));
        Assert.Equal(4, hcack2);
        var ev2 = await _mcs.WaitForEventAsync(201, 3000);
        Assert.NotNull(ev2);                                // 恢复后正常上报
    }

    [Fact]
    public async Task EchoDelay_BeyondTimeout_Fails()
    {
        // 执行快、回显 2500ms 后才写：两次 500ms 等待都超时 → 命令失败，无 201
        _plc.EchoDelayMs = 2500;
        var hcack = await _mcs.SendS2F41Async("CarrierDataInstall",
            ("CARRIERID", "D001"), ("CARRIERLOC", "Buffer1_Port3"));
        Assert.Equal(4, hcack);
        var ev = await _mcs.WaitForEventAsync(201, 2000);
        Assert.Null(ev);                                    // 无 201 上报
        _plc.EchoDelayMs = 0;
    }

    [Fact]
    public async Task EchoDelay_WithinTimeout_Succeeds()
    {
        _plc.EchoDelayMs = 200;                             // 回显 200ms 后到，在 500ms 超时内 → 成功
        var hcack = await _mcs.SendS2F41Async("CarrierDataInstall",
            ("CARRIERID", "D002"), ("CARRIERLOC", "Buffer1_Port3"));
        Assert.Equal(4, hcack);
        Assert.NotNull(await _mcs.WaitForEventAsync(201, 3000));
        Assert.Equal("D002", _plc.GetCarrierId(3));
        _plc.EchoDelayMs = 0;
    }

    [Fact]
    public async Task CmdFailed_RaisesAlarm9001()
    {
        // 命令失败（C1 补偿）：HCACK=4 已确认 → 失败 → S5F1 内部告警 9001
        _plc.CommandHang = true;
        var hcack = await _mcs.SendS2F41Async("CarrierDataInstall",
            ("CARRIERID", "T900"), ("CARRIERLOC", "Buffer1_Port3"));
        Assert.Equal(4, hcack);
        var ev = await _mcs.WaitForEventAsync(201, 2000);
        Assert.Null(ev);                                    // 无 201
        var s5 = await _mcs.WaitForEventAsync(0, 3000);     // 任意 S5F1（9001 告警）
        Assert.NotNull(s5);
        var l = s5!.Items()[0].Children!;                   // S5F1 = L3[B ALCD, U4 ALID, A ALTX]
        Assert.Equal(BufferC.Core.BufferCService.AlarmCmdFailed, l[1].AsUInt());
        _plc.CommandHang = false;
    }

    [Fact]
    public async Task EchoDrop_Once_RetrySucceeds()
    {
        // 回显丢失：重试以新命令编号重新执行（写 ID 幂等）→ 成功并上报 201
        _plc.EchoDropCount = 1;
        var hcack = await _mcs.SendS2F41Async("CarrierDataInstall",
            ("CARRIERID", "D003"), ("CARRIERLOC", "Buffer1_Port3"));
        Assert.Equal(4, hcack);
        Assert.NotNull(await _mcs.WaitForEventAsync(201, 3000));
        Assert.Equal("D003", _plc.GetCarrierId(3));         // PLC 存储区已写入
        _plc.EchoDropCount = 0;
    }
}
