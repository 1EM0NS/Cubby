using System.IO;
using System.Windows;
using Cubby.Core.Model;
using Cubby.Core.Platform;

namespace Cubby.App;

/// <summary>
/// 映射文件夹的同步器：按布局维护"一个映射盒子一个监视器"，目录一变就重扫并推给界面。
///
/// 它是**唯一**会在用户没操作时修改盒子模型的地方，因此有两条硬规矩：
/// 1. 重扫只读目录、只改布局里那条 `Items` 引用，绝不碰磁盘上的任何文件（P4）；
/// 2. 内容没变就什么都不做——FileSystemWatcher 的抖动足以把界面刷爆。
/// </summary>
internal sealed class MappedFolderService : IDisposable
{
    private readonly LayoutService _layout;
    private readonly Action<Box> _applyToView;
    private readonly Dictionary<string, MappedFolderWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    public MappedFolderService(LayoutService layout, Action<Box> applyToView)
    {
        _layout = layout;
        _applyToView = applyToView;
    }

    public int WatcherCount
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Count;
            }
        }
    }

    /// <summary>所有监视器累计的重扫次数（诊断与验收用）。</summary>
    public int TotalRescanCount
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Values.Sum(w => w.RescanCount);
            }
        }
    }

    /// <summary>累计的缓冲区溢出重建次数。</summary>
    public int TotalOverflowCount
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Values.Sum(w => w.OverflowRescanCount);
            }
        }
    }

    /// <summary>每个映射盒子一行诊断：监听状态、重扫次数、溢出次数。</summary>
    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _watchers
                    .Select(pair =>
                    {
                        var box = _layout.Boxes.FirstOrDefault(b => b.Id == pair.Key);
                        return $"  {box?.Name ?? pair.Key,-14} {pair.Value.Folder}  " +
                               $"{(pair.Value.Unavailable ? "不可用" : "监听中")}  " +
                               $"重扫 {pair.Value.RescanCount} 次（其中溢出重建 {pair.Value.OverflowRescanCount} 次）" +
                               (pair.Value.LastDiagnostic is { } diagnostic ? $"  {diagnostic}" : string.Empty);
                    })
                    .ToList();
            }
        }
    }

    /// <summary>按当前布局调整监视器：新映射建监视器，取消映射销毁监视器，换目录则重建。</summary>
    public void Sync(IReadOnlyList<Box> boxes)
    {
        if (_disposed)
        {
            return;
        }

        var mapped = boxes.Where(b => !string.IsNullOrWhiteSpace(b.MappedFolder)).ToList();

        lock (_gate)
        {
            foreach (var id in _watchers.Keys.ToList())
            {
                var stillWanted = mapped.Any(b => b.Id == id && b.MappedFolder == _watchers[id].Folder);
                if (!stillWanted)
                {
                    _watchers[id].Dispose();
                    _watchers.Remove(id);
                }
            }

            foreach (var box in mapped)
            {
                if (_watchers.ContainsKey(box.Id))
                {
                    continue;
                }

                var watcher = new MappedFolderWatcher(box.MappedFolder!);
                var boxId = box.Id;
                watcher.Changed += (_, _) => OnFolderChanged(boxId);
                _watchers[boxId] = watcher;

                // 建监视器时目录就不可用（比如刚被删掉）：立刻落一条提示条目，别让它静默
                if (watcher.Unavailable)
                {
                    OnFolderChanged(boxId);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }

    /// <summary>
    /// FileSystemWatcher 的回调在工作线程上，而盒子模型与界面都归 UI 线程管，必须切回来。
    /// （这也是"空闲时 CPU≈0"的前提：只有事件来了才干活，没有任何轮询。）
    /// </summary>
    private void OnFolderChanged(string boxId)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Rescan(boxId);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => Rescan(boxId)));
    }

    /// <summary>重扫某个映射盒子。内容没变就直接返回，避免无意义的重绘与落盘。</summary>
    internal void Rescan(string boxId)
    {
        var box = _layout.Boxes.FirstOrDefault(b => b.Id == boxId);
        if (box?.MappedFolder is null)
        {
            return;
        }

        var items = FolderMap.Scan(box.MappedFolder);
        if (SameItems(box.Items, items))
        {
            return;
        }

        var updated = box with { Items = items };
        _layout.UpdateBox(updated);
        _applyToView(updated);
    }

    private static bool SameItems(IReadOnlyList<BoxItem> left, IReadOnlyList<BoxItem> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].TargetPath, right[i].TargetPath, StringComparison.OrdinalIgnoreCase) ||
                left[i].DisplayName != right[i].DisplayName)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把一个目录映射到盒子（首次扫描）。目录不可用时返回 false 并给出原因。</summary>
    public static bool TryMap(Box box, string folder, out Box updated, out string diagnostic)
    {
        updated = box;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            diagnostic = $"「{folder}」不存在或不可访问，没有建立映射。";
            return false;
        }

        var items = FolderMap.Scan(folder);
        updated = box with { MappedFolder = folder, Items = items, IsCollapsed = false };
        diagnostic = $"已映射「{folder}」（{items.Count} 条内容）。Cubby 不会移动或复制任何文件。";
        return true;
    }
}