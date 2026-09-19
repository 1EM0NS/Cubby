using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Cubby.App.Views;
using Cubby.Core;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 浮层窗口：一台显示器一个实例，覆盖该显示器的完整物理范围，承载该屏上的所有盒子。
///
/// 它同时是：
/// 1. 产品界面——盒子在这里渲染与交互；
/// 2. 被验证的对象——逐像素 alpha 与区域窗口两种命中机制；
/// 3. 自动化的观测点——统计自己收到了多少次左键，用来证明「盒子内确实被拦截」或「确实没被拦截」。
/// </summary>
public partial class OverlayWindow : Window
{
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;

    private readonly List<Box> _boxes;
    private readonly Dictionary<string, BoxView> _views = new();
    private readonly StyleSettings _style;
    private readonly IBoxChangeSink _sink;

    internal OverlayWindow(
        MonitorSurface surface,
        IReadOnlyList<Box> boxes,
        StyleSettings style,
        IBoxChangeSink sink)
    {
        InitializeComponent();

        Surface = surface;
        _boxes = [.. boxes];
        _style = style.Normalized();
        _sink = sink;

        // 诊断用：区分「Win32 层收到了点击」与「WPF 层路由到了元素」。
        // 这两个计数不一致时，问题一定在命中测试或视觉树，而不是输入本身。
        PreviewMouseLeftButtonDown += (_, _) => WpfMouseDownCount++;
    }

    /// <summary>显示状态发生变化（分辨率 / DPI / 显示器增减），由 OverlayManager 决定如何处理。</summary>
    public event EventHandler<int>? DisplayChanged;

    public OverlayHost? Host { get; private set; }

    public MonitorSurface? Surface { get; private set; }

    public IReadOnlyList<Box> Boxes => _boxes;

    /// <summary>盒子换算成物理像素后的命中矩形。</summary>
    public IReadOnlyList<PixelRect> Regions { get; private set; } = [];

    public int OverlayClickCount { get; private set; }

    public int OverlayRightClickCount { get; private set; }

    public int BoxVisualCount => _views.Count;

    public int WpfMouseDownCount { get; private set; }

    public void ResetWpfMouseCount() => WpfMouseDownCount = 0;

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

    /// <summary>换一组盒子（布局重新加载时调用）。</summary>
    public void UpdateBoxes(IReadOnlyList<Box> boxes)
    {
        _boxes.Clear();
        _boxes.AddRange(boxes);
        RenderBoxes();
        RefreshRegions();
    }

    /// <summary>换到另一台显示器上（分辨率或 DPI 变化时调用）。</summary>
    public void PlaceOn(MonitorSurface surface)
    {
        Surface = surface;
        Host?.Attach(surface);
        RenderBoxes();
        RefreshRegions();
        Host?.EnsureBehind();
    }

    /// <summary>全局样式变化后重新渲染所有盒子。</summary>
    public void ApplyStyle()
    {
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

    /// <summary>生成探测采样点：盒子中心（期望拦截）+ 透明区（期望放行）。</summary>
    public IReadOnlyList<SamplePoint> SamplePoints()
    {
        var surface = Surface ?? throw new InvalidOperationException("窗口尚未初始化");
        var points = new List<SamplePoint>();

        foreach (var box in _boxes)
        {
            var physical = surface.ToPhysical(box.Bounds);

            // 取标题栏下方的条目区中心：折叠状态下没有条目区，就退回标题栏中心
            var centerY = box.IsCollapsed
                ? physical.Top + (int)(BoxGeometry.TitleBarHeight * surface.DpiScale / 2)
                : (physical.Top + physical.Bottom) / 2;

            points.Add(new SamplePoint(
                $"盒子 {box.Name} 中心",
                (physical.Left + physical.Right) / 2,
                centerY,
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

    /// <summary>按盒子 Id 取视图。供自动化验收直接驱动某个盒子的交互（如拖入）。</summary>
    internal BoxView? ViewOf(string boxId) => _views.TryGetValue(boxId, out var view) ? view : null;

    public string DescribeRegions() =>
        Regions.Count == 0 ? "（无）" : string.Join("、", Regions.Select(r => r.ToString()));

    private void RenderBoxes()
    {
        BoxLayer.Children.Clear();
        _views.Clear();

        foreach (var box in _boxes)
        {
            var view = new BoxView(box, _style, Surface!);
            view.BoxChanged += OnBoxViewChanged;
            view.ItemOpenRequested += (_, item) => _sink.OnItemOpen(item);
            view.ItemRevealRequested += (_, item) => _sink.OnItemReveal(item);

            Canvas.SetLeft(view, box.Bounds.X);
            Canvas.SetTop(view, box.Bounds.Y);

            BoxLayer.Children.Add(view);
            _views[box.Id] = view;
        }
    }

    private void OnBoxViewChanged(object? sender, Box updated)
    {
        var index = _boxes.FindIndex(b => b.Id == updated.Id);
        if (index < 0)
        {
            return;
        }

        _boxes[index] = updated;

        if (_views.TryGetValue(updated.Id, out var view))
        {
            if (!ReferenceEquals(view, sender))
            {
                view.Update(updated);
            }

            Canvas.SetLeft(view, updated.Bounds.X);
            Canvas.SetTop(view, updated.Bounds.Y);
        }

        _sink.OnBoxChanged(updated);
        RefreshRegions();
    }

    private void RefreshRegions()
    {
        if (Surface is null)
        {
            return;
        }

        Regions = HitRegion.ToPhysical(_boxes, Surface);

        // 逐像素 alpha 模式下命中完全由像素透明度决定，不需要反复设置窗口区域；
        // 只有"区域模式"才必须跟着盒子一起更新。
        if (Host is { Mode: HitMode.WindowRegion })
        {
            Host.SetMode(HitMode.WindowRegion, Regions, rounded: false);
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