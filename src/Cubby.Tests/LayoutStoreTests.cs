using Cubby.Core.Model;
using Cubby.Core.Storage;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 布局持久化的测试。重点不是"能存能读"，而是两个容易出事的地方：
/// 写入的原子性，以及读到坏文件时不能让程序起不来。
/// </summary>
public sealed class LayoutStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;

    public LayoutStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cubby-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "layout.json");
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

    private static LayoutDocument SampleDocument() => new()
    {
        Boxes =
        [
            new Box("b1", "工作区", new DipRect(60, 80, 420, 300))
            {
                MonitorId = @"\\.\DISPLAY1",
                MonitorWidth = 2560,
                MonitorHeight = 1440,
                Columns = 5,
                IsLocked = true,
                Items = [new BoxItem("i1", "需求.md", @"C:\docs\需求.md", ItemKind.File)],
            },
        ],
        Monitors = [new MonitorSurface(@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), 1.25)],
    };

    [Fact]
    public void 保存后能原样读回()
    {
        var store = new LayoutStore(_filePath);

        store.Save(SampleDocument());
        var loaded = store.Load();

        Assert.Null(store.LastLoadDiagnostic);
        Assert.Single(loaded.Boxes);
        var box = loaded.Boxes[0];
        Assert.Equal("工作区", box.Name);
        Assert.Equal(60, box.Bounds.X);
        Assert.Equal(5, box.Columns);
        Assert.True(box.IsLocked);
        Assert.Equal(2560, box.MonitorWidth);
        Assert.Single(box.Items);
        Assert.Equal(ItemKind.File, box.Items[0].Kind);
        Assert.Equal(1.25, loaded.Monitors[0].DpiScale);
    }

    [Fact]
    public void 枚举以字符串形式写入便于人工查看()
    {
        var store = new LayoutStore(_filePath);
        store.Save(SampleDocument());

        var json = File.ReadAllText(_filePath);

        Assert.Contains("\"Kind\": \"File\"", json);
        Assert.Contains("SavedAt", json);
    }

    [Fact]
    public void 覆盖保存会留下备份且不留临时文件()
    {
        var store = new LayoutStore(_filePath);
        store.Save(SampleDocument());
        store.Save(SampleDocument());

        Assert.True(File.Exists(_filePath));
        Assert.True(File.Exists(_filePath + ".bak"));
        Assert.False(File.Exists(_filePath + ".tmp"));
    }

    [Fact]
    public void 首次保存不产生备份文件()
    {
        var store = new LayoutStore(_filePath);
        store.Save(SampleDocument());

        Assert.True(File.Exists(_filePath));
        Assert.False(File.Exists(_filePath + ".bak"));
    }

    [Fact]
    public void 文件不存在时返回空布局且无诊断信息()
    {
        var store = new LayoutStore(_filePath);

        var loaded = store.Load();

        Assert.Empty(loaded.Boxes);
        Assert.Null(store.LastLoadDiagnostic);
    }

    [Fact]
    public void 布局损坏时不抛异常_备份现场并返回空布局()
    {
        File.WriteAllText(_filePath, "{ 这不是合法 JSON ");
        var store = new LayoutStore(_filePath);

        var loaded = store.Load();

        Assert.Empty(loaded.Boxes);
        Assert.NotNull(store.LastLoadDiagnostic);
        Assert.NotEmpty(Directory.GetFiles(_directory, "layout.json.corrupt-*"));
        Assert.False(File.Exists(_filePath));
    }

    [Fact]
    public void 遇到更高版本时拒绝加载而不是带着未知字段乱跑()
    {
        File.WriteAllText(_filePath, """{ "SchemaVersion": 99, "Boxes": [] }""");
        var store = new LayoutStore(_filePath);

        var loaded = store.Load();

        Assert.Empty(loaded.Boxes);
        Assert.Contains("99", store.LastLoadDiagnostic);
    }

    [Fact]
    public void 未知字段会被忽略而不是导致失败()
    {
        File.WriteAllText(_filePath, """
            { "SchemaVersion": 1, "Boxes": [], "未来才有的字段": 123 }
            """);
        var store = new LayoutStore(_filePath);

        var loaded = store.Load();

        Assert.Null(store.LastLoadDiagnostic);
        Assert.Equal(LayoutSchema.CurrentVersion, loaded.SchemaVersion);
    }
}