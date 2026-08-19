using System.Text;
using System.Text.Json;
using BufferC.Core.Config;

namespace BufferC.Core.Core;

/// <summary>
/// AGVC 出站 HTTP 客户端（方向1）：站口 AGV 放入完成（2→1）且无货物 ID 时，
/// 调 AGVC 的 POST /api/hsmsRpc/queryMachines 查询货物 ID（请求 {cmsIndex}，
/// 响应 data[0].carrierId 直接携带 ID），BufferC 拿到后自行写 PLC（命令1）。
/// 仅日志不告警（最终失败现场人工在 Web 补）。
/// </summary>
public sealed class AgvcClient : IDisposable
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private sealed record QueryResponse(string? Code, string? Message, bool Interrupt, List<QueryData>? Data);
    private sealed record QueryData(string? CarrierId);
    private sealed record PushResponse(string? Code, string? Message, bool Interrupt, string? Data);

    private readonly AgvcConfig _cfg;
    private readonly HttpClient _http;
    private readonly Action<string, string, LogLevel> _log;
    private readonly Action<string, string, string, string, bool>? _onTraffic;

    /// <param name="onTraffic">交互记录回调（联调面板用）：类型/cmsIndex/请求体/最终响应文本/是否成功</param>
    public AgvcClient(AgvcConfig cfg, Action<string, string, LogLevel> log,
        Action<string, string, string, string, bool>? onTraffic = null)
    {
        _cfg = cfg;
        _log = log;
        _onTraffic = onTraffic;
        _http = new HttpClient
        {
            BaseAddress = new Uri(cfg.BaseUrl!),
            Timeout = TimeSpan.FromSeconds(cfg.TimeoutSec),
        };
    }

    /// <summary>
    /// 查询指定 cmsIndex 站口的货物 ID：code=="0" 且 data[0].carrierId 非空 → 返回 ID；
    /// 无结果/中断/传输失败按 RetryCount 重试（间隔 RetryIntervalMs），仅日志；
    /// 最终失败返回 null（该站口保持无 ID，现场人工在 Web 补）。
    /// reqCode 缺省自动生成（时间戳+随机 ≤32 每次唯一——现场要求 2026-08-17，Q6）。
    /// </summary>
    public async Task<string?> QueryCarrierIdAsync(int plc, int station, CancellationToken ct, string? reqCode = null)
    {
        string cmsIndex = (plc * _cfg.CmsIndexBase + station).ToString();
        var payload = JsonSerializer.Serialize(new { cmsIndex, reqCode = reqCode ?? NewReqCode() }, Camel);

        int attempts = 1 + _cfg.RetryCount;
        string lastResp = "";
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                using var resp = await _http.PostAsync("/mpms/api/hsmsRpc/queryMachines",
                    new StringContent(payload, Encoding.UTF8, "application/json"), ct);
                string text = await resp.Content.ReadAsStringAsync(ct);
                lastResp = text;
                var r = JsonSerializer.Deserialize<QueryResponse>(text, CaseInsensitive);
                if (r?.Code == "0" && r.Data is { Count: > 0 } && !string.IsNullOrWhiteSpace(r.Data[0].CarrierId))
                {
                    var id = r.Data[0].CarrierId!;
                    _log("AGVC", $"查询货物 ID {cmsIndex} → \"{id}\"", LogLevel.Info);
                    _onTraffic?.Invoke("queryMachines", cmsIndex, payload, text, true);
                    return id;
                }
                _log("AGVC", $"查询货物 ID {cmsIndex} 无结果（第 {i + 1}/{attempts} 次）: code={r?.Code} message={r?.Message} interrupt={r?.Interrupt} data={(r?.Data?.Count ?? 0)} 条", LogLevel.Warn);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                lastResp = e.Message;
                _log("AGVC", $"查询货物 ID {cmsIndex} 失败（第 {i + 1}/{attempts} 次）: {e.Message}", LogLevel.Warn);
            }
            if (i + 1 < attempts)
            {
                try { await Task.Delay(_cfg.RetryIntervalMs, ct); }
                catch (OperationCanceledException) { _onTraffic?.Invoke("queryMachines", cmsIndex, payload, "已取消", false); return null; }
            }
        }
        _log("AGVC", $"查询货物 ID {cmsIndex} 最终失败（{attempts} 次尝试），该站口保持无 ID，现场人工在 Web 补", LogLevel.Error);
        _onTraffic?.Invoke("queryMachines", cmsIndex, payload, lastResp, false);
        return null;
    }

    /// <summary>
    /// 推送单站口设备状态（变化即推）：service=PLC 34~49 控制状态寄存器原始值、present=状态区有货(1/5)、
    /// trayId=货物ID（inputRequest/outputRequest 字段已按现场要求移除）。失败按 RetryCount 重试，仅日志；成功判定 code=="0"。
    /// </summary>
    public async Task<bool> PushStatusAsync(int plc, int station, ushort serviceReg, bool present, string trayId, CancellationToken ct)
    {
        string cmsIndex = (plc * _cfg.CmsIndexBase + station).ToString();
        // 现场确认：去掉 inputRequest/outputRequest 两个字段（不再发送）；reqCode 每次请求随机值、长度≤32（时间戳+随机数，现场要求 2026-08-17）
        var body = new
        {
            reqCode = NewReqCode(),
            status = new object[]
            {
                new
                {
                    cmsIndex,
                    service = serviceReg.ToString(),
                    present = present ? "1" : "0",
                    trayId,
                }
            }
        };
        var payload = JsonSerializer.Serialize(body, Camel);

        int attempts = 1 + _cfg.RetryCount;
        string lastResp = "";
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                using var resp = await _http.PostAsync("/mpms/api/hsmsRpc/pushDeviceStatusInfo",
                    new StringContent(payload, Encoding.UTF8, "application/json"), ct);
                string text = await resp.Content.ReadAsStringAsync(ct);
                lastResp = text;
                var r = JsonSerializer.Deserialize<PushResponse>(text, CaseInsensitive);
                if (r?.Code == "0")
                {
                    _log("AGVC", $"推送设备状态 {cmsIndex} OK（service={serviceReg} present={(present ? 1 : 0)} trayId=\"{trayId}\"）", LogLevel.Info);
                    _onTraffic?.Invoke("pushDeviceStatusInfo", cmsIndex, payload, text, true);
                    return true;
                }
                _log("AGVC", $"推送设备状态 {cmsIndex} 应答非成功（第 {i + 1}/{attempts} 次）: code={r?.Code} message={r?.Message}", LogLevel.Warn);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                lastResp = e.Message;
                _log("AGVC", $"推送设备状态 {cmsIndex} 失败（第 {i + 1}/{attempts} 次）: {e.Message}", LogLevel.Warn);
            }
            if (i + 1 < attempts)
            {
                try { await Task.Delay(_cfg.RetryIntervalMs, ct); }
                catch (OperationCanceledException) { _onTraffic?.Invoke("pushDeviceStatusInfo", cmsIndex, payload, "已取消", false); return false; }
            }
        }
        _log("AGVC", $"推送设备状态 {cmsIndex} 最终失败（{attempts} 次尝试）", LogLevel.Error);
        _onTraffic?.Invoke("pushDeviceStatusInfo", cmsIndex, payload, lastResp, false);
        return false;
    }

    /// <summary>请求唯一码（时间戳 17 位 + 随机 4 位 = 21 字符 ≤32，每次请求唯一——现场要求 2026-08-17）</summary>
    private static string NewReqCode() => $"{DateTime.Now:yyyyMMddHHmmssfff}{Random.Shared.Next(1000, 9999)}";

    public void Dispose() => _http.Dispose();
}
