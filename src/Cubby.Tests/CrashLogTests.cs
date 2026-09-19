using System.Text;
using Cubby.Core.Diagnostics;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 崩溃日志的落盘、轮转与保留策略测试。
///
/// 这块逻辑的特殊之处在于**它自己不许出问题**：日志写失败不能升级成崩溃。
/// 所以除了"写对了"，还要专门测"写不进去时安静放弃"。
/// </summary>
public sealed class CrashLogTests : IDisposable
{
    private readonly string _directory;

    public CrashLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cubby-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论
        }
    }

    [Fact]
    public void 文件按天命名且可从文件名反解出日期()
    {
        var date = new DateTime(2026, 9, 20);

        var name = CrashLog.FileNameFor(date);

        Assert.Equal("cubby-20260920.log", name);
        Assert.True(CrashLog.TryParseDate(name, out var parsed));
        Assert.Equal(date, parsed);
    }

    [Fact]
    public void 不是日志的文件名不会被误判成日期()
    {
        Assert.False(CrashLog.TryParseDate("layout.json", out _));
        Assert.False(CrashLog.TryParseDate("cubby-notadate.log", out _));
        Assert.False(CrashLog.TryParseDate("other-20260920.log", out _));
    }

    [Fact]
    public void 文件头包含版本_系统与运行时三要素()
    {
        var header = CrashLog.BuildHeader(new DateTime(2026, 9, 20), "1.2.3", "Windows 测试", ".NET 测试");

        Assert.Contains("版本=1.2.3", header);
        Assert.Contains("OS=Windows 测试", header);
        Assert.Contains("运行时=.NET 测试", header);
        Assert.Contains("cubby-20260920.log", header);
    }

    [Fact]
    public void 单行格式含时间戳_级别_来源与完整堆栈()
    {
        var line = CrashLog.Format(
            new DateTime(2026, 9, 20, 1, 2, 3, 456),
            CrashLogLevel.Fatal,
            "AppDomain.UnhandledException",
            "炸了",
            "System.Exception: 炸了\n   在 某方法()");

        Assert.Contains("2026-09-20 01:02:03.456", line);
        Assert.Contains("FATAL", line);
        Assert.Contains("AppDomain.UnhandledException", line);
        Assert.Contains("炸了", line);
        Assert.Contains("在 某方法()", line);
    }

    [Fact]
    public void 摘要里的换行会被压成一行避免撑坏日志行()
    {
        var line = CrashLog.Format(DateTime.Now, CrashLogLevel.Error, "来源", "第一行\r\n第二行");

        Assert.Single(line.Split('\n'));
        Assert.Contains("第一行 第二行", line);
    }

    [Fact]
    public void 第一次写入会带上文件头_后续追加不再重复写头()
    {
        CrashLog.Write(_directory, CrashLogLevel.Error, "来源一", "第一条", timestamp: new DateTime(2026, 9, 20, 10, 0, 0));
        CrashLog.Write(_directory, CrashLogLevel.Error, "来源二", "第二条", timestamp: new DateTime(2026, 9, 20, 11, 0, 0));

        var text = File.ReadAllText(CrashLog.FilePathFor(_directory, new DateTime(2026, 9, 20)), Encoding.UTF8);

        Assert.Equal(1, CountOccurrences(text, "# Cubby 日志"));
        Assert.Contains("第一条", text);
        Assert.Contains("第二条", text);
    }

    [Fact]
    public void 超过保留天数的文件会被清掉()
    {
        var now = new DateTime(2026, 9, 20);
        for (var offset = 0; offset < 20; offset++)
        {
            File.WriteAllText(CrashLog.FilePathFor(_directory, now.AddDays(-offset)), "x");
        }

        var removed = CrashLog.Prune(_directory, retentionDays: 14, now: now);

        var survivors = CrashLog.ListFiles(_directory);
        Assert.Equal(15, survivors.Count); // 今天 + 最近 14 天
        Assert.Equal(5, removed.Count);
        Assert.True(CrashLog.TryParseDate(survivors[0], out var oldestKept));
        Assert.Equal(now.AddDays(-14), oldestKept);
    }

    [Fact]
    public void 保留天数为零只留当天()
    {
        var now = new DateTime(2026, 9, 20);
        for (var offset = 0; offset < 4; offset++)
        {
            File.WriteAllText(CrashLog.FilePathFor(_directory, now.AddDays(-offset)), "x");
        }

        CrashLog.Prune(_directory, retentionDays: 0, now: now);

        var survivors = CrashLog.ListFiles(_directory);
        Assert.Single(survivors);
        Assert.Equal(CrashLog.FileNameFor(now), Path.GetFileName(survivors[0]));
    }

    [Fact]
    public void 总量超额时从最老的开始删()
    {
        var now = new DateTime(2026, 9, 20);
        for (var offset = 0; offset < 10; offset++)
        {
            File.WriteAllText(CrashLog.FilePathFor(_directory, now.AddDays(-offset)), new string('x', 4096));
        }

        // 上限 8KB，10 个 4KB 文件必然超额
        CrashLog.Prune(_directory, retentionDays: 3650, maxTotalBytes: 8 * 1024, now: now);

        var survivors = CrashLog.ListFiles(_directory);
        Assert.True(survivors.Sum(path => new FileInfo(path).Length) <= 8 * 1024);
        Assert.Equal(2, survivors.Count);
        // 剩下的一定是最新的两个（9-20、9-19）
        Assert.Equal(CrashLog.FileNameFor(now), Path.GetFileName(survivors[1]));
        Assert.Equal(CrashLog.FileNameFor(now.AddDays(-1)), Path.GetFileName(survivors[0]));
    }

    [Fact]
    public void 目录不存在时列举与清理都不抛异常()
    {
        var missing = Path.Combine(_directory, "还没建过");

        Assert.Empty(CrashLog.ListFiles(missing));
        Assert.Empty(CrashLog.Prune(missing));
    }

    /// <summary>
    /// 这是本类型最重要的一条：日志路径出问题时必须**安静地失败**。
    /// 让"目录"指向一个已存在的文件，任何创建都会失败——此时 <see cref="CrashLog.Write"/> 必须返回 null 而不是抛异常。
    /// </summary>
    [Fact]
    public void 日志写不进去时安静返回null而不是抛异常()
    {
        var blocked = Path.Combine(_directory, "占位文件");
        File.WriteAllText(blocked, "占住这个名字");

        var result = CrashLog.Write(blocked, CrashLogLevel.Error, "测试", "写不进去");

        Assert.Null(result);
    }

    [Fact]
    public void 保留的文件不会被误删()
    {
        var now = new DateTime(2026, 9, 20);
        File.WriteAllText(CrashLog.FilePathFor(_directory, now), "x");
        File.WriteAllText(Path.Combine(_directory, "layout.json"), "{}");

        CrashLog.Prune(_directory, retentionDays: 14, now: now);

        Assert.True(File.Exists(Path.Combine(_directory, "layout.json")));
        Assert.Single(CrashLog.ListFiles(_directory));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
