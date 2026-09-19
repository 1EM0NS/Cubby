using System.Text;

namespace Cubby.Core.Diagnostics;

/// <summary>日志级别。崩溃用 <see cref="Fatal"/>，其余留给将来的正常路径（本阶段只写异常）。</summary>
public enum CrashLogLevel
{
    Info,
    Warning,
    Error,
    Fatal,
}

/// <summary>
/// 崩溃 / 异常日志的落盘、轮转与保留（issue #39）。
///
/// 刻意放在 Core 且**全部路径由调用方给出**，是为了让轮转与保留策略能被单元测试直接怼
/// （造 20 个假日期文件、断言删剩几个），而不是只能靠人去翻 <c>%AppData%</c>。
///
/// 三条硬要求：
/// 1. **绝不让日志问题升级成崩溃**：本类型所有公开方法都不抛异常，写不进去就安静放弃；
/// 2. **按天分文件** <c>cubby-yyyyMMdd.log</c>，文件头写一次「版本 / OS / 运行时」三要素
///    ——排查时最先要问的就是这三个，事后很难补；
/// 3. **保留策略带上限**：默认保留最近 14 天，且总量不超过 8MB，不与用户抢磁盘。
/// </summary>
public static class CrashLog
{
    /// <summary>保留最近多少天（含当天）。</summary>
    public const int DefaultRetentionDays = 14;

    /// <summary>总量上限。日志只用来排查，超出这个量说明出了别的问题。</summary>
    public const long DefaultMaxTotalBytes = 8L * 1024 * 1024;

    private const string FilePrefix = "cubby-";
    private const string FileSuffix = ".log";

    /// <summary>日志目录：<c>%AppData%\Cubby\logs</c>，与布局、快照同处一棵树，卸载时好一起清。</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cubby",
        "logs");

    /// <summary>某一天对应的文件名。用文件名承载日期，轮转就不需要看文件内容。</summary>
    public static string FileNameFor(DateTime date) => $"{FilePrefix}{date:yyyyMMdd}{FileSuffix}";

    public static string FilePathFor(string directory, DateTime date) =>
        Path.Combine(directory, FileNameFor(date));

    /// <summary>
    /// 文件头。**每个日志文件第一行写一次**，记录排查必需的三要素。
    /// 参数留了默认值，是为了让单元测试能塞进确定的字符串。
    /// </summary>
    public static string BuildHeader(DateTime createdAt, string? version = null, string? os = null, string? runtime = null) =>
        $"# Cubby 日志 | 文件={FileNameFor(createdAt)} | 版本={version ?? CurrentVersion()} | " +
        $"OS={os ?? CurrentOs()} | 运行时={runtime ?? CurrentRuntime()} | 建文件于={createdAt:yyyy-MM-dd HH:mm:ss}";

    public static string CurrentVersion() =>
        typeof(CrashLog).Assembly.GetName().Version?.ToString() ?? "未知";

    public static string CurrentOs() =>
        $"{Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})";

    public static string CurrentRuntime() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";

    /// <summary>把一条记录格式化成日志文本（含时间戳、级别、来源、异常类型、完整堆栈）。</summary>
    public static string Format(
        DateTime timestamp,
        CrashLogLevel level,
        string source,
        string message,
        string? stack = null)
    {
        var builder = new StringBuilder();
        builder.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        builder.Append(" | ").Append(level.ToString().ToUpperInvariant().PadRight(5));
        builder.Append(" | ").Append(source);
        builder.Append(" | ").Append(message.ReplaceLineEndings(" "));

        if (!string.IsNullOrWhiteSpace(stack))
        {
            // 堆栈保持多行（完整堆栈是排查的关键），每行缩进，便于人眼扫
            foreach (var line in stack.ReplaceLineEndings("\n").Split('\n'))
            {
                builder.AppendLine();
                builder.Append("    ").Append(line);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 写一条日志。返回写入的文件路径（失败时返回 null）。
    /// **本方法绝不抛异常**——日志本身出问题不该升级成崩溃问题。
    /// </summary>
    public static string? Write(
        string directory,
        CrashLogLevel level,
        string source,
        string message,
        string? stack = null,
        DateTime? timestamp = null,
        string? version = null,
        string? os = null,
        string? runtime = null,
        int retentionDays = DefaultRetentionDays,
        long maxTotalBytes = DefaultMaxTotalBytes)
    {
        try
        {
            var now = timestamp ?? DateTime.Now;
            Directory.CreateDirectory(directory);

            var path = FilePathFor(directory, now);
            var payload = new StringBuilder();

            // 文件是新的时候先写头：头只出现一次，重复追加不会刷屏
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                payload.AppendLine(BuildHeader(now, version, os, runtime));
            }

            payload.AppendLine(Format(now, level, source, message, stack));

            File.AppendAllText(path, payload.ToString(), new UTF8Encoding(false));

            Prune(directory, retentionDays, maxTotalBytes);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 写不进去就算了：绝不让日志路径把程序带崩
            return null;
        }
    }

    /// <summary>某目录下已有的日志文件，按文件名升序（= 按日期升序）。</summary>
    public static IReadOnlyList<string> ListFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            return Directory
                .EnumerateFiles(directory, $"{FilePrefix}*{FileSuffix}")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 轮转与保留：先删超过保留天数的，再在总量超上限时从最老的开始删。
    /// 返回被删掉的文件（供验收核对）。**绝不抛异常。**
    /// </summary>
    public static IReadOnlyList<string> Prune(
        string directory,
        int retentionDays = DefaultRetentionDays,
        long maxTotalBytes = DefaultMaxTotalBytes,
        DateTime? now = null)
    {
        var removed = new List<string>();

        try
        {
            var files = ListFiles(directory);
            if (files.Count == 0)
            {
                return removed;
            }

            var today = (now ?? DateTime.Now).Date;

            foreach (var path in files)
            {
                // 日期从文件名解析（轮转只看文件名，不看内容，也不依赖文件时间戳）
                if (!TryParseDate(path, out var date))
                {
                    continue;
                }

                if (retentionDays >= 0 && (today - date).TotalDays > retentionDays)
                {
                    if (TryDelete(path))
                    {
                        removed.Add(path);
                    }
                }
            }

            // 总量上限：剩下的按时间从老到新删，直到达标
            var survivors = ListFiles(directory)
                .Where(path => !removed.Contains(path, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var total = survivors.Sum(LengthOf);
            foreach (var path in survivors)
            {
                if (total <= maxTotalBytes)
                {
                    break;
                }

                var size = LengthOf(path);
                if (TryDelete(path))
                {
                    removed.Add(path);
                    total -= size;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响写日志
        }

        return removed;
    }

    /// <summary>从 <c>cubby-yyyyMMdd.log</c> 的文件名里取日期。</summary>
    public static bool TryParseDate(string path, out DateTime date)
    {
        date = default;

        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = name[FilePrefix.Length..];
        return DateTime.TryParseExact(
            digits,
            "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out date);
    }

    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
