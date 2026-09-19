using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 管理「每台显示器一个浮层窗口」，把布局分配到各屏，并处理显示器变化后的重建。
///
/// 当前重建策略是**整体重建**：显示器数量/分辨率/DPI 变化时，枚举当前显示器、
/// 重新规划盒子归属、销毁旧窗口并按新显示器创建新窗口。
/// 代价是切换瞬间有一次闪烁（事件本身很稀有）；增量复用窗口留待后续优化。
/// </summary>
internal sealed class OverlayManager : IBoxChangeSink
{
    private readonly LayoutService _layout;
    private readonly List<OverlayWindow> _windows = [];

    /// <summary>盒子是否可见。重建浮层后要按这个状态恢复，否则显示器一变盒子就自己冒出来。</summary>
    private bool _boxesVisible = true;

    public OverlayManager(LayoutService layout) => _layout = layout;

    public IReadOnlyList<OverlayWindow> Windows => _windows;

    public IReadOnlyList<MonitorSurface> Monitors { get; private set; } = [];

    public IReadOnlyList<MonitorPlan> Plans { get; private set; } = [];

    public int RebuildCount { get; private set; }

    public string LastRebuildReason { get; private set; } = "(尚未发生)";

    public string? LastRebuildAt { get; private set; }

    /// <summary>最近一次打开条目失败的原因（正常为 null）。</summary>
    public string? LastOpenError { get; private set; }

    /// <summary>最近一次桌面图标吸附的结果摘要（正常为 null）。</summary>
    public string? LastAdoptSummary { get; private set; }

    public OverlayWindow? PrimaryWindow =>
        _windows.FirstOrDefault(w => w.Surface?.IsPrimary == true) ?? _windows.FirstOrDefault();

    public void Start() => Rebuild("启动");

    /// <summary>重新枚举显示器并按布局重建浮层。可由界面按钮手动触发，也会被显示变化事件触发。</summary>
    public void Rebuild(string reason)
    {
        CloseAll();

        Monitors = MonitorSurfaces.Enumerate();
        _layout.EnsureDefaults(Monitors);
        _layout.UpdateMonitors(Monitors);

        Plans = OverlayPlanner.Plan(Monitors, _layout.Boxes);

        foreach (var plan in Plans)
        {
            var window = new OverlayWindow(
                plan.Monitor,
                plan.Boxes.Select(planned => planned.Box).ToList(),
                _layout.Style,
                this);

            window.DisplayChanged += OnWindowDisplayChanged;
            _windows.Add(window);
        }

        foreach (var window in _windows)
        {
            // Show 之后才有 HWND，窗口在 OnSourceInitialized 里完成浮层初始化
            window.Show();
            window.Host?.SetVisible(_boxesVisible);
        }

        RebuildCount++;
        LastRebuildReason = reason;
        LastRebuildAt = DateTime.Now.ToString("HH:mm:ss.fff");

        AppendRebuildLog(reason);
    }

    public void Stop()
    {
        CloseAll();
        _layout.SaveNow();
    }

    /// <summary>盒子当前是否显示。</summary>
    public bool BoxesVisible => _boxesVisible;

    /// <summary>显示 / 隐藏所有盒子（托盘菜单）。隐藏期间浮层不参与命中测试。</summary>
    public void SetBoxesVisible(bool visible)
    {
        _boxesVisible = visible;

        foreach (var window in _windows)
        {
            window.Host?.SetVisible(visible);
        }
    }

    /// <summary>把全局样式套用到所有浮层（设置窗口拖动滑块时调用）。</summary>
    public void ApplyStyle(StyleSettings style)
    {
        _layout.UpdateStyle(style);

        foreach (var window in _windows)
        {
            window.ChangeStyle(_layout.Style);
        }
    }

    /// <summary>某个窗口句柄是否属于我们的任一浮层（多屏下不能只比一个句柄）。</summary>
    public bool IsOurOverlay(nint hwnd) =>
        hwnd != 0 && _windows.Any(w => DesktopProbe.BelongsTo(hwnd, w.Host?.Handle ?? 0));

    // ---- IBoxChangeSink ----

    public void OnBoxChanged(Box box) => _layout.UpdateBox(box);

    public void OnItemOpen(BoxItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.TargetPath) { UseShellExecute = true });
            LastOpenError = null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            LastOpenError = $"{item.DisplayName}: {ex.Message}";
            MessageBox.Show(
                $"打不开「{item.DisplayName}」：{ex.Message}",
                "Cubby",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>在资源管理器中定位条目。只读操作，不改变磁盘上的任何东西。</summary>
    public void OnItemReveal(BoxItem item)
    {
        try
        {
            // /select 需要目标已存在；不存在时退回打开其所在目录
            var target = File.Exists(item.TargetPath) || Directory.Exists(item.TargetPath)
                ? item.TargetPath
                : Path.GetDirectoryName(item.TargetPath);

            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            LastOpenError = null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            LastOpenError = $"{item.DisplayName}: {ex.Message}";
        }
    }

    public void OnAdoptReport(string summary) => LastAdoptSummary = summary;

    /// <summary>诊断用的显示器摘要。</summary>
    public string DescribeMonitors()
    {
        if (Monitors.Count == 0)
        {
            return "（未枚举到显示器）";
        }

        return string.Join(
            Environment.NewLine,
            Monitors.Select(m =>
                $"  {m.Id,-16} {m.Bounds}  像素 {m.Bounds.Width}×{m.Bounds.Height}  " +
                $"DPI 缩放 {m.DpiScale:0.##}{(m.IsPrimary ? "  [主屏]" : string.Empty)}"));
    }

    private void OnWindowDisplayChanged(object? sender, int message)
    {
        // 显示器变化是在窗口过程里收到的，此时不能立刻销毁窗口（还在处理这条消息），
        // 因此派发到消息队列末尾再重建。
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => Rebuild(message switch
            {
                0x007E => "WM_DISPLAYCHANGE",
                0x02E0 => "WM_DPICHANGED",
                _ => $"消息 0x{message:X4}",
            })));
    }

    /// <summary>
    /// 把每次重建追加到诊断日志。既方便现场排查，也让「显示器变化真的被处理了」这件事可被自动取证。
    /// （M3 做崩溃日志时会统一搬到 %AppData%。）
    /// </summary>
    private void AppendRebuildLog(string reason)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(directory);

            var line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {reason,-22} | " +
                $"显示器 {Monitors.Count} 台 | 浮层窗口 {_windows.Count} 个 | " +
                $"盒子 {_layout.Boxes.Count} 个 | " +
                string.Join(" ; ", Monitors.Select(m => $"{m.Id} {m.Bounds.Width}x{m.Bounds.Height}@{m.DpiScale:0.##}")) +
                Environment.NewLine;

            File.AppendAllText(
                Path.Combine(directory, "overlay-rebuilds.log"),
                line,
                new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // 日志写不进去不影响主流程
        }
    }

    private void CloseAll()
    {
        foreach (var window in _windows)
        {
            window.DisplayChanged -= OnWindowDisplayChanged;
            window.Close();
        }

        _windows.Clear();
    }
}