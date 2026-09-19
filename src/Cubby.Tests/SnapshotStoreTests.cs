using Cubby.Core.Model;
using Cubby.Core.Storage;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 布局快照。重点在三处：快照与布局用同一套序列化（否则将来迁移会夹生）、
/// 保留策略真的按时间砍老快照、坏快照只影响它自己。
/// </summary>
public sealed class SnapshotStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly SnapshotStore _store;

    public SnapshotStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cubby-snapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new SnapshotStore(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }

    private static LayoutDocument Sample(string boxName = "工作区") => new()
    {
        Boxes =
        [
            new Box("b1", boxName, new DipRect(60, 80, 420, 300))
            {
                MonitorId = @"\\.\DISPLAY1",
                Items = [new BoxItem("i1", "需求.md", @"C:\docs\需求.md", ItemKind.File)],
            },
        ],
        Monitors = [new MonitorSurface(@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), 1.25)],
    };

    [Fact]
    public void 创建后能原样读回且概要计数正确()
    {
        var snapshot = _store.Create(Sample(), "手动");

        Assert.True(File.Exists(snapshot.FilePath));
        Assert.Equal(1, snapshot.BoxCount);
        Assert.Equal(1, snapshot.ItemCount);
        Assert.Equal("手动", snapshot.Label);

        var loaded = _store.TryLoad(snapshot.FilePath, out var diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(loaded);
        Assert.Equal("工作区", loaded!.Boxes[0].Name);
        Assert.Equal(60, loaded.Boxes[0].Bounds.X);
        Assert.Equal(@"C:\docs\需求.md", loaded.Boxes[0].Items[0].TargetPath);
        Assert.Equal(LayoutSchema.CurrentVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void 快照与布局共用同一套序列化_枚举以字符串写入()
    {
        var snapshot = _store.Create(Sample());

        var json = File.ReadAllText(snapshot.FilePath);

        Assert.Contains("\"Kind\": \"File\"", json);
        Assert.Contains("SnapshotLabel", json);
    }

    [Fact]
    public void 列表按时间倒序排列()
    {
        var first = _store.Create(Sample("第一份"), "第一份");
        Thread.Sleep(20);
        var second = _store.Create(Sample("第二份"), "第二份");

        var list = _store.List();

        Assert.Equal(2, list.Count);
        Assert.Equal(second.FilePath, list[0].FilePath);
        Assert.Equal(first.FilePath, list[1].FilePath);
        Assert.Equal("第二份", list[0].Label);
    }

    [Fact]
    public void 标签里的非法字符会被清掉()
    {
        var snapshot = _store.Create(Sample(), @"还原/前\测试");

        Assert.True(File.Exists(snapshot.FilePath));
        Assert.DoesNotContain('/', Path.GetFileName(snapshot.FilePath));
        Assert.DoesNotContain('\\', Path.GetFileName(snapshot.FilePath));
        Assert.Equal("还原前测试", snapshot.Label);
    }

    [Fact]
    public void 保留策略只留最新的若干份()
    {
        for (var i = 0; i < 5; i++)
        {
            _store.Create(Sample($"第 {i} 份"), keep: 3);
            Thread.Sleep(10);
        }

        Assert.Equal(3, _store.List().Count);
    }

    [Fact]
    public void 保留份数为零时不清理()
    {
        for (var i = 0; i < 4; i++)
        {
            _store.Create(Sample($"第 {i} 份"), keep: 0);
        }

        Assert.Equal(4, _store.List().Count);
    }

    [Fact]
    public void 坏快照只影响它自己_列表跳过它_读取给出诊断()
    {
        var good = _store.Create(Sample());
        var broken = Path.Combine(_directory, "layout-20990101-000000-000-broken.json");
        File.WriteAllText(broken, "{ 这不是合法 JSON");

        var list = _store.List();

        Assert.Single(list);
        Assert.Equal(good.FilePath, list[0].FilePath);

        Assert.Null(_store.TryLoad(broken, out var diagnostic));
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void 版本高于当前时拒绝加载()
    {
        var path = Path.Combine(_directory, "layout-20990101-000001-000-future.json");
        File.WriteAllText(path, """{ "SchemaVersion": 99, "Boxes": [] }""");

        Assert.Null(_store.TryLoad(path, out var diagnostic));
        Assert.Contains("99", diagnostic);
    }

    [Fact]
    public void 文件不存在时给出明确说明()
    {
        Assert.Null(_store.TryLoad(Path.Combine(_directory, "没有这个文件.json"), out var diagnostic));
        Assert.Equal("快照文件不存在", diagnostic);
    }

    [Fact]
    public void 布局里的新增字段不会破坏旧的读取路径()
    {
        // SnapshotKeep 是后加的带默认值字段：老文件（没这个字段）读出来应该是默认值而不是 0
        var legacy = Path.Combine(_directory, "layout-20990101-000002-000-legacy.json");
        File.WriteAllText(legacy, """{ "SchemaVersion": 1, "Boxes": [] }""");

        var loaded = _store.TryLoad(legacy, out var diagnostic);

        Assert.Null(diagnostic);
        Assert.Equal(10, loaded!.SnapshotKeep);
    }
}