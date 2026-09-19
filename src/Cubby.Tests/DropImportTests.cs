using Cubby.Core.Layout;
using Cubby.Core.Model;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 拖入生成的条目。这里的核心断言是 P4——**拖入只登记引用，磁盘上什么都没变**，
/// 所以测试直接对真实临时文件跑，并逐项比对指纹。
/// </summary>
public sealed class DropImportTests : IDisposable
{
    private readonly string _directory;

    public DropImportTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cubby-drop-tests", Guid.NewGuid().ToString("N"));
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
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public void 按磁盘实际情况判定条目类型()
    {
        var file = Path.Combine(_directory, "报告.txt");
        var url = Path.Combine(_directory, "主页.url");
        var folder = Path.Combine(_directory, "资料夹");
        File.WriteAllText(file, "x");
        File.WriteAllText(url, "[InternetShortcut]");
        Directory.CreateDirectory(folder);

        var items = DropImport.Create([file, url, folder], []);

        Assert.Equal(3, items.Count);
        Assert.Equal(ItemKind.File, items[0].Kind);
        Assert.Equal("报告.txt", items[0].DisplayName);
        Assert.Equal(ItemKind.Url, items[1].Kind);
        Assert.Equal(ItemKind.Folder, items[2].Kind);
        Assert.Equal("资料夹", items[2].DisplayName);
        Assert.All(items, item => Assert.Equal(32, item.Id.Length));
    }

    [Fact]
    public void 不存在的路径按扩展名归类而不是报错()
    {
        var missing = Path.Combine(_directory, "已经没了.txt");
        var items = DropImport.Create([missing], []);

        Assert.Single(items);
        Assert.Equal(ItemKind.File, items[0].Kind);
        Assert.Equal(missing, items[0].TargetPath);
    }

    [Fact]
    public void 已入盒的路径与本次重复的路径都会被去重()
    {
        var a = Path.Combine(_directory, "a.txt");
        var b = Path.Combine(_directory, "b.txt");
        var existing = new List<BoxItem> { new("id-a", "a.txt", a, ItemKind.File) };

        var items = DropImport.Create([a, b, b, a], existing);

        Assert.Single(items);
        Assert.Equal(b, items[0].TargetPath);
    }

    [Fact]
    public void 大小写不同的同一路径视为重复()
    {
        var path = Path.Combine(_directory, "Case.txt");
        var existing = new List<BoxItem> { new("id", "Case.txt", path.ToUpperInvariant(), ItemKind.File) };

        Assert.Empty(DropImport.Create([path], existing));
    }

    [Fact]
    public void 空白路径被忽略()
    {
        Assert.Empty(DropImport.Create(["", "   ", "\t"], []));
    }

    [Fact]
    public void 盘符根目录的显示名回退为完整路径()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var items = DropImport.Create([root], []);

        Assert.Single(items);
        Assert.Equal(root, items[0].TargetPath);
        Assert.False(string.IsNullOrWhiteSpace(items[0].DisplayName));
    }

    [Fact]
    public void 生成引用不改变磁盘现场_路径存在性与时间戳都不变()
    {
        var nested = Path.Combine(_directory, "子目录");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(_directory, "重要文件.txt");
        var inner = Path.Combine(nested, "内部.txt");
        File.WriteAllText(file, "keep me");
        File.WriteAllText(inner, "keep me too");

        var before = new[] { file, inner, nested }.ToDictionary(p => p, Fingerprint);
        var beforeEntries = Directory.GetFileSystemEntries(_directory, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 连续生成两次，确保连"重复入盒"这条路径也不会碰磁盘
        _ = DropImport.Create([file, inner, nested], []);
        _ = DropImport.Create([file, inner, nested], []);

        foreach (var (path, fingerprint) in before)
        {
            Assert.Equal(fingerprint, Fingerprint(path));
        }

        Assert.Equal(
            beforeEntries,
            Directory.GetFileSystemEntries(_directory, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    /// <summary>路径 + 类型 + 存在性 + 最后写入时间 + 大小。</summary>
    private static string Fingerprint(string path) =>
        Directory.Exists(path)
            ? $"dir|{path}|{new DirectoryInfo(path).LastWriteTimeUtc:O}"
            : $"file|{path}|{new FileInfo(path).LastWriteTimeUtc:O}|{new FileInfo(path).Length}";
}