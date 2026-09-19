using System.IO;
using Cubby.Core.Model;
using Cubby.Core.Platform;
using Cubby.Core.Rules;

namespace Cubby.App;

/// <summary>
/// 归类规则的宿主：读规则文件、算计划、应用到盒子、以及**撤销上一次归类**。
///
/// 边界很清楚：
/// - 规则引擎（Core）只算"谁该归到哪个盒子"；
/// - 这里只改布局里的 `Items` 引用（P4：**永远不移动、复制、删除任何文件**）；
/// - 撤销靠的是应用前记下的每个盒子的条目快照，恢复它即可——不需要反向推导。
/// </summary>
internal sealed class RuleService
{
    private readonly LayoutService _layout;
    private readonly Action<Box> _applyToView;
    private readonly RuleStore _store;
    private readonly Func<string?> _defaultBoxId;

    private RuleSet _rules;
    private UndoRecord? _lastUndo;

    private sealed record UndoRecord(DateTime At, Dictionary<string, IReadOnlyList<BoxItem>> ItemsBefore);

    public RuleService(LayoutService layout, Action<Box> applyToView, Func<string?> defaultBoxId, string? filePath = null)
    {
        _layout = layout;
        _applyToView = applyToView;
        _defaultBoxId = defaultBoxId;

        _store = new RuleStore(filePath ?? DefaultPath());
        _rules = _store.Load();

        if (!File.Exists(_store.FilePath))
        {
            // 首次使用：先在内存里放一份示例模板，**等用户真的用到时才落盘**
            _rules = RuleStore.Sample(defaultBoxId() ?? string.Empty);
        }
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cubby",
        "rules.json");

    public string FilePath => _store.FilePath;

    public RuleSet Rules => _rules;

    public bool Enabled => _rules.Enabled;

    public string? LastDiagnostic => _store.LastLoadDiagnostic;

    /// <summary>上一次归类是否可撤销。</summary>
    public bool CanUndo => _lastUndo is not null;

    public string LastUndoDescription => _lastUndo is null
        ? "（没有可撤销的归类）"
        : $"{_lastUndo.At:HH:mm:ss} 的归类涉及 {_lastUndo.ItemsBefore.Count} 个盒子";

    public string Describe() =>
        $"{_rules.Rules.Count} 条规则（启用 {_rules.Rules.Count(r => r.Enabled)} 条），总开关{(_rules.Enabled ? "开" : "关")}" +
        (LastDiagnostic is { } diagnostic ? $"；载入诊断：{diagnostic}" : string.Empty);

    /// <summary>规则明细，供界面与诊断展示。</summary>
    public IReadOnlyList<string> DescribeRules() =>
        _rules.Rules
            .OrderBy(r => r.Priority)
            .Select(r => $"  [{(r.Enabled ? "✓" : " ")}] {r.Priority,4}  {r.Name,-18} → {BoxNameOf(r.TargetBoxId)}  {r.DescribeConditions()}")
            .ToList();

    /// <summary>把规则文件落到磁盘（首次使用或用户改过之后调用）。</summary>
    public void EnsureOnDisk()
    {
        if (!File.Exists(_store.FilePath))
        {
            _store.Save(_rules);
        }
    }

    public void Save(RuleSet ruleSet)
    {
        _rules = ruleSet;
        _store.Save(_rules);
    }

    /// <summary>重新从磁盘读规则（用户在外部编辑了 JSON 之后用）。</summary>
    public void Reload()
    {
        _rules = _store.Load();
        if (!File.Exists(_store.FilePath))
        {
            _rules = RuleStore.Sample(_defaultBoxId() ?? string.Empty);
        }
    }

    /// <summary>候选来源：桌面目录里**尚未入盒**的条目。这就是"自动归类"实际会处理的那批东西。</summary>
    public IReadOnlyList<RuleCandidate> Candidates()
    {
        var inBoxes = _layout.Boxes
            .SelectMany(box => box.Items)
            .Select(item => item.TargetPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return DesktopFolders.EnumerateEntries()
            .Where(path => !inBoxes.Contains(path))
            .Select(RuleCandidates.TryCreate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToList();
    }

    /// <summary>预览：算一遍计划但不改任何东西。与 <see cref="Apply"/> 用的是同一份计算。</summary>
    public RuleEngine.Plan Preview(IReadOnlyList<RuleCandidate> candidates) =>
        RuleEngine.Classify(_rules, candidates, _layout.Boxes.Select(b => b.Id).ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// 应用计划：把命中的条目登记进目标盒子（只加引用），并记下撤销用的快照。
    /// </summary>
    public RuleEngine.Plan Apply(IReadOnlyList<RuleCandidate> candidates)
    {
        EnsureOnDisk();

        var plan = Preview(candidates);
        if (plan.Matches.Count == 0)
        {
            _lastUndo = null;
            return plan;
        }

        var itemsBefore = new Dictionary<string, IReadOnlyList<BoxItem>>(StringComparer.Ordinal);

        foreach (var match in plan.Matches)
        {
            var box = _layout.Boxes.FirstOrDefault(b => b.Id == match.BoxId);
            if (box is null)
            {
                continue;
            }

            var candidate = candidates.FirstOrDefault(c => c.Path == match.CandidatePath);
            if (candidate is null)
            {
                continue;
            }

            if (!itemsBefore.ContainsKey(box.Id))
            {
                itemsBefore[box.Id] = box.Items;
            }

            var updated = box with { Items = [.. box.Items, ToItem(candidate)] };
            _layout.UpdateBox(updated);
            _applyToView(updated);
        }

        _lastUndo = new UndoRecord(DateTime.Now, itemsBefore);
        return plan;
    }

    /// <summary>撤销上一次归类：把涉及到的盒子恢复到应用前的条目清单。返回恢复的盒子数。</summary>
    public int Undo()
    {
        if (_lastUndo is null)
        {
            return 0;
        }

        var restored = 0;

        foreach (var (boxId, items) in _lastUndo.ItemsBefore)
        {
            var box = _layout.Boxes.FirstOrDefault(b => b.Id == boxId);
            if (box is null)
            {
                continue;
            }

            var updated = box with { Items = items };
            _layout.UpdateBox(updated);
            _applyToView(updated);
            restored++;
        }

        _lastUndo = null;
        return restored;
    }

    /// <summary>把计划写成给人看的多行文本（预览区与验收报告共用）。</summary>
    public string DescribePlan(RuleEngine.Plan plan)
    {
        if (plan.IsEmpty && plan.Unmatched.Count == 0 && plan.MissingTargets.Count == 0)
        {
            return "没有可处理的条目（桌面上的东西都已经在盒子里了）。";
        }

        var lines = new List<string>();

        foreach (var match in plan.Matches)
        {
            lines.Add($"  → 「{BoxNameOf(match.BoxId)}」  {Path.GetFileName(match.CandidatePath)}    {match.Reason}");
        }

        if (plan.MissingTargets.Count > 0)
        {
            lines.Add($"  目标盒子不存在（这些规则不会生效）：{string.Join("、", plan.MissingTargets)}");
        }

        if (plan.Unmatched.Count > 0)
        {
            lines.Add($"  未命中任何规则：{plan.Unmatched.Count} 条");
        }

        return lines.Count == 0 ? "没有命中的条目。" : string.Join(Environment.NewLine, lines);
    }

    private string BoxNameOf(string boxId) =>
        _layout.Boxes.FirstOrDefault(b => b.Id == boxId)?.Name ?? $"（未知盒子 {boxId}）";

    private static BoxItem ToItem(RuleCandidate candidate) =>
        new(
            Guid.NewGuid().ToString("N"),
            candidate.Name,
            candidate.Path,
            Directory.Exists(candidate.Path) ? ItemKind.Folder : ItemKind.File);
}