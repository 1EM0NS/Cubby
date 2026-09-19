using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cubby.Core.Model;

namespace Cubby.Core.Storage;

/// <summary>
/// 布局文件的读写。
///
/// 两条硬要求：
/// 1. **写入必须原子**——先写临时文件再替换，任何时刻强杀进程都不能留下半个 JSON；
/// 2. **读取不能抛**——布局损坏不该让程序起不来，备份现场后以空布局继续。
/// </summary>
public sealed class LayoutStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public LayoutStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    /// <summary>上一次读取出的问题（用于诊断与日志），正常时为 null。</summary>
    public string? LastLoadDiagnostic { get; private set; }

    public LayoutDocument Load()
    {
        LastLoadDiagnostic = null;

        if (!File.Exists(FilePath))
        {
            return new LayoutDocument();
        }

        try
        {
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var document = JsonSerializer.Deserialize<LayoutDocument>(json, SerializerOptions)
                ?? throw new InvalidDataException("反序列化结果为空");

            if (document.SchemaVersion > LayoutSchema.CurrentVersion)
            {
                throw new NotSupportedException(
                    $"布局文件版本为 {document.SchemaVersion}，高于本程序支持的 {LayoutSchema.CurrentVersion}");
            }

            if (document.SchemaVersion < LayoutSchema.CurrentVersion)
            {
                // 目前只有 v1；将来新增版本时在这里补迁移链，并补对应单元测试
                throw new NotSupportedException(
                    $"暂不支持从布局版本 {document.SchemaVersion} 迁移");
            }

            return document;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or IOException)
        {
            LastLoadDiagnostic = $"{ex.GetType().Name}: {ex.Message}";
            BackupCorrupted();
            return new LayoutDocument();
        }
    }

    public void Save(LayoutDocument document)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = document with { SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
        var json = JsonSerializer.Serialize(payload, SerializerOptions);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));

        if (File.Exists(FilePath) && OperatingSystem.IsWindows())
        {
            // 原子替换，同时留下上一版便于人工回滚
            File.Replace(temporary, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, FilePath, overwrite: true);
        }
    }

    /// <summary>把无法解析的文件改名留存，避免下一次保存直接把它覆盖掉。</summary>
    private void BackupCorrupted()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(FilePath, $"{FilePath}.corrupt-{stamp}", overwrite: true);
        }
        catch (IOException)
        {
            // 备份失败不影响主流程：本次读不到就用空布局继续
        }
    }
}