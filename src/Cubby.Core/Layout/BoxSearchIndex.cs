using Cubby.Core.Model;

namespace Cubby.Core.Layout;

/// <summary>
/// 盒子条目的内存索引。纯托管、无 IO、无 Win32，因此可以直接测「500 条之内查询耗时」。
///
/// 设计要点：
/// 1. **预计算小写**：查询时不做大小写转换，避免每次输入都重新分配字符串；
/// 2. **不排序整表**：只在命中集里分档（名称前缀 &gt; 名称包含 &gt; 路径包含），
///    500 条规模下就是微秒级；
/// 3. **重建而非轮询**：索引只在条目真正变化时重建（映射目录的变化也走同一条路），
///    空闲时一次都不跑——这是"空闲 CPU≈0"的前提。
/// </summary>
public sealed class BoxSearchIndex
{
    private readonly List<Entry> _entries = [];

    private readonly record struct Entry(BoxItem Item, string Name, string Path);

    /// <summary>重建次数（诊断：能证明"目录变化后索引确实重建了"）。</summary>
    public int BuildCount { get; private set; }

    /// <summary>最近一次重建对应哪个盒子（空索引时为 null）。</summary>
    public string? BoxId { get; private set; }

    public int Count => _entries.Count;

    /// <summary>整体重建索引。条目里的展示名与路径会预先转成小写。</summary>
    public void Rebuild(string boxId, IReadOnlyList<BoxItem> items)
    {
        BoxId = boxId;
        BuildCount++;
        _entries.Clear();

        foreach (var item in items)
        {
            _entries.Add(new Entry(
                item,
                item.DisplayName.ToLowerInvariant(),
                item.TargetPath.ToLowerInvariant()));
        }
    }

    /// <summary>
    /// 查询。空查询返回全部（受 <paramref name="limit"/> 限制）。
    /// 命中优先级：展示名前缀 → 展示名包含 → 路径包含；同一档内按展示名排序，保证结果稳定。
    /// </summary>
    public IReadOnlyList<BoxItem> Query(string? text, int limit = 200)
    {
        var maximum = Math.Max(1, limit);
        var term = text?.Trim().ToLowerInvariant() ?? string.Empty;

        if (term.Length == 0)
        {
            return _entries.Take(maximum).Select(entry => entry.Item).ToList();
        }

        var hits = new List<(int Rank, string Name, BoxItem Item)>();

        foreach (var entry in _entries)
        {
            var rank = RankOf(entry, term);
            if (rank >= 0)
            {
                hits.Add((rank, entry.Name, entry.Item));
            }
        }

        return hits
            .OrderBy(hit => hit.Rank)
            .ThenBy(hit => hit.Name, StringComparer.Ordinal)
            .Take(maximum)
            .Select(hit => hit.Item)
            .ToList();
    }

    private static int RankOf(Entry entry, string term)
    {
        if (entry.Name.StartsWith(term, StringComparison.Ordinal))
        {
            return 0;
        }

        if (entry.Name.Contains(term, StringComparison.Ordinal))
        {
            return 1;
        }

        return entry.Path.Contains(term, StringComparison.Ordinal) ? 2 : -1;
    }
}