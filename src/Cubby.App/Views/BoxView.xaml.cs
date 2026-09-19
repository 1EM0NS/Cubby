using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cubby.Core.Layout;
using Cubby.Core.Model;

namespace Cubby.App.Views;

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

    public BoxView(Box box, StyleSettings style, MonitorSurface surface)
    {
        InitializeComponent();

        Current = box;
        BoxStyle = style.Normalized();
        _surface = surface;

        Refresh();
    }

    /// <summary>盒子模型变化（移动 / 缩放 / 锁定 / 折叠 / 重命名），宿主据此保存并重算命中区域。</summary>
    public event EventHandler<Box>? BoxChanged;

    /// <summary>双击条目，或通过右键菜单请求打开。</summary>
    public event EventHandler<BoxItem>? ItemOpenRequested;

    public Box Current { get; private set; }

    public StyleSettings BoxStyle { get; }

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

        stack.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                ItemOpenRequested?.Invoke(this, item);
                e.Handled = true;
            }
        };

        return stack;
    }

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