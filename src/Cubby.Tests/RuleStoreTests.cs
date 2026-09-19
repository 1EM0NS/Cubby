using Cubby.Core.Rules;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 规则文件的读写。除了往返一致性，重点验证两条防线：
/// 坏 JSON 不能让程序起不来、版本过高要拒绝而不是带着未知字段跑。
/// </summary>
public sealed class RuleStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;

    public RuleStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cubby-rules", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "rules.json");
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
    public void 保存后能原样读回()
    {
        var store = new RuleStore(_filePath);
        store.Save(RuleStore.Sample("box-1"));

        var loaded = new RuleStore(_filePath).Load();

        Assert.Equal(RuleSchema.CurrentVersion, loaded.SchemaVersion);
        Assert.True(loaded.Enabled);
        Assert.Equal(4, loaded.Rules.Count);
        Assert.Equal("images", loaded.Rules[0].Id);
        Assert.Equal(RuleMatchKind.Mime, loaded.Rules[0].Conditions[0].Kind);
        Assert.Equal(3, loaded.Rules[1].Conditions[1].WithinDays);
        Assert.Equal("box-1", loaded.Rules[0].TargetBoxId);
    }

    [Fact]
    public void 枚举以字符串写入便于人工编辑()
    {
        var store = new RuleStore(_filePath);
        store.Save(RuleStore.Sample("box-1"));

        var json = File.ReadAllText(_filePath);

        Assert.Contains("\"Kind\": \"Extension\"", json);
        Assert.Contains("\"Kind\": \"Regex\"", json);
    }

    [Fact]
    public void 文件不存在时返回空规则集且无诊断()
    {
        var store = new RuleStore(_filePath);

        var loaded = store.Load();

        Assert.Empty(loaded.Rules);
        Assert.Null(store.LastLoadDiagnostic);
    }

    [Fact]
    public void 坏JSON时降级为空规则集并给出诊断()
    {
        File.WriteAllText(_filePath, "{ 这不是合法 JSON ");
        var store = new RuleStore(_filePath);

        var loaded = store.Load();

        Assert.Empty(loaded.Rules);
        Assert.NotNull(store.LastLoadDiagnostic);
    }

    [Fact]
    public void 版本高于当前时拒绝加载()
    {
        File.WriteAllText(_filePath, """{ "SchemaVersion": 99, "Rules": [] }""");
        var store = new RuleStore(_filePath);

        var loaded = store.Load();

        Assert.Empty(loaded.Rules);
        Assert.Contains("99", store.LastLoadDiagnostic);
    }

    [Fact]
    public void 未知字段被忽略()
    {
        File.WriteAllText(_filePath, """{ "SchemaVersion": 1, "Rules": [], "未来字段": 1 }""");
        var store = new RuleStore(_filePath);

        var loaded = store.Load();

        Assert.Null(store.LastLoadDiagnostic);
        Assert.Empty(loaded.Rules);
    }

    [Fact]
    public void 覆盖保存留下备份()
    {
        var store = new RuleStore(_filePath);
        store.Save(RuleStore.Sample("a"));
        store.Save(RuleStore.Sample("b"));

        Assert.True(File.Exists(_filePath + ".bak"));
        Assert.False(File.Exists(_filePath + ".tmp"));
    }

    [Fact]
    public void 候选能从真实目录读出来()
    {
        File.WriteAllText(Path.Combine(_directory, "照片.JPG"), "x");
        Directory.CreateDirectory(Path.Combine(_directory, "子目录"));

        var candidates = RuleCandidates.FromFolder(_directory);

        Assert.Equal(2, candidates.Count);
        var photo = candidates.First(c => c.Name == "照片.JPG");
        Assert.Equal("jpg", photo.Extension);
        Assert.Equal("image/jpeg", photo.Mime);
        Assert.Equal(_directory, photo.SourcePath);

        var folder = candidates.First(c => c.Name == "子目录");
        Assert.Equal(string.Empty, folder.Extension);
    }

    [Fact]
    public void 目录不存在时返回空候选而不是抛异常()
    {
        Assert.Empty(RuleCandidates.FromFolder(Path.Combine(_directory, "并不存在")));
        Assert.Empty(RuleCandidates.FromFolder(null));
    }
}