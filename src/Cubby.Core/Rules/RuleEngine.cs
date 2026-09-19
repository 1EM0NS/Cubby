using System.Text;
using System.Text.RegularExpressions;

namespace Cubby.Core.Rules;

/// <summary>
/// 规则引擎：纯托管、无 IO、无 Win32。
///
/// **它只产出"谁该归到哪个盒子"的计划，绝不执行任何动作**——应用由 App 层做，
/// 这样预览与真正应用走的是同一份计算结果，不会出现"预览说会进 A、实际进了 B"。
/// </summary>
public static class RuleEngine
{
    /// <summary>一条命中：候选 → 目标盒子。</summary>
    public sealed record Match(string CandidatePath, string BoxId, string RuleId, string RuleName, string Reason);

    /// <summary>计划：预览与应用共用。目标盒子不存在、无规则命中的都会被单独列出来，便于向用户解释。</summary>
    public sealed record Plan(
        IReadOnlyList<Match> Matches,
        IReadOnlyList<string> Unmatched,
        IReadOnlyList<string> MissingTargets)
    {
        public bool IsEmpty => Matches.Count == 0;
    }

    /// <summary>规划：每条候选取**第一条命中**的启用规则（按 Priority 升序，其次按 Id 稳定排序）。</summary>
    public static Plan Classify(
        RuleSet ruleSet,
        IReadOnlyList<RuleCandidate> candidates,
        IReadOnlySet<string> knownBoxIds,
        DateTime? now = null)
    {
        if (!ruleSet.Enabled || candidates.Count == 0)
        {
            return new Plan([], candidates.Select(c => c.Path).ToList(), []);
        }

        var moment = now ?? DateTime.UtcNow;

        var rules = ruleSet.Rules
            .Where(rule => rule.Enabled && rule.Conditions.Count > 0)
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Id, StringComparer.Ordinal)
            .ToList();

        var matches = new List<Match>();
        var unmatched = new List<string>();
        var missingTargets = new List<string>();

        foreach (var candidate in candidates)
        {
            var hit = rules.FirstOrDefault(rule => Matches(rule, candidate, moment));
            if (hit is null)
            {
                unmatched.Add(candidate.Path);
                continue;
            }

            if (!knownBoxIds.Contains(hit.TargetBoxId))
            {
                // 目标盒子没了：不报错、不硬塞，单独记一笔让界面能提示
                if (!missingTargets.Contains(hit.TargetBoxId, StringComparer.Ordinal))
                {
                    missingTargets.Add(hit.TargetBoxId);
                }

                unmatched.Add(candidate.Path);
                continue;
            }

            matches.Add(new Match(
                candidate.Path,
                hit.TargetBoxId,
                hit.Id,
                hit.Name,
                $"{hit.Name}：{hit.DescribeConditions()}"));
        }

        return new Plan(matches, unmatched, missingTargets);
    }

    /// <summary>
    /// 一条规则是否命中：启用、有至少一个条件、且所有条件都满足（AND）。
    /// 前两条都要显式拦：空条件的 <c>All()</c> 恒为真，不拦的话"没写条件的规则"会吞掉所有条目。
    /// </summary>
    public static bool Matches(ClassificationRule rule, RuleCandidate candidate, DateTime now) =>
        rule.Enabled &&
        rule.Conditions.Count > 0 &&
        rule.Conditions.All(condition => Matches(condition, candidate, now));

    /// <summary>单个条件是否命中。</summary>
    public static bool Matches(RuleCondition condition, RuleCandidate candidate, DateTime now)
    {
        var result = condition.Kind switch
        {
            RuleMatchKind.Extension => MatchesExtension(condition.Value, candidate.Extension),
            RuleMatchKind.Mime => MatchesWildcard(condition.Value, candidate.Mime),
            RuleMatchKind.Time => MatchesTime(condition, candidate, now),
            RuleMatchKind.SourcePath => MatchesWildcard(condition.Value, candidate.SourcePath),
            RuleMatchKind.Regex => MatchesRegex(condition.Value, candidate.Name),
            _ => false,
        };

        return condition.Negate ? !result : result;
    }

    private static bool MatchesExtension(string value, string extension)
    {
        var wanted = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Length == 0)
        {
            return false;
        }

        var actual = extension.TrimStart('.');

        return wanted.Any(one =>
            one.Equals(actual, StringComparison.OrdinalIgnoreCase) ||
            one.Equals("*", StringComparison.Ordinal));
    }

    /// <summary>
    /// 时间条件：<c>WithinDays</c> 优先（最近 N 天内）；否则用 After/Before 区间（按最后修改时间）。
    /// 两个都没给就视为不命中——"没有约束"应该是删掉这个条件，而不是写个空条件。
    /// </summary>
    private static bool MatchesTime(RuleCondition condition, RuleCandidate candidate, DateTime now)
    {
        if (condition.WithinDays is { } days)
        {
            return candidate.ModifiedUtc >= now.AddDays(-Math.Max(0, days));
        }

        if (condition.After is null && condition.Before is null)
        {
            return false;
        }

        var after = condition.After ?? DateTime.MinValue;
        var before = condition.Before ?? DateTime.MaxValue;

        return candidate.ModifiedUtc >= after && candidate.ModifiedUtc <= before;
    }

    private static bool MatchesRegex(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        try
        {
            // 用户手写的正则很可能写错，写错就当不命中，绝不把异常抛给调用方
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// 通配符匹配：<c>*</c> 任意多字符、<c>?</c> 单字符。用于 MIME（<c>image/*</c>）与路径（<c>*\Downloads\*</c>）。
    /// 其余字符按字面量处理——用户写的是路径，不是正则。
    /// </summary>
    public static bool MatchesWildcard(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        var builder = new StringBuilder("^");
        foreach (var character in pattern)
        {
            builder.Append(character switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(character.ToString()),
            });
        }

        builder.Append('$');

        try
        {
            return Regex.IsMatch(text, builder.ToString(), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }
}