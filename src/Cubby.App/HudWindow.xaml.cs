using System.Text;
using System.Windows;
using System.Windows.Threading;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 诊断面板。存在的意义是让「人工验证」有据可依：
/// 鼠标移到任意位置都能立刻看到这一刻的点击会落到谁身上，而不是靠肉眼猜。
/// </summary>
public partial class HudWindow : Window
{
    private readonly OverlayManager _manager;
    private readonly LayoutService _layout;
    private readonly DispatcherTimer _timer;
    private HitMode _mode = HitMode.PerPixelAlpha;

    internal HudWindow(OverlayManager manager, LayoutService layout)
    {
        InitializeComponent();
        _manager = manager;
        _layout = layout;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Closed += (_, _) =>
        {
            // 只停自己的刷新定时器：浮层与托盘的存活由 App 统一管，关掉诊断面板不等于退出程序
            _timer.Stop();
        };
    }

    private void Refresh()
    {
        var overlay = _manager.PrimaryWindow;
        var builder = new StringBuilder();

        builder.AppendLine("== 显示器 ==");
        builder.AppendLine(_manager.DescribeMonitors());
        builder.AppendLine($"浮层窗口数：{_manager.Windows.Count}");
        builder.AppendLine($"重建次数：{_manager.RebuildCount}    最近一次：{_manager.LastRebuildReason} @ {_manager.LastRebuildAt ?? "(无)"}");
        builder.AppendLine();

        builder.AppendLine("== 布局 ==");
        builder.AppendLine($"文件       : {_layout.FilePath}");
        builder.AppendLine($"盒子总数   : {_layout.Boxes.Count}");
        builder.AppendLine(
            $"保存次数   : {_layout.SaveCount}    最近保存：{_layout.LastSaveAt ?? "(尚未保存)"}" +
            $"{(_layout.HasPendingSave ? "    [待写入]" : string.Empty)}");
        builder.AppendLine(
            $"样式       : 透明度 {_layout.Style.Opacity:0.##} / 圆角 {_layout.Style.CornerRadius:0} / " +
            $"列数 {_layout.Style.Columns} / 字号 {_layout.Style.FontSize:0}");
        if (_layout.LoadDiagnostic is { } diagnostic)
        {
            builder.AppendLine($"载入诊断   : {diagnostic}");
        }

        if (_manager.LastOpenError is { } openError)
        {
            builder.AppendLine($"打开失败   : {openError}");
        }

        if (_manager.LastAdoptSummary is { } adopt)
        {
            builder.AppendLine($"吸附结果   : {adopt}");
        }

        builder.AppendLine();
        builder.AppendLine("== 盒子 ==");
        foreach (var box in _layout.Boxes)
        {
            builder.AppendLine(
                $"  {box.Name,-14} ({box.Bounds.X:0},{box.Bounds.Y:0}) {box.Bounds.Width:0}×{box.Bounds.Height:0}  " +
                $"锁定={box.IsLocked} 折叠={box.IsCollapsed} 条目={box.Items.Count}");
        }

        builder.AppendLine();

        if (overlay?.Host is null || overlay.Surface is null)
        {
            builder.AppendLine("（主屏浮层尚未初始化）");
            DiagText.Text = builder.ToString();
            return;
        }

        var hwnd = overlay.Host.Handle;
        var info = DesktopProbe.Describe(hwnd);
        var snapshot = DesktopProbe.ZOrderSnapshot(400);
        var index = -1;
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (snapshot[i].Handle == hwnd)
            {
                index = i;
                break;
            }
        }

        builder.AppendLine($"== 主屏浮层（{overlay.Surface.Id}）==");
        builder.AppendLine($"命中模式     : {DescribeMode(_mode)}");
        builder.AppendLine($"窗口句柄     : 0x{hwnd.ToInt64():X8}");
        builder.AppendLine($"可见         : {info?.IsVisible}");
        builder.AppendLine($"Z 序序号     : {index} / 共 {snapshot.Count} 个可见顶层窗口（0 为最顶层）");
        builder.AppendLine($"置底触发次数 : {overlay.Host.EnsureBehindCount}");
        builder.AppendLine($"区域设置次数 : {overlay.Host.RegionApplyCount}");
        builder.AppendLine($"收到左键     : {overlay.OverlayClickCount}    右键：{overlay.OverlayRightClickCount}");
        builder.AppendLine($"命中区域     : {overlay.DescribeRegions()}");
        builder.AppendLine();

        if (MouseClicker.TryGetCursorPosition(out var x, out var y))
        {
            var probe = DesktopProbe.WindowAt(x, y, 0);
            var isOurs = _manager.IsOurOverlay(probe.Handle);

            builder.AppendLine($"光标位置     : ({x}, {y})");
            builder.AppendLine($"光标命中窗口 : {(isOurs ? "★ 我们的浮层（该点点击会被拦截）" : "其他窗口（该点点击会放行）")}");
            builder.AppendLine($"              class={probe.ClassName}  pid={probe.ProcessId}  title={probe.Title}");
            if (!isOurs && probe.IsDesktopLayer)
            {
                builder.AppendLine("              这是桌面图层，说明点击落到了桌面本身 ✓");
            }

            builder.AppendLine();
        }

        builder.AppendLine("== Z 序最底部 5 个窗口（判断浮层有没有被压到 Progman 之下）==");
        foreach (var window in snapshot.TakeLast(5))
        {
            builder.AppendLine($"  {window.Describe()}");
        }

        DiagText.Text = builder.ToString();
    }

    private static string DescribeMode(HitMode mode) =>
        mode == HitMode.PerPixelAlpha
            ? "A 逐像素 alpha（不设置窗口区域）"
            : "B 窗口区域（区域之外一律放行）";

    private void OnToggleMode(object sender, RoutedEventArgs e)
    {
        _mode = _mode == HitMode.PerPixelAlpha ? HitMode.WindowRegion : HitMode.PerPixelAlpha;

        foreach (var overlay in _manager.Windows)
        {
            overlay.ApplyMode(_mode);
        }

        Refresh();
    }

    private void OnEnsureBehind(object sender, RoutedEventArgs e)
    {
        foreach (var overlay in _manager.Windows)
        {
            overlay.EnsureBehind();
        }

        Refresh();
    }

    private void OnRebuild(object sender, RoutedEventArgs e)
    {
        _manager.Rebuild("手动触发（界面按钮）");
        Refresh();
        StatusText.Text = $"已重建：{_manager.Windows.Count} 个浮层窗口，检测到 {_manager.Monitors.Count} 台显示器";
    }

    private async void OnRunSelfTest(object sender, RoutedEventArgs e)
    {
        var overlay = _manager.PrimaryWindow;
        if (overlay is null)
        {
            StatusText.Text = "没有可用的浮层窗口，自测无法进行。";
            return;
        }

        SelfTestButton.IsEnabled = false;
        StatusText.Text = "自测进行中：会短暂移动鼠标并在盒子内单击……";

        try
        {
            var code = await SelfTestRunner.RunAsync(overlay, new SpikeOptions(true, null, false, false, false));
            StatusText.Text = code == 0
                ? "自测通过（exit 0），报告已写入 artifacts/"
                : $"自测未通过（exit {code}），请看 artifacts/ 下的报告";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"自测异常：{ex.Message}";
        }
        finally
        {
            SelfTestButton.IsEnabled = true;
            Refresh();
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}