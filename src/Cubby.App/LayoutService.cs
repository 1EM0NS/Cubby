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

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    public string FilePath => _store.FilePath;

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