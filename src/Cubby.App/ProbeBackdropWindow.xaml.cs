using System.Windows;
using System.Windows.Interop;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 自测用的受控背景层。
/// 自动化验收最怕依赖「用户桌面恰好是干净的」，而这层是我们自己的窗口，
/// 因此无论用户桌面上开着什么，都能稳定地验证：
/// 浮层透明区域的点击必须落到紧挨着它下面的这一层。
/// </summary>
public partial class ProbeBackdropWindow : Window
{
    private const int WmLeftButtonDown = 0x0201;

    public ProbeBackdropWindow() => InitializeComponent();

    public nint Handle { get; private set; }

    /// <summary>背景层收到的左键次数——这是「点击真的穿透下来了」的直接证据。</summary>
    public int ClickCount { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
        Handle = source.Handle;

        WindowPlacement.MakeNonActivating(Handle);

        if (Surface is not null)
        {
            WindowPlacement.PlaceOn(Handle, Surface);
        }

        UpdateCountText();
    }

    public MonitorSurface? Surface { get; set; }

    public void SetTopmost(bool topmost) => WindowPlacement.SetTopmost(Handle, topmost);

    public void ResetClickCount()
    {
        ClickCount = 0;
        UpdateCountText();
    }

    public string Describe()
    {
        var info = DesktopProbe.Describe(Handle);
        return info?.Describe() ?? "(背景层窗口信息不可用)";
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmLeftButtonDown)
        {
            ClickCount++;
            UpdateCountText();
        }

        return 0;
    }

    private void UpdateCountText() => CountText.Text = $"背景层已收到左键：{ClickCount} 次";
}