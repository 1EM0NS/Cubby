using Cubby.Shell.Interop;

namespace Cubby.Shell.Diagnostics;

/// <summary>
/// 鼠标注入，仅用于自动化验收（移动光标并单击）。只做左键单击，不做双击，
/// 避免在用户桌面上触发打开/重命名等副作用。
/// </summary>
public static class MouseClicker
{
    public static bool TryGetCursorPosition(out int x, out int y)
    {
        if (NativeMethods.GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    public static void MoveTo(int x, int y) => NativeMethods.SetCursorPos(x, y);

    /// <summary>
    /// 相对移动光标。与 <see cref="MoveTo"/> 的区别很重要：
    /// SetCursorPos 是直接设置位置，不产生经过钩子链的输入事件；
    /// 这个方法走 SendInput，会产生真实的移动事件，因此可以用来验证钩子链是否活着。
    /// </summary>
    public static bool Nudge(int deltaX, int deltaY)
    {
        var inputs = new[]
        {
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Union = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput
                    {
                        Dx = deltaX,
                        Dy = deltaY,
                        Flags = NativeMethods.MouseeventfMove,
                    },
                },
            },
        };

        var sent = NativeMethods.SendInput(
            1,
            inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());

        return sent == 1;
    }

    /// <summary>在光标当前所在位置按一次左键。</summary>
    public static bool LeftClick()
    {
        var inputs = new[]
        {
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Union = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput { Flags = NativeMethods.MouseeventfLeftDown },
                },
            },
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Union = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput { Flags = NativeMethods.MouseeventfLeftUp },
                },
            },
        };

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());
        return sent == inputs.Length;
    }
}