using System.Globalization;
using System.Text;
using Cubby.Core.Model;

namespace Cubby.Core.Storage;

/// <summary>一份快照的概要，用于列表展示。真正的文档在需要时才读。</summary>
public sealed record SnapshotInfo(
    string FilePath,
    string Label,
    DateTime CreatedAt,
    int BoxCount,
    int ItemCount,
    int SchemaVersion)
{
    public string Describe() =>
        $"{CreatedAt:yyyy-MM-dd HH:mm:ss}  {Label,-16} 盒子 {BoxCount} 个 / 条目 {ItemCount} 条";

    /// <summary>与 <see cref="Describe"/> 同义，供 WPF 绑定用（绑定不能直接调方法）。</summary>
    public string Summary => Describe();
}

/// <summary>
/// 布局快照：`layout.json` 的历史副本，放在独立目录里。
///
/// 两个设计点：
/// 1. **快照与布局用同一套序列化与版本校验**（<see cref="LayoutSerialization"/>），
///    否则将来做 schema 迁移时会出现"布局能升级、快照升不了"的夹生状态；
/// 2. **创建快照遵守保留策略**，老快照自动清理，避免目录无限膨胀。
/// </summary>
public sealed class SnapshotStore
{
    private const string FilePrefix = "layout-";

    /// <summary>文件名里的时间戳格式（毫秒精度）与长度。文件名是时间的权威来源——比 JSON 里秒精度的 SavedAt 细。</summary>
    private const string StampFormat = "yyyyMMdd-HHmmss-fff";

    private const int StampLength = 19;

    public SnapshotStore(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>创建一份快照并返回它的概要。文件名自带时间戳，标签只作人类可读的补充。</summary>
    public SnapshotInfo Create(LayoutDocument document, string? label = null, int keep = 0)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var createdAt = DateTime.Now;
        var safeLabel = Sanitize(label);
        var path = UniquePath(createdAt, safeLabel);

        var payload = document with
        {
            SavedAt = createdAt.ToString("yyyy-MM-dd HH:mm:ss"),
            SnapshotLabel = safeLabel.Length == 0 ? null : safeLabel,
        };

        File.WriteAllText(path, LayoutSerialization.Serialize(payload), new UTF8Encoding(false));

        if (keep > 0)
        {
            Prune(keep);
        }

        return Describe(path, payload, createdAt, safeLabel);
    }

    /// <summary>
    /// 同一毫秒内连续创建会撞名（自动化验收里就会发生），撞了就加序号，
    /// 否则后一份会**静默覆盖**前一份——那等于悄悄弄丢一个还原点。
    /// </summary>
    private string UniquePath(DateTime createdAt, string label)
    {
        var stem = $"{FilePrefix}{createdAt.ToString(StampFormat, CultureInfo.InvariantCulture)}" +
                   (label.Length == 0 ? string.Empty : $"-{label}");

        for (var attempt = 0; ; attempt++)
        {
            var candidate = Path.Combine(Directory, attempt == 0 ? $"{stem}.json" : $"{stem}-{attempt}.json");

            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>按时间倒序列出全部快照（新的在前）。读不出概要的文件会被跳过而不是让列表整体失败。</summary>
    public IReadOnlyList<SnapshotInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var snapshots = new List<SnapshotInfo>();

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, FilePrefix + "*.json"))
        {
            if (TryRead(path) is { } info)
            {
                snapshots.Add(info);
            }
        }

        return snapshots.OrderByDescending(s => s.CreatedAt).ToList();
    }

    /// <summary>读取一份快照。文件坏了或版本不认识时返回 null，并通过 <paramref name="diagnostic"/> 说明原因。</summary>
    public LayoutDocument? TryLoad(string filePath, out string? diagnostic)
    {
        diagnostic = null;

        if (!File.Exists(filePath))
        {
            diagnostic = "快照文件不存在";
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            var document = LayoutSerialization.Deserialize(json);

            if (document is null)
            {
                diagnostic = "快照内容为空";
                return null;
            }

            if (LayoutSerialization.ValidateVersion(document) is { } problem)
            {
                diagnostic = problem;
                return null;
            }

            return document;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            diagnostic = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>只保留最新的 <paramref name="keep"/> 份，返回实际删掉的数量。</summary>
    public int Prune(int keep)
    {
        var maximum = Math.Max(1, keep);
        var excess = List().Skip(maximum).ToList();

        var removed = 0;
        foreach (var snapshot in excess)
        {
            try
            {
                File.Delete(snapshot.FilePath);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉就留着：快照多几份不影响使用
            }
        }

        return removed;
    }

    private SnapshotInfo? TryRead(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var document = LayoutSerialization.Deserialize(json);
            if (document is null)
            {
                return null;
            }

            var createdAt = ParseTimestamp(path, document.SavedAt);
            var label = document.SnapshotLabel
                        ?? LabelFromFileName(path, createdAt);

            return Describe(path, document, createdAt, label);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static SnapshotInfo Describe(string path, LayoutDocument document, DateTime createdAt, string label) =>
        new(
            path,
            label,
            createdAt,
            document.Boxes.Count,
            document.Boxes.Sum(b => b.Items.Count),
            document.SchemaVersion);

    /// <summary>
    /// 时间戳优先取文件名里的毫秒级时间戳（秒内连建多份时，JSON 里秒精度的 SavedAt 会撞在一起、
    /// 导致列表顺序不稳定），读不出再退回 SavedAt，最后退回文件写入时间。
    /// </summary>
    private static DateTime ParseTimestamp(string path, string? savedAt)
    {
        if (TimestampFromFileName(path) is { } stamped)
        {
            return stamped;
        }

        if (!string.IsNullOrEmpty(savedAt) && DateTime.TryParse(savedAt, out var parsed))
        {
            return parsed;
        }

        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }

    private static DateTime? TimestampFromFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (!stem.StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = stem[FilePrefix.Length..];
        if (rest.Length < StampLength)
        {
            return null;
        }

        return DateTime.TryParseExact(
            rest[..StampLength],
            StampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    private static string LabelFromFileName(string path, DateTime createdAt)
    {
        var stem = Path.GetFileNameWithoutExtension(path)[FilePrefix.Length..];
        var prefix = createdAt.ToString(StampFormat, CultureInfo.InvariantCulture);
        return stem.Length > prefix.Length ? stem[(prefix.Length + 1)..] : string.Empty;
    }

    /// <summary>标签要能安全地做文件名：去掉路径分隔符与其它非法字符。</summary>
    private static string Sanitize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(label.Trim()
            .Where(c => !invalid.Contains(c) && c != ' ')
            .ToArray());

        return cleaned.Length > 24 ? cleaned[..24] : cleaned;
    }
}