using Cubby.Core.Layout;
using Cubby.Core.Model;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 显示器分配计划的测试。这条路径是「拔插显示器 / 改分辨率后盒子还在不在、在哪」的决策点。
/// 本机只有一台显示器，所以多屏场景只能靠这里的单元测试覆盖（见 PR 说明）。
/// </summary>
public sealed class OverlayPlannerTests
{
    private static readonly MonitorSurface Primary =
        new(@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), 1.25) { IsPrimary = true };

    private static readonly MonitorSurface Secondary =
        new(@"\\.\DISPLAY2", new PixelRect(-1920, 0, 0, 1080), 1.0);

    private static readonly MonitorSurface[] Both = [Primary, Secondary];

    private static Box BoxOn(string name, string? monitorId, int width = 0, int height = 0) =>
        new(name, name, new DipRect(20, 20, 300, 200))
        {
            MonitorId = monitorId,
            MonitorWidth = width,
            MonitorHeight = height,
        };

    [Fact]
    public void 每台显示器都得到一条计划即使没有盒子()
    {
        var plans = OverlayPlanner.Plan(Both, []);

        Assert.Equal(2, plans.Count);
        Assert.All(plans, p => Assert.Empty(p.Boxes));
    }

    [Fact]
    public void 没有显示器时返回空计划而不是崩溃()
    {
        var plans = OverlayPlanner.Plan([], [BoxOn("a", null)]);

        Assert.Empty(plans);
    }

    [Fact]
    public void 盒子按保存的显示器精确归位()
    {
        var plans = OverlayPlanner.Plan(Both,
        [
            BoxOn("主屏的", @"\\.\DISPLAY1"),
            BoxOn("副屏的", @"\\.\DISPLAY2"),
        ]);

        var primary = plans.Single(p => p.Monitor.Id == @"\\.\DISPLAY1");
        var secondary = plans.Single(p => p.Monitor.Id == @"\\.\DISPLAY2");

        Assert.Equal("主屏的", Assert.Single(primary.Boxes).Box.Name);
        Assert.Equal(MonitorMatchKind.ExactId, primary.Boxes[0].Kind);
        Assert.Equal("副屏的", Assert.Single(secondary.Boxes).Box.Name);
    }

    [Fact]
    public void 原显示器不在了盒子落到主屏并被标记为回退()
    {
        var plans = OverlayPlanner.Plan(Both, [BoxOn("孤儿盒", @"\\.\DISPLAY9")]);

        var primary = plans.Single(p => p.Monitor.Id == @"\\.\DISPLAY1");
        var secondary = plans.Single(p => p.Monitor.Id == @"\\.\DISPLAY2");

        Assert.Equal("孤儿盒", Assert.Single(primary.Boxes).Box.Name);
        Assert.Equal(MonitorMatchKind.Fallback, primary.Boxes[0].Kind);
        Assert.True(primary.HasFallback);
        Assert.Empty(secondary.Boxes);
        Assert.False(secondary.HasFallback);
    }

    [Fact]
    public void 显示器标识变了但分辨率相同仍回到该屏且不算回退()
    {
        var renamed = new MonitorSurface(@"\\.\DISPLAY7", new PixelRect(-1920, 0, 0, 1080), 1.0);
        var monitors = new[] { Primary, renamed };

        var plans = OverlayPlanner.Plan(monitors, [BoxOn("副屏的", @"\\.\DISPLAY2", 1920, 1080)]);

        var target = plans.Single(p => p.Monitor.Id == @"\\.\DISPLAY7");
        Assert.Equal("副屏的", Assert.Single(target.Boxes).Box.Name);
        Assert.Equal(MonitorMatchKind.SameResolution, target.Boxes[0].Kind);
        Assert.False(target.HasFallback);
    }

    [Fact]
    public void 一个盒子都不会被丢掉()
    {
        var boxes = new[]
        {
            BoxOn("a", @"\\.\DISPLAY1"),
            BoxOn("b", @"\\.\DISPLAY2"),
            BoxOn("c", @"\\.\DISPLAY9"),
        };

        var plans = OverlayPlanner.Plan(Both, boxes);

        Assert.Equal(3, plans.Sum(p => p.Boxes.Count));
    }
}