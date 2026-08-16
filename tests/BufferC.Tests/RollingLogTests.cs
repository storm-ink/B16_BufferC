using System.Text;
using BufferC.Core;
using BufferC.Core.Config;
using Xunit;

namespace BufferC.Tests;

/// <summary>按天滚动日志单测（2026-08-17 日志梳理）：Clock 拨动日期 → 切文件 / 同日 append / 过期清理</summary>
public class RollingLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rolltest-{Guid.NewGuid():N}");

    public RollingLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private BufferCConfig Cfg() => new()
    {
        LogFile = Path.Combine(_dir, "test.log"),
        AuditFile = Path.Combine(_dir, "test.audit.log"),
        Hsms = new HsmsConfig { ListenPort = 0 },   // 清理测试需 Start()：监听临时端口不占 5000
    };

    [Fact]
    public void Rollover_CreatesDailyFile_PrevFileStopsGrowing()
    {
        var svc = new BufferCService(Cfg());
        var d1 = new DateTime(2026, 8, 17, 23, 59, 0);
        var d2 = new DateTime(2026, 8, 18, 0, 1, 0);
        svc.Clock = () => d1;
        svc.Log("T", "day1");
        svc.Clock = () => d2;
        svc.Log("T", "day2");
        svc.Dispose();

        var f1 = File.ReadAllText(Path.Combine(_dir, "test-20260817.log"));
        var f2 = File.ReadAllText(Path.Combine(_dir, "test-20260818.log"));
        Assert.Contains("day1", f1);
        Assert.DoesNotContain("day2", f1);   // 旧文件不再增长
        Assert.Contains("day2", f2);
    }

    [Fact]
    public void SameDay_RestartAppends_NoBomInMiddle()
    {
        var day = new DateTime(2026, 8, 17, 10, 0, 0);
        var svc1 = new BufferCService(Cfg());
        svc1.Clock = () => day;
        svc1.Log("T", "first");
        svc1.Dispose();

        var svc2 = new BufferCService(Cfg());
        svc2.Clock = () => day;
        svc2.Log("T", "second");
        svc2.Dispose();

        var path = Path.Combine(_dir, "test-20260817.log");
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);       // append 不覆盖
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        // BOM 只出现在文件头一次（同日重启 append 不得再写入文件中间）
        Assert.Equal(1, CountBom(bytes));
    }

    private static int CountBom(byte[] hay)
    {
        int cnt = 0;
        for (int i = 0; i + 3 <= hay.Length; i++)
            if (hay[i] == 0xEF && hay[i + 1] == 0xBB && hay[i + 2] == 0xBF) cnt++;
        return cnt;
    }

    [Fact]
    public void Cleanup_DeletesOlderThanRetention_KeepsRecentAndUnrelated()
    {
        var today = new DateTime(2026, 8, 17, 10, 0, 0);
        for (int i = 1; i <= 9; i++)
            File.WriteAllText(Path.Combine(_dir, $"test-{today.AddDays(-i):yyyyMMdd}.log"), "old");
        for (int i = 1; i <= 9; i++)
            File.WriteAllText(Path.Combine(_dir, $"test.audit-{today.AddDays(-i):yyyyMMdd}.log"), "old");
        File.WriteAllText(Path.Combine(_dir, "test-README.log"), "keep");   // 无 8 位日期：不得误删

        var svc = new BufferCService(Cfg());
        svc.Clock = () => today;
        svc.Start();    // 启动清理
        svc.Dispose();

        // 保留 7 天：今天-7 保留、今天-8 及更早删除
        Assert.True(File.Exists(Path.Combine(_dir, $"test-{today.AddDays(-7):yyyyMMdd}.log")));
        Assert.False(File.Exists(Path.Combine(_dir, $"test-{today.AddDays(-8):yyyyMMdd}.log")));
        Assert.False(File.Exists(Path.Combine(_dir, $"test-{today.AddDays(-9):yyyyMMdd}.log")));
        // 审计文件同样清理、主日志清理不误伤审计
        Assert.True(File.Exists(Path.Combine(_dir, $"test.audit-{today.AddDays(-7):yyyyMMdd}.log")));
        Assert.False(File.Exists(Path.Combine(_dir, $"test.audit-{today.AddDays(-8):yyyyMMdd}.log")));
        // 防御：无日期文件不动
        Assert.True(File.Exists(Path.Combine(_dir, "test-README.log")));
    }
}
