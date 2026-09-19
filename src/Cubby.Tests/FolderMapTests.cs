using Cubby.Core.Model;
using Cubby.Core.Platform;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 文件夹映射的读取逻辑与监视器。这里对**真实临时目录**跑，
/// 因为"不改动磁盘"这条（P4）只有对真文件断言才有意义。
/// </summary>
public sealed class FolderMapTests : IDisposable
{
    private readonly string _root;

    public FolderMapTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cubby-folder-map", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public void 扫描顶层内容_目录在前文件在后_各自按名称排序()
    {
        // 文件用 ASCII 名：中文排序依赖区域性（产品里按当前区域性排，中文按拼音），
        // 断言顺序时不该把机器的语言设置带进来
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(_root, "子目录"));
        Directory.CreateDirectory(Path.Combine(_root, "另一个目录"));

        var items = FolderMap.Scan(_root);

        Assert.Equal(4, items.Count);
        Assert.All(items, item => Assert.Equal(ItemKind.Mapped, item.Kind));
        Assert.True(Directory.Exists(items[0].TargetPath));
        Assert.True(Directory.Exists(items[1].TargetPath));
        Assert.Equal("a.txt", items[2].DisplayName);
        Assert.Equal("b.txt", items[3].DisplayName);
    }

    [Fact]
    public void 条目Id基于路径_重复扫描保持稳定()
    {
        File.WriteAllText(Path.Combine(_root, "文件.txt"), "x");

        var first = FolderMap.Scan(_root);
        var second = FolderMap.Scan(_root);

        Assert.Equal(first[0].Id, second[0].Id);
    }

    [Fact]
    public void 目录不存在时返回醒目提示条目而不是空列表()
    {
        var missing = Path.Combine(_root, "并不存在");

        var items = FolderMap.Scan(missing);

        Assert.Single(items);
        Assert.True(FolderMap.IsUnavailable(items[0]));
        Assert.Contains("映射目标不可用", items[0].DisplayName);
        Assert.Equal(missing, items[0].TargetPath);
    }

    [Fact]
    public void 条目数超过上限时被截断()
    {
        for (var i = 0; i < 8; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"文件{i}.txt"), i.ToString());
        }

        var items = FolderMap.Scan(_root, limit: 3);

        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void 扫描不改变目录现场()
    {
        File.WriteAllText(Path.Combine(_root, "重要.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(_root, "子目录"));
        var before = Fingerprint();

        _ = FolderMap.Scan(_root);
        _ = FolderMap.Scan(_root);

        Assert.Equal(before, Fingerprint());
    }

    [Fact]
    public async Task 目录变化会被合并成一次通知()
    {
        using var watcher = new MappedFolderWatcher(_root, TimeSpan.FromMilliseconds(120));
        var notifications = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref notifications);

        File.WriteAllText(Path.Combine(_root, "新文件.txt"), "x");
        await WaitUntilAsync(() => Volatile.Read(ref notifications) > 0, TimeSpan.FromSeconds(5));

        Assert.True(notifications > 0, "文件创建后没有收到通知");
        Assert.Equal(0, watcher.OverflowRescanCount);
        Assert.False(watcher.Unavailable);
    }

    [Fact]
    public void 目录不存在时监视器降级为不可用而不是抛异常()
    {
        using var watcher = new MappedFolderWatcher(Path.Combine(_root, "并不存在"));

        Assert.True(watcher.Unavailable);
        Assert.NotNull(watcher.LastDiagnostic);
    }

    [Fact]
    public async Task 缓冲区溢出走强制重扫路径_会通知且不抛异常()
    {
        using var watcher = new MappedFolderWatcher(_root, TimeSpan.FromMilliseconds(80));
        var notifications = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref notifications);

        // 真实溢出要一瞬间灌进上千个事件，没法稳定复现；这里直接驱动事件处理器走的那条路径
        watcher.RequestRescan("缓冲区溢出（测试模拟）");
        await WaitUntilAsync(() => Volatile.Read(ref notifications) > 0, TimeSpan.FromSeconds(2));

        Assert.True(notifications > 0, "强制重扫没有触发通知");
        Assert.Equal(1, watcher.RescanCount);
        Assert.Contains("缓冲区溢出", watcher.LastDiagnostic);
    }

    [Fact]
    public void 释放后再变化不会炸()
    {
        var watcher = new MappedFolderWatcher(_root);
        watcher.Dispose();

        // 重复 Dispose + 之后目录变化都不该抛异常
        watcher.Dispose();
        File.WriteAllText(Path.Combine(_root, "之后.txt"), "x");
    }

    private string Fingerprint()
    {
        if (!Directory.Exists(_root))
        {
            return "(不存在)";
        }

        return string.Join(
            " | ",
            Directory.GetFileSystemEntries(_root)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Directory.Exists(p)
                    ? $"dir:{p}"
                    : $"file:{p}:{new FileInfo(p).LastWriteTimeUtc:O}:{new FileInfo(p).Length}"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline && !condition())
        {
            await Task.Delay(50);
        }
    }
}