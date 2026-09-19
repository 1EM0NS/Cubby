using Cubby.Core.Layout;
using Cubby.Core.Model;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 显示器匹配的测试。核心诉求只有一个：**拔插显示器后盒子不能丢**。
/// </summary>
public sealed class MonitorMatcherTests
{
    private static readonly MonitorSurface Primary =
        new(@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), 1.25);

    private static readonly MonitorSurface Secondary =
        new(@"\\.\DISPLAY2", new PixelRect(-1920, 0, 0, 1080), 1.0);

    private static readonly MonitorSurface[] Both = [Primary, Secondary];

    [Fact]
    public void 标识一致时精确匹配()
    {
        var (monitor, kind) = MonitorMatcher.Resolve(@"\\.\DISPLAY2", 1920, 1080, Both, Primary);

        Assert.Equal(@"\\.\DISPLAY2", monitor.Id);
        Assert.Equal(MonitorMatchKind.ExactId, kind);
    }

    [Fact]
    public void 标识变化但分辨率一致时按分辨率匹配()
    {
        var (monitor, kind) = MonitorMatcher.Resolve(@"\\.\DISPLAY9", 1920, 1080, Both, Primary);

        Assert.Equal(@"\\.\DISPLAY2", monitor.Id);
        Assert.Equal(MonitorMatchKind.SameResolution, kind);
    }

    [Fact]
    public void 标识与分辨率都不一致时回退到兜底显示器()
    {
        var (monitor, kind) = MonitorMatcher.Resolve(@"\\.\DISPLAY9", 3840, 2160, Both, Primary);

        Assert.Equal(@"\\.\DISPLAY1", monitor.Id);
        Assert.Equal(MonitorMatchKind.Fallback, kind);
    }

    [Fact]
    public void 当前没有任何显示器时也返回兜底显示器而不是崩溃()
    {
        var (monitor, kind) = MonitorMatcher.Resolve(@"\\.\DISPLAY1", 2560, 1440, [], Primary);

        Assert.Equal(Primary, monitor);
        Assert.Equal(MonitorMatchKind.Fallback, kind);
    }

    [Fact]
    public void 没有保存过显示器信息时回退到兜底显示器()
    {
        var (monitor, kind) = MonitorMatcher.Resolve(null, 0, 0, Both, Primary);

        Assert.Equal(Primary, monitor);
        Assert.Equal(MonitorMatchKind.Fallback, kind);
    }

    [Fact]
    public void 批量匹配不会丢掉任何一个盒子()
    {
        var boxes = new[]
        {
            new Box("b1", "在副屏", new DipRect(10, 10, 200, 200)) { MonitorId = @"\\.\DISPLAY2" },
            new Box("b2", "屏没了", new DipRect(10, 10, 200, 200)) { MonitorId = @"\\.\DISPLAY7" },
            new Box("b3", "没记录", new DipRect(10, 10, 200, 200)),
        };

        var resolved = MonitorMatcher.ResolveAll(boxes, Both, Primary);

        Assert.Equal(3, resolved.Count);
        Assert.Equal(@"\\.\DISPLAY2", resolved[0].Monitor.Id);
        Assert.Equal(MonitorMatchKind.ExactId, resolved[0].Kind);
        Assert.Equal(MonitorMatchKind.Fallback, resolved[1].Kind);
        Assert.Equal(MonitorMatchKind.Fallback, resolved[2].Kind);
    }
}