using System.Text.Json;
using System.Text.Json.Serialization;

namespace BufferC.Core.Config;

/// <summary>BufferC 配置（JSON 外部化）</summary>
public sealed class BufferCConfig
{
    public List<PlcConfig> Plcs { get; set; } = new();
    public HsmsConfig Hsms { get; set; } = new();
    public double PollIntervalMs { get; set; } = 500;      // 快速轮询周期
    public int EchoTimeoutMs { get; set; } = 5000;         // 命令回显超时
    public int EchoRetryCount { get; set; } = 1;           // 超时重试次数
    public string LogFile { get; set; } = "bufferc.log";
    public string? DbPath { get; set; }                    // 库存台账 SQLite 路径（null=内存模式）
    public int WebPort { get; set; }                       // Web 界面端口（0=不启动）

    public static BufferCConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BufferCConfig>(json, JsonOpts) ?? new BufferCConfig();
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class PlcConfig
{
    public int Index { get; set; }                         // 1~15
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 502;
    public byte UnitId { get; set; } = 1;                  // 现场确认（1/255）
    public string ByteOrder { get; set; } = "high";        // 货物ID 字内字节序: high=高字节在前
    public int TimeoutMs { get; set; } = 3000;
    public uint LastSeq { get; set; }
}

public sealed class HsmsConfig
{
    public int ListenPort { get; set; } = 5000;
    public string Mdln { get; set; } = "BUFFERC";
    public string SoftRev { get; set; } = "0.1.0";
    public int T3Ms { get; set; } = 45_000;
}
