namespace Cubby.Core.Rules;

/// <summary>条件类型。规则以 JSON 描述，改这份枚举要同步升 <see cref="RuleSchema"/> 版本并在 RuleStore 里补迁移。</summary>
public enum RuleMatchKind
{
    /// <summary>扩展名（不含点；多个用逗号分隔，如 <c>jpg,png,webp</c>）。</summary>
    Extension,

    /// <summary>MIME 类型（支持 <c>image/*</c> 这样的通配）。</summary>
    Mime,

    /// <summary>时间：最近 <see cref="RuleCondition.WithinDays"/> 天内创建/修改，或落在 After/Before 之间。</summary>
    Time,

    /// <summary>来源路径：条目所在目录的前缀 / 通配符匹配（如 <c>*\Downloads\*</c>）。</summary>
    SourcePath,

    /// <summary>文件名的正则。</summary>
    Regex,
}

/// <summary>
/// 一个条件。字段刻意做得"平"（不搞多态子类），因为它是给用户手写 JSON 用的：
/// 平结构一眼能看懂，也让规则文件的版本迁移简单得多。
/// </summary>
public sealed record RuleCondition
{
    public RuleMatchKind Kind { get; init; }

    /// <summary>条件值。含义随 <see cref="Kind"/> 变化（扩展名清单 / MIME / 路径通配 / 正则）。</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>时间条件用：最近多少天内。null 表示不用这个维度。</summary>
    public int? WithinDays { get; init; }

    /// <summary>时间条件用：区间起点（ISO 8601），含。</summary>
    public DateTime? After { get; init; }

    /// <summary>时间条件用：区间终点（ISO 8601），含。</summary>
    public DateTime? Before { get; init; }

    /// <summary>取反——"不是这些扩展名"这种需求很常见，没必要为此单开一种条件类型。</summary>
    public bool Negate { get; init; }

    public string Describe() => Kind switch
    {
        RuleMatchKind.Extension => $"扩展名 {(Negate ? "不是" : "是")} {Value}",
        RuleMatchKind.Mime => $"MIME {(Negate ? "不是" : "是")} {Value}",
        RuleMatchKind.Time => $"时间 {DescribeTime()}",
        RuleMatchKind.SourcePath => $"来源路径 {(Negate ? "不匹配" : "匹配")} {Value}",
        RuleMatchKind.Regex => $"文件名正则 {(Negate ? "不匹配" : "匹配")} {Value}",
        _ => $"未知条件 {Kind}",
    };

    private string DescribeTime()
    {
        if (WithinDays is { } days)
        {
            return $"最近 {days} 天内";
        }

        var from = After?.ToString("yyyy-MM-dd") ?? "(不限)";
        var to = Before?.ToString("yyyy-MM-dd") ?? "(不限)";
        return $"{from} ~ {to}";
    }
}

/// <summary>
/// 一条归类规则：**所有条件都满足**才命中（AND），命中后把条目归到 <see cref="TargetBoxId"/>。
/// 规则只决定"展示归属"，不移动任何文件（P4）。
/// </summary>
public sealed record ClassificationRule
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>目标盒子 Id。盒子没了这条规则就只是不生效，不会报错。</summary>
    public string TargetBoxId { get; init; } = string.Empty;

    /// <summary>越小越先匹配；同一批候选按优先级取第一条命中的规则。</summary>
    public int Priority { get; init; }

    public IReadOnlyList<RuleCondition> Conditions { get; init; } = [];

    public string DescribeConditions() =>
        Conditions.Count == 0 ? "(无条件，永不命中)" : string.Join(" 且 ", Conditions.Select(c => c.Describe()));
}

/// <summary>规则集。整体写在一个 JSON 文件里，便于用户编辑与导入导出。</summary>
public sealed record RuleSet
{
    public int SchemaVersion { get; init; } = RuleSchema.CurrentVersion;

    /// <summary>总开关。关掉之后预览与应用都直接返回空计划。</summary>
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<ClassificationRule> Rules { get; init; } = [];

    public string? SavedAt { get; init; }
}

public static class RuleSchema
{
    public const int CurrentVersion = 1;
}