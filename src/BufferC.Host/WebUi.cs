using BufferC.Core;
using BufferC.Core.Core;
using BufferC.Core.Modbus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BufferC.Host;

/// <summary>Web 界面 API（开发文档 §2 界面服务层）：状态查看 / 告警确认 / 手动命令 / 调试</summary>
public sealed record ManualCommand(string Cmd, string CarrierId, string CarrierLoc);
public sealed record DebugToggle(bool Enabled);
public sealed record DebugLevelReq(string Level);
public sealed record DebugCmdReq(int Plc, int Station, ushort Cmd, string? CarrierId);
public sealed record DebugRegWrite(int Plc, int Addr, ushort[] Values);

public static class WebUi
{
    public static WebApplication Start(BufferCService service, int port)
    {
        // net6.0 无 CreateSlimBuilder（.NET 8+）：用 CreateBuilder
        var builder = WebApplication.CreateBuilder();
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
        app.MapGet("/api/logs", (HttpContext ctx, int? tail, string? category, string? level) =>
        {
            // level 语义：最大显示级别（info=只 Info，trace=全显示）
            LogLevel? maxLevel = level != null && Enum.TryParse<LogLevel>(level, true, out var lv) ? lv : null;
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

        app.StartAsync().GetAwaiter().GetResult();
        Console.WriteLine($"[Web] 界面已启动 http://0.0.0.0:{port}");
        return app;
    }

    /// <summary>写 JSON 响应（WriteAsJsonAsync 默认 JsonSerializerDefaults.Web，与 Results.Json 行为一致）</summary>
    private static Task Json(HttpContext ctx, object? value, int status = StatusCodes.Status200OK)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(value, cancellationToken: ctx.RequestAborted);
    }
}
