namespace Cubby.Core.Model;

/// <summary>盒子里的一个条目。只保存引用，**绝不移动文件实体**（P4）。</summary>
public sealed record BoxItem(string Id, string DisplayName, string TargetPath, ItemKind Kind)
{
    public bool IsPinned { get; init; }
}

public enum ItemKind
{
    File,
    Folder,
    Url,

    /// <summary>来自文件夹映射的条目（内容在磁盘上，只是被映射展示）。</summary>
    Mapped,
}

/// <summary>
/// 一个盒子。
/// </summary>
/// <remarks>
/// <see cref="Bounds"/> 是**相对所属显示器左上角**的 DIP 坐标，而不是虚拟桌面绝对坐标。
/// 这样换显示器、换分辨率时布局都不需要重算，只换 <see cref="MonitorId"/> 即可。
/// </remarks>
public sealed record Box(string Id, string Name, DipRect Bounds)
{
    /// <summary>所属显示器标识（保存时记录，用于拔插显示器后回退匹配）。</summary>
    public string? MonitorId { get; init; }

    /// <summary>保存时该显示器的物理像素尺寸，用于显示器 Id 变化时的回退匹配。</summary>
    public int MonitorWidth { get; init; }

    public int MonitorHeight { get; init; }

    /// <summary>每行显示的条目数。</summary>
    public int Columns { get; init; } = 4;

    /// <summary>背景不透明度（0.0 - 1.0）。</summary>
    public double Opacity { get; init; } = 0.85;

    public bool IsLocked { get; init; }

    public bool IsCollapsed { get; init; }

    /// <summary>文件夹映射的源目录；为 null 表示普通盒子。</summary>
    public string? MappedFolder { get; init; }

    public IReadOnlyList<BoxItem> Items { get; init; } = [];
}