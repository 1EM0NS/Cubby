namespace Cubby.Core.Model;

/// <summary>以 DIP 表示的矩形。布局层统一用 DIP 存储，物理像素只在 Shell 层出现。</summary>
public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>物理像素矩形，坐标原点为主显示器左上角（虚拟屏幕坐标）。</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>命中测试使用左闭右开，避免相邻矩形边界重复命中。</summary>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

/// <summary>一个显示器上的浮层矩形范围与 DPI 缩放。</summary>
public sealed record MonitorSurface(string Id, PixelRect Bounds, double DpiScale)
{
    /// <summary>把该显示器上的 DIP 矩形换算为虚拟屏幕物理像素矩形。</summary>
    public PixelRect ToPhysical(DipRect dip) => new(
        Bounds.Left + (int)Math.Round(dip.X * DpiScale),
        Bounds.Top + (int)Math.Round(dip.Y * DpiScale),
        Bounds.Left + (int)Math.Round(dip.Right * DpiScale),
        Bounds.Top + (int)Math.Round(dip.Bottom * DpiScale));
}

/// <summary>盒子。spike 阶段只保留命中测试所需字段，后续再扩展条目列表等。</summary>
public sealed record Box(string Id, string Name, DipRect Bounds);