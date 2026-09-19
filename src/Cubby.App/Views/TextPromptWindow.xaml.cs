using System.Windows;

namespace Cubby.App.Views;

/// <summary>
/// 极简文本输入框，用于重命名盒子。
/// 之所以需要单独一个窗口：浮层窗口用的是 `WS_EX_NOACTIVATE`（不抢焦点），
/// 它本身无法接收键盘输入；而重命名是用户主动发起的操作，弹一个可激活的小窗口是合理的。
/// </summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string prompt, string initialValue)
    {
        InitializeComponent();

        PromptText.Text = prompt;
        Input.Text = initialValue;

        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}