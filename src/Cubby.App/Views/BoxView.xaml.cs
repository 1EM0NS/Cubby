using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Core.Platform;
using Cubby.Shell.Desktop;
using WinForms = System.Windows.Forms;

namespace Cubby.App.Views;

/// <summary>条目上的用户动作，右键菜单与自动化验收走同一套入口。</summary>
internal enum ItemAction
{
    /// <summary>按系统关联打开。</summary>
    Open,

    /// <summary>在资源管理器中定位。</summary>
    Reveal,

    /// <summary>改展示名（**不重命名磁盘文件**）。</summary>
    Rename,

    /// <summary>把条目移出盒子（**不删除磁盘文件**）。</summary>
    Remove,
}

/// <summary>
/// 单个盒子的视图：标题栏（拖动 / 锁定 / 折叠）、条目网格、右下角缩放手柄。
///
/// 几何运算全部委托给 <see cref="BoxGeometry"/>（纯逻辑、有单测），
/// 这里只负责把鼠标位移换算成模型变化并通过 <see cref="BoxChanged"/> 抛给宿主。
/// </summary>
public partial class BoxView : UserControl
{
    // Segoe MDL2 Assets 的矢量字形：比彩色 emoji 干净，而且跟随前景色（主题统一）
    private const string GlyphEye = "\uE7B3";
    private const string GlyphEyeOff = "\uED1A";
    private const string GlyphSearch = "\uE721";
    private const string GlyphLock = "\uE72E";
    private const string GlyphUnlock = "\uE785";
    private const string GlyphChevronDown = "\uE70D";
    private const string GlyphChevronRight = "\uE76C";
    private const string GlyphLink = "\uE71B";
    private const string GlyphAdd = "\uE710";

    private static readonly Color WarnColor = Color.FromRgb(0xEF, 0xAA, 0x17);
    private static readonly Color MappedColor = Color.FromRgb(0x27, 0xD2, 0xBF);

    // 色值统一从主题资源取（盒子是代码里构建的，取不到时退回上面的常量，绝不因此崩溃）
    private readonly Color _warn;
    private readonly Color _mapped;
    private readonly Color _iconIdle;

    private readonly MonitorSurface _surface;
    private IInputElement? _dragSpace;
    private Point _lastPoint;
    private bool _moving;
    private bool _resizing;
    private bool _hover;

    // 条目拖出的待定状态：按下后超过系统拖动阈值才算拖拽，否则算单击
    private BoxItem? _pendingDragItem;
    private Point _pendingDragOrigin;
    private FrameworkElement? _pendingDragSource;

    public BoxView(Box box, StyleSettings style, MonitorSurface surface)
    {
        InitializeComponent();

        Current = box;
        BoxStyle = style.Normalized();
        _surface = surface;

        _warn = ThemeColor("Cubby.Brush.Warn", WarnColor);
        _mapped = ThemeColor("Cubby.Brush.Info", MappedColor);
        _iconIdle = ThemeColor("Cubby.Brush.TextMuted", Color.FromRgb(0xC8, 0xD2, 0xDC));

        Root.ContextMenu = BuildBoxMenu();

        // 悬停提亮：浮层是 WS_EX_NOACTIVATE，拿不到键盘焦点，但鼠标事件照常，因此悬停反馈可行
        Root.MouseEnter += (_, _) => SetHover(true);
        Root.MouseLeave += (_, _) => SetHover(false);

        Refresh();
    }

    /// <summary>盒子模型变化（移动 / 缩放 / 锁定 / 折叠 / 重命名），宿主据此保存并重算命中区域。</summary>
    public event EventHandler<Box>? BoxChanged;

    /// <summary>双击条目，或通过右键菜单请求打开。</summary>
    public event EventHandler<BoxItem>? ItemOpenRequested;

    /// <summary>右键菜单「打开位置」。</summary>
    public event EventHandler<BoxItem>? ItemRevealRequested;

    /// <summary>一次桌面图标吸附的结果摘要，交给宿主记录（诊断面板会显示）。</summary>
    public event EventHandler<string>? AdoptReported;

    /// <summary>请求切换桌面图标的显隐（标题栏 👁）。</summary>
    public event EventHandler? DesktopIconToggleRequested;

    /// <summary>请求打开这个盒子的搜索窗口（标题栏 🔍 或右键菜单）。</summary>
    public event EventHandler<Box>? SearchRequested;

    public Box Current { get; private set; }

    public StyleSettings BoxStyle { get; }

    /// <summary>最近一次拖出被目标接受的效果（诊断用）。</summary>
    public string? LastDragOutEffect { get; private set; }

    /// <summary>用新的模型刷新视图（例如宿主从磁盘重载了布局）。</summary>
    public void Update(Box box)
    {
        Current = box;
        Refresh();
    }

    private double MonitorWidthDip => _surface.Bounds.Width / _surface.DpiScale;

    private double MonitorHeightDip => _surface.Bounds.Height / _surface.DpiScale;

    private void Refresh()
    {
        var box = Current;

        Width = box.Bounds.Width;
        Height = BoxGeometry.EffectiveHeight(box);

        ApplyChrome(box);

        // 状态图标：锁定 > 映射。标题左移给图标让位，避免文字压在上面
        var hasState = box.IsLocked || box.MappedFolder is not null;
        TitleIcon.Text = box.IsLocked ? GlyphLock : GlyphLink;
        TitleIcon.Foreground = new SolidColorBrush(box.IsLocked ? _warn : _mapped);
        TitleIcon.Visibility = hasState ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Margin = new Thickness(hasState ? 30 : 14, 0, 0, 0);
        TitleText.Text = box.Name;
        TitleText.FontSize = BoxStyle.FontSize;

        DesktopIconButton.Content = DesktopIcons.IsVisible() ? GlyphEye : GlyphEyeOff;
        SearchButton.Content = GlyphSearch;
        LockButton.Content = box.IsLocked ? GlyphLock : GlyphUnlock;
        LockButton.Foreground = new SolidColorBrush(box.IsLocked ? _warn : _iconIdle);
        CollapseButton.Content = box.IsCollapsed ? GlyphChevronRight : GlyphChevronDown;

        ItemArea.Visibility = box.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ResizeGrip.Visibility = box.IsCollapsed || box.IsLocked ? Visibility.Collapsed : Visibility.Visible;

        BuildItems();
    }

    /// <summary>
    /// 盒子的"外壳"：一层中性的半透明底 + 一圈极淡的渐变描边。
    ///
    /// 这里刻意**不再用状态色包一整圈边框**（上一版是荧光绿 / 黄 / 青，桌面上一看像"全都被选中了"）。
    /// 层级交给描边的明度差表达，状态信息交给标题栏的图标——这是 Fluent 的做法：
    /// 常驻元素保持中性，提醒只在需要时局部出现。
    ///
    /// **所有绘制都必须落在盒子矩形内**：盒子外必须保持 alpha=0，否则会抢走桌面与动态壁纸的点击（P2）。
    /// 因此这里不用任何外扩 Effect（阴影 / 发光都会向外扩散）。立体感只靠渐变与内描边。
    /// </summary>
    private void ApplyChrome(Box box)
    {
        var alpha = (byte)Math.Round(Math.Clamp(BoxStyle.Opacity, 0.2, 1.0) * 255);

        Root.CornerRadius = new CornerRadius(BoxStyle.CornerRadius);
        Root.Background = BoxBackground(alpha);

        // 描边用"上亮下暗"的极淡白，模拟玻璃边缘受光，比一圈等亮度的线自然。
        // 下限 0x24 是为了把透明度调得很低时仍看得出盒子边界（否则就是一团没有形状的雾）
        var edge = (byte)Math.Round(Math.Max(alpha * 0.13, 0x24) * (_hover ? 1.8 : 1.0));
        Root.BorderBrush = new LinearGradientBrush(
            Color.FromArgb(edge, 0xFF, 0xFF, 0xFF),
            Color.FromArgb((byte)(edge * 0.4), 0xFF, 0xFF, 0xFF),
            new Point(0, 0),
            new Point(0, 1));

        // 强调条只在"需要提醒"的状态下出现；普通盒子不留任何色条
        if (AccentFor(box) is { } accent)
        {
            AccentBar.Visibility = Visibility.Visible;
            AccentBar.Background = new SolidColorBrush(WithAlpha(accent, _hover ? (byte)0xD9 : (byte)0x99));
        }
        else
        {
            AccentBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>需要提醒的状态才有强调色：锁定（黄）/ 映射文件夹（青）；普通盒子返回 null。</summary>
    private Color? AccentFor(Box box) =>
        box.IsLocked ? _warn : box.MappedFolder is not null ? _mapped : null;

    private Brush BoxBackground(byte alpha)
    {
        // 悬停时整体提亮一档，形成"被指到"的反馈
        var lift = _hover ? 0x0A : 0x00;

        // 中性灰而不是偏蓝黑：Windows 11 深色模式的底是 #202020 这一族。
        // 上一版用的是 #24 2E 3C / #17 1F 2A 这类带蓝的深色，跟系统界面摆在一起会显"脏"。
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };

        // 顶部亮、底部暗：配合盒子顶部那条 1px 高光，读起来才像一块"被光照到的玻璃板"，
        // 而不是一块均匀的黑板。差值刻意留得明显——太均匀就没有材质感了。
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, (byte)(0x2E + lift), (byte)(0x2E + lift), (byte)(0x2F + lift)), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, (byte)(0x23 + lift), (byte)(0x23 + lift), (byte)(0x24 + lift)), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, (byte)(0x1B + lift), (byte)(0x1B + lift), (byte)(0x1C + lift)), 1));
        return brush;
    }

    private void SetHover(bool hover)
    {
        if (_hover == hover)
        {
            return;
        }

        _hover = hover;
        ApplyChrome(Current);
    }

    /// <summary>从主题资源里取一个颜色；资源缺失时退回常量（盒子渲染绝不能因为主题问题炸掉）。</summary>
    private Color ThemeColor(string key, Color fallback) =>
        TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private void BuildItems()
    {
        var columns = Math.Max(1, Math.Min(BoxStyle.Columns, Current.Columns <= 0 ? BoxStyle.Columns : Current.Columns));
        var available = Math.Max(80, Current.Bounds.Width - 24);
        var itemWidth = available / columns;

        if (ItemsHost.ItemsPanel.LoadContent() is WrapPanel panel)
        {
            // 空盒子时**不设 ItemWidth**：引导块要独占一整行。
            // 一旦设了它，WrapPanel 会把子元素强行压成"一个格子"那么宽，文字全被挤成三行。
            panel.ItemWidth = Current.Items.Count == 0 ? double.NaN : itemWidth;
        }

        ItemsHost.Items.Clear();

        if (Current.Items.Count == 0)
        {
            ItemsHost.Items.Add(CreateEmptyState(available));
            return;
        }

        foreach (var item in Current.Items)
        {
            ItemsHost.Items.Add(CreateItemVisual(item, itemWidth));
        }
    }

    /// <summary>
    /// 空盒子 / 映射为空的引导：一句能照着做的话，**占满整行居中**。
    /// 早先它被固定成一个格子的宽度，于是"只登记引用，不移动文件"被挤成三行——
    /// 一句话被折断就不成话了，那是上一版最显"没做完"的地方。
    /// </summary>
    private FrameworkElement CreateEmptyState(double availableWidth)
    {
        var mapped = Current.MappedFolder is not null;

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        panel.Children.Add(new TextBlock
        {
            Text = GlyphAdd,
            FontFamily = (FontFamily)FindResource("Cubby.Font.Icon"),
            FontSize = 18,
            Foreground = new SolidColorBrush(WithAlpha(ThemeColor("Cubby.Brush.TextMuted", Color.FromRgb(0xC6, 0xC6, 0xC6)), 0x99)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text = mapped ? "这个文件夹现在是空的" : "把文件拖进来",
            Foreground = new SolidColorBrush(WithAlpha(ThemeColor("Cubby.Brush.TextMuted", Color.FromRgb(0xC6, 0xC6, 0xC6)), 0xE6)),
            FontSize = Math.Max(11, BoxStyle.FontSize - 1),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });

        // 只有映射盒子才再补一行来源。普通空盒子不必解释"拖进来是什么"——
        // 多写一句，反而把"能照着做的那句话"淹进说明里
        if (mapped)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Current.MappedFolder,
                Foreground = new SolidColorBrush(WithAlpha(ThemeColor("Cubby.Brush.TextDim", Color.FromRgb(0x8C, 0x8C, 0x8C)), 0xCC)),
                FontSize = Math.Max(10, BoxStyle.FontSize - 3),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        return new Border
        {
            Width = Math.Max(160, availableWidth),
            Margin = new Thickness(2, 4, 2, 4),
            Padding = new Thickness(12, 20, 12, 20),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            Child = panel,
        };
    }

    /// <summary>条目图标槽位（DIP）。32 是 Windows 的"大图标"，也是用户在桌面上最熟悉的尺寸。</summary>
    private const double IconSize = 32;

    private FrameworkElement CreateItemVisual(BoxItem item, double itemWidth)
    {
        var name = new TextBlock
        {
            Text = item.DisplayName,
            Foreground = new SolidColorBrush(ThemeColor("Cubby.Brush.Text", Color.FromRgb(0xF0, 0xF0, 0xF0))),
            FontSize = Math.Max(10, BoxStyle.FontSize - 2),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 32,
            Margin = new Thickness(2, 6, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        // 图标优先用 shell 给的真实图标。这是"看起来像桌面"的关键一步：
        // 自绘的字母方块无论做得多精致，都会被一眼认出"这不是系统的图标"。
        var icon = FileIconCache.For(item);
        stack.Children.Add(icon is null ? CreateFallbackBadge(item) : CreateIconImage(icon));
        stack.Children.Add(name);

        var tile = new Border
        {
            Style = (Style)FindResource("Cubby.ItemTile"),
            Width = itemWidth,
            Margin = new Thickness(0, 3, 0, 4),
            Cursor = Cursors.Hand,
            ToolTip = item.TargetPath,
            Child = stack,
            ContextMenu = BuildItemMenu(item),
        };

        tile.MouseLeftButtonDown += (_, e) => OnItemMouseDown(tile, item, e);
        tile.MouseMove += OnItemMouseMove;
        tile.MouseLeftButtonUp += (_, _) => ResetPendingDrag();

        return tile;
    }

    private static FrameworkElement CreateIconImage(ImageSource icon)
    {
        var image = new Image
        {
            Source = icon,
            Width = IconSize,
            Height = IconSize,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // 图标是位图，缩放时用高质量插值，否则 125% / 150% 缩放下会有明显锯齿
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    /// <summary>
    /// 拿不到系统图标时的降级：**中性**的字母方块。
    /// 刻意不再按类型上色——一排图标四种颜色会让盒子像块调色板，比没有图标还扎眼。
    /// </summary>
    private FrameworkElement CreateFallbackBadge(BoxItem item) => new Border
    {
        Width = IconSize,
        Height = IconSize,
        CornerRadius = new CornerRadius(7),
        Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
        Child = new TextBlock
        {
            Text = BadgeText(item),
            Foreground = new SolidColorBrush(
                WithAlpha(ThemeColor("Cubby.Brush.TextMuted", Color.FromRgb(0x9A, 0x9A, 0x9A)), 0xE6)),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    // ---- 右键菜单 ----

    /// <summary>盒子自身的右键菜单（条目上的右键菜单是另一套，见 <see cref="BuildItemMenu"/>）。</summary>
    internal ContextMenu BuildBoxMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("吸附盒子范围内的桌面图标", OnAdoptMenuClick));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("搜索条目…", RequestSearch));
        menu.Items.Add(MenuEntry("隐藏 / 显示桌面图标", () => OnToggleDesktopIcons(this, new RoutedEventArgs())));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("映射文件夹…", OnMapFolderMenuClick));
        menu.Items.Add(MenuEntry("解除映射", OnUnmapFolderMenuClick));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("重命名盒子", RequestRename));

        return menu;
    }

    private void OnMapFolderMenuClick()
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择要映射到这个盒子的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        var folder = dialog.SelectedPath;

        var answer = CubbyDialog.Confirm(
            Window.GetWindow(this),
            "Cubby · 映射文件夹",
            $"将把「{folder}」的内容显示在这个盒子里，并跟随它的增删变化。{Environment.NewLine}{Environment.NewLine}" +
            "Cubby 不会移动、复制或删除该文件夹里的任何东西，只是换个地方展示。继续？");

        if (!answer)
        {
            return;
        }

        // 目标不可用时要明确说话，不能让用户以为"这个文件夹本来就是空的"
        if (!MapFolder(folder))
        {
            CubbyDialog.Info(
                Window.GetWindow(this),
                "Cubby · 映射文件夹",
                LastMapDiagnostic ?? "映射失败。");
        }
    }

    private void OnUnmapFolderMenuClick()
    {
        if (Current.MappedFolder is null)
        {
            CubbyDialog.Info(Window.GetWindow(this), "Cubby", "这个盒子没有映射文件夹。");
            return;
        }

        UnmapFolder();
    }

    /// <summary>
    /// 把一个目录映射到本盒子：内容来自首次扫描。目录不可用时返回 false，并留下原因。
    /// **只读目录，不改动磁盘上的任何东西**（P4）。
    /// </summary>
    internal bool MapFolder(string? folder)
    {
        if (!MappedFolderService.TryMap(Current, folder ?? string.Empty, out var updated, out var diagnostic))
        {
            LastMapDiagnostic = diagnostic;
            return false;
        }

        Apply(updated);
        LastMapDiagnostic = diagnostic;
        return true;
    }

    /// <summary>解除映射：只摘掉映射关系与条目引用，磁盘上的目录与文件一个都不动（P4）。</summary>
    internal void UnmapFolder()
    {
        Apply(Current with { MappedFolder = null, Items = [] });
        LastMapDiagnostic = "已解除映射；文件夹里的内容与位置完全没有改动。";
    }

    /// <summary>最近一次映射操作的说明（诊断与自动化验收用）。</summary>
    internal string? LastMapDiagnostic { get; private set; }

    private void OnAdoptMenuClick()
    {
        var added = AdoptDesktopIcons();

        // 一条都没吸到时必须给个说法，否则用户只会觉得"菜单点了没反应"
        if (added == 0)
        {
            CubbyDialog.Info(
                Window.GetWindow(this),
                "Cubby · 吸附桌面图标",
                $"{LastAdoptSummary ?? "读取桌面图标失败"}{Environment.NewLine}{Environment.NewLine}" +
                "提示：吸附只认「图标位置落在盒子矩形内」的项；「此电脑」「回收站」这类虚拟图标不在桌面目录里，匹配不上属于正常。");
        }
    }

    /// <summary>
    /// 把位置落在本盒子范围内的桌面图标吸进盒子。
    /// **全程只读**：读图标位置与名字、读桌面目录清单，然后把匹配到的路径登记为引用（P4）。
    /// 读不到图标（权限 / Explorer 重启 / 版本差异）时返回 0，整体降级为"不吸附"。
    /// </summary>
    internal int AdoptDesktopIcons()
    {
        var icons = DesktopIcons.Read();
        var area = _surface.ToPhysical(Current.Bounds);
        var candidates = DesktopFolders.EnumerateEntries();

        var plan = DesktopAdoption.Plan(icons, area, candidates);
        var added = DropImport.Create(plan.MatchedPaths, Current.Items);

        if (added.Count > 0)
        {
            Apply(Current with { Items = [.. Current.Items, .. added] });
        }

        LastAdoptSummary = $"「{Current.Name}」新增 {added.Count} 条；{plan.Describe()}";
        AdoptReported?.Invoke(this, LastAdoptSummary);

        return added.Count;
    }

    /// <summary>最近一次吸附的摘要（诊断与自动化验收用）。</summary>
    internal string? LastAdoptSummary { get; private set; }

    /// <summary>条目右键菜单。菜单项本身不做磁盘操作，只把动作转给 <see cref="InvokeItemAction"/>。</summary>
    internal ContextMenu BuildItemMenu(BoxItem item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("打开", () => InvokeItemAction(ItemAction.Open, item)));
        menu.Items.Add(MenuEntry("打开位置", () => InvokeItemAction(ItemAction.Reveal, item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("重命名", () => RenameInteractive(item)));
        menu.Items.Add(MenuEntry("移出盒子", () => InvokeItemAction(ItemAction.Remove, item)));

        return menu;
    }

    private static MenuItem MenuEntry(string header, Action action)
    {
        var entry = new MenuItem { Header = header };
        entry.Click += (_, _) => action();
        return entry;
    }

    /// <summary>
    /// 条目动作的统一入口。右键菜单与自动化验收都从这里走，
    /// 因此验收覆盖到的就是用户点击时真正执行的那段代码。
    /// </summary>
    internal void InvokeItemAction(ItemAction action, BoxItem item, string? newName = null)
    {
        switch (action)
        {
            case ItemAction.Open:
                ItemOpenRequested?.Invoke(this, item);
                break;

            case ItemAction.Reveal:
                ItemRevealRequested?.Invoke(this, item);
                break;

            case ItemAction.Rename when !string.IsNullOrWhiteSpace(newName):
                // 只改展示名，不碰磁盘上的文件名（P4）
                Apply(Current with
                {
                    Items = Current.Items
                        .Select(i => i.Id == item.Id ? i with { DisplayName = newName.Trim() } : i)
                        .ToList(),
                });
                break;

            case ItemAction.Remove:
                var remaining = Current.Items.Where(i => i.Id != item.Id).ToList();
                if (remaining.Count != Current.Items.Count)
                {
                    // 只摘下引用，磁盘上的文件原样留在原处（P4）
                    Apply(Current with { Items = remaining });
                }

                break;
        }
    }

    private void RenameInteractive(BoxItem item)
    {
        var prompt = new TextPromptWindow("重命名条目", item.DisplayName)
        {
            Owner = Window.GetWindow(this),
        };

        if (prompt.ShowDialog() == true)
        {
            InvokeItemAction(ItemAction.Rename, item, prompt.Value);
        }
    }

    // ---- 拖出到资源管理器 ----

    private void OnItemMouseDown(FrameworkElement source, BoxItem item, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ItemOpenRequested?.Invoke(this, item);
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 1)
        {
            _pendingDragItem = item;
            _pendingDragOrigin = e.GetPosition(this);
            _pendingDragSource = source;
        }
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDragItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _pendingDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _pendingDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _pendingDragItem;
        var source = _pendingDragSource ?? (DependencyObject)this;
        ResetPendingDrag();
        StartDragOut(item, source);
    }

    private void ResetPendingDrag()
    {
        _pendingDragItem = null;
        _pendingDragSource = null;
    }

    /// <summary>
    /// 拖出：只提供 <c>CF_HDROP</c>（FileDrop），由目标窗口决定复制还是移动。
    /// **Cubby 自己不执行任何文件操作**；若目标把文件移走了，条目引用随之失效，于是移出盒子。
    /// 详见 docs/adr/0004-drag-out-allows-target-to-move.md。
    /// </summary>
    private void StartDragOut(BoxItem item, DependencyObject source)
    {
        try
        {
            var effect = DragDrop.DoDragDrop(
                source,
                BuildDragOutData(item),
                DragDropEffects.Copy | DragDropEffects.Move);

            LastDragOutEffect = effect.ToString();

            if (effect.HasFlag(DragDropEffects.Move))
            {
                InvokeItemAction(ItemAction.Remove, item);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            LastDragOutEffect = ex.Message;
        }
    }

    /// <summary>拖出时给目标窗口的数据：只有一条 <c>CF_HDROP</c>（FileDrop）路径，不含任何"删除/移动"指令。</summary>
    internal static DataObject BuildDragOutData(BoxItem item) =>
        new(DataFormats.FileDrop, new[] { item.TargetPath });

    private static string BadgeText(BoxItem item) => item.Kind switch
    {
        ItemKind.Folder => "DIR",
        ItemKind.Url => "URL",
        ItemKind.Mapped => "MAP",
        _ => ExtensionOf(item.TargetPath),
    };

    private static string ExtensionOf(string path)
    {
        var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return string.IsNullOrEmpty(extension) ? "FILE" : extension[..Math.Min(4, extension.Length)];
    }

    /// <summary>
    /// 拖动时的参考坐标系。**必须用不会跟着盒子移动的容器**（父 Canvas），
    /// 不能用 <c>e.GetPosition(this)</c>：盒子跟着鼠标走，参考系也一起动，位移会自我抵消，
    /// 表现为"拖一下就停住"。缩放不受影响，是因为缩放只改右下角、左上角不动。
    /// </summary>
    private Point ReferencePoint(MouseEventArgs e)
    {
        _dragSpace ??= VisualTreeHelper.GetParent(this) as IInputElement;
        return _dragSpace is null ? e.GetPosition(this) : e.GetPosition(_dragSpace);
    }

    // ---- 标题栏：拖动移动 ----

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject) || Current.IsLocked)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            RequestRename();
            e.Handled = true;
            return;
        }

        _moving = true;
        _lastPoint = ReferencePoint(e);
        Mouse.Capture(TitleBar);
    }

    private void OnTitleMouseMove(object sender, MouseEventArgs e)
    {
        if (!_moving)
        {
            return;
        }

        var point = ReferencePoint(e);
        var deltaX = point.X - _lastPoint.X;
        var deltaY = point.Y - _lastPoint.Y;
        _lastPoint = point;

        ApplyGeometry(BoxGeometry.MoveTo(Current.Bounds, deltaX, deltaY, MonitorWidthDip, MonitorHeightDip));
    }

    private void OnTitleMouseUp(object sender, MouseButtonEventArgs e)
    {
        _moving = false;
        Mouse.Capture(null);
    }

    // ---- 右下角：缩放 ----

    private void OnGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Current.IsLocked)
        {
            return;
        }

        _resizing = true;
        _lastPoint = ReferencePoint(e);
        Mouse.Capture(ResizeGrip);
    }

    private void OnGripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizing)
        {
            return;
        }

        var point = ReferencePoint(e);
        var deltaWidth = point.X - _lastPoint.X;
        var deltaHeight = point.Y - _lastPoint.Y;
        _lastPoint = point;

        ApplyGeometry(BoxGeometry.ResizeBy(Current.Bounds, deltaWidth, deltaHeight, MonitorWidthDip, MonitorHeightDip));
    }

    private void OnGripMouseUp(object sender, MouseButtonEventArgs e)
    {
        _resizing = false;
        Mouse.Capture(null);
    }

    // ---- 拖入：生成引用 ----

    private void OnDragOver(object sender, DragEventArgs e) =>
        e.Effects = CanImport(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object sender, DragEventArgs e)
    {
        var added = ImportDrop(e.Data);
        e.Handled = added.Count > 0;
    }

    /// <summary>
    /// 把一次拖放的数据转成盒子条目。
    /// 抽成方法是为了让自动化验收能直接喂一个 <see cref="DataObject"/> 进来，
    /// 而不必真的去驱动一次系统级拖放（那没法用 SendInput 复现）。
    /// </summary>
    internal IReadOnlyList<BoxItem> ImportDrop(IDataObject? data)
    {
        var paths = PathsOf(data);
        if (paths.Count == 0)
        {
            return [];
        }

        // P4：这里只登记引用，绝不触碰磁盘上的实体
        var added = DropImport.Create(paths, Current.Items);
        if (added.Count == 0)
        {
            return [];
        }

        Apply(Current with { Items = [.. Current.Items, .. added] });
        return added;
    }

    private static bool CanImport(IDataObject? data) => PathsOf(data).Count > 0;

    private static IReadOnlyList<string> PathsOf(IDataObject? data) =>
        data?.GetDataPresent(DataFormats.FileDrop) == true && data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths
            : [];

    // ---- 按钮 ----

    private void OnToggleLock(object sender, RoutedEventArgs e) =>
        Apply(Current with { IsLocked = !Current.IsLocked });

    private void OnToggleCollapse(object sender, RoutedEventArgs e) =>
        Apply(Current with { IsCollapsed = !Current.IsCollapsed });

    /// <summary>打开这个盒子的搜索窗口（标题栏 🔍 与右键菜单共用）。</summary>
    internal void RequestSearch() => SearchRequested?.Invoke(this, Current);

    private void OnSearch(object sender, RoutedEventArgs e) => RequestSearch();

    private void OnToggleDesktopIcons(object sender, RoutedEventArgs e) =>
        DesktopIconToggleRequested?.Invoke(this, EventArgs.Empty);

    private void RequestRename()
    {
        var prompt = new TextPromptWindow("重命名盒子", Current.Name)
        {
            Owner = Window.GetWindow(this),
        };

        if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.Value))
        {
            Apply(Current with { Name = prompt.Value.Trim() });
        }
    }

    private void ApplyGeometry(DipRect bounds)
    {
        if (bounds == Current.Bounds)
        {
            return;
        }

        Apply(Current with { Bounds = bounds });
    }

    private void Apply(Box updated)
    {
        Current = updated;
        Refresh();
        BoxChanged?.Invoke(this, updated);
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}