using System.IO;

namespace Cubby.Core.Platform;

/// <summary>
/// 记录"桌面图标是我们隐藏的"这个事实。
///
/// 为什么需要落一个标记文件：把图标藏起来之后，程序**被强杀 / 崩溃**时没有任何机会执行还原代码。
/// 唯一能保证"下次一定恢复"的办法，就是把"我藏过"这件事先记在磁盘上，
/// 下次启动第一件事就是读它——有标记就立刻恢复显示，然后清掉标记。
/// 这是本项目对"退出或崩溃后桌面图标必定恢复"这条验收标准的实现方式。
/// </summary>
public static class DesktopIconStateMarker
{
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cubby",
        "desktop-icons-hidden.flag");

    /// <summary>隐藏前记一笔。</summary>
    public static void MarkHidden(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath();
            var directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(target, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去也不该影响隐藏本身；只是下次崩溃后的自动恢复会失效
        }
    }

    /// <summary>恢复显示后清掉标记。</summary>
    public static void Clear(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath();
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 同上
        }
    }

    /// <summary>上次退出时是否留下了"图标还是隐藏的"状态。</summary>
    public static bool WasLeftHidden(string? path = null)
    {
        try
        {
            return File.Exists(path ?? DefaultPath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}