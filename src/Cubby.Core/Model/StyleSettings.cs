namespace Cubby.Core.Model;

/// <summary>
/// 全局样式。M1 里由诊断面板统一调整，并作为新建盒子的默认值。
/// 圆角与字号只作用于渲染；透明度与列数会同时写进每个盒子的字段（便于将来做单盒覆盖）。
/// </summary>
public sealed record StyleSettings
{
    public double Opacity { get; init; } = 0.85;

    /// <summary>
    /// 圆角默认取 8：这是 Windows 11 给"顶层容器（窗口 / 浮出层）"的圆角，
    /// 桌面上的盒子正好属于这一类。控件级的 4 与嵌套的 6/4 由主题内部自己遵守。
    /// </summary>
    public double CornerRadius { get; init; } = 8;

    public int Columns { get; init; } = 4;

    public double FontSize { get; init; } = 13;

    /// <summary>把影响布局的字段规范化到合法范围，避免配置文件被手改坏后界面炸掉。</summary>
    public StyleSettings Normalized() => this with
    {
        Opacity = Math.Clamp(Opacity, 0.2, 1.0),
        CornerRadius = Math.Clamp(CornerRadius, 0, 32),
        Columns = Math.Clamp(Columns, 1, 12),
        FontSize = Math.Clamp(FontSize, 10, 24),
    };
}