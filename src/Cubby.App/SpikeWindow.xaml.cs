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
/// M0 spike 的浮层窗口。它同时是：
/// 1. 被验证的对象——逐像素 alpha 与区域窗口两种命中机制；
/// 2. 自动化的观测点——统计自己收到了多少次左键，用来证明「盒子内确实被拦截」或「确实没被拦截」。
/// </summary>
public partial class SpikeWindow : Window
{
    private const int WmLeftButtonDown = 0x0201;

    private readonly List<Box> _boxes = [];

    public SpikeWindow() => InitializeComponent();

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
        Surface = MonitorSurfaces.Primary(hwnd);

        BuildBoxes(Surface);
        Regions = HitRegion.ToPhysical(_boxes, Surface);

        Host = new OverlayHost(hwnd);
        Host.Attach(Surface);
        Host.SetMode(HitMode.PerPixelAlpha, Regions, rounded: false);
        Host.EnsureBehind();
        Host.StartAutoBehind();

        RenderBoxes();
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

    private void BuildBoxes(MonitorSurface surface)
    {
        // DIP 坐标来自统一布局层；这里放到显示器左上区域，右侧留出透明区用于采样
        _boxes.Add(new Box("A", "盒子 A", new DipRect(60, 80, 420, 300)));
        _boxes.Add(new Box("B", "盒子 B", new DipRect(60, 420, 420, 260)));

        var dipWidth = surface.Bounds.Width / surface.DpiScale;
        var dipHeight = surface.Bounds.Height / surface.DpiScale;

        // 分辨率过小时收缩盒子，避免铺满整屏导致没有透明区可采样
        if (dipHeight < 900)
        {
            _boxes.Clear();
            _boxes.Add(new Box("A", "盒子 A", new DipRect(40, 60, 360, (dipHeight - 200) / 2)));
            _boxes.Add(new Box("B", "盒子 B", new DipRect(40, 120 + (dipHeight - 200) / 2, 360, (dipHeight - 200) / 2)));
        }

        if (dipWidth < 800)
        {
            _boxes.Clear();
            _boxes.Add(new Box("A", "盒子 A", new DipRect(20, 60, dipWidth * 0.45, 260)));
            _boxes.Add(new Box("B", "盒子 B", new DipRect(20, 360, dipWidth * 0.45, 220)));
        }
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
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"DIP 位置 ({box.Bounds.X:0},{box.Bounds.Y:0})  尺寸 {box.Bounds.Width:0}×{box.Bounds.Height:0}",
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
            case 0x0204:
                OverlayRightClickCount++;
                break;
        }

        // 不吞任何消息：交给 WPF 的默认处理流程
        return 0;
    }
}