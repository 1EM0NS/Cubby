using Cubby.Core;
using Cubby.Core.Model;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 命中区域计算的单元测试。这块逻辑决定 P2 是否成立，值得用测试锁住。
/// </summary>
public sealed class HitRegionTests
{
    private static readonly MonitorSurface SingleMonitor =
        new("primary", new PixelRect(0, 0, 1920, 1080), 1.0);

    private static readonly MonitorSurface ScaledMonitor =
        new("primary-150", new PixelRect(0, 0, 3840, 2160), 1.5);

    private static readonly MonitorSurface SecondaryMonitor =
        new("secondary", new PixelRect(-1920, 0, 0, 1080), 1.0);

    [Fact]
    public void 缩放为1时DIP与物理像素一致()
    {
        var boxes = new[] { new Box("A", "盒子 A", new DipRect(60, 80, 420, 300)) };

        var regions = HitRegion.ToPhysical(boxes, SingleMonitor);

        Assert.Single(regions);
        Assert.Equal(new PixelRect(60, 80, 480, 380), regions[0]);
    }

    [Fact]
    public void 缩放为15时按比例放大()
    {
        var boxes = new[] { new Box("A", "盒子 A", new DipRect(60, 80, 420, 300)) };

        var regions = HitRegion.ToPhysical(boxes, ScaledMonitor);

        Assert.Single(regions);
        Assert.Equal(new PixelRect(90, 120, 720, 570), regions[0]);
    }

    [Fact]
    public void 副屏原点为负时正确偏移()
    {
        var boxes = new[] { new Box("A", "盒子 A", new DipRect(10, 20, 100, 50)) };

        var regions = HitRegion.ToPhysical(boxes, SecondaryMonitor);

        Assert.Equal(new PixelRect(-1910, 20, -1810, 70), regions[0]);
    }

    [Fact]
    public void 命中测试左闭右开避免相邻边界重复命中()
    {
        var regions = new[] { new PixelRect(100, 100, 200, 200) };

        Assert.True(HitRegion.HitTest(regions, 100, 100));
        Assert.True(HitRegion.HitTest(regions, 199, 199));
        Assert.False(HitRegion.HitTest(regions, 200, 150));
        Assert.False(HitRegion.HitTest(regions, 150, 200));
        Assert.False(HitRegion.HitTest(regions, 99, 150));
    }

    [Fact]
    public void 空矩形会被剔除()
    {
        var boxes = new[]
        {
            new Box("A", "空的", new DipRect(10, 10, 0, 100)),
            new Box("B", "正常", new DipRect(10, 10, 100, 100)),
        };

        var regions = HitRegion.ToPhysical(boxes, SingleMonitor);

        Assert.Single(regions);
    }

    [Fact]
    public void 无盒子时命中区域为空且测试不命中()
    {
        var regions = HitRegion.ToPhysical([], SingleMonitor);

        Assert.Empty(regions);
        Assert.False(HitRegion.HitTest(regions, 0, 0));
        Assert.Equal(0, HitRegion.TotalArea(regions));
    }

    [Fact]
    public void 总面积按各矩形累加()
    {
        var regions = new[]
        {
            new PixelRect(0, 0, 100, 100),
            new PixelRect(200, 200, 300, 250),
        };

        Assert.Equal(10000 + 5000, HitRegion.TotalArea(regions));
    }
}