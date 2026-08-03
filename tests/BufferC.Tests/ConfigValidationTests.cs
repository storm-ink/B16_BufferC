using BufferC.Core.Config;
using Xunit;

namespace BufferC.Tests;

/// <summary>启动配置校验（开发文档 §6）</summary>
public class ConfigValidationTests
{
    private static BufferCConfig Cfg(params PlcConfig[] plcs) => new()
    {
        Plcs = plcs.ToList(),
        Hsms = new HsmsConfig { ListenPort = 5000 },
        WebPort = 0,
    };

    [Fact]
    public void ValidConfig_NoErrors()
    {
        var cfg = Cfg(new PlcConfig { Index = 1, Ip = "192.168.1.10", Port = 502 });
        Assert.Empty(BufferCConfig.Validate(cfg));
    }

    [Fact]
    public void DuplicateIndex_Detected()
    {
        var cfg = Cfg(
            new PlcConfig { Index = 1, Ip = "192.168.1.10", Port = 502 },
            new PlcConfig { Index = 1, Ip = "192.168.1.11", Port = 502 });
        var errors = BufferCConfig.Validate(cfg);
        Assert.Contains(errors, e => e.Contains("重复"));
    }

    [Fact]
    public void IndexOutOfRange_Detected()
    {
        var cfg = Cfg(new PlcConfig { Index = 16, Ip = "192.168.1.10", Port = 502 });
        Assert.Contains(BufferCConfig.Validate(cfg), e => e.Contains("越界"));
    }

    [Fact]
    public void EmptyIp_Detected()
    {
        var cfg = Cfg(new PlcConfig { Index = 1, Ip = "", Port = 502 });
        Assert.Contains(BufferCConfig.Validate(cfg), e => e.Contains("IP 为空"));
    }

    [Fact]
    public void WebPortConflict_Detected()
    {
        var cfg = Cfg(new PlcConfig { Index = 1, Ip = "192.168.1.10", Port = 502 });
        cfg.WebPort = 5000;   // 与 HSMS 冲突
        Assert.Contains(BufferCConfig.Validate(cfg), e => e.Contains("冲突"));
    }
}
