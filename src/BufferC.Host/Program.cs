using System.Runtime.InteropServices;
using BufferC.Core;
using BufferC.Core.Config;
using Microsoft.AspNetCore.Builder;

namespace BufferC.Host;

/// <summary>入口：加载配置 → 启动服务 → 等待退出</summary>
public static class Program
{
    public static void Main(string[] args)
    {
        string cfgPath = args.Length > 0 ? args[0] : "config.json";
        if (!File.Exists(cfgPath))
        {
            Console.Error.WriteLine($"找不到配置文件: {cfgPath}");
            Environment.Exit(2);
        }
        var cfg = BufferCConfig.Load(cfgPath);
        var errors = BufferCConfig.Validate(cfg);
        if (errors.Count > 0)
        {
            Console.Error.WriteLine("配置错误（启动中止）：");
            foreach (var e in errors) Console.Error.WriteLine($"  - {e}");
            Environment.Exit(2);
        }
        using var service = new BufferCService(cfg);
        service.Start();

        WebApplication? web = null;
        if (cfg.WebPort > 0) web = WebUi.Start(service, cfg.WebPort);

        var exit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
        if (OperatingSystem.IsLinux())
        {
            // Linux 下 systemctl stop 发 SIGTERM：不处理则优雅关闭卡死在 exit.Wait()（实测挂 90s+ 被 SIGKILL）
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; exit.Set(); });
        }
        service.Log("SVC", "按 Ctrl+C 退出");
        exit.Wait();
        service.Log("SVC", "收到退出信号，开始停止");
        service.Stop();
        web?.StopAsync().GetAwaiter().GetResult();
        Console.WriteLine("BufferC 已停止");   // Stop 已 dispose 日志 writer：收尾行保持裸 Console（防重建句柄）
    }
}
