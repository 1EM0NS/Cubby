using Cubby.Core.Platform;
using Xunit;

namespace Cubby.Tests;

/// <summary>共存检测（A8）的纯逻辑用例。检测是只读的，因此这些用例不碰任何进程。</summary>
public class CoexistToolsTests
{
    [Fact]
    public void Detect_FindsKnownTool()
    {
        var detected = CoexistTools.Detect(["chrome", "Fences", "explorer"]);

        Assert.Single(detected);
        Assert.Equal("Fences", detected[0].ProcessName);
    }

    [Fact]
    public void Detect_IsCaseInsensitive()
    {
        var detected = CoexistTools.Detect(["deskgo"]);

        Assert.Single(detected);
        Assert.Equal("DeskGo", detected[0].ProcessName);
    }

    [Fact]
    public void Detect_ReturnsEmpty_WhenNothingRuns()
    {
        Assert.Empty(CoexistTools.Detect([]));
        Assert.Empty(CoexistTools.Detect(["chrome", "explorer", "Code"]));
    }

    [Fact]
    public void Detect_IgnoresBlankNames()
    {
        Assert.Empty(CoexistTools.Detect(["", "   "]));
    }

    /// <summary>
    /// 最要紧的一条：Wallpaper Engine 是本项目要保护的场景，**绝不能**被算成冲突软件。
    /// 一旦误判，用户会收到"建议二选一"的提示，而它恰恰是我们比同类工具强的地方。
    /// </summary>
    [Theory]
    [InlineData("wallpaper32")]
    [InlineData("wallpaper64")]
    [InlineData("WALLPAPER64")]
    public void Detect_NeverFlagsWallpaperEngine(string processName)
    {
        Assert.True(CoexistTools.IsCompatible(processName));
        Assert.Empty(CoexistTools.Detect([processName]));
    }

    [Fact]
    public void ConflictingList_FollowsTheCompatibleRule()
    {
        Assert.NotEmpty(CoexistTools.Conflicting);

        // 名单本身必须是干净的：不能把可共存的进程写进去
        Assert.DoesNotContain(CoexistTools.Conflicting, tool => CoexistTools.IsCompatible(tool.ProcessName));

        // 每条都要能说清"冲突在哪"，否则提示没有信息量
        Assert.All(CoexistTools.Conflicting, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(tool.Why));
            Assert.DoesNotContain(".exe", tool.ProcessName, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Describe_SaysNothingFound_WhenEmpty()
    {
        Assert.Equal("没有检测到同类桌面整理软件。", CoexistTools.Describe([]));
    }

    [Fact]
    public void Describe_ListsDisplayNameAndProcessName()
    {
        var described = CoexistTools.Describe(CoexistTools.Detect(["DeskGo"]));

        Assert.Contains("腾讯桌面整理", described);
        Assert.Contains("DeskGo.exe", described);
    }
}