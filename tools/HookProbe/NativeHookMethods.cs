using System.Runtime.InteropServices;

namespace HookProbe;

/// <summary>
/// 钩子相关的 Win32 声明。**仅供验收工具使用，产品代码（src/）禁止**。
/// 依据：docs/adr/0003-hookprobe-may-install-mouse-hook.md
/// </summary>
internal static class NativeHookMethods
{
    internal const int WhMouseLl = 14;

    internal const int WmMouseMove = 0x0200;
    internal const int WmLeftButtonDown = 0x0201;
    internal const int WmLeftButtonUp = 0x0202;

    /// <summary>
    /// 双击消息。连续在同一位置快速点击时，系统会把第二次按下变成这个消息而不是 WM_LBUTTONDOWN，
    /// 计数时漏掉它就会误判成「有人吞消息」。
    /// </summary>
    internal const int WmLeftButtonDblClk = 0x0203;

    internal const uint LlkhfInjected = 0x00000001;
    internal const uint PmRemove = 0x0001;

    internal delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MsllHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public Point Pt;
    }

    // guard-exempt: ADR-0003 允许验收工具注册「只观测、无条件放行」的低级别鼠标钩子
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int hookId, HookProc callback, nint moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(nint hook);

    /// <summary>无条件放行：本项目绝不允许在钩子链上吞消息（P1）。</summary>
    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    internal static extern bool PeekMessage(out Msg msg, nint hwnd, uint filterMin, uint filterMax, uint removeMsg);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessage(ref Msg msg);

    [DllImport("kernel32.dll")]
    internal static extern uint GetLastError();
}