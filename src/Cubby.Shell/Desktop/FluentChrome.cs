using Cubby.Shell.Interop;

namespace Cubby.Shell.Desktop;

/// <summary>
/// 把窗口的**非客户区**（标题栏 / 圆角）调成与深色内容一致的 Windows 11 观感。
///
/// 为什么需要它：系统标题栏不是 WPF 元素，`Window` 上的 `Background` / `Foreground` 完全管不到它
/// ——Win32 里它是非客户区，由 DWM 按系统主题画。Windows 默认给的是**白色**标题栏，
/// 扣在我们深色的窗口内容上就是"白帽子配黑衣服"，用户当场骂过
/// （原话："白色标题栏加黑色背景…你直接去抄人家行吗"）。
///
/// 只作用于传进来的那个句柄，也就是**我们自己创建的窗口**：不枚举、不遍历、不碰任何别人的窗口。
/// 调用失败（系统太老 / 被策略拦）只是外观不变，绝不影响程序运行。
/// </summary>
public static class FluentChrome
{
    /// <summary>给窗口套上深色标题栏与系统圆角。返回是否至少有一项生效。</summary>
    public static bool Apply(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        var dark = DarkTitleBar(hwnd);
        var rounded = RoundedCorners(hwnd);

        return dark || rounded;
    }

    private static bool DarkTitleBar(nint hwnd)
    {
        var useDark = 1;

        // 先试新属性号，再退到旧号。版本不匹配时 DWM 只是返回非 0（不会抛异常），
        // 而且没有可用的探测 API，所以"两个号都试一遍"是这里唯一可靠的做法。
        return NativeMethods.DwmSetWindowAttribute(
                   hwnd,
                   NativeMethods.DwmwaUseImmersiveDarkMode,
                   ref useDark,
                   sizeof(int)) == 0
            || NativeMethods.DwmSetWindowAttribute(
                   hwnd,
                   NativeMethods.DwmwaUseImmersiveDarkModeLegacy,
                   ref useDark,
                   sizeof(int)) == 0;
    }

    /// <summary>
    /// 让 DWM 把窗口四角切成 Windows 11 的圆角（与系统对话框、设置页一致）。
    /// 只在 Windows 11 上有效；老系统会静默忽略，不会报错。
    /// </summary>
    private static bool RoundedCorners(nint hwnd)
    {
        var preference = NativeMethods.DwmwcpRound;

        return NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaWindowCornerPreference,
            ref preference,
            sizeof(int)) == 0;
    }
}
