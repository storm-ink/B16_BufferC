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

    /// <summary>启动前配置校验（PLC 配置/端口冲突），返回错误列表</summary>
    public static List<string> Validate(BufferCConfig cfg)
    {
        var errors = new List<string>();
        var seen = new HashSet<int>();
        foreach (var p in cfg.Plcs)
        {
            if (p.Index is < 1 or > 15) errors.Add($"PLC index {p.Index} 越界（允许 1~15）");
            if (!seen.Add(p.Index)) errors.Add($"PLC index {p.Index} 重复");
            if (string.IsNullOrWhiteSpace(p.Ip)) errors.Add($"PLC{FormatIdx(p)} IP 为空");
            if (p.Port is < 1 or > 65535) errors.Add($"PLC{FormatIdx(p)} 端口非法: {p.Port}");
        }
        if (cfg.Hsms.ListenPort is < 1 or > 65535) errors.Add($"HSMS 端口非法: {cfg.Hsms.ListenPort}");
        if (cfg.WebPort is < 0 or > 65535) errors.Add($"Web 端口非法: {cfg.WebPort}");
        if (cfg.WebPort > 0 && cfg.WebPort == cfg.Hsms.ListenPort)
            errors.Add($"Web 端口 {cfg.WebPort} 与 HSMS 端口 {cfg.Hsms.ListenPort} 冲突");
        return errors;
    }

    private static string FormatIdx(PlcConfig p) => p.Index == 0 ? "(index 缺失)" : p.Index.ToString();

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
    public string SvidCarrierFormat { get; set; } = "l4";  // S1F4 SVID15 结构：l4=L,4(ID,Loc,Time,State) / l2=L,2(ID,Loc)（现场按 MCS 解析确认）
    public bool LogFrames { get; set; } = true;            // 协议帧日志（联调期开，运行期可关）
}
