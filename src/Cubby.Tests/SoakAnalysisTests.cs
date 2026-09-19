using Cubby.Core.Diagnostics;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 挂机门禁判定逻辑（A7）的测试。
///
/// 这里最重要的不是"干净序列能通过"，而是**构造出来的泄漏序列必须被判定为失败**——
/// 一个永远返回 PASS 的判定器，和没有判定器是一样的。
/// 所以每个守门条件都配了一个"应当失败"的用例。
/// </summary>
public sealed class SoakAnalysisTests
{
    /// <summary>
    /// 造一条平稳的采样序列（可指定小幅波动）。
    /// 工作集与私有字节**分开给**：这两项在真实进程里差几十 MB，混用一个值会造出
    /// "工作集超预算"和"私有字节超预算"同时成立的场景，测不出单一口径的行为。
    /// </summary>
    private static List<SoakSample> Flat(
        int count = 10,
        double intervalMinutes = 0.5,
        double workingSetMb = 60,
        double privateMb = 80,
        int handles = 500,
        uint gdi = 20,
        uint user = 40,
        int threads = 12)
    {
        var samples = new List<SoakSample>();

        for (var i = 0; i < count; i++)
        {
            // 轻微上下波动：真实进程的数字不会一动不动
            var wobble = i % 2 == 0 ? 0 : 1;

            samples.Add(new SoakSample(
                i * intervalMinutes,
                workingSetMb + wobble,
                privateMb + wobble,
                handles + wobble,
                gdi + (uint)wobble,
                user + (uint)wobble,
                threads));
        }

        return samples;
    }

    [Fact]
    public void 平稳序列通过门禁()
    {
        var verdict = SoakAnalysis.Analyze(Flat());

        Assert.True(verdict.Passed, verdict.Conclusion);
        Assert.Equal(10 - SoakAnalysis.DefaultWarmupSamples, verdict.UsedSamples);
        Assert.Equal(SoakAnalysis.DefaultWarmupSamples, verdict.SkippedSamples);
        Assert.Contains("无泄漏", verdict.Conclusion);
    }

    [Fact]
    public void 空序列不通过而不是崩溃()
    {
        var verdict = SoakAnalysis.Analyze([]);

        Assert.False(verdict.Passed);
        Assert.Contains("无法判定", verdict.Conclusion);
    }

    [Fact]
    public void 句柄单调增长会被判失败()
    {
        var samples = Flat();
        for (var i = 0; i < samples.Count; i++)
        {
            samples[i] = samples[i] with { Handles = 500 + (i * 5) };
        }

        var verdict = SoakAnalysis.Analyze(samples);

        Assert.False(verdict.Passed);
        Assert.Contains("句柄数", verdict.Conclusion);
        Assert.Contains("单调增长", verdict.Conclusion);
    }

    [Fact]
    public void GDI对象缓慢泄漏也会被判失败()
    {
        // 每采样一次涨 1 个：幅度不大，但单调涨——正是最容易被人眼忽略的泄漏
        var samples = Flat(gdi: 100);
        for (var i = 0; i < samples.Count; i++)
        {
            samples[i] = samples[i] with { GdiObjects = (uint)(100 + i) };
        }

        var verdict = SoakAnalysis.Analyze(samples);

        Assert.False(verdict.Passed);
        Assert.Contains("GDI 对象", verdict.Conclusion);
    }

    [Fact]
    public void USER对象增长超噪声阈值会被判失败()
    {
        var samples = Flat();
        // 首末差 20，远超噪声阈值 4；但不是严格单调（中间回落一次），所以考的是"差值"这一条
        for (var i = 0; i < samples.Count; i++)
        {
            samples[i] = samples[i] with { UserObjects = (uint)(40 + (i == 5 ? 0 : i * 2)) };
        }

        var verdict = SoakAnalysis.Analyze(samples);

        Assert.False(verdict.Passed);
        Assert.Contains("USER 对象", verdict.Conclusion);
    }

    [Fact]
    public void 线程数泄漏会被判失败()
    {
        var samples = Flat();
        for (var i = 0; i < samples.Count; i++)
        {
            samples[i] = samples[i] with { Threads = 12 + i };
        }

        var verdict = SoakAnalysis.Analyze(samples);

        Assert.False(verdict.Passed);
        Assert.Contains("线程数", verdict.Conclusion);
    }

    /// <summary>
    /// 工作集超预算**不参与 PASS/FAIL**，但必须被如实报出来（<see cref="SoakVerdict.AdvisoryFailures"/>）。
    /// 理由：「工作集」含共享页，与第 8 章说的「常驻内存」不是同一个口径，两者本机差约 40MB。
    /// 这个取舍写在 <see cref="SoakAnalysis"/> 的类注释里，并且两个数都会出现在报告中。
    /// </summary>
    [Fact]
    public void 工作集超预算只报不判但会被列为提示项()
    {
        var verdict = SoakAnalysis.Analyze(Flat(workingSetMb: 200));

        Assert.True(verdict.Passed, verdict.Conclusion);
        Assert.Empty(verdict.Failures);
        var advisory = Assert.Single(verdict.AdvisoryFailures);
        Assert.Contains("工作集", advisory.Metric);
        Assert.Contains("超出预算", verdict.Conclusion);
    }

    [Fact]
    public void 工作集在预算内时没有提示项()
    {
        var verdict = SoakAnalysis.Analyze(Flat(workingSetMb: SoakAnalysis.WorkingSetLimitMb - 1));

        Assert.True(verdict.Passed, verdict.Conclusion);
        Assert.Empty(verdict.AdvisoryFailures);
    }

    /// <summary>私有字节才是"常驻内存"的判定口径，它超预算必须判失败。</summary>
    [Fact]
    public void 私有字节末值超预算会被判失败()
    {
        var verdict = SoakAnalysis.Analyze(Flat(workingSetMb: 60).Select(sample => sample with { PrivateMb = 200 }).ToList());

        Assert.False(verdict.Passed);
        Assert.Contains("私有字节", verdict.Conclusion);
        Assert.Contains("超过预算", verdict.Conclusion);
    }

    [Fact]
    public void 私有字节持续上涨会被判失败()
    {
        var samples = Flat();
        for (var i = 0; i < samples.Count; i++)
        {
            // 每次涨 20MB：斜率 40MB/分钟，远超 1MB/分钟的上限，且首末差远超 8MB 噪声带
            samples[i] = samples[i] with { PrivateMb = 50 + (i * 20) };
        }

        var verdict = SoakAnalysis.Analyze(samples);

        Assert.False(verdict.Passed);
        Assert.Contains("私有字节", verdict.Conclusion);
    }

    /// <summary>
    /// 短跑里的内存斜率会被 GC 抖动带偏：首末几乎没动，但两点之间的斜率很大。
    /// 这种情况**不该**被判成泄漏——否则短跑会随机变红，判定器就没人信了。
    /// </summary>
    [Fact]
    public void 内存斜率超标但首末几乎没动时不算泄漏()
    {
        var samples = Flat();
        for (var i = 0; i < samples.Count; i++)
        {
            // 锯齿：一路涨到中间再落回来，首末差值只有几 MB
            samples[i] = samples[i] with { PrivateMb = i switch { 0 => 50, 1 => 70, 2 => 90, 3 => 60, 4 => 55, _ => 52 } };
        }

        var verdict = SoakAnalysis.Analyze(samples);

        Assert.True(verdict.Passed, verdict.Conclusion);
    }

    [Fact]
    public void 预热采样被排除在判定之外()
    {
        // 前两次采样故意很高（模拟 JIT / 首帧渲染的启动成本），后面平稳
        var samples = Flat(count: 8);
        samples[0] = samples[0] with { Handles = 900, GdiObjects = 300 };
        samples[1] = samples[1] with { Handles = 800, GdiObjects = 200 };

        // 不跳预热时应当失败（首末句柄差了几百个）
        var naive = SoakAnalysis.Analyze(samples, warmupSamples: 0);
        var proper = SoakAnalysis.Analyze(samples);

        Assert.False(naive.Passed);
        Assert.True(proper.Passed, proper.Conclusion);
    }

    [Fact]
    public void 采样点太少时不会把点全跳光()
    {
        // 只有 2 个点时，跳预热的数量要被夹住，保证至少留 2 个点算斜率
        var verdict = SoakAnalysis.Analyze(Flat(count: 2));

        Assert.Equal(0, verdict.SkippedSamples);
        Assert.Equal(2, verdict.UsedSamples);
    }

    [Fact]
    public void 最小二乘斜率算得对()
    {
        // y = 2x + 1
        var samples = new List<SoakSample>
        {
            new(0, 1, 1, 1, 1, 1, 1),
            new(1, 3, 3, 3, 3, 3, 3),
            new(2, 5, 5, 5, 5, 5, 5),
        };

        var slope = SoakAnalysis.Slope(samples, sample => sample.Handles);

        Assert.Equal(2.0, slope, 6);
    }

    [Fact]
    public void 时间没有推进时斜率为零而不是除零()
    {
        var samples = new List<SoakSample>
        {
            new(5, 1, 1, 1, 1, 1, 1),
            new(5, 9, 9, 9, 9, 9, 9),
        };

        Assert.Equal(0, SoakAnalysis.Slope(samples, sample => sample.Handles));
    }

    [Fact]
    public void 单调性判断要求每一步都在涨()
    {
        var rising = new List<SoakSample>
        {
            new(0, 1, 1, 1, 0, 0, 1),
            new(1, 1, 1, 2, 0, 0, 1),
            new(2, 1, 1, 3, 0, 0, 1),
        };

        var withDip = new List<SoakSample>
        {
            new(0, 1, 1, 1, 0, 0, 1),
            new(1, 1, 1, 2, 0, 0, 1),
            new(2, 1, 1, 1, 0, 0, 1),
        };

        Assert.True(SoakAnalysis.IsMonotonicIncreasing(rising, sample => sample.Handles));
        Assert.False(SoakAnalysis.IsMonotonicIncreasing(withDip, sample => sample.Handles));
    }

    [Fact]
    public void 判定结论里带上实际时长与使用样本数()
    {
        var verdict = SoakAnalysis.Analyze(Flat(count: 6, intervalMinutes: 1));

        Assert.Equal(5.0, verdict.DurationMinutes, 6);
        Assert.Contains("5.0 分钟", verdict.Conclusion);
        Assert.Contains("跳过前 2 次预热", verdict.Conclusion);
    }
}
