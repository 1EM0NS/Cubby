using Cubby.Core.Model;

namespace Cubby.Core.Layout;

/// <summary>一次吸附的规划结果。之所以把中间计数也带出来，是为了让"什么都没吸到"这件事可解释。</summary>
/// <param name="MatchedPaths">匹配到真实文件的路径（已按路径去重）。</param>
/// <param name="UnmatchedNames">在盒子范围内、但没匹配到文件的图标名（虚拟图标等）。</param>
/// <param name="IconsInArea">落在盒子范围内的图标总数。</param>
/// <param name="IconTotal">读到的桌面图标总数。</param>
public sealed record AdoptResult(
    IReadOnlyList<string> MatchedPaths,
    IReadOnlyList<string> UnmatchedNames,
    int IconsInArea,
    int IconTotal)
{
    public string Describe() =>
        $"桌面图标 {IconTotal} 个，落在盒子范围内 {IconsInArea} 个，匹配到文件 {MatchedPaths.Count} 个" +
        (UnmatchedNames.Count == 0 ? string.Empty : $"，未匹配 {UnmatchedNames.Count} 个（{string.Join("、", UnmatchedNames.Take(5))}）");
}

/// <summary>
/// 桌面图标吸附的**纯逻辑**：读到的图标 + 盒子范围 + 桌面目录清单 → 该入盒哪些文件。
///
/// 它不读注册表、不调 Win32、不动文件，因此可以直接单测——这正是 P4 能被验证的前提：
/// 决策链上任何一环都不需要写权限。
/// </summary>
public static class DesktopAdoption
{
    /// <summary>
    /// 规划一次吸附：把落在 <paramref name="area"/>（虚拟屏幕物理像素）内的桌面图标，
    /// 匹配到 <paramref name="candidates"/>（桌面目录里的真实条目）。
    /// </summary>
    public static AdoptResult Plan(IReadOnlyList<DesktopIcon> icons, PixelRect area, IReadOnlyList<string> candidates)
    {
        var inArea = icons.Where(icon => area.Contains(icon.X, icon.Y)).ToList();
        var matched = new List<string>();
        var unmatched = new List<string>();

        foreach (var icon in inArea)
        {
            var path = Match(icon.Name, candidates);

            if (path is null)
            {
                unmatched.Add(icon.Name);
            }
            else if (!matched.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                matched.Add(path);
            }
        }

        return new AdoptResult(matched, unmatched, inArea.Count, icons.Count);
    }

    /// <summary>
    /// 图标名 → 文件路径。
    ///
    /// Explorer 默认隐藏已知扩展名（桌面显示「报告」而磁盘上是「报告.txt」），
    /// 所以「完全相同」与「文件名去掉扩展名后相同」两种都算匹配。
    /// 只按名字匹配，**不解析 .lnk 指向的目标**——那是另一件事，且会更危险。
    /// </summary>
    public static string? Match(string iconName, IReadOnlyList<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return null;
        }

        foreach (var path in candidates)
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals(iconName, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            if (Path.GetFileNameWithoutExtension(fileName).Equals(iconName, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }
}