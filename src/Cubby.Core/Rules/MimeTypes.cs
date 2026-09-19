namespace Cubby.Core.Rules;

/// <summary>
/// 扩展名 → MIME 的内置对照表。
///
/// **诚实说明**：真正的 MIME 探测要么读注册表（Windows 上还常常是错的）、要么嗅探文件内容
/// （要读盘、还可能碰上不完整的文件）。这里只覆盖常见类型，认不出来的一律
/// <c>application/octet-stream</c>，规则里用 <c>*/*</c> 或 <c>类型/*</c> 通配即可。
/// 需要更准的话，将来可以加"内容嗅探"作为可选步骤，而不改变规则模型。
/// </summary>
public static class MimeTypes
{
    public const string Unknown = "application/octet-stream";

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 图片
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["png"] = "image/png",
        ["gif"] = "image/gif",
        ["bmp"] = "image/bmp",
        ["webp"] = "image/webp",
        ["svg"] = "image/svg+xml",
        ["ico"] = "image/x-icon",
        ["tif"] = "image/tiff",
        ["tiff"] = "image/tiff",

        // 文档
        ["pdf"] = "application/pdf",
        ["doc"] = "application/msword",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xls"] = "application/vnd.ms-excel",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["ppt"] = "application/vnd.ms-powerpoint",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["txt"] = "text/plain",
        ["md"] = "text/markdown",
        ["csv"] = "text/csv",
        ["json"] = "application/json",
        ["xml"] = "application/xml",
        ["html"] = "text/html",
        ["htm"] = "text/html",

        // 音视频
        ["mp3"] = "audio/mpeg",
        ["wav"] = "audio/wav",
        ["flac"] = "audio/flac",
        ["m4a"] = "audio/mp4",
        ["mp4"] = "video/mp4",
        ["mkv"] = "video/x-matroska",
        ["avi"] = "video/x-msvideo",
        ["mov"] = "video/quicktime",
        ["webm"] = "video/webm",

        // 压缩包与可执行
        ["zip"] = "application/zip",
        ["7z"] = "application/x-7z-compressed",
        ["rar"] = "application/vnd.rar",
        ["tar"] = "application/x-tar",
        ["gz"] = "application/gzip",
        ["exe"] = "application/vnd.microsoft.portable-executable",
        ["msi"] = "application/x-msi",
        ["lnk"] = "application/x-ms-shortcut",
        ["url"] = "application/internet-shortcut",
    };

    /// <summary>按扩展名取 MIME；认不出来返回 <see cref="Unknown"/>。</summary>
    public static string FromExtension(string? extension)
    {
        var key = (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        return key.Length > 0 && Map.TryGetValue(key, out var mime) ? mime : Unknown;
    }

    /// <summary>表格里认得的扩展名数量（诊断/文档用）。</summary>
    public static int KnownCount => Map.Count;
}