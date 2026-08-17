using System.Text.Json;
using System.Text.Json.Serialization;
using BufferC.Core.Modbus;

namespace BufferC.Core.Config;

/// <summary>BufferC 配置（JSON 外部化）</summary>
public sealed class BufferCConfig
{
    public List<PlcConfig> Plcs { get; set; } = new();
    public HsmsConfig Hsms { get; set; } = new();
    public double PollIntervalMs { get; set; } = 500;      // 快速轮询周期
    public int EchoTimeoutMs { get; set; } = 5000;         // 命令回显超时
    public int EchoRetryCount { get; set; } = 1;           // 超时重试次数
    public int EchoPollIntervalMs { get; set; } = 100;     // 回显等待轮询粒度（D6 配置化）
    public int ReconnectMaxBackoffMs { get; set; } = 30_000;   // 断线重连退避封顶（D6 配置化）
    public int HistoryRetentionRows { get; set; } = 2000;  // 历史表每表保留行数（D6 配置化）
    public int DebugReadChunkWords { get; set; } = 16;     // Web 调试读（/api/debug/regread）每帧字数：现场 PLC 对长帧读慢/异常，默认 16 字循环短读（轮询器同款已验证）
    public string LogFile { get; set; } = "bufferc.log";
    public int LogRetentionDays { get; set; } = 7;           // 滚动日志保留天数（按天滚动，启动与日期翻转时清理过期文件）
    public string? AuditFile { get; set; } = "bufferc.audit.log";   // 审计日志基名（SECS 收发摘要/命令生命周期）；null/空=审计通道关闭
    public string? DbPath { get; set; }                    // 库存台账 SQLite 路径（null=内存模式）
    public int WebPort { get; set; }                       // Web 界面端口（0=不启动）
    public string LogLevel { get; set; } = "info";         // error|warn|info|debug|trace（启动最小级别；联调可 /api/debug 运行时切换）
    public bool LogModbusFrames { get; set; }              // Modbus TX/RX 帧日志（trace 级，默认关；联调开）
    public bool ReconcileStrict { get; set; }              // 对账严格模式：true=InventoryDataSend 时删除 MCS 未包含的本地条目；false=只记差异（默认保守）
    public AgvcConfig Agvc { get; set; } = new();          // AGVC HTTP 集成（baseUrl 空=出站调用禁用）

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
        if (!new[] { "error", "warn", "info", "debug", "trace" }.Contains(cfg.LogLevel.ToLowerInvariant()))
            errors.Add($"logLevel 非法: {cfg.LogLevel}（允许 error/warn/info/debug/trace）");
        if (cfg.LogRetentionDays is < 1 or > 365)
            errors.Add($"logRetentionDays 非法: {cfg.LogRetentionDays}（允许 1~365）");
        if (cfg.EchoPollIntervalMs is < 10 or > 1000)
            errors.Add($"echoPollIntervalMs 非法: {cfg.EchoPollIntervalMs}（允许 10~1000）");
        if (cfg.ReconnectMaxBackoffMs is < 1000 or > 300_000)
            errors.Add($"reconnectMaxBackoffMs 非法: {cfg.ReconnectMaxBackoffMs}（允许 1000~300000）");
        if (cfg.HistoryRetentionRows is < 100 or > 100_000)
            errors.Add($"historyRetentionRows 非法: {cfg.HistoryRetentionRows}（允许 100~100000）");
        if (cfg.DebugReadChunkWords is < 1 or > 125)
            errors.Add($"debugReadChunkWords 非法: {cfg.DebugReadChunkWords}（允许 1~125，Modbus 单帧上限）");
        if (cfg.Agvc.CmsIndexBase <= RegisterMap.StationsPerPlc)
            errors.Add($"agvc.cmsIndexBase {cfg.Agvc.CmsIndexBase} 非法（必须大于站口数 {RegisterMap.StationsPerPlc}，否则站口位溢出到机台位）");
        if (cfg.Agvc.CmsIndexBase > 1_000_000)
            errors.Add($"agvc.cmsIndexBase {cfg.Agvc.CmsIndexBase} 过大（上限 1000000）");
        if (cfg.Agvc.TimeoutSec is < 1 or > 60) errors.Add($"agvc.timeoutSec {cfg.Agvc.TimeoutSec} 非法（允许 1~60）");
        if (cfg.Agvc.RetryCount is < 0 or > 10) errors.Add($"agvc.retryCount {cfg.Agvc.RetryCount} 非法（允许 0~10）");
        if (cfg.Agvc.ArrivalGraceMs is < 0 or > 60_000) errors.Add($"agvc.arrivalGraceMs {cfg.Agvc.ArrivalGraceMs} 非法（允许 0~60000）");
        if (!string.IsNullOrWhiteSpace(cfg.Agvc.BaseUrl)
            && (!Uri.TryCreate(cfg.Agvc.BaseUrl, UriKind.Absolute, out var agvcUri)
                || agvcUri.Scheme is not ("http" or "https")))
            errors.Add($"agvc.baseUrl 非法: {cfg.Agvc.BaseUrl}（需 http/https 绝对地址）");
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
    public int UnitLimit { get; set; } = 0;                // S1F4 SVID29 单位列表上限（0=全部；联调期可先只报前 N 个）
    public bool LogFrames { get; set; } = true;            // 协议帧日志（联调期开，运行期可关）
}

/// <summary>AGVC HTTP 集成（仅出站 queryMachines 取货物ID；入站接口已移除 2026-08-14）</summary>
public sealed class AgvcConfig
{
    public string? BaseUrl { get; set; }                   // AGVC 服务器地址（空=出站调用禁用）
    public int TimeoutSec { get; set; } = 5;               // 出站 HTTP 超时
    public int RetryCount { get; set; } = 3;               // 首次失败后重试次数（总尝试 = 1+RetryCount）
    public int RetryIntervalMs { get; set; } = 2000;       // 重试间隔
    public int CmsIndexBase { get; set; } = 10000;         // cmsIndex = 机台号×base + 站口号（10001=1号机台1号站口）
    public int ArrivalGraceMs { get; set; } = 3000;        // 站口 2→1 后等扫码握手(340)的窗口；0=不等待
}
