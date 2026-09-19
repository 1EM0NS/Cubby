using System.Diagnostics;
using System.IO;
using System.Text;
using Cubby.Core.Platform;

namespace Cubby.App;

/// <summary>
/// 同类软件共存的自动化验收（issue #36 / A8）。
///
/// A8 的正文是「与冲突软件共存」。这里要证明两件事，而且第二件比第一件重要：
///
/// 1. **能认出来**：同类桌面整理软件在跑时，启动会提示一次，并且记住用户的选择（下次不再打扰）。
/// 2. **什么都没动**：Cubby 只提示，**不结束对方进程、不改对方窗口**。
///    第 2 条靠一个真实造出来的同名进程来证：复制一份 <c>cmd.exe</c> 成 <c>DeskGo.exe</c> 跑起来，
///    走完整条提示路径后断言**它仍然活着**。这比"代码里没写 Kill" 强得多。
///
/// 另外单独钉一条：**Wallpaper Engine 不能被判成冲突软件**——本项目的立身之本就是与它零冲突。
/// </summary>
internal static class CoexistTestRunner
{
    private const string DecoyName = "DeskGo";

    public static async Task<int> RunAsync(LayoutService layout, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var acknowledgedBefore = layout.CoexistAcknowledged.ToList();
        Process? decoy = null;
        var decoyDirectory = Path.Combine(Path.GetTempPath(), "cubby-coexist-probe");
        CoexistWindow? window = null;

        try
        {
            // 1. 名单自检：Wallpaper Engine 绝不能被当成冲突软件
            var wallpaper = CoexistTools.Detect(["wallpaper32", "wallpaper64", "explorer"]);
            results.Add((
                "名单自检：Wallpaper Engine 不算冲突软件（立身之本，误判即毁核心卖点）",
                wallpaper.Count == 0 && !CoexistTools.Conflicting.Any(t => CoexistTools.IsCompatible(t.ProcessName)),
                $"wallpaper32/64 的检测结果：{(wallpaper.Count == 0 ? "空（正确）" : CoexistTools.Describe(wallpaper))}；" +
                $"可共存名单：{string.Join("、", CoexistTools.Compatible)}"));

            // 2. 纯逻辑：命中与大小写
            var fences = CoexistTools.Detect(["FENCES", "chrome"]);
            var none = CoexistTools.Detect(["chrome", "explorer"]);
            results.Add((
                "检测逻辑：命中同名进程（不区分大小写），无关进程不误报",
                fences.Count == 1 && fences[0].ProcessName == "Fences" && none.Count == 0,
                $"[\"FENCES\",\"chrome\"] → {CoexistTools.Describe(fences)}；[\"chrome\",\"explorer\"] → {none.Count} 项"));

            // 3. 没有冲突时完全静默：不创建窗口
            var silent = CoexistNotice.Show([], layout);
            results.Add((
                "未检测到冲突时完全静默（不弹窗、不打扰）",
                silent is null,
                silent is null ? "没有创建任何窗口" : "竟然创建了窗口"));

            // 4. 造一个真实的同名进程，让"检测"这一步不是纸上谈兵
            decoy = StartDecoy(out var decoyDetail);
            await Task.Delay(900);

            var alive = decoy is not null && IsAlive(decoy);
            results.Add((
                "造出真实的 DeskGo.exe 进程并确认它活着（下一步的「不抢占」才有对象可证）",
                alive,
                decoyDetail));

            if (decoy is null)
            {
                results.Add(("检测真实运行的进程", false, "没有可用对象，后续断言无法进行"));
            }
            else
            {
                var detected = CoexistTools.DetectRunning();
                var hit = detected.FirstOrDefault(t => t.ProcessName.Equals(DecoyName, StringComparison.OrdinalIgnoreCase));
                results.Add((
                    "检测真实运行的进程时认出它",
                    hit is not null,
                    hit is null ? $"检测到：{CoexistTools.Describe(detected)}" : $"检测到「{hit.DisplayName}」：{hit.Why}"));

                // 5. 走真实的提示路径（与启动时同一个入口）
                var pending = CoexistNotice.PendingFor(layout);
                window = CoexistNotice.Show(pending, layout);
                results.Add((
                    "有待确认的冲突软件时，走真实入口弹出提示并列出它",
                    window is not null && window.BoundCount == pending.Count && window.Detected.Count > 0,
                    window is null
                        ? "没有弹窗（不应发生）"
                        : $"窗口列出 {window.BoundCount} 项，合计「{CoexistTools.Describe(window.Detected)}」"));

                // 6. **不抢占**：整条提示路径走完，对方进程必须还活着
                var stillAlive = IsAlive(decoy);
                results.Add((
                    "不抢占：走完提示路径后对方进程仍然存活（没被结束、没被接管）",
                    stillAlive,
                    stillAlive
                        ? $"pid {decoy.Id} 仍在运行；Cubby 全程只读取进程名"
                        : "对方进程消失了（不应发生）"));

                // 7. 记住用户的选择
                if (window is not null)
                {
                    window.SetRemember(true);
                    window.Acknowledge();
                    await Task.Delay(200);

                    var again = CoexistNotice.PendingFor(layout);
                    results.Add((
                        "勾选「不再提示」后记入布局，再次决策为空（下次启动不再打扰）",
                        again.Count == 0 && layout.CoexistAcknowledged.Contains(DecoyName, StringComparer.OrdinalIgnoreCase),
                        $"已确认清单：{(layout.CoexistAcknowledged.Count == 0 ? "(空)" : string.Join("、", layout.CoexistAcknowledged))}；再决策 {again.Count} 项"));
                }

                // 8. 反过来：新装了另一个同类软件仍会提示（不是"提示过就永久闭嘴"）
                var fresh = CoexistNotice.Pending(
                    [.. CoexistTools.Conflicting.Where(t => t.ProcessName is "Fences" or DecoyName)],
                    layout.CoexistAcknowledged);
                results.Add((
                    "已确认的软件不再提示，但**新出现的同类软件**仍会提示一次",
                    fresh.Count == 1 && fresh[0].ProcessName == "Fences",
                    $"示例：已确认 {DecoyName} 后，Fences 仍会提示（{fresh.Count} 项）"));
            }
        }
        catch (Exception ex)
        {
            results.Add(("验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            // 还原现场：已确认清单、我们造的进程、临时目录，一个都不留
            layout.RestoreCoexistAcknowledged(acknowledgedBefore);
            layout.SaveNow();

            try
            {
                window?.Close();
            }
            catch (InvalidOperationException)
            {
                // 窗口已关，忽略
            }

            if (decoy is not null && IsAlive(decoy))
            {
                try
                {
                    decoy.Kill();
                    decoy.WaitForExit(2000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 杀不掉就留着，不影响结论（它是我们自己在临时目录里造的）
                }
            }

            decoy?.Dispose();

            try
            {
                if (Directory.Exists(decoyDirectory))
                {
                    Directory.Delete(decoyDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // 目录被占用就留着
            }
        }

        var failed = results.Count(r => !r.Pass);
        Write(options, results, acknowledgedBefore);

        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// 复制一份 cmd.exe 成指定名字再跑起来，用来造出"真实运行的同类软件"。
    /// 用系统自带的 cmd.exe 是因为它体积小、必然存在、且不会真的去碰桌面。
    /// </summary>
    private static Process? StartDecoy(out string detail)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "cubby-coexist-probe");
            Directory.CreateDirectory(directory);

            var source = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            if (!File.Exists(source))
            {
                detail = $"找不到 {source}，无法造出同名进程";
                return null;
            }

            var target = Path.Combine(directory, DecoyName + ".exe");
            File.Copy(source, target, overwrite: true);

            var process = Process.Start(new ProcessStartInfo(target, "/c timeout /t 120 /nobreak")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            detail = process is null
                ? "进程启动返回 null"
                : $"{target}（pid {process.Id}）";

            return process;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            detail = $"造同名进程失败：{ex.Message}";
            return null;
        }
    }

    private static bool IsAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void Write(
        SpikeOptions options,
        IReadOnlyList<(string Step, bool Pass, string Detail)> results,
        IReadOnlyList<string> acknowledgedBefore)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 同类软件共存自动化验收报告（issue #36 / A8）");
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
        builder.AppendLine("说明：A8 的关键不是「能不能认出来」，而是**认出来之后什么都不做**——");
        builder.AppendLine("      所以验收造了一个真实的 DeskGo.exe 进程，走完整条提示路径后断言它仍然存活。");
        builder.AppendLine($"      验收前已确认清单：{(acknowledgedBefore.Count == 0 ? "(空)" : string.Join("、", acknowledgedBefore))}，结束后已还原。");
        builder.AppendLine("      未覆盖的部分：真机上与各软件的实际互抢表现需要人工观察，本报告只覆盖 Cubby 自身的行为。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "coexist-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}