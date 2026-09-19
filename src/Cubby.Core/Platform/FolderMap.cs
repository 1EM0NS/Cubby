using Cubby.Core.Model;

namespace Cubby.Core.Platform;

/// <summary>
/// 文件夹映射的**纯逻辑**：把一个目录的顶层内容读成盒子条目。
///
/// 只读，绝不创建/复制/移动/删除任何东西（P4）。映射只是"换个地方看这个文件夹"，
/// 腾讯桌面整理「释放 C 盘」卖点的关键也在这里：**实体一个都不搬**。
/// </summary>
public static class FolderMap
{
    /// <summary>默认最多映射多少条。大目录（比如几千个文件）全塞进盒子只会把界面拖垮。</summary>
    public const int DefaultLimit = 500;

    /// <summary>映射目标不可用时使用的条目 Id，供界面识别。</summary>
    public const string UnavailableId = "map:unavailable";

    /// <summary>
    /// 扫描目录顶层内容：目录在前、文件在后，各自按名称排序，最多 <paramref name="limit"/> 条。
    /// 目录不存在或读不了时返回**一条醒目的提示条目**，而不是空列表——
    /// 静默失败会让用户以为"这个文件夹本来就是空的"。
    /// </summary>
    public static IReadOnlyList<BoxItem> Scan(string? folder, int limit = DefaultLimit)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [Unavailable(folder)];
        }

        try
        {
            var entries = Directory.GetFileSystemEntries(folder)
                .Select(path => (Path: path, IsDirectory: Directory.Exists(path)))
                .OrderByDescending(entry => entry.IsDirectory)
                // 用当前区域性比较：资源管理器也是按语言习惯排的（中文按拼音），
                // 用 Ordinal 会把「甲」排到「乙」后面，用户看着就是乱的
                .ThenBy(entry => Path.GetFileName(entry.Path), StringComparer.CurrentCultureIgnoreCase)
                .Take(Math.Max(1, limit))
                .Select(entry => new BoxItem(
                    IdOf(entry.Path),
                    Path.GetFileName(entry.Path),
                    entry.Path,
                    ItemKind.Mapped))
                .ToList();

            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [Unavailable(folder)];
        }
    }

    /// <summary>映射目标不可用时的提示条目。它指向的是那个文件夹本身，不是被搬来的文件。</summary>
    public static BoxItem Unavailable(string? folder) =>
        new(
            UnavailableId,
            string.IsNullOrWhiteSpace(folder) ? "⚠ 映射目标不可用" : $"⚠ 映射目标不可用：{folder}",
            folder ?? string.Empty,
            ItemKind.Mapped);

    /// <summary>该条目是不是"映射目标不可用"的提示条目。</summary>
    public static bool IsUnavailable(BoxItem item) => item.Id == UnavailableId;

    /// <summary>条目 Id 直接用路径：映射内容会随目录变化反复重建，用稳定 Id 才能让界面少抖。</summary>
    private static string IdOf(string path) => "map:" + path.ToLowerInvariant();
}