using System.Windows;

namespace Cubby.App;

/// <summary>引导里的一条说明：图标 + 标题 + 细则。</summary>
internal sealed record OnboardingPoint(string Glyph, string Title, string Detail);

/// <summary>
/// 首次运行引导窗（issue #38）。
///
/// 为什么不复用浮层：浮层是 <c>WS_EX_NOACTIVATE</c> 的，**收不到键盘、也不能激活**，
/// 那正是它不抢动态壁纸焦点的原因，但也决定了它当不了引导界面——引导必须是一个普通可激活窗口。
///
/// 它不参与桌面命中测试（是独立顶层窗口，不是盒子），因此对 P1–P4 没有任何影响。
/// </summary>
public partial class OnboardingWindow : Window
{
    private readonly Func<string> _createBox;

    internal OnboardingWindow(Func<string> createBox)
    {
        InitializeComponent();
        _createBox = createBox;
        PointList.ItemsSource = GuidePoints;
    }

    /// <summary>
    /// 引导要讲清的五件事，顺序即展示顺序，逐条对应 issue #38 的验收标准。
    /// 放在代码里而不是散在 XAML 中，是为了让自动化验收能**逐条断言**这些内容真的写进了界面。
    /// </summary>
    internal static IReadOnlyList<OnboardingPoint> GuidePoints { get; } =
    [
        new(
            "\uE710",
            "① 把桌面上的文件拖进盒子",
            "从桌面或资源管理器拖进来即可，盒子会立刻多出一个条目。"),
        new(
            "\uE71B",
            "② 盒子里的只是引用，文件实体永不移动",
            "Cubby 只记住它在哪里，从不搬运、不改名、不删除。最坏只会丢配置，不会丢你的文件。"),
        new(
            "\uE713",
            "③ 托盘菜单是主入口",
            "通知区域找到 Cubby 图标，右键即可显示 / 隐藏盒子、设置样式、备份布局快照、退出程序。"),
        new(
            "\uE7B3",
            "④ 按 Ctrl+Alt+H 隐藏 / 显示真实桌面图标",
            "盒子接管展示后把真实图标藏起来可以避免重影；再按一次就回来。"),
        new(
            "\uE8B7",
            "⑤ 可以映射一个磁盘文件夹",
            "把一个文件夹映射成盒子内容，文件实体仍留在原处——用来给 C 盘减负。"),
    ];

    /// <summary>
    /// 用户是否勾了「不再自动显示」（供自动化验收）。关闭时以它为准**双向**写回布局标记。
    ///
    /// 复选框**固定默认勾选**，刻意不从布局标记初始化：真·首次运行时标记就是 false，
    /// 若据此渲染成未勾选，用户点一下「开始使用」反而把引导留给了下次启动——那正是最烦人的行为。
    /// </summary>
    internal bool RememberChoice => RememberBox.IsChecked == true;

    /// <summary>界面上真实列出来的条目数（证明界面拿到的就是这份清单，而不是另算了一份）。</summary>
    internal int PointCount => PointList.Items.Count;

    /// <summary>界面上真实出现的文字（逐条断言文案用）。</summary>
    internal IReadOnlyList<string> PointTexts =>
        GuidePoints.Select(point => $"{point.Title}｜{point.Detail}").ToList();

    /// <summary>「创建第一个盒子」的结果说明；尚未点击时为 null。</summary>
    internal string? Status =>
        string.IsNullOrEmpty(StatusText.Text) ? null : StatusText.Text;

    internal void SetRemember(bool remember) => RememberBox.IsChecked = remember;

    /// <summary>程序化点击「创建第一个盒子」（走的是与用户点击同一个处理函数）。</summary>
    internal void CreateFirstBox() => OnCreateBox(this, new RoutedEventArgs());

    /// <summary>程序化点击「开始使用」。</summary>
    internal void Start() => OnStart(this, new RoutedEventArgs());

    private void OnCreateBox(object sender, RoutedEventArgs e)
    {
        var summary = _createBox();

        StatusText.Text = summary;
        StatusText.Visibility = Visibility.Visible;

        // 刻意**不禁用**这个按钮：重复点应该看到「已经有了」这句如实的说明，
        // 而不是按钮变灰让人猜。幂等性由 OverlayManager.CreateFirstBoxOnPrimary 保证。
    }

    private void OnStart(object sender, RoutedEventArgs e) => Close();
}
