using Cubby.Core.Model;
using Cubby.Core.Platform;
using Cubby.Shell.Desktop;

namespace Cubby.App;

/// <summary>
/// 桌面图标显隐的**唯一入口**。
///
/// 三个入口（托盘菜单、全局热键、盒子按钮）都必须经过这里，好处是"记标记 / 清标记"只写一次，
/// 不会出现"某个入口隐藏了却忘了记标记"——那正是崩溃后图标回不来的原因。
///
/// 另外还有两条硬规矩：
/// 1. 关闭开关（<see cref="Box.EnableDesktopIconToggle"/> 为 false）时一律不动图标；
/// 2. 退出时无条件恢复显示，且只在"确实是我们藏起来的"情况下才动它——
///    用户自己把桌面图标关掉了，我们没资格替他打开。
/// </summary>
internal sealed class DesktopIconController
{
    private readonly LayoutService _layout;

    public DesktopIconController(LayoutService layout) => _layout = layout;

    /// <summary>最近一次操作说明（界面与验收都用）。</summary>
    public string LastAction { get; private set; } = "（尚未操作）";

    /// <summary>功能开关：为 false 时三个入口都不生效（SysListView32 的 ShowWindow 在个别版本上行为不稳，留个后门）。</summary>
    public bool IsEnabled => _layout.Document.EnableDesktopIconToggle;

    /// <summary>桌面图标当前是否可见。</summary>
    public bool IsVisible => DesktopIcons.IsVisible();

    /// <summary>切换显隐。返回切换后的可见状态。</summary>
    public bool Toggle()
    {
        var target = !IsVisible;
        SetVisible(target);
        return IsVisible;
    }

    /// <summary>设置显隐。返回是否真的改变了状态。</summary>
    public bool SetVisible(bool visible)
    {
        if (!IsEnabled)
        {
            LastAction = "桌面图标开关已在配置里关闭（EnableDesktopIconToggle=false），本次不做任何改变。";
            return false;
        }

        if (DesktopIcons.IsVisible() == visible)
        {
            LastAction = visible ? "桌面图标本来就是显示的。" : "桌面图标本来就是隐藏的。";
            return false;
        }

        if (!DesktopIcons.SetVisible(visible))
        {
            // 找不到 SysListView32（Explorer 正在重启）：明确说出来，不要静默失败
            LastAction = "找不到桌面图标层（Explorer 可能正在重启），本次未改变。";
            return false;
        }

        if (visible)
        {
            DesktopIconStateMarker.Clear();
            LastAction = "已恢复桌面图标显示，并清掉「我们藏过」的标记。";
        }
        else
        {
            DesktopIconStateMarker.MarkHidden();
            LastAction = "已隐藏桌面图标，并落了一枚标记：下次启动（哪怕是崩溃后）会先恢复显示。";
        }

        return true;
    }

    /// <summary>退出时调用：只有"我们藏过"才恢复，并且无条件清标记。</summary>
    public string RestoreOnExit()
    {
        if (DesktopIcons.IsVisible())
        {
            DesktopIconStateMarker.Clear();
            return "退出时桌面图标本来就是显示的。";
        }

        if (!DesktopIconStateMarker.WasLeftHidden())
        {
            // 图标是隐藏的，但不是我们藏的（用户自己在系统设置里关掉了）——不越俎代庖
            return "退出时桌面图标是隐藏的，但标记表明不是我们藏起来的，保持原样。";
        }

        DesktopIcons.SetVisible(true);
        DesktopIconStateMarker.Clear();
        LastAction = "退出时已恢复桌面图标显示（我们藏起来的那次）。";
        return LastAction;
    }

    /// <summary>
    /// 启动时调用：上次如果是被强杀 / 崩溃的，图标可能还藏着——第一件事就是把它放出来。
    /// 这是一条**不依赖任何退出代码**的兜底路径。
    /// </summary>
    public static string RecoverIfLeftHidden()
    {
        if (!DesktopIconStateMarker.WasLeftHidden())
        {
            return "上次没有留下「隐藏桌面图标」的标记。";
        }

        var restored = DesktopIcons.SetVisible(true);
        DesktopIconStateMarker.Clear();

        return restored
            ? "检测到上次异常退出时桌面图标还是隐藏的，已自动恢复显示。"
            : "检测到上次异常退出的标记，但找不到桌面图标层（Explorer 可能正在重启），标记已清除。";
    }
}