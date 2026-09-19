using System.IO;
using System.Text;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;

namespace Cubby.App;

/// <summary>
/// 运行状态诊断报告。用于回答那种"看界面看不出来、只看退出码也看不出来"的问题：
/// 例如盒子到底渲染出来了没有、命中区域算得对不对、Win32 层与 WPF 层的鼠标计数是否一致。
/// </summary>
internal static class StateReport
{
    public static string Build(OverlayManager manager, LayoutService layout)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine("== 布局 ==");
        builder.AppendLine($"文件     : {layout.FilePath}");
        builder.AppendLine($"盒子总数 : {layout.Boxes.Count}");
        builder.AppendLine($"保存次数 : {layout.SaveCount}  最近保存：{layout.LastSaveAt ?? "(尚未保存)"}");
        if (layout.LoadDiagnostic is { } diagnostic)
        {
            builder.AppendLine($"载入诊断 : {diagnostic}");
        }

        if (manager.LastAdoptSummary is { } adopt)
        {
            builder.AppendLine($"吸附结果 : {adopt}");
        }

        if (manager.MappingWatcherCount > 0)
        {
            builder.AppendLine($"映射监听 : {manager.MappingWatcherCount} 个；累计重扫 {manager.MappingRescanCount} 次（溢出重建 {manager.MappingOverflowCount} 次）");
        }

        builder.AppendLine();
        builder.AppendLine("== 显示器 ==");
        builder.AppendLine(manager.DescribeMonitors());

        builder.AppendLine();
        builder.AppendLine("== 浮层窗口 ==");
        foreach (var window in manager.Windows)
        {
            var handle = window.Host?.Handle ?? 0;
            builder.AppendLine($"  窗口 0x{handle.ToInt64():X8}  显示器={window.Surface?.Id}");
            builder.AppendLine($"    模型盒子数     : {window.Boxes.Count}");
            builder.AppendLine($"    实际视图数     : {window.BoxVisualCount}");
            builder.AppendLine($"    命中区域       : {window.DescribeRegions()}");
            builder.AppendLine($"    置底触发次数   : {window.Host?.EnsureBehindCount}");
            builder.AppendLine($"    Win32 收到左键 : {window.OverlayClickCount}");
            builder.AppendLine($"    WPF 收到左键   : {window.WpfMouseDownCount}");
        }

        if (manager.Windows.Count == 0)
        {
            builder.AppendLine("  （没有任何浮层窗口）");
        }

        builder.AppendLine();
        builder.AppendLine("== 盒子明细 ==");
        foreach (var box in layout.Boxes)
        {
            builder.AppendLine(
                $"  {box.Name,-12} 显示器={box.MonitorId ?? "(未记录)"}  " +
                $"({box.Bounds.X:0},{box.Bounds.Y:0}) {box.Bounds.Width:0}×{box.Bounds.Height:0}  " +
                $"锁定={box.IsLocked} 折叠={box.IsCollapsed} 条目={box.Items.Count}");
        }

        builder.AppendLine();
        builder.AppendLine("== 常驻资源（A7：挂机后应无增长）==");
        builder.AppendLine(DescribeResources());

        builder.AppendLine();
        builder.AppendLine("判读提示：若「实际视图数」为 0 而「模型盒子数」大于 0，说明盒子没有渲染出来；");
        builder.AppendLine("         若「Win32 收到左键」大于 0 而「WPF 收到左键」等于 0，说明 WPF 层没有路由到元素；");
        builder.AppendLine("         间隔数小时各跑一次 --dump-state，比对上面这五个数字即可判断有没有泄漏。");

        return builder.ToString();
    }

    /// <summary>
    /// 常驻稳定性（A7）需要看的就是这四个数：工作集、私有内存、句柄数、GDI / USER 对象数。
    /// 后两者最容易泄漏，且任务管理器默认看不到，所以单独列出来。
    /// </summary>
    private static string DescribeResources()
    {
        try
        {
            var sample = ResourceProbe.Sample();

            return $"  工作集       : {sample.WorkingSetMb:0.0} MB{Environment.NewLine}" +
                   $"  私有内存     : {sample.PrivateMb:0.0} MB{Environment.NewLine}" +
                   $"  句柄数       : {sample.Handles}{Environment.NewLine}" +
                   $"  GDI 对象     : {sample.GdiObjects}{Environment.NewLine}" +
                   $"  USER 对象    : {sample.UserObjects}{Environment.NewLine}" +
                   $"  运行时长     : {sample.UptimeMinutes:0.0} 分钟";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"  （读取失败：{ex.Message}）";
        }
    }

    public static string Write(OverlayManager manager, LayoutService layout, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Build(manager, layout), new UTF8Encoding(false));
        return path;
    }
}