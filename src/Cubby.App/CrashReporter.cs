using System.IO;
using System.Windows;
using Cubby.Core.Diagnostics;

namespace Cubby.App;

/// <summary>提示方式。<see cref="Modal"/> 只用于致命异常（进程随后就没了，值得拦住让用户看一眼）。</summary>
internal enum CrashPromptMode
{
    None,
    NonModal,
    Modal,
}

/// <summary>一次崩溃处理的结果，供诊断面板与自动化验收核对。</summary>
internal sealed record CrashReport(
    string Source,
    string? LogPath,
    string Summary,
    IReadOnlyList<string> Steps,
    CrashWindow? Window);

/// <summary>
/// 全局异常兜底（issue #39）。
///
/// 三处入口都汇到同一个 <see cref="Handle"/>，因为**顺序本身是需求的一部分**：
///
/// 1. **先还原桌面图标标记**——这是我们留在用户机器上的副作用，优先级高于一切。
///    日志写不写得上都无所谓，图标必须回来：写日志失败顶多丢证据，图标回不来是用户看得见的问题。
/// 2. **再写日志**——落盘、轮转、保留。
/// 3. **最后才提示用户**——给日志路径、能复制、能打开目录。绝不静默退出。
///
/// 三处的处理策略刻意不同，理由写在 <see cref="Attach"/> 里。
/// </summary>
internal static class CrashReporter
{
    private static readonly List<string> Steps = [];

    /// <summary>最近一次处理的步骤序列（按发生顺序）。自动化验收靠它证明"还原排在写日志之前"。</summary>
    internal static IReadOnlyList<string> LastSteps => Steps;

    /// <summary>最近一次崩溃提示窗（没弹则为 null）。</summary>
    internal static CrashWindow? LastWindow { get; private set; }

    /// <summary>日志目录。<c>%AppData%\Cubby\logs</c>。</summary>
    internal static string LogDirectory => CrashLog.DefaultDirectory();

    /// <summary>
    /// 挂上三处全局异常。
    ///
    /// - <c>DispatcherUnhandledException</c>：UI 线程上的异常。**拦下来不让程序死**（Handled=true）——
    ///   一个盒子画崩了不该连累整个常驻程序，用户还有托盘可以退出。
    /// - <c>AppDomain.UnhandledException</c>：非 UI 线程的致命异常，进程随后就会结束。
    ///   这里能做的是"留证据 + 让用户看到"，因此用模态提示拦住一小会儿。
    /// - <c>TaskScheduler.UnobservedTaskException</c>：**只记日志、不弹窗**。
    ///   它不是一个崩溃（.NET Core 默认不会因此结束进程），每次都弹窗会变成骚扰；
    ///   记下来是为了排查"某个后台任务一直在悄悄失败"这类问题。
    /// </summary>
    internal static void Attach(Application app)
    {
        app.DispatcherUnhandledException += (_, args) =>
        {
            var report = Handle(
                "DispatcherUnhandledException",
                args.Exception,
                CrashPromptMode.NonModal);

            // UI 线程的异常已经被记下、也已经告诉用户了；不让它把常驻程序带走
            args.Handled = true;

            if (report.LogPath is null)
            {
                Console.Error.WriteLine("崩溃日志写入失败（异常仍已被拦截）。");
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Handle(
                "AppDomain.UnhandledException",
                args.ExceptionObject as Exception,
                CrashPromptMode.Modal,
                terminating: true);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Handle(
                "TaskScheduler.UnobservedTaskException",
                args.Exception,
                CrashPromptMode.None);

            // 标记为已观察：避免同一个异常被反复上报，也避免 .NET 的"未观察异常"升级策略
            args.SetObserved();
        };
    }

    /// <summary>
    /// 一次崩溃处理的完整路径。**所有入口都走这里**，包括自动化验收注入的假异常，
    /// 因此验收证明的是"用户真会走的那条路"。
    /// </summary>
    internal static CrashReport Handle(
        string source,
        Exception? exception,
        CrashPromptMode prompt = CrashPromptMode.NonModal,
        bool terminating = false)
    {
        Steps.Clear();

        // ---- 1. 桌面图标还原（优先级最高：这是我们留在用户机器上的副作用）----
        var recovery = DesktopIconController.RecoverIfLeftHidden();
        Steps.Add($"桌面图标还原：{recovery}");

        // ---- 2. 写日志 ----
        var summary = Describe(exception);
        var logPath = CrashLog.Write(
            LogDirectory,
            terminating ? CrashLogLevel.Fatal : CrashLogLevel.Error,
            source,
            summary,
            exception?.ToString());

        Steps.Add(logPath is null ? "写日志：失败（已忽略，不影响上面的还原）" : $"写日志：{logPath}");

        // ---- 3. 提示用户（绝不静默退出）----
        var window = ShowPrompt(source, prompt, summary, logPath);
        Steps.Add(window is null ? $"提示用户：跳过（{prompt}）" : $"提示用户：已弹出（{prompt}）");

        return new CrashReport(source, logPath, summary, Steps.ToList(), window);
    }

    /// <summary>异常的一句话摘要：类型 + 消息（消息里可能含换行，压成一行便于进界面）。</summary>
    internal static string Describe(Exception? exception) =>
        exception is null
            ? "（没有异常对象，可能是非托管异常或异常对象不是 Exception）"
            : $"{exception.GetType().FullName}: {exception.Message.ReplaceLineEndings(" ")}";

    private static CrashWindow? ShowPrompt(
        string source,
        CrashPromptMode mode,
        string summary,
        string? logPath)
    {
        if (mode == CrashPromptMode.None)
        {
            return null;
        }

        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        try
        {
            // 界面必须在 UI 线程上创建；致命异常可能来自任意线程，所以统一派发过去
            return application.Dispatcher.Invoke(() =>
            {
                var headline = mode == CrashPromptMode.Modal
                    ? "Cubby 遇到致命异常，即将退出"
                    : "Cubby 遇到了一个未处理的异常";

                var window = new CrashWindow(source, headline, summary, logPath);
                LastWindow = window;

                if (mode == CrashPromptMode.Modal)
                {
                    // 进程马上就要没了，模态拦住让用户有机会看到日志在哪
                    window.ShowDialog();
                }
                else
                {
                    window.Show();
                }

                return window;
            });
        }
        catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException or TimeoutException)
        {
            // 关停中 / 消息泵已经停了：日志已经写下了，提示弹不出来是次要的
            return null;
        }
    }
}
