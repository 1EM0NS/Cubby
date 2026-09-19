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
    private readonly SpikeWindow _overlay;
    private readonly DispatcherTimer _timer;
    private HitMode _mode = HitMode.PerPixelAlpha;

    public HudWindow(SpikeWindow overlay)
    {
        InitializeComponent();
        _overlay = overlay;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var hwnd = _overlay.Host?.Handle ?? 0;
        if (hwnd == 0)
        {
            DiagText.Text = "浮层尚未初始化";
            return;
        }

        var surface = _overlay.Surface!;
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

        var builder = new StringBuilder();
        builder.AppendLine($"命中模式     : {DescribeMode(_mode)}");
        builder.AppendLine($"浮层窗口     : 0x{hwnd.ToInt64():X8}");
        builder.AppendLine($"浮层可见     : {info?.IsVisible}");
        builder.AppendLine($"Z 序序号     : {index} / 共 {snapshot.Count} 个可见顶层窗口（0 为最顶层）");
        builder.AppendLine($"置底触发次数 : {_overlay.Host!.EnsureBehindCount}");
        builder.AppendLine($"区域设置次数 : {_overlay.Host.RegionApplyCount}");
        builder.AppendLine($"浮层收到左键 : {_overlay.OverlayClickCount}    右键：{_overlay.OverlayRightClickCount}");
        builder.AppendLine($"显示器       : {surface.Bounds}  DPI 缩放 {surface.DpiScale:0.##}");
        builder.AppendLine($"命中区域     : {_overlay.DescribeRegions()}");
        builder.AppendLine();

        if (MouseClicker.TryGetCursorPosition(out var x, out var y))
        {
            var probe = DesktopProbe.WindowAt(x, y, hwnd);
            builder.AppendLine($"光标位置     : ({x}, {y})");
            builder.AppendLine($"光标命中窗口 : {(probe.IsOurs ? "★ 我们的浮层（该点点击会被拦截）" : "其他窗口（该点点击会放行）")}");
            builder.AppendLine($"              class={probe.ClassName}  pid={probe.ProcessId}  title={probe.Title}");
            builder.AppendLine($"              {(probe.IsDesktopLayer ? "这是桌面图层，说明点击落到了桌面本身 ✓" : string.Empty)}");
            builder.AppendLine();
        }

        builder.AppendLine("Z 序最底部 5 个窗口（判断浮层有没有被压到 Progman 之下）：");
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
        _overlay.ApplyMode(_mode);
        Refresh();
    }

    private void OnEnsureBehind(object sender, RoutedEventArgs e)
    {
        _overlay.EnsureBehind();
        Refresh();
    }

    private async void OnRunSelfTest(object sender, RoutedEventArgs e)
    {
        SelfTestButton.IsEnabled = false;
        StatusText.Text = "自测进行中：会短暂移动鼠标并在盒子内单击……";

        try
        {
            var code = await SelfTestRunner.RunAsync(_overlay, new SpikeOptions(SelfTest: true, OutputDirectory: null));
            StatusText.Text = code == 0
                ? $"自测通过（exit 0），报告已写入 artifacts/"
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