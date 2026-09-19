using System.IO;
using System.Text;
using System.Windows;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 管理「每台显示器一个浮层窗口」，并负责显示器变化后的重建。
///
/// 当前策略是**整体重建**：显示器数量/分辨率/DPI 变化时，枚举当前显示器、
/// 重新规划盒子归属、销毁旧窗口并按新显示器创建新窗口。
/// 代价是切换瞬间有一次闪烁（这种事件本身很稀有）；增量复用窗口留待后续优化。
/// </summary>
internal sealed class OverlayManager
{
    private readonly List<SpikeWindow> _windows = [];

    public IReadOnlyList<SpikeWindow> Windows => _windows;

    public IReadOnlyList<MonitorSurface> Monitors { get; private set; } = [];

    public IReadOnlyList<MonitorPlan> Plans { get; private set; } = [];

    /// <summary>重建次数，用于诊断（也让"显示器变化真的被处理了"可被验证）。</summary>
    public int RebuildCount { get; private set; }

    public string LastRebuildReason { get; private set; } = "(尚未发生)";

    /// <summary>最近一次重建的时间，便于人工核对与屏幕变化是否对应。</summary>
    public string? LastRebuildAt { get; private set; }

    public SpikeWindow? PrimaryWindow =>
        _windows.FirstOrDefault(w => w.Surface?.IsPrimary == true) ?? _windows.FirstOrDefault();

    public void Start()
    {
        Rebuild("启动");
    }

    /// <summary>重新枚举显示器并重建浮层。可从界面按钮手动触发，也会被显示变化事件触发。</summary>
    public void Rebuild(string reason)
    {
        CloseAll();

        Monitors = MonitorSurfaces.Enumerate();

        // 演示阶段：每台显示器各自的盒子，再走一遍分配计划（证明这条路径是通的）
        var boxes = Monitors.SelectMany(SpikeDemoLayout.CreateFor).ToList();
        Plans = OverlayPlanner.Plan(Monitors, boxes);

        foreach (var plan in Plans)
        {
            var window = new SpikeWindow(plan.Monitor, plan.Boxes.Select(b => b.Box).ToList());
            window.DisplayChanged += OnWindowDisplayChanged;
            _windows.Add(window);
        }

        foreach (var window in _windows)
        {
            // Show 之后才有 HWND，窗口在 OnSourceInitialized 里完成浮层初始化
            window.Show();
        }

        RebuildCount++;
        LastRebuildReason = reason;
        LastRebuildAt = DateTime.Now.ToString("HH:mm:ss.fff");

        AppendRebuildLog(reason);
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

    public void Stop()
    {
        CloseAll();
    }

    /// <summary>某个窗口句柄是否属于我们的任一浮层（多屏下不能只比一个句柄）。</summary>
    public bool IsOurOverlay(nint hwnd) =>
        hwnd != 0 && _windows.Any(w => DesktopProbe.BelongsTo(hwnd, w.Host?.Handle ?? 0));

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
                $"  {m.Id,-16} {m.Bounds} DPI 缩放 {m.DpiScale:0.##}{(m.IsPrimary ? "  [主屏]" : string.Empty)}"));
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