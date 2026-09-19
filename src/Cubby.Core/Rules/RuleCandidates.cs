using System.IO;

namespace Cubby.Core.Rules;

/// <summary>
/// 参与归类的候选条目。刻意做成"从文件系统读出来的一份快照"，
/// 而不是直接把手伸进盒子模型——规则引擎因此完全与 UI / 存储解耦，可以纯逻辑单测。
/// </summary>
public sealed record RuleCandidate(
    string Name,
    string Path,
    string SourcePath,
    string Extension,
    long SizeBytes,
    DateTime CreatedUtc,
    DateTime ModifiedUtc)
{
    /// <summary>按名字猜出来的 MIME（见 <see cref="MimeTypes"/>）。</summary>
    public string Mime => MimeTypes.FromExtension(Extension);
}

/// <summary>把目录读成候选清单。只读，不创建/移动/删除任何东西（P4）。</summary>
public static class RuleCandidates
{
    public static IReadOnlyList<RuleCandidate> FromFolder(string? folder, int limit = 2000)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        var candidates = new List<RuleCandidate>();

        try
        {
            foreach (var entry in Directory.GetFileSystemEntries(folder).Take(limit))
            {
                if (TryCreate(entry) is { } candidate)
                {
                    candidates.Add(candidate);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录读不了就当没有候选，绝不抛
        }

        return candidates;
    }

    /// <summary>单个路径转候选；读不到（比如正被占用）返回 null。</summary>
    public static RuleCandidate? TryCreate(string path)
    {
        try
        {
            var isDirectory = Directory.Exists(path);
            var info = isDirectory ? null : new FileInfo(path);

            return new RuleCandidate(
                System.IO.Path.GetFileName(path),
                path,
                System.IO.Path.GetDirectoryName(path) ?? string.Empty,
                isDirectory ? string.Empty : System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                info?.Length ?? 0,
                info?.CreationTimeUtc ?? Directory.GetCreationTimeUtc(path),
                info?.LastWriteTimeUtc ?? Directory.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}