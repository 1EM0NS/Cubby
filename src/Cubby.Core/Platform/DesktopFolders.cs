using System.IO;

namespace Cubby.Core.Platform;

/// <summary>
/// 桌面图标背后的真实文件在哪儿：当前用户桌面 + 公共桌面。
///
/// 桌面上的「此电脑」「回收站」是虚拟图标，不在这两个目录里——它们匹配不上属于正常现象，
/// 由吸附逻辑记为"未匹配"并跳过。
/// </summary>
public static class DesktopFolders
{
    public static IReadOnlyList<string> Directories()
    {
        var directories = new List<string>();

        foreach (var kind in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var path = Environment.GetFolderPath(kind);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                directories.Add(path);
            }
        }

        return directories;
    }

    /// <summary>枚举两个桌面目录下的顶层文件与文件夹。任何一个目录读不到都只是跳过，绝不抛。</summary>
    public static IReadOnlyList<string> EnumerateEntries()
    {
        var entries = new List<string>();

        foreach (var directory in Directories())
        {
            try
            {
                entries.AddRange(Directory.GetFileSystemEntries(directory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 权限或瞬时故障：这个目录跳过，其余的照常
            }
        }

        return entries;
    }
}