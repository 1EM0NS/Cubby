using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace HookProbe;

/// <summary>
/// 鼠标消息钩子链探针，用于验收 P1 / A1 / A3：证明本程序没有吞掉鼠标消息。
///
/// 原理：把自己挂在低级别鼠标钩子链的**末端**，注入 N 次左键，数自己收到多少条按下类消息。
/// 链上游（例如动态壁纸软件）吞掉的消息，我们就收不到。
///
/// 为什么必须做基线对比：本机装了 Wallpaper Engine 这类会挂鼠标钩子的软件，
/// 它会吞掉一部分点击，因此「注入了 10 次却只收到 6 次」并不等价于「被测程序吞了消息」。
/// 唯一有意义的判据是——**被测程序运行时的观测结果，不差于它未运行时的基线**。
///
/// 用法：
///   HookProbe --out baseline.json        采集基线（此时被测程序不要运行）
///   HookProbe --baseline baseline.json   与被测程序运行中的结果对比并判定
///   HookProbe --clicks 10                注入次数（默认 10）
///   HookProbe --at 1200,800              指定注入坐标（默认自动找桌面空白处）
///
/// 退出码：0 通过或仅采集；1 判定失败；2 环境不满足。
/// </summary>
internal static class Program
{
    /// <summary>环境噪声容差：允许被测状态下比基线少这么多条，超出才判定为「被测程序吞了消息」。</summary>
    private const int NoiseTolerance = 1;

    private static int _moveCount;
    private static int _downCount;
    private static int _dblClkCount;
    private static int _upCount;
    private static int _injectedDownCount;
    private static nint _hook;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var clicks = IntOf(args, "--clicks") ?? 10;
        var settleMs = IntOf(args, "--settle") ?? 800;
        var outFile = ValueOf(args, "--out");
        var baselineFile = ValueOf(args, "--baseline");

        var surface = MonitorSurfaces.Primary();
        var target = PointOf(args, "--at") ?? FindDesktopPoint(surface);

        if (target is null)
        {
            Console.Error.WriteLine("环境不满足：找不到落在桌面层上的安全注入点。");
            Console.Error.WriteLine("请先按 Win+D 显示桌面，或关掉遮挡屏幕的窗口后重试。");
            Console.Error.WriteLine($"（当前桌面区域：{surface.Bounds}）");
            return 2;
        }

        var (x, y) = target.Value;
        var probe = DesktopProbe.WindowAt(x, y, 0);
        Console.WriteLine($"注入点：({x}, {y})，命中窗口：class={probe.ClassName} pid={probe.ProcessId}");

        if (!MouseClicker.TryGetCursorPosition(out var originalX, out var originalY))
        {
            originalX = 0;
            originalY = 0;
        }

        // guard-exempt 见 NativeHookMethods.cs：把观测钩子挂到链的末端
        _hook = NativeHookMethods.SetWindowsHookEx(
            NativeHookMethods.WhMouseLl,
            OnHook,
            NativeHookMethods.GetModuleHandle(null),
            0);

        if (_hook == 0)
        {
            Console.Error.WriteLine($"安装观测钩子失败，GetLastError={NativeHookMethods.GetLastError()}");
            return 2;
        }

        ProbeSample sample;
        try
        {
            Console.WriteLine($"观测钩子已挂载，等待 {settleMs} ms 生效……");
            PumpFor(settleMs);

            Console.WriteLine($"开始注入 {clicks} 次左键……");
            for (var i = 0; i < clicks; i++)
            {
                // 先产生一次真实移动事件：既证明钩子活着，也避免连点被系统识别成双击
                var offset = i % 2 == 0 ? -4 : 4;
                MouseClicker.Nudge(offset, 0);
                PumpFor(50);

                MouseClicker.LeftClick();
                PumpFor(150);
            }

            PumpFor(300);

            sample = Snapshot(clicks);
        }
        finally
        {
            NativeHookMethods.UnhookWindowsHookEx(_hook);
            MouseClicker.MoveTo(originalX, originalY);
        }

        PrintSample(sample);

        if (outFile is not null)
        {
            File.WriteAllText(outFile, JsonSerializer.Serialize(sample, JsonOptions), new UTF8Encoding(false));
            Console.WriteLine($"已写出基线：{outFile}");
            Console.WriteLine("（本模式只采集，不做判定）");
            return 0;
        }

        if (baselineFile is null)
        {
            Console.WriteLine("未提供 --baseline，本次只报告、不做判定。");
            Console.WriteLine("要做判定：先在被测程序未运行时 --out 基线，再在被测程序运行时 --baseline 对比。");
            return 0;
        }

        return Compare(sample, baselineFile);
    }

    private static int Compare(ProbeSample current, string baselineFile)
    {
        ProbeSample? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<ProbeSample>(File.ReadAllText(baselineFile), JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读取基线失败：{ex.Message}");
            return 2;
        }

        if (baseline is null)
        {
            Console.Error.WriteLine("基线文件内容为空。");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("== 与基线对比 ==");
        Console.WriteLine($"  项               基线           被测运行中");
        Console.WriteLine($"  注入次数         {baseline.Injected,-14} {current.Injected}");
        Console.WriteLine($"  按下类消息       {baseline.DownEvents,-14} {current.DownEvents}");
        Console.WriteLine($"  抬起消息         {baseline.Up,-14} {current.Up}");
        Console.WriteLine($"  移动消息         {baseline.Move,-14} {current.Move}");
        Console.WriteLine();

        if (current.Move == 0 && current.DownEvents == 0)
        {
            Console.Error.WriteLine("FAIL：什么都没观测到，说明探针钩子没生效，本次结论无效。");
            return 1;
        }

        var downDelta = current.DownEvents - baseline.DownEvents;
        var upDelta = current.Up - baseline.Up;

        if (downDelta < -NoiseTolerance || upDelta < -NoiseTolerance)
        {
            Console.Error.WriteLine(
                $"FAIL：被测状态下比基线少收到消息（按下类 {downDelta}，抬起 {upDelta}，容差 {NoiseTolerance}），" +
                "说明有被测程序在钩子链上吞消息。");
            return 1;
        }

        Console.WriteLine(
            $"PASS：被测状态下的观测结果不差于基线（按下类 {downDelta:+#;-#;0}，抬起 {upDelta:+#;-#;0}，" +
            $"容差 {NoiseTolerance}）。被测程序没有让任何鼠标消息消失，P1 成立。");
        return 0;
    }

    private static ProbeSample Snapshot(int injected) => new(
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        injected,
        _downCount,
        _dblClkCount,
        _upCount,
        _moveCount,
        _injectedDownCount);

    private static void PrintSample(ProbeSample sample)
    {
        Console.WriteLine();
        Console.WriteLine("== 钩子链观测结果 ==");
        Console.WriteLine($"  注入左键次数      : {sample.Injected}");
        Console.WriteLine($"  观测到普通按下    : {sample.Down}（其中带注入标记 {sample.InjectedFlagged}）");
        Console.WriteLine($"  观测到双击按下    : {sample.DblClk}");
        Console.WriteLine($"  观测到抬起        : {sample.Up}");
        Console.WriteLine($"  观测到鼠标移动    : {sample.Move}");
        Console.WriteLine($"  按下类消息合计    : {sample.DownEvents}");
    }

    private static nint OnHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            switch ((int)wParam)
            {
                case NativeHookMethods.WmMouseMove:
                    _moveCount++;
                    break;

                case NativeHookMethods.WmLeftButtonDown:
                    _downCount++;
                    if (IsInjected(lParam))
                    {
                        _injectedDownCount++;
                    }

                    break;

                case NativeHookMethods.WmLeftButtonDblClk:
                    _dblClkCount++;
                    break;

                case NativeHookMethods.WmLeftButtonUp:
                    _upCount++;
                    break;
            }
        }

        // 只观测、不拦截：P1 要求钩子函数无条件放行，绝不返回非零值
        return NativeHookMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsInjected(nint lParam)
    {
        if (lParam == 0)
        {
            return false;
        }

        var data = Marshal.PtrToStructure<NativeHookMethods.MsllHookStruct>(lParam);
        return (data.Flags & NativeHookMethods.LlkhfInjected) != 0;
    }

    /// <summary>低级别钩子的回调靠消息泵驱动，所以等待期间必须抽空消息队列。</summary>
    private static void PumpFor(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            while (NativeHookMethods.PeekMessage(out var msg, 0, 0, 0, NativeHookMethods.PmRemove))
            {
                NativeHookMethods.TranslateMessage(ref msg);
                NativeHookMethods.DispatchMessage(ref msg);
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>找一个落在桌面层上的点，避免把点击注入到用户的应用程序里。</summary>
    private static (int X, int Y)? FindDesktopPoint(MonitorSurface surface)
    {
        var width = surface.Bounds.Width;
        var height = surface.Bounds.Height;
        var left = surface.Bounds.Left;
        var top = surface.Bounds.Top;

        var candidates = new (double Fx, double Fy)[]
        {
            (0.86, 0.88),
            (0.78, 0.92),
            (0.86, 0.55),
            (0.62, 0.88),
            (0.50, 0.94),
        };

        foreach (var (fx, fy) in candidates)
        {
            var x = left + (int)(width * fx);
            var y = top + (int)(height * fy);
            if (DesktopProbe.WindowAt(x, y, 0).IsDesktopLayer)
            {
                return (x, y);
            }
        }

        return null;
    }

    private static int? IntOf(string[] args, string name)
    {
        var raw = ValueOf(args, name);
        return raw is not null && int.TryParse(raw, out var value) ? value : null;
    }

    private static (int X, int Y)? PointOf(string[] args, string name)
    {
        var raw = ValueOf(args, name);
        if (raw is null)
        {
            return null;
        }

        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y)
            ? (x, y)
            : null;
    }

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

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

/// <summary>一次探针采样的结果。</summary>
internal sealed record ProbeSample(
    string Timestamp,
    int Injected,
    int Down,
    int DblClk,
    int Up,
    int Move,
    int InjectedFlagged)
{
    /// <summary>按下类消息 = 普通按下 + 双击按下（系统会把连点的第二次按下变成后者）。</summary>
    public int DownEvents => Down + DblClk;
}