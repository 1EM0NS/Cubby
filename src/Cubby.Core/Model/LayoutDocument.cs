namespace Cubby.Core.Model;

/// <summary>布局文件的 schema 版本。改变数据形状时必须递增，并在 <see cref="Storage.LayoutStore"/> 里补迁移。</summary>
public static class LayoutSchema
{
    public const int CurrentVersion = 1;
}

/// <summary>整套桌面布局，持久化的根对象。</summary>
public sealed record LayoutDocument
{
    public int SchemaVersion { get; init; } = LayoutSchema.CurrentVersion;

    public IReadOnlyList<Box> Boxes { get; init; } = [];

    /// <summary>全局样式与新建盒子的默认值。</summary>
    public StyleSettings Style { get; init; } = new();

    /// <summary>保存时记录在案的显示器清单，用于布局还原时做匹配。</summary>
    public IReadOnlyList<MonitorSurface> Monitors { get; init; } = [];

    /// <summary>上次保存时间（ISO 8601），仅用于诊断。</summary>
    public string? SavedAt { get; init; }
}