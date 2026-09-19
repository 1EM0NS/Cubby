using System.Runtime.InteropServices;
using Cubby.Shell.Interop;

namespace Cubby.Shell.Desktop;

/// <summary>
/// 一个全局热键（用于切换桌面图标显隐）。
///
/// **为什么不违反 P1**：P1 禁的是**全局鼠标钩子**（会抢输入、破坏壁纸软件的交互）。
/// <c>RegisterHotKey</c> 是系统提供的"注册一个组合键给我"接口，只拦下**这一个组合键**，
/// 其余按键原样走，既不挂钩子链也不碰任何鼠标消息。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    /// <summary>WM_HOTKEY，热键被按下时发到注册它的窗口。</summary>
    public const int WmHotkey = 0x0312;

    /// <summary>修饰键：Alt。</summary>
    public const uint ModAlt = 0x0001;

    /// <summary>修饰键：Ctrl。</summary>
    public const uint ModControl = 0x0002;

    /// <summary>修饰键：Shift。</summary>
    public const uint ModShift = 0x0004;

    private const uint ModNoRepeat = 0x4000;

    private readonly nint _hwnd;
    private bool _registered;

    public HotkeyService(nint hwnd, int id, uint modifiers, uint virtualKey)
    {
        _hwnd = hwnd;
        Id = id;
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public int Id { get; }

    public uint Modifiers { get; }

    public uint VirtualKey { get; }

    public bool IsRegistered => _registered;

    /// <summary>最近一次注册失败的原因（正常为 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>注册热键。失败不抛异常，只记下原因并返回 false。</summary>
    public bool Register()
    {
        if (_registered || _hwnd == 0)
        {
            return _registered;
        }

        // MOD_NOREPEAT：按住不放不要刷消息
        _registered = NativeMethods.RegisterHotKey(_hwnd, Id, Modifiers | ModNoRepeat, VirtualKey);

        LastError = _registered
            ? null
            : $"注册失败（可能已被别的程序占用）：{Marshal.GetLastWin32Error()}";

        return _registered;
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_hwnd, Id);
        _registered = false;
    }

    /// <summary>热键的显示文本，如 <c>Ctrl+Alt+H</c>。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if ((Modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        parts.Add(((char)VirtualKey).ToString());
        return string.Join("+", parts);
    }

    /// <summary>
    /// 往窗口投递一条 <c>WM_HOTKEY</c>，模拟"热键被按下"。
    ///
    /// 用途是自动化验收：系统按下热键后发的就是这条消息，投递它等于走完了整条消息处理路径，
    /// 又不依赖"测试时能不能真的抢到键盘焦点"。投递到自己的窗口，不影响其他程序。
    /// </summary>
    public static bool SimulatePress(nint hwnd, int id) =>
        hwnd != 0 && NativeMethods.PostMessage(hwnd, WmHotkey, id, 0);

    public void Dispose() => Unregister();
}