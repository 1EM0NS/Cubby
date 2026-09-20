using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cubby.App;

/// <summary>
/// 深色主题的模态对话框，替代 <see cref="System.Windows.MessageBox"/>。
///
/// 为什么不用系统 MessageBox：它按**系统主题**画（浅色系统上就是白底黑字），
/// 在深色的 Cubby 里弹出一块亮白，与用户骂过的白色标题栏、白色托盘菜单是同一类问题——
/// 系统画的东西没跟上我们自己的皮肤。
///
/// 用代码建界面而不是 XAML：它只有"一段话 + 一两个按钮"，没必要多一个文件。
/// 深色标题栏与圆角由 <c>App</c> 里注册的类处理器统一处理（窗口一加载就生效）。
/// 内容构建拆成独立方法，<see cref="WindowPreviewRenderer"/> 才能离屏渲染它。
/// </summary>
internal static class CubbyDialog
{
    /// <summary>只有一个「确定」按钮。</summary>
    public static void Info(Window? owner, string title, string message) =>
        Show(owner, title, message, confirm: false);

    /// <summary>「是 / 否」。返回 true 表示用户选了「是」。</summary>
    public static bool Confirm(Window? owner, string title, string message) =>
        Show(owner, title, message, confirm: true);

    private static bool Show(Window? owner, string title, string message, bool confirm)
    {
        var window = new Window
        {
            Title = title,
            Style = (Style)Application.Current.FindResource("Cubby.Window"),
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            MinWidth = 380,
            MaxWidth = 560,
        };

        if (owner is not null)
        {
            window.Owner = owner;
        }

        var result = false;

        var yes = PrimaryButton(confirm ? "是" : "确定");
        yes.IsDefault = true;
        yes.Click += (_, _) =>
        {
            result = true;
            window.Close();
        };

        Button? no = null;

        if (confirm)
        {
            no = new Button { Content = "否", MinWidth = 88, IsCancel = true };
            no.Click += (_, _) => window.Close();
        }

        window.Content = BuildContent(message, confirm, yes, no);
        window.ShowDialog();

        return result;
    }

    /// <summary>
    /// 对话框的内容（消息 + 按钮行）。窗口与预览共用这一份，保证预览图上看到的就是真品。
    /// </summary>
    internal static FrameworkElement BuildContent(string message, bool confirm, Button yes, Button? no)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };

        if (no is not null)
        {
            buttons.Children.Add(no);
        }

        buttons.Children.Add(yes);

        var panel = new StackPanel { Margin = new Thickness(24), MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(buttons);

        return panel;
    }

    /// <summary>主操作按钮：强调色底 + 深色字。次要按钮直接用主题里的默认 Button 样式。</summary>
    internal static Button PrimaryButton(string text)
    {
        var accent = (Color)Application.Current.FindResource("Cubby.Color.Accent");

        return new Button
        {
            Content = text,
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(accent),
            BorderBrush = new SolidColorBrush(accent),
            Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18)),
            FontWeight = FontWeights.SemiBold,
        };
    }
}
