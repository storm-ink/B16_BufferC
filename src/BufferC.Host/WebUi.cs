using BufferC.Core;
using BufferC.Core.Core;
using BufferC.Core.Modbus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BufferC.Host;

/// <summary>Web 界面 API（开发文档 §2 界面服务层）：状态查看 / 告警确认 / 手动命令 / 调试</summary>
public sealed record ManualCommand(string Cmd, string CarrierId, string CarrierLoc);
public sealed record DebugToggle(bool Enabled);
public sealed record DebugLevelReq(string Level);
public sealed record DebugCmdReq(int Plc, int Station, ushort Cmd, string? CarrierId);
public sealed record DebugRegWrite(int Plc, int Addr, ushort[] Values);
public sealed record DebugCimReq(bool Online);
public sealed record ManualQueryReq(string CmsIndex);
public sealed record ManualPushReq(string CmsIndex, string? Service, string? Present, string? TrayId);

public static class WebUi
{
    public static WebApplication Start(BufferCService service, int port)
    {
        // net6.0 无 CreateSlimBuilder（.NET 8+）：用 CreateBuilder
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();   // 消除 ASP.NET 自带 Console logger（统一走 BufferC 日志管道）
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Services.AddRouting();
        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        // net6.0 minimal API 无 Results/IResult（.NET 7+）：统一走 Json 辅助，状态码用 HttpResponse 控制
        app.MapGet("/api/status", (HttpContext ctx) => Json(ctx, service.GetStatusView()));
        app.MapGet("/api/alarms", (HttpContext ctx) => Json(ctx, service.Alarms));
        app.MapGet("/api/commands", (HttpContext ctx, int? tail) => Json(ctx, new
        {
            history = service.GetCommands(tail ?? 50),        // 命令历史（活动面板）
            pending = service.FailedCommands,                 // 悬空命令（C1）
        }));
        app.MapGet("/api/events", (HttpContext ctx, int? tail) => Json(ctx, service.GetEvents(tail ?? 50)));
        app.MapGet("/api/alarm-history", (HttpContext ctx, int? tail) => Json(ctx, service.GetAlarmHistory(tail ?? 50)));
        app.MapGet("/api/agvc/traffic", (HttpContext ctx, int? tail) => Json(ctx, new { traffic = service.GetAgvcTraffic(tail ?? 50) }));
        // HSMS 收发记录（MCS 联调页：MCS 发来 / 回给 MCS 的每条报文原文）
        app.MapGet("/api/hsms/traffic", (HttpContext ctx, int? tail) => Json(ctx, new { traffic = service.GetHsmsTraffic(tail ?? 50) }));
        // 台账（站口合并视图，表直出）：MCS 联调页「台账」卡片点击刷新
        app.MapGet("/api/inventory", (HttpContext ctx) => Json(ctx, new { stations = service.GetInventoryView() }));
        // AGVC 联调页：两接口手动触发（与自动路径同口径，调用同样进入 traffic 记录）
        // 注意：net6 minimal API 的 async 处理器在 await 后响应已开始，Json 助手设 StatusCode 会抛「Headers are read-only」
        // → 沿用全站同步处理器风格（阻塞式，与 ManualInstall 等命令路径一致）
        // D8：异步处理器消除 sync-over-async（await 后返回 Results.Json、不手动碰 ctx.Response——
        // 08-14 续 9 踩的「Headers are read-only」只在 await 后改 StatusCode 时才触发，此写法安全）
        app.MapPost("/api/agvc/manual/queryMachines", async Task<IResult> (ManualQueryReq req) =>
        {
            var (ok, msg) = await service.ManualQueryMachines(req.CmsIndex);
            return Results.Json(new { ok, message = msg });
        });
        app.MapPost("/api/agvc/manual/pushDeviceStatusInfo", async Task<IResult> (ManualPushReq req) =>
        {
            var (ok, msg) = await service.ManualPushStatus(req.CmsIndex, req.Service, req.Present, req.TrayId);
            return Results.Json(new { ok, message = msg });
        });
        app.MapGet("/api/logs", (HttpContext ctx, int? tail, string? category, string? level) =>
        {
            // level 语义：最大显示级别（error=只 Error，info=到 Info 为止，trace=全显示）
            BufferC.Core.Core.LogLevel? maxLevel =
                level != null && Enum.TryParse<BufferC.Core.Core.LogLevel>(level, true, out var lv) ? lv : null;
            return Json(ctx, service.GetLogs(tail ?? 200, category, maxLevel));
        });
        app.MapPost("/api/alarms/{alid:long}/ack", (HttpContext ctx, long alid) =>
            service.AckAlarm((uint)alid) ? Json(ctx, true) : Json(ctx, null, StatusCodes.Status404NotFound));
        app.MapPost("/api/command", (HttpContext ctx, ManualCommand req) =>
        {
            bool ok = req.Cmd switch
            {
                "install" => service.ManualInstall(req.CarrierId, req.CarrierLoc, "MANUAL"),
                "remove" => service.ManualRemove(req.CarrierId, "MANUAL"),
                _ => false,
            };
            return ok ? Json(ctx, true) : Json(ctx, null, StatusCodes.Status400BadRequest);
        });
        // AGVC 集成仅出站（§D1 方向1）：queryMachines 查询货物 ID 后自行写 PLC（见 AgvcClient/StartAgvcArrivalTimer）；
        // 入站送达通知与站口服务状态两个接口已按现场要求移除（2026-08-14）

        // 联调诊断（调试功能设计）：运行参数 + 统计 + 开关状态；帧日志/日志级别运行时切换（不落盘 config）
        app.MapGet("/api/debug", (HttpContext ctx) => Json(ctx, service.GetDebugInfo()));
        app.MapPost("/api/debug/logframes", (HttpContext ctx, DebugToggle req) =>
        {
            service.SetRuntimeLogFrames(req.Enabled);
            return Json(ctx, true);
        });
        app.MapPost("/api/debug/loglevel", (HttpContext ctx, DebugLevelReq req) =>
            service.SetRuntimeLogLevel(req.Level) ? Json(ctx, true)
                : Json(ctx, null, StatusCodes.Status400BadRequest));

        // PLC 调试面板（现场联调：站口命令直发 + 寄存器直读直写，绕过业务副作用）
        app.MapGet("/api/debug/regread", (HttpContext ctx, int plc, int addr, int count) =>
        {
            var values = service.ReadPlcRegs(plc, addr, count);
            return values != null ? Json(ctx, new { plc, addr, values })
                : Json(ctx, null, StatusCodes.Status400BadRequest);
        });
        app.MapPost("/api/debug/regwrite", (HttpContext ctx, DebugRegWrite req) =>
            service.WritePlcRegs(req.Plc, req.Addr, req.Values) ? Json(ctx, true)
                : Json(ctx, null, StatusCodes.Status400BadRequest));
        app.MapPost("/api/debug/cmd", (HttpContext ctx, DebugCmdReq req) =>
        {
            // 命令码放开（0~65535 任意，JSON 反序列化 ushort 自动挡越界）；仅 1=写入ID 会先写 ID 区
            if (req.Station < 1 || req.Station > 16)
                return Json(ctx, null, StatusCodes.Status400BadRequest);
            var r = service.DebugSendCmd(req.Plc, req.Station, req.Cmd,
                req.Cmd == RegisterMap.CmdWrite ? req.CarrierId : null);
            return Json(ctx, new { ok = r.Ok, elapsedMs = r.ElapsedMs });
        });
        // CIM 状态事件（1.1.4 OFFLINE→ONLINEREMOTE By CIM 自测）：online=false→CEID 1，true→CEID 3
        app.MapPost("/api/debug/cim", (HttpContext ctx, DebugCimReq req) =>
            Json(ctx, new { sent = service.TriggerCimState(req.Online) }));
        // 库存更新请求（1.2.6）：手动触发 S6F11 CEID 502，请求 MCS 下发 InventoryDataSend
        app.MapPost("/api/debug/inventory-request", (HttpContext ctx) =>
            Json(ctx, new { sent = service.TriggerInventoryUpdateRequest() }));

        app.StartAsync().GetAwaiter().GetResult();
        service.Log("Web", $"界面已启动 http://0.0.0.0:{port}");
        return app;
    }

    /// <summary>写 JSON 响应（WriteAsJsonAsync 默认 JsonSerializerDefaults.Web，与 Results.Json 行为一致）</summary>
    private static Task Json(HttpContext ctx, object? value, int status = StatusCodes.Status200OK)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(value, cancellationToken: ctx.RequestAborted);
    }
}
