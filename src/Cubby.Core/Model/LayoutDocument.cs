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

    /// <summary>快照保留份数（0 表示不自动清理）。重新加载布局时由 <see cref="Storage.LayoutStore"/> 读回。</summary>
    public int SnapshotKeep { get; init; } = 10;

    /// <summary>仅快照文件会写入：创建这份快照时用户给的标签。</summary>
    public string? SnapshotLabel { get; init; }

    /// <summary>
    /// 是否允许切换桌面图标显隐。默认开；关掉后托盘菜单 / 热键 / 盒子按钮都不生效。
    /// （SysListView32 的 ShowWindow 在个别 Windows 版本上行为不稳，留这个开关做降级。）
    /// </summary>
    public bool EnableDesktopIconToggle { get; init; } = true;

    /// <summary>
    /// 用户已经确认过「知道有同类软件在跑、仍要继续用 Cubby」的进程名（A8）。
    /// 记的是进程名而不是"显示过提示"这个布尔值：这样**后来新装了另一个同类软件**仍会再提示一次，
    /// 而不会因为"提示过就不再提示"把新冲突咽掉。
    /// </summary>
    public IReadOnlyList<string> CoexistAcknowledgedTools { get; init; } = [];
}