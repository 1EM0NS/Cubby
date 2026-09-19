using System.Windows;
using Cubby.Core.Platform;

namespace Cubby.App;

/// <summary>
/// 「检测到同类桌面软件」的一次性提示（A8）。
///
/// 刻意做成一个独立窗口而不是 <c>MessageBox</c>：需要「不再提示」这个勾选项，
/// 而且要把"我们不抢占"这句话完整说清楚——用户看到这类提示时最担心的就是被强行关掉别的东西。
/// </summary>
public partial class CoexistWindow : Window
{
    internal CoexistWindow(IReadOnlyList<CoexistTool> detected)
    {
        InitializeComponent();

        Detected = detected;
        ToolList.ItemsSource = detected;
        SummaryText.Text = CoexistTools.Describe(detected);
    }

    /// <summary>本次检测到的同类软件（供自动化验收）。</summary>
    internal IReadOnlyList<CoexistTool> Detected { get; }

    /// <summary>用户是否勾了「不再提示」（供自动化验收）。</summary>
    internal bool RememberChoice => RememberBox.IsChecked == true;

    /// <summary>窗口上真实列出来的条目数（证明界面拿到的就是检测结果，而不是另算了一份）。</summary>
    internal int BoundCount => ToolList.Items.Count;

    internal void SetRemember(bool remember) => RememberBox.IsChecked = remember;

    /// <summary>程序化点击「我知道了」（自动化验收用，走的是与用户点击同一个处理函数）。</summary>
    internal void Acknowledge() => OnOk(this, new RoutedEventArgs());

    private void OnOk(object sender, RoutedEventArgs e) => Close();
}