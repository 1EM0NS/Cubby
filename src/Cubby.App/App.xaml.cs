using System.Windows;

namespace Cubby.App;

/// <summary>
/// 入口。无参启动进入交互式 spike（浮层 + 诊断面板），
/// 带 <c>--selftest</c> 则自动跑命中测试、写报告后退出。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = SpikeOptions.Parse(e.Args);
        var overlay = new SpikeWindow();
        overlay.Show();

        if (options.SelfTest)
        {
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

        new HudWindow(overlay).Show();
    }
}

/// <summary>命令行参数。</summary>
internal sealed record SpikeOptions(bool SelfTest, string? OutputDirectory)
{
    public static SpikeOptions Parse(string[] args) => new(
        SelfTest: Has(args, "--selftest"),
        OutputDirectory: ValueOf(args, "--out"));

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