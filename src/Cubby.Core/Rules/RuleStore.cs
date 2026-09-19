using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cubby.Core.Rules;

/// <summary>
/// 规则文件的读写：<c>%AppData%\Cubby\rules.json</c>。
///
/// 与布局一样的三条要求：原子写、读取永不抛、版本高于程序时拒绝加载而不是带着未知字段乱跑。
/// 首次使用会落一份**带示例规则的模板**——用户看到一份能直接改的例子，比看空文件强得多。
/// </summary>
public sealed class RuleStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public RuleStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    /// <summary>上次读取的问题（正常为 null）。</summary>
    public string? LastLoadDiagnostic { get; private set; }

    public RuleSet Load()
    {
        LastLoadDiagnostic = null;

        if (!File.Exists(FilePath))
        {
            return new RuleSet();
        }

        try
        {
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var ruleSet = JsonSerializer.Deserialize<RuleSet>(json, Options)
                ?? throw new InvalidDataException("反序列化结果为空");

            if (ruleSet.SchemaVersion > RuleSchema.CurrentVersion)
            {
                throw new NotSupportedException(
                    $"规则文件版本为 {ruleSet.SchemaVersion}，高于本程序支持的 {RuleSchema.CurrentVersion}");
            }

            return ruleSet;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or IOException)
        {
            LastLoadDiagnostic = $"{ex.GetType().Name}: {ex.Message}";
            return new RuleSet();
        }
    }

    public void Save(RuleSet ruleSet)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = ruleSet with { SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
        var json = JsonSerializer.Serialize(payload, Options);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));

        if (File.Exists(FilePath) && OperatingSystem.IsWindows())
        {
            File.Replace(temporary, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, FilePath, overwrite: true);
        }
    }

    /// <summary>首次使用时写入的示例规则：覆盖五类条件里的四类，改起来有参照。</summary>
    public static RuleSet Sample(string targetBoxId) => new()
    {
        Rules =
        [
            new ClassificationRule
            {
                Id = "images",
                Name = "图片收进图片盒子",
                TargetBoxId = targetBoxId,
                Priority = 10,
                Conditions =
                [
                    new RuleCondition { Kind = RuleMatchKind.Mime, Value = "image/*" },
                    new RuleCondition { Kind = RuleMatchKind.Extension, Value = "jpg,png,gif,webp,bmp" },
                ],
            },
            new ClassificationRule
            {
                Id = "recent-docs",
                Name = "最近三天改过的文档",
                TargetBoxId = targetBoxId,
                Priority = 20,
                Conditions =
                [
                    new RuleCondition { Kind = RuleMatchKind.Extension, Value = "docx,pdf,md,txt" },
                    new RuleCondition { Kind = RuleMatchKind.Time, WithinDays = 3 },
                ],
            },
            new ClassificationRule
            {
                Id = "from-downloads",
                Name = "从下载目录来的东西",
                TargetBoxId = targetBoxId,
                Priority = 30,
                Conditions =
                [
                    new RuleCondition { Kind = RuleMatchKind.SourcePath, Value = "*\\Downloads" },
                ],
            },
            new ClassificationRule
            {
                Id = "screenshots",
                Name = "截图（文件名正则）",
                TargetBoxId = targetBoxId,
                Priority = 5,
                Conditions =
                [
                    new RuleCondition { Kind = RuleMatchKind.Regex, Value = "^(截图|Screenshot|Snipaste)" },
                ],
            },
        ],
    };
}