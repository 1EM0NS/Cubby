using System.Windows;
using Cubby.Core.Model;

namespace Cubby.App;

/// <summary>
/// 全局样式设置。四个滑块都**即时生效**：值一变就通过 <see cref="Apply"/> 抛给宿主，
/// 由 <see cref="OverlayManager"/> 重绘所有盒子并延迟落盘。
///
/// 初始化时会赋值四个滑块，那时也会触发 ValueChanged——用 <see cref="_loading"/> 挡掉，
/// 避免刚打开设置窗口就把用户的样式"改"成默认值。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Action<StyleSettings> _apply;
    private bool _loading = true;

    internal SettingsWindow(StyleSettings current, Action<StyleSettings> apply)
    {
        InitializeComponent();

        _apply = apply;
        var style = current.Normalized();

        OpacitySlider.Value = style.Opacity;
        CornerSlider.Value = style.CornerRadius;
        ColumnsSlider.Value = style.Columns;
        FontSlider.Value = style.FontSize;

        UpdateLabels(style);
        _loading = false;
    }

    /// <summary>供自动化验收读取当前四个值（也就是界面正在显示的值）。</summary>
    internal StyleSettings Current => new()
    {
        Opacity = OpacitySlider.Value,
        CornerRadius = CornerSlider.Value,
        Columns = (int)ColumnsSlider.Value,
        FontSize = FontSlider.Value,
    };

    /// <summary>
    /// 供自动化验收驱动：直接改某个滑块并走与用户拖动完全相同的处理路径。
    /// </summary>
    internal void SetValue(string name, double value)
    {
        switch (name)
        {
            case nameof(OpacitySlider):
                OpacitySlider.Value = value;
                break;
            case nameof(CornerSlider):
                CornerSlider.Value = value;
                break;
            case nameof(ColumnsSlider):
                ColumnsSlider.Value = Math.Round(value);
                break;
            case nameof(FontSlider):
                FontSlider.Value = Math.Round(value);
                break;
            default:
                throw new ArgumentException($"未知的滑块：{name}", nameof(name));
        }
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Push();

    private void OnCornerChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Push();

    private void OnColumnsChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Push();

    private void OnFontChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Push();

    private void Push()
    {
        if (_loading)
        {
            return;
        }

        var style = Current.Normalized();
        UpdateLabels(style);
        _apply(style);
    }

    private void UpdateLabels(StyleSettings style)
    {
        OpacityLabel.Text = $"透明度：{style.Opacity:0.00}";
        CornerLabel.Text = $"圆角：{style.CornerRadius:0} px";
        ColumnsLabel.Text = $"每行列数：{style.Columns}";
        FontLabel.Text = $"字号：{style.FontSize:0}";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}