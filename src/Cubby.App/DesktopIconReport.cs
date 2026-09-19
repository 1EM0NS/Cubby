using System.IO;
using System.Text;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Core.Platform;
using Cubby.Shell.Desktop;

namespace Cubby.App;

/// <summary>
/// 桌面图标读取的诊断报告（<c>--dump-desktop-icons</c>）。
/// 回答的是"到底读到了什么"：图标层句柄、图标总数、每个图标的屏幕坐标、
/// 以及它们与桌面目录里真实文件的匹配情况——吸附不准时先看这份报告。
/// </summary>
internal static class DesktopIconReport
{
    public static string Write(SpikeOptions options)
    {
        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "desktop-icons.txt");
        File.WriteAllText(path, Build(), new UTF8Encoding(false));

        return path;
    }

    private static string Build()
    {
        var builder = new StringBuilder();
        var handle = DesktopIcons.ListViewHandle();
        var icons = DesktopIcons.Read();
        var candidates = DesktopFolders.EnumerateEntries();

        builder.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine("== 桌面图标层 ==");
        builder.AppendLine($"SysListView32 句柄 : {(handle == 0 ? "（未找到）" : $"0x{handle.ToInt64():X8}")}");
        builder.AppendLine($"图标总数           : {icons.Count}");
        builder.AppendLine($"图标层可见         : {DesktopIcons.IsVisible()}");
        builder.AppendLine();

        builder.AppendLine("== 桌面目录 ==");
        foreach (var directory in DesktopFolders.Directories())
        {
            builder.AppendLine($"  {directory}");
        }

        builder.AppendLine($"真实条目数         : {candidates.Count}");
        builder.AppendLine();

        builder.AppendLine("== 图标明细（屏幕坐标，只读）==");
        builder.AppendLine("| 序号 | 显示名 | X | Y | 匹配到的文件 |");
        builder.AppendLine("|---|---|---|---|---|");

        for (var i = 0; i < icons.Count; i++)
        {
            var icon = icons[i];
            var match = DesktopAdoption.Match(icon.Name, candidates);
            builder.AppendLine($"| {i + 1} | {icon.Name} | {icon.X} | {icon.Y} | {match ?? "（未匹配，多为虚拟图标）"} |");
        }

        builder.AppendLine();
        builder.AppendLine("说明：读取只发 LVM_GETITEMCOUNT / LVM_GETITEMPOSITION / LVM_GETITEMTEXTW 三条取消息，");
        builder.AppendLine("      位置坐标仅用于判断图标落在哪个盒子范围内，绝不回写（P4）。");

        return builder.ToString();
    }
}