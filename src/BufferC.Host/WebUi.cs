using BufferC.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BufferC.Host;

/// <summary>Web 界面 API（开发文档 §2 界面服务层）：状态查看 / 告警确认 / 手动命令</summary>
public sealed record ManualCommand(string Cmd, string CarrierId, string CarrierLoc);

public static class WebUi
{
    public static WebApplication Start(BufferCService service, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Services.AddRouting();
        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/status", () => Results.Json(service.GetStatusView()));
        app.MapGet("/api/alarms", () => Results.Json(service.Alarms));
        app.MapGet("/api/commands", (int? tail) => Results.Json(new
        {
            history = service.GetCommands(tail ?? 50),        // 命令历史（活动面板）
            pending = service.FailedCommands,                 // 悬空命令（C1）
        }));
        app.MapGet("/api/events", (int? tail) => Results.Json(service.GetEvents(tail ?? 50)));
        app.MapGet("/api/alarm-history", (int? tail) => Results.Json(service.GetAlarmHistory(tail ?? 50)));
        app.MapGet("/api/logs", (int? tail, string? category) =>
            Results.Json(service.GetLogs(tail ?? 200, category)));
        app.MapPost("/api/alarms/{alid:long}/ack", (long alid) =>
            service.AckAlarm((uint)alid) ? Results.Ok() : Results.NotFound());
        app.MapPost("/api/command", (ManualCommand req) =>
        {
            bool ok = req.Cmd switch
            {
                "install" => service.ManualInstall(req.CarrierId, req.CarrierLoc, "MANUAL"),
                "remove" => service.ManualRemove(req.CarrierId, "MANUAL"),
                _ => false,
            };
            return ok ? Results.Ok() : Results.BadRequest();
        });
        // AGVC 送达通知（§4.6）：字段名 carrierId/carrierLoc 待 D1 对齐；写入幂等
        app.MapPost("/api/agvc/delivery", (ManualCommand req) =>
            service.ManualInstall(req.CarrierId, req.CarrierLoc, "AGVC") ? Results.Ok() : Results.BadRequest());

        app.StartAsync().GetAwaiter().GetResult();
        Console.WriteLine($"[Web] 界面已启动 http://0.0.0.0:{port}");
        return app;
    }
}
