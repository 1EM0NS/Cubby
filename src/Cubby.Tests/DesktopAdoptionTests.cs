using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Core.Platform;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 桌面图标吸附的决策逻辑。这里全部是纯计算——不读注册表、不调 Win32、不动文件，
/// 因此"吸附过程不需要写权限"这件事本身就能被测试固定下来（P4）。
/// </summary>
public sealed class DesktopAdoptionTests
{
    private static readonly PixelRect Area = new(0, 0, 400, 400);

    [Fact]
    public void 只吸附落在盒子范围内的图标()
    {
        var icons = new List<DesktopIcon>
        {
            new("在范围内", 100, 100),
            new("在范围外", 500, 100),
        };

        var candidates = new List<string> { @"C:\Desktop\在范围内.txt", @"C:\Desktop\在范围外.txt" };
        var plan = DesktopAdoption.Plan(icons, Area, candidates);

        Assert.Equal(["C:\\Desktop\\在范围内.txt"], plan.MatchedPaths);
        Assert.Equal(1, plan.IconsInArea);
        Assert.Equal(2, plan.IconTotal);
    }

    [Fact]
    public void 范围判定是左闭右开()
    {
        var icons = new List<DesktopIcon>
        {
            new("左上角", 0, 0),
            new("右下角内侧", 399, 399),
            new("右下角边界", 400, 400),
        };

        var plan = DesktopAdoption.Plan(icons, Area, []);

        Assert.Equal(2, plan.IconsInArea);
    }

    [Fact]
    public void 文件名带不带扩展名都能匹配上()
    {
        var candidates = new List<string> { @"C:\Desktop\报告.txt" };

        Assert.Equal(@"C:\Desktop\报告.txt", DesktopAdoption.Match("报告.txt", candidates));
        Assert.Equal(@"C:\Desktop\报告.txt", DesktopAdoption.Match("报告", candidates));
        Assert.Null(DesktopAdoption.Match("别的文件", candidates));
    }

    [Fact]
    public void 匹配不区分大小写()
    {
        var candidates = new List<string> { @"C:\Desktop\README.MD" };

        Assert.Equal(@"C:\Desktop\README.MD", DesktopAdoption.Match("readme", candidates));
    }

    [Fact]
    public void 虚拟图标记为未匹配而不是硬塞进盒子()
    {
        var icons = new List<DesktopIcon>
        {
            new("回收站", 10, 10),
            new("已有文件", 20, 20),
        };

        var plan = DesktopAdoption.Plan(icons, Area, [@"C:\Desktop\已有文件.txt"]);

        Assert.Single(plan.MatchedPaths);
        Assert.Equal(["回收站"], plan.UnmatchedNames);
    }

    [Fact]
    public void 多个图标指向同一文件时只登记一次()
    {
        var icons = new List<DesktopIcon>
        {
            new("同一个", 10, 10),
            new("同一个.txt", 30, 30),
        };

        var plan = DesktopAdoption.Plan(icons, Area, [@"C:\Desktop\同一个.txt"]);

        Assert.Single(plan.MatchedPaths);
        Assert.Equal(2, plan.IconsInArea);
    }

    [Fact]
    public void 空图标名不会匹配到任何文件()
    {
        Assert.Null(DesktopAdoption.Match("   ", [@"C:\Desktop\a.txt"]));
    }

    [Fact]
    public void 摘要能把没吸到的原因说清楚()
    {
        var plan = DesktopAdoption.Plan(
            [new DesktopIcon("回收站", 1, 1), new DesktopIcon("文档", 2, 2)],
            Area,
            [@"C:\Desktop\文档.txt"]);

        var description = plan.Describe();

        Assert.Contains("桌面图标 2 个", description);
        Assert.Contains("匹配到文件 1 个", description);
        Assert.Contains("回收站", description);
    }

    [Fact]
    public void 桌面目录列表里的路径必须真实存在()
    {
        var directories = DesktopFolders.Directories();

        Assert.NotEmpty(directories);
        Assert.All(directories, directory => Assert.True(Directory.Exists(directory)));
    }
}