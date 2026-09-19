using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Cubby.Core;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 浮层窗口：一台显示器一个实例，覆盖该显示器的完整物理范围。
/// 它同时是：
/// 1. 被验证的对象——逐像素 alpha 与区域窗口两种命中机制；
/// 2. 自动化的观测点——统计自己收到了多少次左键，用来证明「盒子内确实被拦截」或「确实没被拦截」。
///
/// 命名说明：类名沿用了 spike 阶段的叫法，真正的产品化改造（盒子交互、布局接线）在 issue #6 里做。
/// </summary>
public partial class SpikeWindow : Window
{
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;

    private readonly List<Box> _boxes;

    public SpikeWindow(MonitorSurface surface, IReadOnlyList<Box> boxes)
    {
        InitializeComponent();

        Surface = surface;
        _boxes = [.. boxes];
    }

    /// <summary>显示状态发生变化（分辨率 / DPI / 显示器增减），由 OverlayManager 决定如何处理。</summary>
    public event EventHandler<int>? DisplayChanged;

    public OverlayHost? Host { get; private set; }

    public MonitorSurface? Surface { get; private set; }

    public IReadOnlyList<Box> Boxes => _boxes;

    /// <summary>盒子换算成物理像素后的命中矩形。</summary>
    public IReadOnlyList<PixelRect> Regions { get; private set; } = [];

    /// <summary>浮层收到的左键按下次数。</summary>
    public int OverlayClickCount { get; private set; }

    public int OverlayRightClickCount { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);

        var hwnd = source.Handle;
        Regions = HitRegion.ToPhysical(_boxes, Surface!);

        Host = new OverlayHost(hwnd);
        Host.Attach(Surface!);
        Host.SetMode(HitMode.PerPixelAlpha, Regions, rounded: false);
        Host.EnsureBehind();
        Host.StartAutoBehind();

        RenderBoxes();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 必须释放：否则每次重建都会残留一组 WinEvent 钩子
        Host?.Dispose();
        Host = null;

        base.OnClosed(e);
    }

    /// <summary>换一组盒子（布局变化时调用），会同步重算命中区域。</summary>
    public void UpdateBoxes(IReadOnlyList<Box> boxes)
    {
        _boxes.Clear();
        _boxes.AddRange(boxes);
        RefreshRegions();
    }

    /// <summary>换到另一台显示器上（分辨率或 DPI 变化时调用）。</summary>
    public void PlaceOn(MonitorSurface surface)
    {
        Surface = surface;
        Host?.Attach(surface);
        RefreshRegions();
        Host?.EnsureBehind();
    }

    /// <summary>切换命中机制并重绘。</summary>
    public void ApplyMode(HitMode mode)
    {
        if (Host is null)
        {
            return;
        }

        Host.SetMode(mode, Regions, rounded: false);

        // 方案 B 会把绘制裁剪到命中区域，圆角会被切掉——这是区域机制已知的代价
        RenderBoxes();
    }

    public void EnsureBehind() => Host?.EnsureBehind();

    public void ResetClickCount() => OverlayClickCount = 0;

    /// <summary>生成探测采样点：盒子中心（期望拦截）+ 右侧透明区（期望放行）。</summary>
    public IReadOnlyList<SamplePoint> SamplePoints()
    {
        var surface = Surface ?? throw new InvalidOperationException("窗口尚未初始化");
        var points = new List<SamplePoint>();

        foreach (var box in _boxes)
        {
            var physical = surface.ToPhysical(box.Bounds);
            points.Add(new SamplePoint(
                $"盒子 {box.Name} 中心",
                (physical.Left + physical.Right) / 2,
                (physical.Top + physical.Bottom) / 2,
                ExpectOurs: true,
                Note: "该点应被浮层拦截，并注入一次左键验证"));
        }

        void AddOutside(string label, double fractionX, double fractionY)
        {
            var x = surface.Bounds.Left + (int)(surface.Bounds.Width * fractionX);
            var y = surface.Bounds.Top + (int)(surface.Bounds.Height * fractionY);
            var accidentallyInside = HitRegion.HitTest(Regions, x, y);

            points.Add(new SamplePoint(
                label,
                x,
                y,
                ExpectOurs: false,
                Note: accidentallyInside
                    ? "采样点误落在命中区域内，本次采样无效"
                    : "透明区域的点：点击必须穿透到受控背景层，背景层收到点击即为穿透成功的直接证据"));
        }

        AddOutside("盒子外 · 右中", 0.72, 0.30);
        AddOutside("盒子外 · 右下", 0.80, 0.60);
        AddOutside("盒子外 · 中下", 0.45, 0.85);

        return points;
    }

    public string DescribeRegions() =>
        Regions.Count == 0 ? "（无）" : string.Join("、", Regions.Select(r => r.ToString()));

    private void RefreshRegions()
    {
        if (Surface is null)
        {
            return;
        }

        Regions = HitRegion.ToPhysical(_boxes, Surface);
        Host?.SetMode(Host.Mode, Regions, rounded: false);
        RenderBoxes();
    }

    private void RenderBoxes()
    {
        BoxLayer.Children.Clear();

        foreach (var box in _boxes)
        {
            var border = new Border
            {
                Width = box.Bounds.Width,
                Height = box.Bounds.Height,
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1F, 0x2A, 0x37)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x0F, 0xDC, 0x78)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16),
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = box.Name,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xE5, 0xE5, 0xE5)),
                FontSize = 16,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"DIP ({box.Bounds.X:0},{box.Bounds.Y:0})  {box.Bounds.Width:0}×{box.Bounds.Height:0}",
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xA1, 0xA1, 0xAA)),
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
            });
            stack.Children.Add(new TextBlock
            {
                Text = "盒子内应该能收到点击；盒子外（透明区）的点击必须原样落到桌面。",
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xA1, 0xA1, 0xAA)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
            });

            border.Child = stack;
            Canvas.SetLeft(border, box.Bounds.X);
            Canvas.SetTop(border, box.Bounds.Y);
            BoxLayer.Children.Add(border);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmLeftButtonDown:
                OverlayClickCount++;
                break;

            case WmRightButtonDown:
                OverlayRightClickCount++;
                break;

            case WmDisplayChange:
            case WmDpiChanged:
                DisplayChanged?.Invoke(this, msg);
                break;
        }

        // 不吞任何消息：交给 WPF 的默认处理流程
        return 0;
    }
}