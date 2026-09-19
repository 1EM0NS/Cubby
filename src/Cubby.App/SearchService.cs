using System.Diagnostics;
using Cubby.Core.Layout;
using Cubby.Core.Model;

namespace Cubby.App;

/// <summary>
/// 每个盒子一份搜索索引，并负责在条目变化时重建。
///
/// 重建的触发点一共三个，全部是事件驱动，没有任何定时器：
/// 1. 浮层重建（换显示器 / 启动）；
/// 2. 盒子模型变化（拖入、移出、映射目录内容变化）；
/// 3. 映射监视器报错（缓冲区溢出）后的整体重扫——走的就是第 2 条。
/// </summary>
internal sealed class SearchService
{
    private readonly Dictionary<string, BoxSearchIndex> _indexes = new(StringComparer.Ordinal);

    /// <summary>索引重建总次数（诊断用）。</summary>
    public int TotalBuildCount { get; private set; }

    /// <summary>最近一次查询耗时（毫秒），用于验收与诊断。</summary>
    public double LastQueryMilliseconds { get; private set; }

    public int IndexedBoxCount => _indexes.Count;

    public int IndexedItemCount => _indexes.Values.Sum(index => index.Count);

    /// <summary>按当前布局重建全部索引。盒子被删掉时同步清掉它的索引。</summary>
    public void RebuildAll(IReadOnlyList<Box> boxes)
    {
        var live = new HashSet<string>(boxes.Select(b => b.Id), StringComparer.Ordinal);

        foreach (var id in _indexes.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _indexes.Remove(id);
        }

        foreach (var box in boxes)
        {
            if (!_indexes.TryGetValue(box.Id, out var index))
            {
                index = new BoxSearchIndex();
                _indexes[box.Id] = index;
            }

            index.Rebuild(box.Id, box.Items);
            TotalBuildCount++;
        }
    }

    /// <summary>只重建一个盒子的索引（条目变化时用，避免全量重建）。</summary>
    public void RebuildBox(Box box)
    {
        if (!_indexes.TryGetValue(box.Id, out var index))
        {
            index = new BoxSearchIndex();
            _indexes[box.Id] = index;
        }

        index.Rebuild(box.Id, box.Items);
        TotalBuildCount++;
    }

    /// <summary>查询某个盒子的条目。索引不存在（还没建）时返回空集，而不是现建一份。</summary>
    public IReadOnlyList<BoxItem> Query(string boxId, string? text, int limit = 200)
    {
        if (!_indexes.TryGetValue(boxId, out var index))
        {
            return [];
        }

        var watch = Stopwatch.StartNew();
        var result = index.Query(text, limit);
        watch.Stop();

        LastQueryMilliseconds = watch.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>某个盒子当前索引了多少条（诊断用）。</summary>
    public int CountOf(string boxId) =>
        _indexes.TryGetValue(boxId, out var index) ? index.Count : 0;

    public string Describe() =>
        $"{IndexedBoxCount} 个盒子 / {IndexedItemCount} 条索引，重建 {TotalBuildCount} 次，" +
        $"最近一次查询 {LastQueryMilliseconds:0.00} ms";
}