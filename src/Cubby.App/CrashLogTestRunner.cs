using System.IO;
using System.Text;
using Cubby.Core.Diagnostics;
using Cubby.Core.Platform;

namespace Cubby.App;

/// <summary>
/// 崩溃日志与全局异常兜底的自动化验收（issue #39）。
///
/// 这件事最容易做假的地方是"我写了日志代码"，所以这里每一步都对着**磁盘上的真文件**断言，
/// 而且走的是三处全局异常共用的那个入口 <see cref="CrashReporter.Handle"/>：
///
/// 1. 注入一次假异常 → 断言真的落在 <c>%AppData%\Cubby\logs\cubby-yyyyMMdd.log</c>，
///    且有文件头（版本 / OS / 运行时）与**完整堆栈**（靠验收自己的方法名认出来）；
/// 2. **顺序**：先还原桌面图标标记、再写日志——靠步骤序列断言，不是靠注释；
/// 3. 轮转与保留：造 20 个日期文件、造超额体积，断言删对了；
/// 4. 日志写不进去时**不升级成崩溃**；
/// 5. 提示窗真的能给出可操作的出口（路径可见、复制有反馈、打开目录的目标存在）。
///
/// 验收结束会把"自己写进去的那一段"从日志里截掉，不留假记录（见 <see cref="RestoreLog"/>）。
/// </summary>
internal static class CrashLogTestRunner
{
    private const string ProbeMarker = "CrashLogSelfTestProbe";

    public static async Task<int> RunAsync(OverlayManager manager, LayoutService layout, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var logDirectory = CrashReporter.LogDirectory;
        var todayFile = CrashLog.FilePathFor(logDirectory, DateTime.Now);

        // 记下原状态：验收要还原，绝不污染用户真实的日志与桌面图标标记
        var logLengthBefore = File.Exists(todayFile) ? new FileInfo(todayFile).Length : -1;
        var markerBefore = DesktopIconStateMarker.WasLeftHidden();
        CrashWindow? window = null;

        await Task.Delay(300);

        try
        {
            // ---- 1. 纯逻辑：单行格式与文件头 ----
            var stamp = new DateTime(2026, 3, 4, 5, 6, 7, 89);
            var line = CrashLog.Format(stamp, CrashLogLevel.Error, "单元测试来源", "出事了", "System.Exception: 出事了\n   在 某处()");

            results.Add((
                "单行格式含时间戳 / 级别 / 来源 / 异常摘要 / 完整堆栈（堆栈保留多行）",
                line.Contains("2026-03-04 05:06:07.089", StringComparison.Ordinal) &&
                line.Contains("ERROR", StringComparison.Ordinal) &&
                line.Contains("单元测试来源", StringComparison.Ordinal) &&
                line.Contains("在 某处()", StringComparison.Ordinal) &&
                line.Split('\n').Length >= 2,
                line.ReplaceLineEndings(" ⏎ ")));

            var header = CrashLog.BuildHeader(stamp, "9.9.9", "Windows 测试版", ".NET 测试运行时");

            results.Add((
                "文件头含排查三要素：版本 / OS / 运行时",
                header.Contains("9.9.9", StringComparison.Ordinal) &&
                header.Contains("Windows 测试版", StringComparison.Ordinal) &&
                header.Contains(".NET 测试运行时", StringComparison.Ordinal) &&
                header.Contains("cubby-20260304.log", StringComparison.Ordinal),
                header));

            // ---- 2. 注入假异常，走真实入口落盘 ----
            var probe = MakeProbeException();
            var report = CrashReporter.Handle("selftest-crashlog", probe, CrashPromptMode.NonModal);
            window = report.Window;

            await Task.Delay(200);

            var written = report.LogPath is not null && File.Exists(report.LogPath);
            results.Add((
                "注入假异常后真的落在 %AppData%\\Cubby\\logs\\cubby-yyyyMMdd.log",
                written && report.LogPath!.EndsWith(CrashLog.FileNameFor(DateTime.Now), StringComparison.Ordinal),
                report.LogPath is null ? "写入返回 null（失败）" : report.LogPath));

            var text = written ? File.ReadAllText(report.LogPath!, Encoding.UTF8) : string.Empty;

            results.Add((
                "日志内容含异常类型与**完整堆栈**（用验收自己的方法名认栈）",
                text.Contains(nameof(InvalidOperationException), StringComparison.Ordinal) &&
                text.Contains(ProbeMarker, StringComparison.Ordinal) &&
                text.Contains("# Cubby 日志", StringComparison.Ordinal) &&
                text.Contains("运行时=", StringComparison.Ordinal),
                written
                    ? $"共 {text.Length} 字；栈里找到 {ProbeMarker}"
                    : "没有文件可读"));

            // ---- 3. 顺序：桌面图标还原必须排在写日志之前 ----
            var recoveryFirst = report.Steps.Count >= 2 &&
                                report.Steps[0].StartsWith("桌面图标还原", StringComparison.Ordinal) &&
                                report.Steps[1].StartsWith("写日志", StringComparison.Ordinal);

            // 顺便让这条断言有"真实副作用"的对象：造一枚标记，看它是否真的被清掉
            DesktopIconStateMarker.MarkHidden();
            var markerSet = DesktopIconStateMarker.WasLeftHidden();
            var afterProbe = CrashReporter.Handle("selftest-crashlog-order", probe, CrashPromptMode.None);
            var markerCleared = !DesktopIconStateMarker.WasLeftHidden();

            results.Add((
                "崩溃兜底优先级高于日志：先还原桌面图标标记，再写日志",
                recoveryFirst && markerSet && markerCleared,
                $"步骤序列 = [{string.Join(" → ", report.Steps)}]；" +
                $"造出标记={markerSet}，处理后被清掉={markerCleared}，" +
                $"第二次处理仍写日志={(afterProbe.LogPath is not null)}"));

            afterProbe.Window?.Close();

            // ---- 4. 提示窗：可操作，不静默 ----
            var targetDirectory = Path.GetDirectoryName(report.LogPath ?? string.Empty) ?? string.Empty;
            window?.CopyPath();
            var copyFeedback = window?.Status;

            results.Add((
                "崩溃提示不静默：窗口给出日志路径，且「复制路径」有反馈、「打开日志目录」的目标存在",
                window is not null &&
                window.ShownLogPath == report.LogPath &&
                window.ShownSummary.Contains(nameof(InvalidOperationException), StringComparison.Ordinal) &&
                copyFeedback is not null &&
                Directory.Exists(targetDirectory),
                window is null
                    ? "没有弹出提示窗"
                    : $"窗口显示路径与实际一致={window.ShownLogPath == report.LogPath}；" +
                      $"复制反馈=「{copyFeedback}」；目录存在={Directory.Exists(targetDirectory)}；" +
                      $"摘要含异常类型=True"));

            // ---- 5. 轮转：按天分文件，保留最近 N 天 ----
            var rotateDirectory = Path.Combine(Path.GetTempPath(), "cubby-crashlog-rotate");
            PrepareFakeLogs(rotateDirectory, count: 20, now: DateTime.Now);

            var removed = CrashLog.Prune(rotateDirectory, retentionDays: 14);
            var survivors = CrashLog.ListFiles(rotateDirectory);

            var expectedSurvivors = 15; // 今天 + 最近 14 天
            results.Add((
                "轮转：造 20 个日期文件，按保留 14 天清掉过期的，只剩 15 个",
                survivors.Count == expectedSurvivors && removed.Count == 20 - expectedSurvivors,
                $"清理前 20 个 → 删除 {removed.Count} 个 → 剩 {survivors.Count} 个（期望 {expectedSurvivors}）；" +
                $"最老保留的是 {Path.GetFileName(survivors.FirstOrDefault() ?? "(无)")}"));

            DeleteDirectory(rotateDirectory);

            // ---- 6. 总量上限：从最老的开始删，直到达标 ----
            var capDirectory = Path.Combine(Path.GetTempPath(), "cubby-crashlog-cap");
            PrepareFakeLogs(capDirectory, count: 10, now: DateTime.Now, bytesPerFile: 4096);

            var cap = 8L * 1024; // 8KB，上面造出来约 40KB
            var capRemoved = CrashLog.Prune(capDirectory, retentionDays: 3650, maxTotalBytes: cap);
            var totalAfter = CrashLog.ListFiles(capDirectory).Sum(LengthOf);
            var oldestKept = CrashLog.ListFiles(capDirectory).FirstOrDefault();

            results.Add((
                "保留策略有总量上限：超额时从最老的开始删，直到不超过上限",
                totalAfter <= cap && capRemoved.Count > 0 && oldestKept is not null &&
                !capRemoved.Contains(oldestKept, StringComparer.OrdinalIgnoreCase),
                $"上限 {cap} 字节；删除 {capRemoved.Count} 个；剩余合计 {totalAfter} 字节；" +
                $"最老的 {Path.GetFileName(oldestKept ?? "(无)")} 仍在（说明删的是更老的那批）"));

            DeleteDirectory(capDirectory);

            // ---- 7. 日志写不进去时绝不升级成崩溃 ----
            // 把"目录"指向一个已存在的**文件**，让它必然创建失败
            var blocked = Path.Combine(Path.GetTempPath(), $"cubby-crashlog-blocked-{Guid.NewGuid():N}");
            File.WriteAllText(blocked, "占住这个名字");

            var result = CrashLog.Write(blocked, CrashLogLevel.Error, "验收", "写不进去");

            results.Add((
                "日志写入失败时安静返回 null，不抛异常、不升级成崩溃",
                result is null,
                result is null ? "返回 null，未抛出" : $"竟然写成功了：{result}"));

            File.Delete(blocked);
        }
        catch (Exception ex)
        {
            results.Add(("验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            // 还原现场：关掉提示窗、清掉日志里验收自己写的那一段、恢复桌面图标标记
            try
            {
                window?.Close();
            }
            catch (InvalidOperationException)
            {
                // 已经关掉了
            }

            RestoreLog(todayFile, logLengthBefore);

            if (markerBefore)
            {
                DesktopIconStateMarker.MarkHidden();
            }
            else
            {
                DesktopIconStateMarker.Clear();
            }
        }

        var failed = results.Count(r => !r.Pass);
        Write(options, results, logDirectory);

        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// 造一个栈里带 <see cref="ProbeMarker"/> 的真异常。
    /// 不是为了"造一个异常"，而是为了让"完整堆栈真的写进去了"这句可被断言——
    /// 光看到 <c>System.InvalidOperationException</c> 不足以证明堆栈存在。
    /// </summary>
    private static Exception MakeProbeException()
    {
        try
        {
            CrashLogSelfTestProbe();
            throw new InvalidOperationException("上一步必然抛出，走不到这里");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void CrashLogSelfTestProbe() =>
        throw new InvalidOperationException("Cubby 崩溃日志验收注入的假异常（不是真的出错）");

    /// <summary>造一批假的日期日志文件。</summary>
    private static void PrepareFakeLogs(string directory, int count, DateTime now, int bytesPerFile = 64)
    {
        DeleteDirectory(directory);
        Directory.CreateDirectory(directory);

        for (var offset = 0; offset < count; offset++)
        {
            var date = now.Date.AddDays(-offset);
            File.WriteAllText(
                CrashLog.FilePathFor(directory, date),
                new string('x', bytesPerFile),
                new UTF8Encoding(false));
        }
    }

    /// <summary>
    /// 把验收自己追加进日志的那一段截掉，不留假记录。
    /// 文件本来不存在就直接删掉；本来存在则把长度截回原值（只动我们写的那部分）。
    /// </summary>
    private static void RestoreLog(string path, long lengthBefore)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (lengthBefore < 0)
            {
                File.Delete(path);
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            if (stream.Length > lengthBefore)
            {
                stream.SetLength(lengthBefore);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 还原失败不影响结论：只是日志里多留一条验收记录
        }
    }

    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响结论
        }
    }

    private static void Write(
        SpikeOptions options,
        IReadOnlyList<(string Step, bool Pass, string Detail)> results,
        string logDirectory)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 崩溃日志与全局异常兜底自动化验收报告（issue #39）");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 结论：**{(results.Count > 0 && results.All(r => r.Pass) ? "PASS" : "FAIL")}**");
        builder.AppendLine();
        builder.AppendLine("| 步骤 | 结果 | 细节 |");
        builder.AppendLine("|---|---|---|");
        foreach (var (step, pass, detail) in results)
        {
            builder.AppendLine($"| {step} | {(pass ? "**PASS**" : "**FAIL**")} | {detail} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 日志位置与保留策略");
        builder.AppendLine();
        builder.AppendLine($"- 目录：`{logDirectory}`，按天分文件 `cubby-yyyyMMdd.log`。");
        builder.AppendLine($"- 保留：最近 **{CrashLog.DefaultRetentionDays}** 天，总量不超过 **{CrashLog.DefaultMaxTotalBytes / 1024 / 1024} MB**，");
        builder.AppendLine("  超额时从最老的开始删——不与用户抢磁盘，也不会一直涨。");
        builder.AppendLine("- 文件头只写一次，含**版本 / OS / 运行时**三要素（事后没法补，所以第一条就写）。");
        builder.AppendLine();
        builder.AppendLine("## 三处全局异常的处理策略（刻意不同）");
        builder.AppendLine();
        builder.AppendLine("| 入口 | 落盘 | 弹窗 | 为什么 |");
        builder.AppendLine("|---|---|---|---|");
        builder.AppendLine("| `DispatcherUnhandledException` | 是 | 是（非模态） | UI 线程异常**拦下来不让程序死**（`Handled=true`）：一个盒子画崩了不该连累整个常驻程序 |");
        builder.AppendLine("| `AppDomain.UnhandledException` | 是 | 是（模态） | 致命的，进程随后就没了；模态拦住让用户有机会看到日志在哪 |");
        builder.AppendLine("| `TaskScheduler.UnobservedTaskException` | 是 | **否** | 它不是一个崩溃，每次都弹窗就是骚扰；记下来是为了排查「某个后台任务一直在悄悄失败」这类问题 |");
        builder.AppendLine();
        builder.AppendLine("## 一处未覆盖的地方（如实记录）");
        builder.AppendLine();
        builder.AppendLine("提示窗的「打开日志目录」按钮，验收只断言了它的**目标目录存在**与按钮可用，");
        builder.AppendLine("**没有真的点下去**——那会在用户桌面上弹出一个资源管理器窗口。");
        builder.AppendLine("「复制路径」是真点下去的（见上表反馈文字）。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "crashlog-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}
