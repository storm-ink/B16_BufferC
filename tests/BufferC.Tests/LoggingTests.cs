using System.Text.RegularExpressions;
using BufferC.Core;
using BufferC.Core.Config;
using BufferC.Core.Core;
using Xunit;

namespace BufferC.Tests;

/// <summary>日志核心单测（2026-08-17 日志梳理）：五档过滤 / 新行格式 / 多行折叠 / BOM / Audit 通道</summary>
public class LoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"logtest-{Guid.NewGuid():N}");
    private BufferCService? _svc;

    public LoggingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _svc?.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private BufferCService NewService(string? auditFile = null)
    {
        _svc = new BufferCService(new BufferCConfig
        {
            LogFile = Path.Combine(_dir, "test.log"),
            AuditFile = auditFile ?? Path.Combine(_dir, "test.audit.log"),
        });
        return _svc;
    }

    [Fact]
    public void FiveLevels_FilteredByMinLevel()
    {
        var svc = NewService();
        // 默认 info：error/warn/info 放行、debug/trace 过滤
        svc.Log("T", "err", LogLevel.Error);
        svc.Log("T", "warn", LogLevel.Warn);
        svc.Log("T", "info", LogLevel.Info);
        svc.Log("T", "dbg", LogLevel.Debug);
        svc.Log("T", "trc", LogLevel.Trace);
        var all = svc.GetLogs(100);
        Assert.Contains(all, l => l.Message == "err");
        Assert.Contains(all, l => l.Message == "warn");
        Assert.Contains(all, l => l.Message == "info");
        Assert.DoesNotContain(all, l => l.Message == "dbg");
        Assert.DoesNotContain(all, l => l.Message == "trc");

        // 运行时切 error：只记 error
        Assert.True(svc.SetRuntimeLogLevel("error"));
        svc.Log("T", "err2", LogLevel.Error);
        svc.Log("T", "info2", LogLevel.Info);
        var onlyErr = svc.GetLogs(100);
        Assert.Contains(onlyErr, l => l.Message == "err2");
        Assert.DoesNotContain(onlyErr, l => l.Message == "info2");

        // warn 合法、bogus 非法
        Assert.True(svc.SetRuntimeLogLevel("warn"));
        Assert.False(svc.SetRuntimeLogLevel("bogus"));
    }

    [Fact]
    public void LineFormat_HasDateLevelSeq_AndFoldsMultiline()
    {
        var day = new DateTime(2026, 8, 17, 9, 30, 0, 123);
        var svc = NewService();
        svc.Clock = () => day;
        svc.Log("PLC1", "line1\nline2");
        svc.Log("HSMS", "plain", LogLevel.Warn);
        svc.Dispose();
        _svc = null;

        var path = Path.Combine(_dir, "test-20260817.log");
        Assert.True(File.Exists(path));
        // UTF-8 BOM 在文件头（PowerShell 5.1 现场查看兼容）
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);   // 多行消息折叠为一行
        var rx = new Regex(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]\[(ERROR|WARN |INFO |DEBUG|TRACE)\]\[[^\]\[]+\]\[#\d{6}\] ");
        Assert.Matches(rx, lines[0]);
        Assert.Contains("[2026-08-17 09:30:00.123][INFO ][PLC1]", lines[0]);
        Assert.Contains("line1 line2", lines[0]);                       // \n 折叠为空格
        Assert.Contains("[WARN ][HSMS]", lines[1]);
    }

    [Fact]
    public void Audit_WritesAuditFileAndRing_NotMainLog()
    {
        var svc = NewService();
        var date = svc.Clock().ToString("yyyyMMdd");
        svc.Audit("←MCS", "S1F3W sys=47");
        svc.Audit("CMD", "PLC1 站口3 命令完成: CarrierDataInstall BOX001（123ms）");
        svc.Dispose();
        _svc = null;

        var auditLines = File.ReadAllLines(Path.Combine(_dir, $"test.audit-{date}.log"));
        Assert.Contains(auditLines, l => l.Contains("[AUDIT][←MCS]") && l.Contains("S1F3W sys=47"));
        Assert.Contains(auditLines, l => l.Contains("[AUDIT][CMD]"));
        // 不写主日志文件
        Assert.False(File.Exists(Path.Combine(_dir, $"test-{date}.log")));
        // 环形可见（Web /api/logs 面板 + category 过滤）
        Assert.Contains(svc.GetLogs(100), l => l.Category == "←MCS" && l.Level == LogLevel.Info);
    }

    [Fact]
    public void Audit_DisabledWhenAuditFileEmpty()
    {
        var svc = NewService(auditFile: "");
        svc.Audit("CMD", "x");
        Assert.DoesNotContain(svc.GetLogs(100), l => l.Category == "CMD");
    }

    [Fact]
    public void ConfigValidate_AcceptsFiveLevels_RejectsRetentionOutOfRange()
    {
        var ok = new BufferCConfig { LogLevel = "warn", LogRetentionDays = 365 };
        Assert.Empty(BufferCConfig.Validate(ok));
        var badLevel = new BufferCConfig { LogLevel = "verbose" };
        Assert.Contains(BufferCConfig.Validate(badLevel), e => e.Contains("logLevel"));
        var badDays = new BufferCConfig { LogRetentionDays = 0 };
        Assert.Contains(BufferCConfig.Validate(badDays), e => e.Contains("logRetentionDays"));
    }
}
