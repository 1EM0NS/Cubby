using System.IO;
using System.Text;
using System.Windows;

namespace Cubby.App;

/// <summary>
/// 入口。无参启动进入交互式诊断面板；带参数走自动化路径：
/// <c>--selftest</c> 跑命中测试、<c>--dump-monitors</c> 输出显示器与分配计划。
/// </summary>
public partial class App : Application
{
    public App()
    {
        // 崩溃必须留痕：否则自动化运行时只能看到一个退出码，无从排查
        DispatcherUnhandledException += (_, args) => LogCrash("DispatcherUnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = SpikeOptions.Parse(e.Args);

        if (options.DumpMonitors)
        {
            var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
            var path = MonitorReport.Write(Path.Combine(directory, "monitors.txt"));
            Console.WriteLine($"显示器报告已写入：{path}");
            Shutdown(0);
            return;
        }

        var manager = new OverlayManager();
        manager.Start();

        if (options.SelfTest)
        {
            var overlay = manager.PrimaryWindow;
            if (overlay is null)
            {
                Console.Error.WriteLine("没有可用的浮层窗口，自测无法进行。");
                Shutdown(2);
                return;
            }

            var started = false;
            overlay.ContentRendered += async (_, _) =>
            {
                if (started)
                {
                    return;
                }

                started = true;
                var exitCode = await SelfTestRunner.RunAsync(overlay, options);
                Shutdown(exitCode);
            };

            return;
        }

        new HudWindow(manager).Show();
    }

    /// <summary>把未处理异常写到 artifacts/crash.log（M3 做正式崩溃日志时会统一搬到 %AppData%）。</summary>
    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // 连崩溃日志都写不进去时不再做别的事，避免递归
        }
    }
}

/// <summary>命令行参数。</summary>
internal sealed record SpikeOptions(bool SelfTest, string? OutputDirectory, bool DumpMonitors)
{
    public static SpikeOptions Parse(string[] args) => new(
        SelfTest: Has(args, "--selftest"),
        OutputDirectory: ValueOf(args, "--out"),
        DumpMonitors: Has(args, "--dump-monitors"));

    private static bool Has(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? ValueOf(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}