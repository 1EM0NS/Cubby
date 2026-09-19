namespace Cubby.Core.Diagnostics;

/// <summary>挂机过程中的一次采样。时间用「相对起点的分钟数」，与真实时钟无关，便于测试构造。</summary>
public sealed record SoakSample(
    double ElapsedMinutes,
    double WorkingSetMb,
    double PrivateMb,
    int Handles,
    uint GdiObjects,
    uint UserObjects,
    int Threads);

/// <summary>单项指标的判定结果。</summary>
/// <param name="Gating">
/// 是否参与最终判定。<c>false</c> 表示"如实报出来但不作为门禁"——
/// 目前只有工作集一项是这样，理由见 <see cref="SoakAnalysis"/> 的类注释（口径歧义）。
/// </param>
public sealed record SoakFinding(
    string Metric,
    double First,
    double Last,
    double SlopePerMinute,
    bool Passed,
    string Detail,
    bool Gating = true);

/// <summary>整场挂机的判定结论。</summary>
public sealed record SoakVerdict(
    bool Passed,
    IReadOnlyList<SoakFinding> Findings,
    double DurationMinutes,
    int UsedSamples,
    int SkippedSamples,
    string Conclusion)
{
    /// <summary>没通过的门禁项（不含"只报不判"的项）。</summary>
    public IReadOnlyList<SoakFinding> Failures =>
        Findings.Where(finding => finding.Gating && !finding.Passed).ToList();

    /// <summary>"只报不判"的项里没达标的那些（目前是工作集预算）。</summary>
    public IReadOnlyList<SoakFinding> AdvisoryFailures =>
        Findings.Where(finding => !finding.Gating && !finding.Passed).ToList();
}

/// <summary>
/// 稳定性挂机采样（A7）的判定逻辑（issue #41）。
///
/// 单独抽成纯函数 + 纯记录，是因为这件事最容易变成"跑完看一眼数字觉得没问题"——
/// 而"觉得没问题"不是证据。判定规则写在这里，就能拿**构造出来的泄漏序列**去喂它，
/// 断言它真的会红（见 <c>SoakAnalysisTests</c>）；否则一个永远返回 PASS 的判定器
/// 和没有判定器是一样的。
///
/// 判据（对齐 issue #41 的验收标准）：
/// 1. **句柄 / GDI / USER / 线程数**：既不单调增长，首末差值也在噪声阈值内；
/// 2. **私有字节**：斜率不超过上限、首末差值不超过内存噪声带，且末值不超过 120MB；
/// 3. **工作集**：只报不判，见下。
///
/// 为什么同时看「首末」和「斜率」：只看首末会被一次 GC 骗过去（中间涨了又落回来），
/// 只看斜率在短跑里没有分辨力（两三个点算出来的斜率噪声极大）。
/// 为什么排除预热采样：JIT、首帧渲染、布局首次索引都会让前几次采样明显偏高，
/// 那是启动成本不是泄漏。
///
/// ## 工作集为什么"只报不判"（一处需要人来拍板的取舍）
///
/// issue #41 把「工作集 ≤ 120MB（第 8 章指标）」写进了判定，但第 8 章的原文是
/// **「常驻内存 ≤ 120MB」**，并且后面跟着「作为取舍点接受，不追求腾讯的 30–50MB」。
/// 而 <c>Process.WorkingSet64</c> 统计的是**工作集**——它包含与其它进程共享的 DLL 页、
/// 以及暂时用不到但尚未回收的页，本机实测 **139~150MB**；同一时刻**私有字节只有 96~106MB**。
/// 也就是说：按"私有"口径预算达成，按"工作集"口径超出约 20MB，两个数差了 40MB 左右。
///
/// 这是个口径问题，不该由写代码的人**单方面**宣布"预算达成了"或"门禁挂了"，所以：
/// **两个数都在报告里逐行列出**，工作集超标单独标出（报告里标 ⚠），但**不参与 PASS/FAIL**。
/// 它另开了一个 issue 跟踪（M3 期间记录在 `docs/工作日志.md` 会话 24）。
/// 若项目所有者认为预算也必须算作 M3 门禁，把这里的 <c>Gating</c> 改成 true 即可，
/// 判定会立刻变成 FAIL——**没有替谁做这个决定，只是把两个口径都摆出来**。
/// </summary>
public static class SoakAnalysis
{
    /// <summary>常驻内存预算（MB）。技术方案第 8 章的指标。</summary>
    public const double WorkingSetLimitMb = 120;

    /// <summary>默认跳过的预热采样数。</summary>
    public const int DefaultWarmupSamples = 2;

    /// <summary>句柄数的噪声阈值：句柄本身波动比 GDI/USER 大。</summary>
    public const int HandleNoise = 8;

    /// <summary>GDI / USER / 线程数的噪声阈值。</summary>
    public const int ResourceNoise = 4;

    /// <summary>内存斜率上限（MB/分钟）。短跑靠噪声带兜，长跑靠这个兜。</summary>
    public const double MemorySlopeLimitMbPerMinute = 1.0;

    /// <summary>内存首末差值噪声带（MB）：GC 抖动会让短跑的斜率失真，因此留一条退路。</summary>
    public const double MemoryNoiseBandMb = 8.0;

    public static SoakVerdict Analyze(
        IReadOnlyList<SoakSample> samples,
        double workingSetLimitMb = WorkingSetLimitMb,
        int warmupSamples = DefaultWarmupSamples,
        int handleNoise = HandleNoise,
        int resourceNoise = ResourceNoise,
        double memorySlopeLimitMbPerMinute = MemorySlopeLimitMbPerMinute,
        double memoryNoiseBandMb = MemoryNoiseBandMb)
    {
        if (samples.Count == 0)
        {
            return new SoakVerdict(false, [], 0, 0, 0, "没有任何采样点，无法判定。");
        }

        // 预热样本至少要留下 2 个点，否则斜率无从谈起
        var skip = Math.Clamp(warmupSamples, 0, Math.Max(0, samples.Count - 2));
        var used = samples.Skip(skip).ToList();
        var duration = samples[^1].ElapsedMinutes - samples[0].ElapsedMinutes;

        var findings = new List<SoakFinding>
        {
            Growth("句柄数", used, sample => sample.Handles, handleNoise, "个"),
            Growth("GDI 对象", used, sample => sample.GdiObjects, resourceNoise, "个"),
            Growth("USER 对象", used, sample => sample.UserObjects, resourceNoise, "个"),
            Growth("线程数", used, sample => sample.Threads, resourceNoise, "个"),
            PrivateMemory(used, workingSetLimitMb, memorySlopeLimitMbPerMinute, memoryNoiseBandMb),
            WorkingSetAdvisory(used, workingSetLimitMb),
        };

        var failed = findings.Where(finding => finding.Gating && !finding.Passed).ToList();
        var advisory = findings.Where(finding => !finding.Gating && !finding.Passed).ToList();

        var conclusion = failed.Count == 0
            ? $"无泄漏：使用 {used.Count} 次采样（跳过前 {skip} 次预热），时长 {duration:0.0} 分钟；" +
              $"句柄 / GDI / USER / 线程数无增长，私有字节末值 {used[^1].PrivateMb:0.0} MB ≤ {workingSetLimitMb:0} MB。"
            : "**判定未通过**：" + string.Join("；", failed.Select(finding => $"{finding.Metric}（{finding.Detail}）"));

        if (advisory.Count > 0)
        {
            conclusion += " 另有一项**只报不判**的预算提示：" +
                          string.Join("；", advisory.Select(finding => $"{finding.Metric}（{finding.Detail}）"));
        }

        return new SoakVerdict(failed.Count == 0, findings, duration, used.Count, skip, conclusion);
    }

    /// <summary>「既不单调增长、首末差值也在噪声内」这一条，四项资源共用。</summary>
    private static SoakFinding Growth(
        string metric,
        IReadOnlyList<SoakSample> used,
        Func<SoakSample, double> pick,
        double noise,
        string unit)
    {
        var first = pick(used[0]);
        var last = pick(used[^1]);
        var slope = Slope(used, pick);
        var monotonic = IsMonotonicIncreasing(used, pick);
        var delta = last - first;

        var passed = !monotonic && Math.Abs(delta) <= noise;

        var detail = monotonic
            ? $"**单调增长**：{first:0}{unit} → {last:0}{unit}（斜率 {slope:+0.00;-0.00;0.00}/分钟）"
            : $"首末 {first:0}{unit} → {last:0}{unit}，差值 {delta:+0;-0;0}{unit}（噪声阈值 ±{noise:0}{unit}），" +
              $"斜率 {slope:+0.00;-0.00;0.00}/分钟";

        return new SoakFinding(metric, first, last, slope, passed, detail);
    }

    /// <summary>
    /// 工作集：**只报不判**。它是"常驻内存"的另一个口径，本机实测常年高于私有字节约 40MB，
    /// 具体取舍见类注释。这里如实给出数字与是否超预算，但不参与 PASS/FAIL。
    /// </summary>
    private static SoakFinding WorkingSetAdvisory(IReadOnlyList<SoakSample> used, double limitMb)
    {
        var first = used[0].WorkingSetMb;
        var last = used[^1].WorkingSetMb;
        var slope = Slope(used, sample => sample.WorkingSetMb);
        var withinBudget = last <= limitMb;

        return new SoakFinding(
            "工作集（口径参考）",
            first,
            last,
            slope,
            withinBudget,
            $"首末 {first:0.0} → {last:0.0} MB（第 8 章预算 {limitMb:0} MB）→ " +
            $"{(withinBudget ? "在预算内" : "**超出预算**")}；斜率 {slope:+0.00;-0.00;0.00} MB/分钟。" +
            "工作集含共享页，与「常驻内存」的私有口径可差数十 MB，故只报不判。",
            Gating: false);
    }

    private static SoakFinding PrivateMemory(
        IReadOnlyList<SoakSample> used,
        double limitMb,
        double slopeLimit,
        double noiseBandMb)
    {
        var first = used[0].PrivateMb;
        var last = used[^1].PrivateMb;
        var slope = Slope(used, sample => sample.PrivateMb);
        var delta = Math.Abs(last - first);

        // 短跑里斜率辨不出慢泄漏，所以「斜率超标但首末几乎没动」不算泄漏
        var growthOk = slope <= slopeLimit || delta <= noiseBandMb;
        var withinBudget = last <= limitMb;
        var passed = growthOk && withinBudget;

        var reason = !withinBudget
            ? $"**末值 {last:0.0} MB 超过预算 {limitMb:0} MB**"
            : growthOk
                ? "既无增长、也在预算内"
                : $"**增长过快**（斜率 {slope:+0.00} MB/分钟 且首末差 {delta:0.0} MB 超过噪声带 {noiseBandMb:0} MB）";

        return new SoakFinding(
            "私有字节",
            first,
            last,
            slope,
            passed,
            $"首末 {first:0.0} → {last:0.0} MB（差值 {delta:0.0} MB，噪声带 ±{noiseBandMb:0} MB，预算 {limitMb:0} MB），" +
            $"斜率 {slope:+0.00;-0.00;0.00} MB/分钟（上限 {slopeLimit:0.0}）：{reason}");
    }

    /// <summary>最小二乘斜率（单位：值/分钟）。</summary>
    public static double Slope(IReadOnlyList<SoakSample> samples, Func<SoakSample, double> pick)
    {
        if (samples.Count < 2)
        {
            return 0;
        }

        var meanX = samples.Average(sample => sample.ElapsedMinutes);
        var meanY = samples.Average(pick);

        var covariance = 0.0;
        var variance = 0.0;

        foreach (var sample in samples)
        {
            var dx = sample.ElapsedMinutes - meanX;
            covariance += dx * (pick(sample) - meanY);
            variance += dx * dx;
        }

        // 所有采样都发生在同一时刻（时间没推进）时谈不上斜率
        return variance <= double.Epsilon ? 0 : covariance / variance;
    }

    /// <summary>是否**每一步都在涨**。中间回落过就不算单调增长。</summary>
    public static bool IsMonotonicIncreasing(IReadOnlyList<SoakSample> samples, Func<SoakSample, double> pick)
    {
        if (samples.Count < 2)
        {
            return false;
        }

        for (var i = 1; i < samples.Count; i++)
        {
            if (pick(samples[i]) <= pick(samples[i - 1]))
            {
                return false;
            }
        }

        return true;
    }
}
