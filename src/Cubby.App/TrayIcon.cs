using System.Drawing;
using System.Windows.Forms;

namespace Cubby.App;

/// <summary>
/// 托盘常驻图标。用 WinForms 的 <see cref="NotifyIcon"/>：它就是 <c>Shell_NotifyIcon</c> 的托管封装，
/// 省掉自己创建消息窗口、处理 <c>WM_TASKBARCREATED</c>（Explorer 重启后要重新注册）这一整套。
///
/// 这个类**只负责界面**：菜单被点击时抛事件，由 <see cref="App"/> 决定怎么处理，
/// 因此自动化验收可以直接读菜单并驱动同一条路径。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _boxesItem;
    private readonly ToolStripMenuItem _desktopIconsItem;
    private readonly ToolStripMenuItem _autoStartItem;

    public TrayIcon()
    {
        _boxesItem = new ToolStripMenuItem("显示盒子") { Checked = true, CheckOnClick = true };
        _boxesItem.Click += (_, _) => BoxesVisibilityRequested?.Invoke(this, _boxesItem.Checked);

        _desktopIconsItem = new ToolStripMenuItem("显示桌面图标") { Checked = true, CheckOnClick = true };
        _desktopIconsItem.Click += (_, _) => DesktopIconsVisibilityRequested?.Invoke(this, _desktopIconsItem.Checked);

        _autoStartItem = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
        _autoStartItem.Click += (_, _) => AutoStartRequested?.Invoke(this, _autoStartItem.Checked);

        var settings = new ToolStripMenuItem("设置…");
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var snapshots = new ToolStripMenuItem("布局快照…");
        snapshots.Click += (_, _) => SnapshotsRequested?.Invoke(this, EventArgs.Empty);

        var rules = new ToolStripMenuItem("归类规则…");
        rules.Click += (_, _) => RulesRequested?.Invoke(this, EventArgs.Empty);

        var guide = new ToolStripMenuItem("使用指引…");
        guide.Click += (_, _) => GuideRequested?.Invoke(this, EventArgs.Empty);

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip
        {
            // 托盘菜单是 WinForms 的，WPF 主题管不到它；不指定渲染器就是系统白底黑字
            RenderMode = ToolStripRenderMode.Professional,
            Renderer = new DarkMenuRenderer(),
            Font = new Font("Segoe UI", 9f),
        };
        menu.Items.Add(_boxesItem);
        menu.Items.Add(_desktopIconsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(snapshots);
        menu.Items.Add(rules);
        menu.Items.Add(settings);
        menu.Items.Add(guide);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Cubby · 桌面整理",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<bool>? BoxesVisibilityRequested;

    public event EventHandler<bool>? DesktopIconsVisibilityRequested;

    public event EventHandler<bool>? AutoStartRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? SnapshotsRequested;

    public event EventHandler? RulesRequested;

    /// <summary>「使用指引」被点击（重新打开首次运行引导，issue #38）。</summary>
    public event EventHandler? GuideRequested;

    public event EventHandler? ExitRequested;

    /// <summary>菜单项文字，按顺序。供自动化验收核对（issue #11 的验收标准之一）。</summary>
    public IReadOnlyList<string> MenuHeaders =>
        _icon.ContextMenuStrip!.Items
            .OfType<ToolStripItem>()
            .Select(item => item.Text ?? string.Empty)
            .ToList();

    /// <summary>
    /// 按文字程序化点击某个菜单项（自动化验收用）。
    /// 走的是 <see cref="ToolStripItem.PerformClick"/>，也就是**用户点击同一条事件链**，
    /// 因此它证明的是"这一项真的接上了事件"，而不是另造一条捷径。
    /// </summary>
    public bool ClickItem(string header)
    {
        var item = _icon.ContextMenuStrip?.Items
            .OfType<ToolStripItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Text, header, StringComparison.Ordinal));

        if (item is null)
        {
            return false;
        }

        item.PerformClick();
        return true;
    }

    public void SetBoxesChecked(bool visible) => _boxesItem.Checked = visible;

    public void SetDesktopIconsChecked(bool visible) => _desktopIconsItem.Checked = visible;

    public void SetAutoStartChecked(bool enabled) => _autoStartItem.Checked = enabled;

    public void Dispose()
    {
        // 必须先置 Visible=false，否则图标会残留在通知区域直到鼠标划过
        _icon.Visible = false;
        _icon.Dispose();
    }
}