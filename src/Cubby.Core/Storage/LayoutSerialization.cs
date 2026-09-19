using System.Text.Json;
using System.Text.Json.Serialization;
using Cubby.Core.Model;

namespace Cubby.Core.Storage;

/// <summary>
/// 布局的序列化约定。抽出来是为了让**布局文件与快照文件用完全相同的一套规则**——
/// 快照要能被将来的迁移链读到，两处配置一旦漂移就会出现"快照还原不了"这种最难查的问题。
/// </summary>
public static class LayoutSerialization
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(LayoutDocument document) => JsonSerializer.Serialize(document, Options);

    public static LayoutDocument? Deserialize(string json) =>
        JsonSerializer.Deserialize<LayoutDocument>(json, Options);

    /// <summary>
    /// 版本校验。布局文件与快照共用同一套判断，避免"文件能读、快照读不了"这类漂移。
    /// 返回 null 表示可用；否则返回给人看的说明。
    /// </summary>
    public static string? ValidateVersion(LayoutDocument document)
    {
        if (document.SchemaVersion > LayoutSchema.CurrentVersion)
        {
            return $"布局文件版本为 {document.SchemaVersion}，高于本程序支持的 {LayoutSchema.CurrentVersion}";
        }

        if (document.SchemaVersion < LayoutSchema.CurrentVersion)
        {
            // 目前只有 v1；将来新增版本时在这里补迁移链，并补对应单元测试
            return $"暂不支持从布局版本 {document.SchemaVersion} 迁移";
        }

        return null;
    }
}