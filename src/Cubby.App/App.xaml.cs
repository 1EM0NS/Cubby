using System.IO;
using System.Text;
using System.Windows;
using Cubby.Shell.Desktop;

namespace Cubby.App;

/// <summary>
/// 入口。**无参启动 = 常驻形态**：只有托盘图标，不占任务栏，盒子浮层照常工作。
/// 带参数走诊断与自动化路径：<c>--diagnostics</c> 打开诊断面板、<c>--selftest*</c> 跑各类验收、
/// <c>--dump-*</c> 产出报告后退出。
/// </summary>
public partial class App : Application
{
    private OverlayManager? _manager;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;
    private SnapshotsWindow? _snapshots;
    private RulesWindow? _rules;

    /// <summary>启动时的桌面图标恢复说明（诊断面板会显示）。</summary>
    private string _lastDesktopIconRecovery = "（未检查）";

    /// <summary>供验收读取：启动那次"崩溃兜底恢复"的结果。</summary>
    internal string LastDesktopIconRecovery => _lastDesktopIconRecovery;

    public App()
    {
        // 常驻形态下浮层窗口会被反复重建（显示器变化），默认的 OnLastWindowClose 会在
        // 重建的空档里把整个程序关掉。退出只由托盘菜单或自动化流程显式触发。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 崩溃必须留痕：否则自动化运行时只能看到一个退出码，无从排查
        DispatcherUnhandledException += (_, args) => LogCrash("DispatcherUnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = SpikeOptions.Parse(e.Args);

        if (options.UninstallAutoStart)
        {
            // 给未来的安装包留的卸载钩子（M3）：把自启项摘干净
            StartupRegistration.Disable();
            Shutdown(0);
            return;
        }

        if (options.DumpMonitors)
        {
            var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
            var path = MonitorReport.Write(Path.Combine(directory, "monitors.txt"));
            Console.WriteLine($"显示器报告已写入：{path}");
            Shutdown(0);
            return;
        }

        if (options.DumpDesktopIcons)
        {
            Console.WriteLine($"桌面图标报告已写入：{DesktopIconReport.Write(options)}");
            Shutdown(0);
            return;
        }

        var layoutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cubby",
            "layout.json");

        // 崩溃 / 强杀兜底：上次如果留下"图标还是隐藏的"标记，第一件事就是把它放出来。
        // 这条路径不依赖任何退出代码，因此进程被 taskkill 也能救回来。
        _lastDesktopIconRecovery = DesktopIconController.RecoverIfLeftHidden();

        var layout = new LayoutService(layoutPath);
        var manager = new OverlayManager(layout);
        _manager = manager;
        manager.Start();

        if (options.SelfTest || options.Interact || options.Drop || options.Menu || options.Shell || options.Adopt || options.Snapshot || options.Map || options.Search || options.Rules || options.DesktopIcons)
        {
            var overlay = manager.PrimaryWindow;
            if (overlay is null)
            {
                Console.Error.WriteLine("没有可用的浮层窗口，自动化验收无法进行。");
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
                var exitCode = options.SelfTest
                    ? await SelfTestRunner.RunAsync(overlay, options)
                    : options.Interact
                        ? await InteractionTestRunner.RunAsync(overlay, layout, options)
                        : options.Drop
                            ? await DropTestRunner.RunAsync(overlay, layout, options)
                            : options.Menu
                                ? await ItemMenuTestRunner.RunAsync(overlay, layout, options)
                                : options.Shell
                                    ? await ShellTestRunner.RunAsync(manager, layout, options)
                                    : options.Adopt
                                        ? await AdoptTestRunner.RunAsync(overlay, layout, options)
                                        : options.Snapshot
                                            ? await SnapshotTestRunner.RunAsync(manager, layout, options)
                                            : options.Map
                                                ? await MapTestRunner.RunAsync(overlay, layout, manager, options)
                                                : options.Search
                                                    ? await SearchTestRunner.RunAsync(overlay, layout, manager, options)
                                                    : options.Rules
                                                        ? await RuleTestRunner.RunAsync(overlay, layout, manager, options)
                                                        : await DesktopIconTestRunner.RunAsync(overlay, layout, manager, options);

                Shutdown(exitCode);
            };

            return;
        }

        if (options.DumpState)
        {
            // 等窗口初始化与首帧渲染完成再取样，否则读到的是"还没画出来"的中间态
            Dispatcher.InvokeAsync(
                async () =>
                {
                    await Task.Delay(1500);

                    var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
                    var path = StateReport.Write(manager, layout, Path.Combine(directory, "state.txt"));
                    Console.WriteLine($"状态报告已写入：{path}");
                    Shutdown(0);
                },
                System.Windows.Threading.DispatcherPriority.Background);

            return;
        }

        StartTray(manager, layout);

        if (options.Diagnostics)
        {
            new HudWindow(manager, layout).Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 隐藏桌面图标是系统级副作用：退出时**只还原我们自己藏的那次**
        // （用户自己在系统里关掉桌面图标，我们没资格替他打开）。不依赖退出代码的那条兜底见 OnStartup。
        _lastDesktopIconRecovery = _manager?.DesktopIconToggle.RestoreOnExit() ?? _lastDesktopIconRecovery;

        _tray?.Dispose();
        _tray = null;

        _manager?.Stop();
        _manager = null;

        base.OnExit(e);
    }

    /// <summary>常驻托盘：显示 / 隐藏盒子、显示 / 隐藏桌面图标、开机自启、设置、退出。</summary>
    private void StartTray(OverlayManager manager, LayoutService layout)
    {
        var tray = new TrayIcon();
        _tray = tray;

        tray.SetBoxesChecked(manager.BoxesVisible);
        tray.SetDesktopIconsChecked(DesktopIcons.IsVisible());
        tray.SetAutoStartChecked(StartupRegistration.IsEnabled());

        tray.BoxesVisibilityRequested += (_, visible) => manager.SetBoxesVisible(visible);

        tray.DesktopIconsVisibilityRequested += (_, visible) =>
        {
            // 三个入口（托盘 / 热键 / 盒子按钮）都走同一个控制器，藏了要记标记这件事只写一次
            manager.DesktopIconToggle.SetVisible(visible);
            tray.SetDesktopIconsChecked(manager.DesktopIconToggle.IsVisible);
        };

        tray.AutoStartRequested += (_, enabled) =>
        {
            StartupRegistration.SetEnabled(enabled);
            tray.SetAutoStartChecked(StartupRegistration.IsEnabled());
        };

        tray.SettingsRequested += (_, _) => ShowSettings(manager, layout);
        tray.SnapshotsRequested += (_, _) => ShowSnapshots(manager, layout);
        tray.RulesRequested += (_, _) => ShowRules(manager);
        tray.ExitRequested += (_, _) => Shutdown();
    }

    private void ShowSettings(OverlayManager manager, LayoutService layout)
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }

        var current = manager.Windows.FirstOrDefault()?.CurrentStyle ?? layout.Style;

        _settings = new SettingsWindow(current, manager.ApplyStyle);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    private void ShowSnapshots(OverlayManager manager, LayoutService layout)
    {
        if (_snapshots is { IsLoaded: true })
        {
            _snapshots.Activate();
            return;
        }

        _snapshots = new SnapshotsWindow(layout, manager);
        _snapshots.Closed += (_, _) => _snapshots = null;
        _snapshots.Show();
    }

    private void ShowRules(OverlayManager manager)
    {
        if (_rules is { IsLoaded: true })
        {
            _rules.Activate();
            return;
        }

        _rules = new RulesWindow(manager.Rules);
        _rules.Closed += (_, _) => _rules = null;
        _rules.Show();
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
internal sealed record SpikeOptions(
    bool SelfTest,
    string? OutputDirectory,
    bool DumpMonitors,
    bool DumpState,
    bool Interact,
    bool Drop = false,
    bool Menu = false,
    bool Shell = false,
    bool Diagnostics = false,
    bool UninstallAutoStart = false,
    bool DumpDesktopIcons = false,
    bool Adopt = false,
    bool Snapshot = false,
    bool Map = false,
    bool Search = false,
    bool Rules = false,
    bool DesktopIcons = false)
{
    public static SpikeOptions Parse(string[] args) => new(
        SelfTest: Has(args, "--selftest"),
        OutputDirectory: ValueOf(args, "--out"),
        DumpMonitors: Has(args, "--dump-monitors"),
        DumpState: Has(args, "--dump-state"),
        Interact: Has(args, "--selftest-interact"),
        Drop: Has(args, "--selftest-drop"),
        Menu: Has(args, "--selftest-menu"),
        Shell: Has(args, "--selftest-shell"),
        Diagnostics: Has(args, "--diagnostics"),
        UninstallAutoStart: Has(args, "--uninstall-autostart"),
        DumpDesktopIcons: Has(args, "--dump-desktop-icons"),
        Adopt: Has(args, "--selftest-adopt"),
        Snapshot: Has(args, "--selftest-snapshot"),
        Map: Has(args, "--selftest-map"),
        Search: Has(args, "--selftest-search"),
        Rules: Has(args, "--selftest-rules"),
        DesktopIcons: Has(args, "--selftest-desktop-icons"));

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