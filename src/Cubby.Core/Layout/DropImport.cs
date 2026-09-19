using Cubby.Core.Model;

namespace Cubby.Core.Layout;

/// <summary>
/// 把一次拖放投进来的路径清单转换成盒子条目。
///
/// **只读**：本类不创建、不复制、不移动、不删除任何文件，只把路径记下来（P4）。
/// 因此它可以在单元测试里对真实文件运行，并断言时间戳与路径原样不变。
/// </summary>
public static class DropImport
{
    /// <summary>
    /// 生成新条目。<paramref name="existing"/> 里已有的路径会被跳过（同一文件不重复入盒），
    /// 同一次拖放里的重复路径也会被去重。
    /// </summary>
    public static IReadOnlyList<BoxItem> Create(IEnumerable<string> paths, IReadOnlyList<BoxItem> existing)
    {
        var seen = new HashSet<string>(existing.Select(i => i.TargetPath), StringComparer.OrdinalIgnoreCase);
        var added = new List<BoxItem>();

        foreach (var raw in paths)
        {
            var path = Normalize(raw);
            if (path is null || !seen.Add(path))
            {
                continue;
            }

            added.Add(new BoxItem(NewId(), DisplayNameOf(path), path, KindOf(path)));
        }

        return added;
    }

    /// <summary>去掉首尾空白；空串返回 null。这里刻意不做存在性校验，路径可能暂时不可访问。</summary>
    private static string? Normalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>条目显示名：取末段名字；盘符根目录（如 <c>C:\</c>）没有末段，退回完整路径。</summary>
    private static string DisplayNameOf(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);

        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static ItemKind KindOf(string path)
    {
        if (Directory.Exists(path))
        {
            return ItemKind.Folder;
        }

        // 只按扩展名判断，不解析 .url / .lnk 的内容：解析失败不该让整次拖放失败
        var extension = Path.GetExtension(path);
        return extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
            ? ItemKind.Url
            : ItemKind.File;
    }
}