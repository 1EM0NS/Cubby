namespace Cubby.Core.Platform;

/// <summary>
/// 监视一个被映射的文件夹，把「目录变了」合并成一次通知。
///
/// 三个必须做对的点：
/// 1. **合并抖动**：一次保存可能产生好几个事件，直接每次都重扫会把界面刷爆；
/// 2. **缓冲区调大并处理 <c>Error</c>**：默认 8KB 缓冲在批量增删时必然溢出，
///    溢出后事件会丢——此时唯一的正确做法是**整体重扫**，而不是装作没事；
/// 3. **不崩**：目录被删掉、被拔盘、没权限……一律降级为"不可用"，绝不抛到调用方。
/// </summary>
public sealed class MappedFolderWatcher : IDisposable
{
    private readonly TimeSpan _debounce;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _timer;
    private bool _queued;
    private bool _disposed;

    public MappedFolderWatcher(string folder, TimeSpan? debounce = null)
    {
        Folder = folder;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            if (!Directory.Exists(folder))
            {
                Unavailable = true;
                LastDiagnostic = "目录不存在或不可访问";
                return;
            }

            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                // 默认 8KB 太容易溢出，一次批量解压就能把事件冲掉
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };

            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.Error += OnError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Unavailable = true;
            LastDiagnostic = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public string Folder { get; }

    /// <summary>目录现在是否不可用（不存在 / 没权限 / 建监视器失败）。</summary>
    public bool Unavailable { get; private set; }

    /// <summary>实际触发的重扫次数（诊断用）。</summary>
    public int RescanCount { get; private set; }

    /// <summary>缓冲区溢出导致的重扫次数。>0 说明事件确实丢过，但内容已经整体重建。</summary>
    public int OverflowRescanCount { get; private set; }

    public string? LastDiagnostic { get; private set; }

    /// <summary>目录内容变化（可能是抖动合并后的一次）。</summary>
    public event EventHandler? Changed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Changed -= OnChanged;
            _watcher.Renamed -= OnChanged;
            _watcher.Error -= OnError;
            _watcher.Dispose();
        }

        _timer.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Queue();

    private void OnError(object sender, ErrorEventArgs e)
    {
        // 走到这里多半是缓冲区溢出（也有可能是目录被拔掉）。事件已经丢了，
        // 唯一正确的处理是立刻整体重扫一次，而不是假装没发生。
        OverflowRescanCount++;
        RequestRescan($"监视器错误（多为缓冲区溢出）：{e.GetException().Message}");
    }

    /// <summary>
    /// 强制整体重扫一次并立即通知。
    ///
    /// 语义是"我可能漏掉事件了，重来一遍"——缓冲区溢出的 <c>Error</c> 事件走的就是这里。
    /// 之所以做成公开方法，是因为**真实溢出无法稳定复现**（要一瞬间灌进上千个事件），
    /// 验收只能直接驱动这条路径；把路径本身做成可调用的，比在测试里抠私有方法诚实得多。
    /// </summary>
    public void RequestRescan(string reason)
    {
        if (_disposed)
        {
            return;
        }

        LastDiagnostic = reason;
        RescanCount++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把短时间内的多个事件合并成一次通知。</summary>
    private void Queue()
    {
        if (_disposed)
        {
            return;
        }

        if (_queued)
        {
            return;
        }

        _queued = true;

        try
        {
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 正在 Dispose，忽略
        }
    }

    private void Flush()
    {
        _queued = false;

        if (_disposed)
        {
            return;
        }

        RescanCount++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}