namespace Cubby.Core.Model;

/// <summary>
/// 桌面上的一个图标：显示名 + 它在虚拟屏幕坐标里的左上角位置。
/// 位置**只读不写**——吸附功能只把它当筛选依据，绝不回写（P4）。
/// </summary>
public readonly record struct DesktopIcon(string Name, int X, int Y);