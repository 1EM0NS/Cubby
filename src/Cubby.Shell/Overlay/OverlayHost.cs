using Cubby.Core.Model;
using Cubby.Shell.Interop;

namespace Cubby.Shell.Overlay;

/// <summary>浮层的命中测试模式，对应 技术方案.md 第 2.2 节的两种机制。</summary>
public enum HitMode
{
    /// <summary>方案 A：逐像素 alpha。完全透明的像素由系统放行点击，不需要裁剪窗口区域。</summary>
    PerPixelAlpha,

    /// <summary>方案 B：区域窗口。用窗口区域限定命中范围，区域外既不绘制也不参与命中。</summary>
    WindowRegion,
}

/// <summary>
/// 对已有浮层窗口句柄的托管封装：扩展样式、命中模式、置底、窗口区域。
/// 只操作句柄、不创建窗口，因此与 UI 框架解耦。
/// </summary>
public sealed class OverlayHost : IDisposable
{
    private readonly nint _hwnd;
    private readonly List<nint> _eventHooks = [];
    private NativeMethods.WinEventProc? _eventCallback;
    private bool _disposed;
    private bool _wantsVisible = true;
    private bool _autoBehindSuspended;

    public OverlayHost(nint hwnd)
    {
        if (hwnd == 0)
        {
            throw new ArgumentException("窗口句柄不能为空", nameof(hwnd));
        }

        _hwnd = hwnd;
    }

    public nint Handle => _hwnd;

    public HitMode Mode { get; private set; } = HitMode.PerPixelAlpha;

    public MonitorSurface? Surface { get; private set; }

    /// <summary>置底被触发的次数，供诊断展示。</summary>
    public int EnsureBehindCount { get; private set; }

    /// <summary>从「最小化」状态被拉回来的次数，供诊断展示。</summary>
    public int RestoreFromMinimizedCount { get; private set; }

    /// <summary>窗口区域被设置的次数，供诊断展示。</summary>
    public int RegionApplyCount { get; private set; }

    /// <summary>设置扩展样式并把窗口铺满指定显示器（用物理像素，绕开 DIP 换算）。</summary>
    public void Attach(MonitorSurface surface)
    {
        Surface = surface;

        WindowPlacement.AddExtendedStyles(
            _hwnd,
            NativeMethods.WsExLayered | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow);
        WindowPlacement.PlaceOn(_hwnd, surface);
    }

    /// <summary>
    /// 切换命中模式。<paramref name="regionsPhysical"/> 是虚拟屏幕坐标下的命中矩形，
    /// 内部会换算成窗口相对坐标。
    /// </summary>
    public void SetMode(HitMode mode, IReadOnlyList<PixelRect> regionsPhysical, bool rounded)
    {
        Mode = mode;

        if (mode == HitMode.PerPixelAlpha)
        {
            // 传入 0 表示把区域重置为整个窗口：命中完全交给逐像素 alpha
            NativeMethods.SetWindowRgn(_hwnd, 0, true);
        }
        else
        {
            var surface = Surface ?? throw new InvalidOperationException("需要先 Attach 设置显示器信息");
            var region = BuildRegion(regionsPhysical, surface, rounded);
            // SetWindowRgn 成功返回非 0 时，区域归系统所有，不能再 DeleteObject
            NativeMethods.SetWindowRgn(_hwnd, region, true);
        }

        RegionApplyCount++;
    }

    /// <summary>把窗口压到 Z 序底部，且不激活它。</summary>
    public void EnsureBehind()
    {
        if (_disposed)
        {
            return;
        }

        // 最小化状态**不会**被 SWP_SHOWWINDOW 撤销——这一条是实测出来的（A6 验收，
        // 见 artifacts/show-desktop-report.md）：窗口被 shell 收起来之后，我们原以为
        // "带 SWP_SHOWWINDOW 的 SetWindowPos" 就等价于"让它可见"，实际上它会一直躺在
        // 最小化状态里，盒子再也不出现。既然方法名叫 EnsureBehind、语义是"保持我们该在的样子"，
        // 就得真的把可见性负责到底。
        if (_wantsVisible && NativeMethods.IsIconic(_hwnd))
        {
            // 用 SW_SHOWNOACTIVATE：恢复但不抢焦点（浮层永远不该抢焦点，P2/A1）
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShowNoActivate);
            RestoreFromMinimizedCount++;
        }

        var flags = NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate;
        flags |= _wantsVisible ? NativeMethods.SwpShowWindow : NativeMethods.SwpHideWindow;

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HwndBottom, 0, 0, 0, 0, flags);
        EnsureBehindCount++;
    }

    /// <summary>
    /// 暂停/恢复「前台变化即重新置底」。
    /// 自动化验收期间浮层需要临时固定在受控背景层之上，不能被置底逻辑拉走。
    /// </summary>
    public void SuspendAutoBehind() => _autoBehindSuspended = true;

    public void ResumeAutoBehind() => _autoBehindSuspended = false;

    /// <summary>
    /// 事件驱动地维持置底：监听前台窗口与最小化事件，绝不轮询。
    /// 用的是 SetWinEventHook（只监听、不拦截），不涉及任何全局消息钩子。
    /// </summary>
    public void StartAutoBehind()
    {
        if (_eventCallback is not null)
        {
            return;
        }

        _eventCallback = OnShellEvent;
        var flags = NativeMethods.WineventOutOfContext | NativeMethods.WineventSkipOwnProcess;

        _eventHooks.Add(NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground, NativeMethods.EventSystemForeground,
            0, _eventCallback, 0, 0, flags));

        _eventHooks.Add(NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemMinimizeStart, NativeMethods.EventSystemMinimizeEnd,
            0, _eventCallback, 0, 0, flags));

        // 再来一组**只看自己进程**的最小化事件。
        //
        // 上面那组带着 WineventSkipOwnProcess，它让我们"看不见自己"：一旦我们的浮层被收起来
        // （外部动作，或者验收里的最小化测试），没有任何事件会触发自动置底，
        // 「期望可见 ⇒ 可见」这条不变量就只在**别人**触发事件时才成立。
        // A6 验收实测到了这个缺口（`RestoreFromMinimizedCount` 停在 1 不再增长，窗口一直躺在最小化里）。
        //
        // 只钩最小化：我们的窗口成为前台这件事本身不该触发置底（它是 NOACTIVATE 的，本来就不抢焦点），
        // 钩得越少越不容易出意外。
        _eventHooks.Add(NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemMinimizeStart, NativeMethods.EventSystemMinimizeEnd,
            0, _eventCallback, (uint)Environment.ProcessId, 0, NativeMethods.WineventOutOfContext));
    }

    /// <summary>显示/隐藏浮层（托盘菜单「隐藏盒子」用）。隐藏期间窗口不参与命中测试。</summary>
    public void SetVisible(bool visible)
    {
        _wantsVisible = visible;
        EnsureBehind();
    }

    /// <summary>当前期望的可见状态。自动置底不会改变它。</summary>
    public bool WantsVisible => _wantsVisible;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var hook in _eventHooks)
        {
            NativeMethods.UnhookWinEvent(hook);
        }

        _eventHooks.Clear();
        _eventCallback = null;

        // 清掉区域，避免句柄被复用时留下奇怪形状
        NativeMethods.SetWindowRgn(_hwnd, 0, true);
    }

    private void OnShellEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_autoBehindSuspended)
        {
            return;
        }

        EnsureBehind();
    }

    private static nint BuildRegion(IReadOnlyList<PixelRect> rects, MonitorSurface surface, bool rounded)
    {
        if (rects.Count == 0)
        {
            // 没有命中区域时给一个 1x1 的极小区域，效果等价于整窗放行点击
            return NativeMethods.CreateRectRgn(0, 0, 1, 1);
        }

        var union = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        foreach (var rect in rects)
        {
            // 窗口区域用的是窗口相对坐标，需要减去显示器原点
            var left = rect.Left - surface.Bounds.Left;
            var top = rect.Top - surface.Bounds.Top;
            var right = rect.Right - surface.Bounds.Left;
            var bottom = rect.Bottom - surface.Bounds.Top;

            var piece = rounded
                ? NativeMethods.CreateRoundRectRgn(left, top, right, bottom, 16, 16)
                : NativeMethods.CreateRectRgn(left, top, right, bottom);

            NativeMethods.CombineRgn(union, union, piece, NativeMethods.RgnOr);
            NativeMethods.DeleteObject(piece);
        }

        return union;
    }
}