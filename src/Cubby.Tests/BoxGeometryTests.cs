using Cubby.Core.Layout;
using Cubby.Core.Model;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 盒子几何运算的测试。这里的边界条件直接决定"用户能不能把盒子拖丢"。
/// </summary>
public sealed class BoxGeometryTests
{
    private const double MonitorWidth = 2048;
    private const double MonitorHeight = 1152;

    private static DipRect Box(double x = 100, double y = 100, double w = 400, double h = 300) => new(x, y, w, h);

    [Fact]
    public void 拖动按增量平移()
    {
        var moved = BoxGeometry.MoveTo(Box(), 50, -20, MonitorWidth, MonitorHeight);

        Assert.Equal(150, moved.X);
        Assert.Equal(80, moved.Y);
        Assert.Equal(400, moved.Width);
        Assert.Equal(300, moved.Height);
    }

    [Fact]
    public void 拖动不会越过左上角()
    {
        var moved = BoxGeometry.MoveTo(Box(), -1000, -1000, MonitorWidth, MonitorHeight);

        Assert.Equal(0, moved.X);
        Assert.Equal(0, moved.Y);
    }

    [Fact]
    public void 拖动不会越过右下角()
    {
        var moved = BoxGeometry.MoveTo(Box(), 10000, 10000, MonitorWidth, MonitorHeight);

        Assert.Equal(MonitorWidth - 400, moved.X);
        Assert.Equal(MonitorHeight - 300, moved.Y);
    }

    [Fact]
    public void 盒子比显示器还大时拖动仍被夹在左上角()
    {
        var huge = Box(0, 0, MonitorWidth + 500, MonitorHeight + 500);

        var moved = BoxGeometry.MoveTo(huge, 300, 300, MonitorWidth, MonitorHeight);

        Assert.Equal(0, moved.X);
        Assert.Equal(0, moved.Y);
    }

    [Fact]
    public void 缩放不会小于最小尺寸()
    {
        var resized = BoxGeometry.ResizeBy(Box(), -1000, -1000, MonitorWidth, MonitorHeight);

        Assert.Equal(BoxGeometry.MinWidth, resized.Width);
        Assert.Equal(BoxGeometry.MinHeight, resized.Height);
    }

    [Fact]
    public void 缩放不会超出显示器右边界与下边界()
    {
        var box = Box(1800, 1000, 200, 100);

        var resized = BoxGeometry.ResizeBy(box, 1000, 1000, MonitorWidth, MonitorHeight);

        Assert.Equal(MonitorWidth - 1800, resized.Width);
        Assert.Equal(MonitorHeight - 1000, resized.Height);
    }

    [Fact]
    public void 显示器比最小尺寸还小时缩放退化为最小尺寸而不是负数()
    {
        var resized = BoxGeometry.ResizeBy(Box(0, 0, 400, 300), -100, -100, 100, 50);

        Assert.Equal(BoxGeometry.MinWidth, resized.Width);
        Assert.Equal(BoxGeometry.MinHeight, resized.Height);
    }

    [Fact]
    public void 折叠时高度只留标题栏但模型尺寸不变()
    {
        var box = new Box("b", "盒子", Box());
        var collapsed = box with { IsCollapsed = true };

        Assert.Equal(BoxGeometry.TitleBarHeight, BoxGeometry.EffectiveHeight(collapsed));
        Assert.Equal(300, collapsed.Bounds.Height);
        Assert.Equal(300, BoxGeometry.EffectiveHeight(box));
    }
}