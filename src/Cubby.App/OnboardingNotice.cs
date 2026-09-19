namespace Cubby.App;

/// <summary>
/// 首次运行引导的决策与展示（issue #38）。
///
/// 抽成一层与 <see cref="CoexistNotice"/> 同一个理由：让自动化验收走的是**用户真正走的那段代码**
/// ——启动时的自动弹出、托盘菜单的「使用指引」、验收里的手动打开，全部从这里进。
/// </summary>
internal static class OnboardingNotice
{
    /// <summary>是否该自动弹出：只有布局里没记过「不再自动显示」才弹。</summary>
    internal static bool ShouldAutoShow(LayoutService layout) => !layout.OnboardingShown;

    /// <summary>启动时调用：需要引导才弹，否则**完全静默**（连窗口都不创建）。</summary>
    internal static OnboardingWindow? ShowIfNeeded(OverlayManager manager, LayoutService layout) =>
        ShouldAutoShow(layout) ? Show(manager, layout) : null;

    /// <summary>
    /// 打开欢迎窗。托盘菜单的「使用指引」走这里——**无视**「不再自动显示」，
    /// 用户主动点进来就该给他看，这与"别自动打扰"是两回事。
    /// </summary>
    internal static OnboardingWindow Show(OverlayManager manager, LayoutService layout)
    {
        var window = new OnboardingWindow(manager.CreateFirstBoxOnPrimary);

        window.Closed += (_, _) =>
        {
            // 以复选框为准**双向**写回，而不是「只写 true」：
            // 用户在这里取消勾选，意思就是「这个我还要再看一遍」，那条偏好必须能表达出来，
            // 否则勾选项就成了一个只会单方向生效的装饰。
            layout.OnboardingShown = window.RememberChoice;
        };

        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>重置引导标记，让下次启动重新出现（<c>--reset-onboarding</c>）。</summary>
    internal static void Reset(LayoutService layout) => layout.OnboardingShown = false;
}
