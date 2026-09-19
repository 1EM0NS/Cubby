using System.IO;
using System.Windows.Threading;
using Cubby.Core.Model;
using Cubby.Core.Storage;

namespace Cubby.App;

/// <summary>
/// 布局的加载、默认创建与延迟保存。
///
/// 拖动盒子时鼠标移动事件非常密集，若每次都落盘会白白写盘，所以用 <see cref="DispatcherTimer"/>
/// 合并写入：最后一次变化之后静默一段时间才真正保存。
/// </summary>
internal sealed class LayoutService
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly LayoutStore _store;
    private readonly DispatcherTimer _saveTimer;
    private LayoutDocument _document;

    public LayoutService(string filePath)
    {
        _store = new LayoutStore(filePath);
        _document = _store.Load();

        var directory = Path.GetDirectoryName(filePath);
        Snapshots = new SnapshotStore(Path.Combine(directory ?? AppContext.BaseDirectory, "snapshots"));

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    public string FilePath => _store.FilePath;

    /// <summary>快照目录与读写（与 layout.json 同一套序列化与版本校验）。</summary>
    public SnapshotStore Snapshots { get; }

    /// <summary>当前内存里的完整布局文档。快照与还原都需要它，而不只是盒子列表。</summary>
    public LayoutDocument Document => _document;

    /// <summary>上次读取布局时发现的问题（正常为 null）。</summary>
    public string? LoadDiagnostic => _store.LastLoadDiagnostic;

    public IReadOnlyList<Box> Boxes => _document.Boxes;

    public StyleSettings Style => _document.Style.Normalized();

    public int SaveCount { get; private set; }

    public string? LastSaveAt { get; private set; }

    public bool HasPendingSave => _saveTimer.IsEnabled;

    /// <summary>
    /// 布局为空（首次运行，或上次布局损坏被降级）时，按每台显示器创建一个空盒子，
    /// 避免用户面对一个什么都不做的程序。
    /// </summary>
    public void EnsureDefaults(IReadOnlyList<MonitorSurface> monitors)
    {
        if (_document.Boxes.Count > 0 || monitors.Count == 0)
        {
            return;
        }

        var style = Style;
        var boxes = monitors
            .Select(monitor => new Box($"{monitor.Id}-1", "盒子 1", new DipRect(60, 80, 420, 320))
            {
                MonitorId = monitor.Id,
                MonitorWidth = monitor.Bounds.Width,
                MonitorHeight = monitor.Bounds.Height,
                Columns = style.Columns,
                Opacity = style.Opacity,
            })
            .ToList();

        _document = _document with { Boxes = boxes, Monitors = monitors };
        SaveNow();
    }

    public void UpdateBox(Box box)
    {
        var boxes = _document.Boxes.ToList();
        var index = boxes.FindIndex(b => b.Id == box.Id);
        if (index < 0)
        {
            return;
        }

        boxes[index] = box;
        _document = _document with { Boxes = boxes };
        SaveLater();
    }

    public void UpdateMonitors(IReadOnlyList<MonitorSurface> monitors) =>
        _document = _document with { Monitors = monitors };

    /// <summary>更新全局样式。列数与透明度同时写进每个盒子（便于将来做单盒覆盖）。</summary>
    public void UpdateStyle(StyleSettings style)
    {
        var normalized = style.Normalized();
        var boxes = _document.Boxes
            .Select(b => b with { Columns = normalized.Columns, Opacity = normalized.Opacity })
            .ToList();

        _document = _document with { Style = normalized, Boxes = boxes };
        SaveLater();
    }

    public void SaveLater()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>快照保留份数（0 表示不自动清理）。</summary>
    public int SnapshotKeep
    {
        get => _document.SnapshotKeep;
        set
        {
            _document = _document with { SnapshotKeep = Math.Max(0, value) };
            SaveLater();
        }
    }

    /// <summary>桌面图标显隐的开关（个别 Windows 版本上 ShowWindow 行为不稳，留的降级开关）。</summary>
    public bool DesktopIconToggleEnabled
    {
        get => _document.EnableDesktopIconToggle;
        set
        {
            _document = _document with { EnableDesktopIconToggle = value };
            SaveLater();
        }
    }

    /// <summary>用户已确认过的同类软件（A8）。</summary>
    public IReadOnlyList<string> CoexistAcknowledged => _document.CoexistAcknowledgedTools;

    /// <summary>记住用户已经确认过这些同类软件，下次不再打扰。</summary>
    public void AcknowledgeCoexist(IEnumerable<string> processNames)
    {
        var merged = _document.CoexistAcknowledgedTools
            .Concat(processNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (merged.Count == _document.CoexistAcknowledgedTools.Count)
        {
            return;
        }

        _document = _document with { CoexistAcknowledgedTools = merged };
        SaveLater();
    }

    /// <summary>把已确认清单恢复成指定内容（自动化验收用来还原现场）。</summary>
    public void RestoreCoexistAcknowledged(IReadOnlyList<string> processNames)
    {
        _document = _document with { CoexistAcknowledgedTools = [.. processNames] };
        SaveLater();
    }

    /// <summary>创建一份快照，遵守当前的保留策略。</summary>
    public SnapshotInfo CreateSnapshot(string? label = null) =>
        Snapshots.Create(_document, label, _document.SnapshotKeep);

    /// <summary>
    /// 用一份快照整体替换当前布局并立即落盘（还原）。
    /// 只改 Cubby 自己的配置文件，不碰任何用户文件（P4）。
    /// </summary>
    public void Apply(LayoutDocument document)
    {
        _saveTimer.Stop();
        _document = document with
        {
            SchemaVersion = LayoutSchema.CurrentVersion,
            SavedAt = null,
        };

        SaveNow();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();

        try
        {
            _store.Save(_document);
            SaveCount++;
            LastSaveAt = DateTime.Now.ToString("HH:mm:ss");
        }
        catch (IOException ex)
        {
            LastSaveAt = $"失败：{ex.Message}";
        }
    }
}