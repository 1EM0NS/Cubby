using System.Drawing;
using System.Windows.Forms;

namespace Cubby.App;

/// <summary>
/// 托盘菜单的深色渲染器。
///
/// 为什么需要它：托盘菜单是 WinForms 的 <see cref="ContextMenuStrip"/>，**不在 WPF 的主题体系里**，
/// WPF 主题（CubbyTheme.xaml）管不到它。不做任何处理时它弹出来是系统默认的
/// 白底黑字——深色界面上突然蹦出一块亮白，用户骂过的"白色标题栏"和这是同一类问题：
/// 系统画的东西没跟上我们自己的深色皮肤。
///
/// 颜色对齐 CubbyTheme 的中性深灰（#202020 一族），文字浅色、悬停提亮一档，
/// 与盒子弹出的 WPF 菜单观感一致。
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Surface = Color.FromArgb(0x21, 0x21, 0x21);
    private static readonly Color Hover = Color.FromArgb(0x33, 0x33, 0x33);
    private static readonly Color Border = Color.FromArgb(0x3F, 0x3F, 0x3F);
    private static readonly Color Text = Color.FromArgb(0xE8, 0xE8, 0xE8);
    private static readonly Color TextDisabled = Color.FromArgb(0x78, 0x78, 0x78);
    private static readonly Color Glyph = Color.FromArgb(0xC0, 0xC0, 0xC0);

    public DarkMenuRenderer()
        : base(new DarkColorTable())
    {
        // 默认会画圆角边缘，颜色表换成深色后它会留下一圈浅色残边，关掉更干净
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // 文字颜色在这里统一决定，而不是逐个菜单项设 ForeColor——漏一个就是一个黑字
        e.TextColor = e.Item.Enabled ? Text : TextDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        // 子菜单箭头默认是黑的（SystemColors.ControlText），深色底上看不见
        e.ArrowColor = Glyph;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // 对勾默认也是黑的。自己画一个浅色的勾，不用系统那套
        var rectangle = e.ImageRectangle;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var pen = new Pen(Glyph, 1.8f);
        var centerX = rectangle.Left + (rectangle.Width * 0.5f);
        var centerY = rectangle.Top + (rectangle.Height * 0.5f);

        e.Graphics.DrawLines(
            pen,
            new[]
            {
                new PointF(centerX - (rectangle.Width * 0.22f), centerY),
                new PointF(centerX - (rectangle.Width * 0.05f), centerY + (rectangle.Height * 0.18f)),
                new PointF(centerX + (rectangle.Width * 0.25f), centerY - (rectangle.Height * 0.20f)),
            });
    }

    /// <summary>色表：把 Professional 渲染器用到的渐变色全部钉到中性深灰。</summary>
    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;

        // 菜单左侧那条图片栏：没有图标也占一条竖带，颜色必须跟底色一致才看不见
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;

        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;

        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Surface;
        public override Color MenuItemPressedGradientEnd => Hover;

        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;

        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;

        public override Color ButtonSelectedBorder => Hover;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
    }
}
