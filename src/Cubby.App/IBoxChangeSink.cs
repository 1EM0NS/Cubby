using Cubby.Core.Model;

namespace Cubby.App;

/// <summary>
/// 浮层窗口把「盒子变了 / 条目被打开」这类事情抛给宿主（OverlayManager）。
/// 用接口而不是回调，是为了让窗口不需要知道布局与持久化的细节。
/// </summary>
internal interface IBoxChangeSink
{
    /// <summary>盒子模型发生变化（移动 / 缩放 / 锁定 / 折叠 / 重命名），需要保存并重算命中区域。</summary>
    void OnBoxChanged(Box box);

    /// <summary>条目被双击（或从菜单选择打开）。</summary>
    void OnItemOpen(BoxItem item);

    /// <summary>要求在资源管理器里定位该条目（右键菜单「打开位置」）。</summary>
    void OnItemReveal(BoxItem item);

    /// <summary>一次桌面图标吸附的结果摘要（右键菜单「吸附桌面图标」）。</summary>
    void OnAdoptReport(string summary);
}