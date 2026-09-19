using System.IO;
using System.Text;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 显示器与分配计划的诊断报告。写成文件而不是只打印：
/// 本程序是 WinExe，没有控制台，写文件才是可靠的取证方式。
/// </summary>
internal static class MonitorReport
{
    public static string Build()
    {
        var monitors = MonitorSurfaces.Enumerate();
        var builder = new StringBuilder();

        builder.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"显示器数量：{monitors.Count}");
        if (MonitorSurfaces.LastEnumerationError is { } error)
        {
            builder.AppendLine($"枚举错误：{error}");
        }
        foreach (var monitor in monitors)
        {
            builder.AppendLine(
                $"  {monitor.Id,-16} {monitor.Bounds}  像素 {monitor.Bounds.Width}×{monitor.Bounds.Height}  " +
                $"DPI 缩放 {monitor.DpiScale:0.##}{(monitor.IsPrimary ? "  [主屏]" : string.Empty)}");
        }

        if (monitors.Count == 0)
        {
            return builder.ToString();
        }

        // 用合成盒子演示分配计划：一条绑在真实显示器上、一条绑在已拔掉的屏上、一条从未记录过显示器
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        var boxes = new List<Box>
        {
            new("demo-real", "绑定主屏的盒子", new DipRect(10, 10, 100, 100))
            {
                MonitorId = primary.Id,
                MonitorWidth = primary.Bounds.Width,
                MonitorHeight = primary.Bounds.Height,
            },
            new("demo-gone", "绑定已拔掉的屏", new DipRect(10, 10, 100, 100))
            {
                MonitorId = @"\\.\DISPLAY99",
                MonitorWidth = 3840,
                MonitorHeight = 2160,
            },
            new("demo-none", "从未记录过显示器", new DipRect(10, 10, 100, 100)),
        };

        var plans = OverlayPlanner.Plan(monitors, boxes);
        builder.AppendLine();
        builder.AppendLine("分配计划（OverlayPlanner.Plan）：");
        foreach (var plan in plans)
        {
            builder.AppendLine($"  {plan.Monitor.Id}（{plan.Boxes.Count} 个盒子{(plan.HasFallback ? "，含回退" : string.Empty)}）");
            foreach (var planned in plan.Boxes)
            {
                builder.AppendLine($"      {planned.Box.Name,-18} 匹配方式 = {planned.Kind}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("说明：'回退' 表示该盒子原来绑定的显示器当前不存在，盒子被安排到主屏而不是被丢弃。");

        return builder.ToString();
    }

    public static string Write(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Build(), new UTF8Encoding(false));
        return path;
    }
}