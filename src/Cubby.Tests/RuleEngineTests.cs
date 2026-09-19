using Cubby.Core.Rules;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 归类规则引擎。五类条件各有用例，另外覆盖 AND 语义、取反、通配、坏正则、
/// 优先级与"目标盒子不存在"——这些正是用户手写 JSON 时最容易踩的坑。
/// </summary>
public sealed class RuleEngineTests
{
    private static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    private static RuleCandidate Candidate(
        string name,
        string extension,
        string sourcePath = @"C:\Desktop",
        long size = 1024,
        int modifiedDaysAgo = 0) =>
        new(
            name,
            Path.Combine(sourcePath, name),
            sourcePath,
            extension,
            size,
            Now.AddDays(-modifiedDaysAgo),
            Now.AddDays(-modifiedDaysAgo));

    private static RuleCondition Condition(RuleMatchKind kind, string value = "", bool negate = false) =>
        new() { Kind = kind, Value = value, Negate = negate };

    // ---- 扩展名 ----

    [Fact]
    public void 扩展名条件_命中清单里的扩展名()
    {
        var condition = Condition(RuleMatchKind.Extension, "jpg,png,gif");

        Assert.True(RuleEngine.Matches(condition, Candidate("a.jpg", "jpg"), Now));
        Assert.True(RuleEngine.Matches(condition, Candidate("b.PNG", "png"), Now));
        Assert.False(RuleEngine.Matches(condition, Candidate("c.txt", "txt"), Now));
    }

    [Fact]
    public void 扩展名条件_星号匹配任意扩展名()
    {
        Assert.True(RuleEngine.Matches(Condition(RuleMatchKind.Extension, "*"), Candidate("a.zzz", "zzz"), Now));
    }

    [Fact]
    public void 扩展名条件_取反表示不是这些扩展名()
    {
        var condition = Condition(RuleMatchKind.Extension, "jpg,png", negate: true);

        Assert.True(RuleEngine.Matches(condition, Candidate("a.txt", "txt"), Now));
        Assert.False(RuleEngine.Matches(condition, Candidate("a.jpg", "jpg"), Now));
    }

    // ---- MIME ----

    [Fact]
    public void Mime条件_支持类型通配()
    {
        Assert.True(RuleEngine.Matches(Condition(RuleMatchKind.Mime, "image/*"), Candidate("a.jpg", "jpg"), Now));
        Assert.True(RuleEngine.Matches(Condition(RuleMatchKind.Mime, "text/*"), Candidate("a.md", "md"), Now));
        Assert.True(RuleEngine.Matches(Condition(RuleMatchKind.Mime, "*/*"), Candidate("a.whatever", "whatever"), Now));
        Assert.False(RuleEngine.Matches(Condition(RuleMatchKind.Mime, "image/*"), Candidate("a.txt", "txt"), Now));
    }

    [Fact]
    public void Mime条件_认不出的扩展名落到二进制类型()
    {
        Assert.Equal(MimeTypes.Unknown, Candidate("a.zzz", "zzz").Mime);
        Assert.True(RuleEngine.Matches(Condition(RuleMatchKind.Mime, MimeTypes.Unknown), Candidate("a.zzz", "zzz"), Now));
    }

    // ---- 时间 ----

    [Fact]
    public void 时间条件_最近N天内()
    {
        var condition = new RuleCondition { Kind = RuleMatchKind.Time, WithinDays = 3 };

        Assert.True(RuleEngine.Matches(condition, Candidate("新.txt", "txt", modifiedDaysAgo: 1), Now));
        Assert.False(RuleEngine.Matches(condition, Candidate("旧.txt", "txt", modifiedDaysAgo: 10), Now));
    }

    [Fact]
    public void 时间条件_AfterBefore区间()
    {
        var condition = new RuleCondition
        {
            Kind = RuleMatchKind.Time,
            After = Now.AddDays(-5),
            Before = Now.AddDays(-1),
        };

        Assert.True(RuleEngine.Matches(condition, Candidate("中.txt", "txt", modifiedDaysAgo: 3), Now));
        Assert.False(RuleEngine.Matches(condition, Candidate("太新.txt", "txt", modifiedDaysAgo: 0), Now));
        Assert.False(RuleEngine.Matches(condition, Candidate("太旧.txt", "txt", modifiedDaysAgo: 9), Now));
    }

    [Fact]
    public void 时间条件_什么都不填时不命中()
    {
        // "没有约束"应该是删掉这个条件，而不是写个空条件
        Assert.False(RuleEngine.Matches(new RuleCondition { Kind = RuleMatchKind.Time }, Candidate("a.txt", "txt"), Now));
    }

    // ---- 来源路径 ----

    [Fact]
    public void 来源路径条件_支持通配()
    {
        var condition = Condition(RuleMatchKind.SourcePath, @"*\Downloads");

        Assert.True(RuleEngine.Matches(condition, Candidate("a.zip", "zip", @"C:\Users\me\Downloads"), Now));
        Assert.False(RuleEngine.Matches(condition, Candidate("a.zip", "zip", @"C:\Users\me\Desktop"), Now));
    }

    [Fact]
    public void 来源路径条件_问号匹配单个字符()
    {
        var condition = Condition(RuleMatchKind.SourcePath, @"C:\盘?\数据");

        Assert.True(RuleEngine.Matches(condition, Candidate("a.txt", "txt", @"C:\盘A\数据"), Now));
        Assert.False(RuleEngine.Matches(condition, Candidate("a.txt", "txt", @"C:\盘AB\数据"), Now));
    }

    // ---- 正则 ----

    [Fact]
    public void 正则条件_忽略大小写匹配文件名()
    {
        var condition = Condition(RuleMatchKind.Regex, "^screenshot");

        Assert.True(RuleEngine.Matches(condition, Candidate("Screenshot_1.png", "png"), Now));
        Assert.False(RuleEngine.Matches(condition, Candidate("我的Screenshot.png", "png"), Now));
    }

    [Fact]
    public void 正则条件_写错的正则只是不命中_不抛异常()
    {
        Assert.False(RuleEngine.Matches(Condition(RuleMatchKind.Regex, "([未闭合"), Candidate("a.txt", "txt"), Now));
        Assert.False(RuleEngine.Matches(Condition(RuleMatchKind.Regex, string.Empty), Candidate("a.txt", "txt"), Now));
    }

    // ---- 规则与优先级 ----

    private static ClassificationRule Rule(string id, int priority, params RuleCondition[] conditions) =>
        new()
        {
            Id = id,
            Name = id,
            TargetBoxId = "box-a",
            Priority = priority,
            Conditions = conditions,
        };

    [Fact]
    public void 一条规则的所有条件都要满足()
    {
        var rule = Rule("r", 1,
            Condition(RuleMatchKind.Extension, "jpg"),
            new RuleCondition { Kind = RuleMatchKind.Time, WithinDays = 1 });

        Assert.True(RuleEngine.Matches(rule, Candidate("a.jpg", "jpg", modifiedDaysAgo: 0), Now));
        Assert.False(RuleEngine.Matches(rule, Candidate("a.jpg", "jpg", modifiedDaysAgo: 9), Now));
        Assert.False(RuleEngine.Matches(rule, Candidate("a.txt", "txt", modifiedDaysAgo: 0), Now));
    }

    [Fact]
    public void 无条件或未启用的规则永远不命中()
    {
        var noCondition = new ClassificationRule { Id = "empty", TargetBoxId = "box-a" };
        var disabled = Rule("off", 1, Condition(RuleMatchKind.Extension, "*")) with { Enabled = false };

        Assert.False(RuleEngine.Matches(noCondition, Candidate("a.txt", "txt"), Now));
        Assert.False(RuleEngine.Matches(disabled, Candidate("a.txt", "txt"), Now));
    }

    [Fact]
    public void 按优先级取第一条命中的规则()
    {
        var ruleSet = new RuleSet
        {
            Rules =
            [
                Rule("low", 20, Condition(RuleMatchKind.Extension, "txt")),
                Rule("high", 5, Condition(RuleMatchKind.Extension, "txt")),
            ],
        };

        var plan = RuleEngine.Classify(ruleSet, [Candidate("a.txt", "txt")], new HashSet<string> { "box-a" }, Now);

        Assert.Single(plan.Matches);
        Assert.Equal("high", plan.Matches[0].RuleId);
    }

    [Fact]
    public void 目标盒子不存在时归入未命中并单独报告()
    {
        var ruleSet = new RuleSet { Rules = [Rule("r", 1, Condition(RuleMatchKind.Extension, "zip"))] };

        var plan = RuleEngine.Classify(ruleSet, [Candidate("a.zip", "zip")], new HashSet<string> { "别的盒子" }, Now);

        Assert.Empty(plan.Matches);
        Assert.Contains("box-a", plan.MissingTargets);
        Assert.Single(plan.Unmatched);
    }

    [Fact]
    public void 总开关关掉时什么都不做()
    {
        var ruleSet = new RuleSet { Enabled = false, Rules = [Rule("r", 1, Condition(RuleMatchKind.Extension, "*"))] };

        var plan = RuleEngine.Classify(ruleSet, [Candidate("a.txt", "txt")], new HashSet<string> { "box-a" }, Now);

        Assert.Empty(plan.Matches);
        Assert.Single(plan.Unmatched);
    }

    [Fact]
    public void 计划里的命中带上可读的理由()
    {
        var ruleSet = new RuleSet
        {
            Rules = [Rule("r", 1, Condition(RuleMatchKind.Mime, "image/*"))],
        };

        var plan = RuleEngine.Classify(ruleSet, [Candidate("a.png", "png")], new HashSet<string> { "box-a" }, Now);

        Assert.Contains("image/*", plan.Matches[0].Reason);
    }
}