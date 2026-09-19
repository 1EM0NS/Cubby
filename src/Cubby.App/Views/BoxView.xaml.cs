using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Core.Platform;
using Cubby.Shell.Desktop;

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
    private readonly MonitorSurface _surface;
    private IInputElement? _dragSpace;
    private Point _lastPoint;
    private bool _moving;
    private bool _resizing;

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

        Root.ContextMenu = BuildBoxMenu();
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

        Root.CornerRadius = new CornerRadius(BoxStyle.CornerRadius);
        Root.Background = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(BoxStyle.Opacity * 255),
            0x1F,
            0x2A,
            0x37));
        Root.BorderBrush = new SolidColorBrush(box.IsLocked
            ? Color.FromArgb(0xFF, 0xEF, 0xAA, 0x17)
            : Color.FromArgb(0xFF, 0x0F, 0xDC, 0x78));

        TitleText.Text = box.IsLocked ? $"🔒 {box.Name}" : box.Name;
        TitleText.FontSize = BoxStyle.FontSize;

        LockButton.Content = box.IsLocked ? "🔒" : "🔓";
        CollapseButton.Content = box.IsCollapsed ? "▸" : "▾";
        ItemArea.Visibility = box.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ResizeGrip.Visibility = box.IsCollapsed || box.IsLocked ? Visibility.Collapsed : Visibility.Visible;

        BuildItems();
    }

    private void BuildItems()
    {
        var columns = Math.Max(1, Math.Min(BoxStyle.Columns, Current.Columns <= 0 ? BoxStyle.Columns : Current.Columns));
        var available = Math.Max(80, Current.Bounds.Width - 24);
        var itemWidth = available / columns;

        if (ItemsHost.ItemsPanel.LoadContent() is WrapPanel panel)
        {
            panel.ItemWidth = itemWidth;
        }

        ItemsHost.Items.Clear();

        if (Current.Items.Count == 0)
        {
            ItemsHost.Items.Add(new TextBlock
            {
                Text = Current.MappedFolder is null ? "（空盒子：把文件拖进来）" : $"映射：{Current.MappedFolder}",
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0x7A, 0x85)),
                FontSize = Math.Max(11, BoxStyle.FontSize - 2),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 8, 4, 0),
            });

            return;
        }

        foreach (var item in Current.Items)
        {
            ItemsHost.Items.Add(CreateItemVisual(item, itemWidth));
        }
    }

    private FrameworkElement CreateItemVisual(BoxItem item, double itemWidth)
    {
        var stack = new StackPanel
        {
            Width = itemWidth,
            Margin = new Thickness(0, 4, 0, 8),
            Cursor = Cursors.Hand,
            ToolTip = item.TargetPath,
        };

        var badge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(BadgeColor(item)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        badge.Child = new TextBlock
        {
            Text = BadgeText(item),
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x14, 0x18)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new TextBlock
        {
            Text = item.DisplayName,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xE5, 0xE5, 0xE5)),
            FontSize = Math.Max(10, BoxStyle.FontSize - 2),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34,
            Margin = new Thickness(2, 5, 2, 0),
        };

        stack.Children.Add(badge);
        stack.Children.Add(name);

        stack.ContextMenu = BuildItemMenu(item);

        stack.MouseLeftButtonDown += (_, e) => OnItemMouseDown(stack, item, e);
        stack.MouseMove += OnItemMouseMove;
        stack.MouseLeftButtonUp += (_, _) => ResetPendingDrag();

        return stack;
    }

    // ---- 右键菜单 ----

    /// <summary>盒子自身的右键菜单（条目上的右键菜单是另一套，见 <see cref="BuildItemMenu"/>）。</summary>
    internal ContextMenu BuildBoxMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("吸附盒子范围内的桌面图标", OnAdoptMenuClick));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("重命名盒子", RequestRename));

        return menu;
    }

    private void OnAdoptMenuClick()
    {
        var added = AdoptDesktopIcons();

        // 一条都没吸到时必须给个说法，否则用户只会觉得"菜单点了没反应"
        if (added == 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"{LastAdoptSummary ?? "读取桌面图标失败"}{Environment.NewLine}{Environment.NewLine}" +
                "提示：吸附只认「图标位置落在盒子矩形内」的项；「此电脑」「回收站」这类虚拟图标不在桌面目录里，匹配不上属于正常。",
                "Cubby · 吸附桌面图标",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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

    private static Color BadgeColor(BoxItem item) => item.Kind switch
    {
        ItemKind.Folder => Color.FromArgb(0xFF, 0x0F, 0xDC, 0x78),
        ItemKind.Url => Color.FromArgb(0xFF, 0x80, 0xC1, 0xFF),
        ItemKind.Mapped => Color.FromArgb(0xFF, 0x27, 0xD2, 0xBF),
        _ => Color.FromArgb(0xFF, 0xD3, 0xD4, 0xDA),
    };

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