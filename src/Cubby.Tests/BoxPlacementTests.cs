using Cubby.Core.Layout;
using Cubby.Core.Model;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 盒子可见性修正的测试。它守的是一条很具体的底线：
/// **盒子绝不能落在浮层窗口之外**——浮层窗口只覆盖一台显示器，盒子一旦跑到窗口外，
/// 既画不出来也点不到，用户连拖都拖不回来，等于盒子丢了。
///
/// 本机只有一台显示器，所以「拔副屏」「换分辨率」「改 DPI」这些场景只能在这里覆盖。
/// </summary>
public sealed class BoxPlacementTests
{
    /// <summary>2560×1440 @125% → DIP 空间 2048×1152。</summary>
    private static readonly MonitorSurface Primary =
        new(@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), 1.25) { IsPrimary = true };

    [Fact]
    public void 坐标本来就合法时一个字节都不改()
    {
        var box = new DipRect(60, 80, 420, 320);

        var (bounds, fix) = BoxPlacement.FitInto(box, Primary);

        Assert.Equal(PlacementFix.None, fix);
        Assert.Equal(box, bounds);
    }

    [Fact]
    public void 屏幕变小后贴边的盒子被拉回可见范围()
    {
        // 4K 上贴右下角的盒子，换到 1920×1080 的屏
        var smaller = new MonitorSurface(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), 1.0);
        var placed = new DipRect(3400, 1900, 420, 320);

        var (bounds, fix) = BoxPlacement.FitInto(placed, smaller);

        Assert.Equal(PlacementFix.Moved, fix);
        Assert.Equal(1500, bounds.X, 3);   // 1920 - 420
        Assert.Equal(760, bounds.Y, 3);    // 1080 - 320
        Assert.Equal(420, bounds.Width, 3);
        Assert.Equal(320, bounds.Height, 3);
    }

    [Fact]
    public void DPI升高让DIP空间变小同样会越界()
    {
        // 同一块 2560×1440 的屏：@125% 时 DIP 是 2048×1152，@150% 时缩到 1706.7×960
        var at150 = new MonitorSurface(@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), 1.5);
        var box = new DipRect(1600, 900, 300, 200);

        // 先确认它在 125% 下是合法的，否则这个用例就没在测 DPI 变化
        Assert.Equal(PlacementFix.None, BoxPlacement.FitInto(box, Primary).Fix);

        var (bounds, fix) = BoxPlacement.FitInto(box, at150);

        Assert.Equal(PlacementFix.Moved, fix);
        Assert.Equal(1406.667, bounds.X, 2);   // 1706.67 - 300
        Assert.Equal(760, bounds.Y, 3);        // 960 - 200
        Assert.True(bounds.Right <= 1706.67 + 0.01);
        Assert.True(bounds.Bottom <= 960 + 0.01);
    }

    [Fact]
    public void 盒子比屏幕还大时被收进屏幕()
    {
        var huge = new DipRect(0, 0, 3000, 2000);

        var (bounds, fix) = BoxPlacement.FitInto(huge, Primary);

        Assert.Equal(PlacementFix.Resized, fix);
        Assert.Equal(2048, bounds.Width, 3);
        Assert.Equal(1152, bounds.Height, 3);
        Assert.Equal(0, bounds.X, 3);
    }

    [Fact]
    public void 又大又偏时尺寸和位置一起修正()
    {
        var huge = new DipRect(500, 400, 3000, 2000);

        var (bounds, fix) = BoxPlacement.FitInto(huge, Primary);

        Assert.Equal(PlacementFix.MovedAndResized, fix);
        Assert.Equal(0, bounds.X, 3);
        Assert.Equal(0, bounds.Y, 3);
        Assert.Equal(2048, bounds.Width, 3);
        Assert.Equal(1152, bounds.Height, 3);
    }

    [Fact]
    public void 远在屏外的盒子不会得到负坐标()
    {
        var faraway = new DipRect(9000, 9000, 300, 200);

        var (bounds, fix) = BoxPlacement.FitInto(faraway, Primary);

        Assert.Equal(PlacementFix.Moved, fix);
        Assert.Equal(1748, bounds.X, 3);   // 2048 - 300
        Assert.Equal(952, bounds.Y, 3);    // 1152 - 200
    }

    [Fact]
    public void 拿不到有效屏幕范围时不乱动()
    {
        var broken = new MonitorSurface(@"\\.\DISPLAY1", new PixelRect(0, 0, 0, 0), 1.0);
        var box = new DipRect(60, 80, 420, 320);

        var (bounds, fix) = BoxPlacement.FitInto(box, broken);

        // 宁可保持原样，也不要基于一个猜出来的尺寸去挪用户的盒子
        Assert.Equal(PlacementFix.None, fix);
        Assert.Equal(box, bounds);
    }

    [Fact]
    public void 缩放为零时退回像素尺寸而不是除零崩溃()
    {
        var odd = new MonitorSurface(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), 0);
        var box = new DipRect(100, 100, 300, 200);

        var (bounds, fix) = BoxPlacement.FitInto(box, odd);

        Assert.Equal(PlacementFix.None, fix);
        Assert.Equal(box, bounds);
    }

    // ---- 分配 + 修正的联合行为（Planner 是产品路径上唯一调用它的地方）----

    [Fact]
    public void 拔掉副屏后盒子回到主屏且整盒可见()
    {
        var primary = new MonitorSurface(@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), 1.0) { IsPrimary = true };
        var box = new Box("副屏右下的", "副屏右下的", new DipRect(3400, 1800, 420, 320))
        {
            MonitorId = @"\\.\DISPLAY2",
            MonitorWidth = 3840,
            MonitorHeight = 2160,
        };

        var plans = OverlayPlanner.Plan([primary], [box]);

        var planned = Assert.Single(Assert.Single(plans).Boxes);

        Assert.Equal(MonitorMatchKind.Fallback, planned.Kind);
        Assert.NotEqual(PlacementFix.None, planned.Fix);
        Assert.True(planned.Box.Bounds.Right <= primary.DipExtent.Width + 0.01);
        Assert.True(planned.Box.Bounds.Bottom <= primary.DipExtent.Height + 0.01);
    }

    [Fact]
    public void 显示器没变时分配计划不会顺手弄脏布局()
    {
        var box = new Box("主屏的", "主屏的", new DipRect(60, 80, 420, 320))
        {
            MonitorId = @"\\.\DISPLAY1",
            MonitorWidth = 2560,
            MonitorHeight = 1440,
        };

        var plans = OverlayPlanner.Plan([Primary], [box]);

        var planned = Assert.Single(Assert.Single(plans).Boxes);

        // 这条是硬要求：绝大多数重建都发生在显示器没变的情况下，
        // 若那里也"规范化"一下，每次启动都会悄悄改用户的布局
        Assert.Equal(PlacementFix.None, planned.Fix);
        Assert.Equal(box, planned.Box);
        Assert.False(Assert.Single(plans).HasFix);
        Assert.Empty(Assert.Single(plans).Notes);
    }
}
