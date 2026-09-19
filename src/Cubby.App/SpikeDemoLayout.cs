using Cubby.Core.Model;

namespace Cubby.App;

/// <summary>
/// spike 阶段的演示布局。
/// 真正的盒子来自 `LayoutStore` 里的布局文件，接线随 issue #6（盒子渲染与交互）一起做；
/// 这里按每台显示器生成一组演示盒子，并且**写入 MonitorId**，以便走通 OverlayPlanner 的分配路径。
/// </summary>
internal static class SpikeDemoLayout
{
    public static IReadOnlyList<Box> CreateFor(MonitorSurface surface)
    {
        var dipWidth = surface.Bounds.Width / surface.DpiScale;
        var dipHeight = surface.Bounds.Height / surface.DpiScale;

        // 屏幕偏小时收缩盒子，保证右侧与下方留有透明区可用于采样
        var width = Math.Min(420, Math.Max(240, dipWidth * 0.45));
        var height = Math.Min(300, Math.Max(180, dipHeight * 0.3));

        return
        [
            new Box($"{surface.Id}-A", $"盒子 A @ {surface.Id}", new DipRect(60, 80, width, height))
            {
                MonitorId = surface.Id,
                MonitorWidth = surface.Bounds.Width,
                MonitorHeight = surface.Bounds.Height,
            },
            new Box($"{surface.Id}-B", $"盒子 B @ {surface.Id}", new DipRect(60, 80 + height + 40, width, height * 0.85))
            {
                MonitorId = surface.Id,
                MonitorWidth = surface.Bounds.Width,
                MonitorHeight = surface.Bounds.Height,
            },
        ];
    }
}