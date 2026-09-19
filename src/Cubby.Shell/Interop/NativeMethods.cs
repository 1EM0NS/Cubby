using System.Runtime.InteropServices;
using System.Text;

namespace Cubby.Shell.Interop;

/// <summary>
/// Win32 互操作声明的集中地，便于审阅。
/// CI 的设计原则守卫（tools/scripts/guard.ps1）扫描的就是这里出现的符号。
/// </summary>
internal static class NativeMethods
{
    internal const int GwlExStyle = -20;

    // 浮层窗口需要的扩展样式
    internal const long WsExLayered = 0x00080000L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const long WsExToolWindow = 0x00000080L;

    // 说明：本项目禁止给浮层加「整窗透明」类扩展样式，那会让盒子本身也无法交互（违反 P2）。

    internal static readonly nint HwndBottom = new(1);

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint SwpHideWindow = 0x0080;

    internal const int SwHide = 0;
    internal const int SwShowNoActivate = 4;

    /// <summary>取进程占用的 GDI / USER 对象数（A7 常驻稳定性验收用）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetGuiResources(nint process, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hwnd, int cmdShow);

    internal const int RgnOr = 2;

    // ---- 跨进程读取桌面图标（只读，见 DesktopIcons）----

    /// <summary>PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION。</summary>
    internal const int ProcessAccessForRead = 0x0008 | 0x0010 | 0x0020 | 0x0400;

    internal const int MemCommit = 0x1000;
    internal const int MemReserve = 0x2000;
    internal const int MemRelease = 0x8000;
    internal const int PageReadWrite = 0x04;

    /// <summary>SMTO_ABORTIFHUNG：目标（Explorer）卡住时立刻返回，不让本进程跟着僵住。</summary>
    internal const uint SmtoAbortIfHung = 0x0002;

    /// <summary>
    /// 跨进程 <c>LVITEMW</c>。字段顺序与 commctrl.h 一致；x64 下自然对齐后正好 88 字节。
    /// 只在 LVM_GETITEMTEXTW 时用来传参，**不参与任何写操作**。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LvItem
    {
        public uint Mask;
        public int Item;
        public int SubItem;
        public uint State;
        public uint StateMask;
        public nint Text;
        public int TextMax;
        public int Image;
        public nint Param;
        public int Indent;
        public int GroupId;
        public uint Columns;
        public nint ColumnOrder;
        public nint ColumnFormat;
        public int Group;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(int desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint VirtualAllocEx(nint process, nint address, nint size, int allocationType, int protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFreeEx(nint process, nint address, nint size, int freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint written);

    /// <summary>
    /// 带超时的 SendMessage。<c>LVM_*</c> 是同步消息，目标若是卡住的 Explorer，
    /// 普通的 SendMessage 会把本进程一起拖死，所以一律用这个。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint SendMessageTimeout(nint hwnd, int msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    /// <summary>把一组点从 <paramref name="from"/> 的客户区坐标换算到 <paramref name="to"/>；<paramref name="to"/> 为 0 表示屏幕坐标。</summary>
    [DllImport("user32.dll")]
    internal static extern int MapWindowPoints(nint from, nint to, ref Point points, uint count);

    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventSystemMinimizeStart = 0x0016;
    internal const uint EventSystemMinimizeEnd = 0x0017;
    internal const uint WineventOutOfContext = 0x0000;
    internal const uint WineventSkipOwnProcess = 0x0002;

    internal const uint InputMouse = 0;
    internal const uint MouseeventfMove = 0x0001;
    internal const uint MouseeventfLeftDown = 0x0002;
    internal const uint MouseeventfLeftUp = 0x0004;
    internal const uint MouseeventfVirtualDesk = 0x4000;
    internal const uint MouseeventfAbsolute = 0x8000;

    // GetSystemMetrics 的索引
    internal const int SmCxScreen = 0;
    internal const int SmCyScreen = 1;
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    internal delegate void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time);

    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint newLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    internal static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint gdiObject);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint moduleHandle, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, Input[] inputs, int sizeOfInput);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern nint GetParent(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowTitle);

    internal const uint MonitorDefaultToPrimary = 1;
    internal const uint MonitorDefaultToNearest = 2;
    internal const uint MonitorInfoFlagPrimary = 0x00000001;
    internal const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    /// <summary>带设备名的显示器信息。<c>Device</c> 形如 <c>\\.\DISPLAY1</c>，宽度固定 CCHDEVICENAME=32。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    internal delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect rect, nint data);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    /// <summary>
    /// 注意：user32 只导出 <c>GetMonitorInfoW</c>，<c>GetMonitorInfoEx</c> 只是头文件里的宏。
    /// 传 <see cref="MonitorInfoEx"/>（cbSize=104）即可拿到带设备名的结构体，不能写成 GetMonitorInfoExW。
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoEx(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(nint hdc, nint clipRect, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);

    /// <summary>取某台显示器的有效 DPI。返回 HRESULT，0 表示成功。</summary>
    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    internal static string ClassNameOf(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    internal static string TitleOf(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetWindowText(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }
}