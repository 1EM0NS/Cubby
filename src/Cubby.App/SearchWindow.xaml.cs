using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cubby.App.Views;
using Cubby.Core.Model;

namespace Cubby.App;

/// <summary>
/// 盒子内即时搜索——**启动器式面板**，不是带标题栏的对话框。
///
/// 为什么是独立小窗口而不是直接在盒子里放个输入框：浮层是 <c>WS_EX_NOACTIVATE</c>，
/// 按设计永远不抢焦点，因此**收不到键盘**——重命名那件事当初也是靠单独的可激活窗口解决的。
/// 这里是同一个套路，好处是搜索窗口可以正常获得焦点与输入法。
///
/// 形态与交互借两个同类项目的成熟做法：
/// - DeskBox 的 <c>SearchResultRowControl</c>：结果行 = 图标 + 标题 + 暗色路径，
///   选中态是左侧一条 3px 的选中条而不是大块填色；
/// - DesktopFramesPlus 的 SpotSearch：快捷键唤起的紧凑搜索面板，做完动作**面板就消失**，
///   不留下一个占地方的窗口——本类"打开条目后关闭窗口"这条规则就是这么来的。
///
/// 过滤全程走内存索引（<see cref="SearchService"/>），输入一次查一次，没有定时器。
/// </summary>
public partial class SearchWindow : Window
{
    private readonly SearchService _search;
    private readonly Action<BoxItem> _open;
    private readonly string _boxId;
    private readonly string _boxName;

    internal SearchWindow(string boxId, string boxName, SearchService search, Action<BoxItem> open)
    {
        InitializeComponent();

        _boxId = boxId;
        _boxName = boxName;
        _search = search;
        _open = open;

        Placeholder.Text = $"搜索「{boxName}」…";
        UpdatePlaceholder();
        Refresh();

        // 启动器惯例：出现在屏幕中上部（靠近视线起点），而不是正中间
        var area = SystemParameters.WorkArea;
        Left = area.Left + ((area.Width - Width) / 2);
        Top = area.Top + (area.Height * 0.18);

        Loaded += (_, _) => QueryBox.Focus();
    }

    /// <summary>当前结果（供自动化验收读取）。</summary>
    internal IReadOnlyList<BoxItem> Results => ResultList.ItemsSource?.Cast<BoxItem>().ToList() ?? [];

    internal string Status => StatusText.Text;

    /// <summary>供自动化验收驱动：等价于用户在输入框里打字。</summary>
    internal void SetQuery(string text)
    {
        QueryBox.Text = text;
        QueryBox.CaretIndex = QueryBox.Text.Length;
    }

    /// <summary>供自动化验收读取：文本框当前的过滤串。</summary>
    internal string Query => QueryBox.Text;

    private void Refresh()
    {
        // 上限放到 500：ListBox 默认虚拟化，不会因为行数多就卡
        var items = _search.Query(_boxId, QueryBox.Text, limit: 500);
        ResultList.ItemsSource = items;

        var indexed = _search.CountOf(_boxId);
        StatusText.Text = items.Count == 0
            ? $"没有匹配项（该盒子共索引 {indexed} 条）"
            : $"匹配 {items.Count} 条 / 共索引 {indexed} 条    用时 {_search.LastQueryMilliseconds:0.00} ms";

        if (items.Count > 0)
        {
            ResultList.SelectedIndex = 0;
        }
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholder();
        Refresh();
    }

    private void OnQueryFocusChanged(object sender, RoutedEventArgs e) => UpdatePlaceholder();

    private void UpdatePlaceholder() =>
        Placeholder.Visibility =
            string.IsNullOrEmpty(QueryBox.Text) && !QueryBox.IsKeyboardFocused
                ? Visibility.Visible
                : Visibility.Collapsed;

    /// <summary>
    /// 键盘逻辑收在窗口层而不是某个控件里：焦点在输入框或结果列表里，
    /// 同一套快捷键（Enter / Esc / 方向键）都必须生效。
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;

            case Key.Enter:
                e.Handled = true;
                OpenSelected();
                break;

            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var count = ResultList.Items.Count;

        if (count == 0)
        {
            return;
        }

        var next = ResultList.SelectedIndex + delta;
        next = next < 0 ? count - 1 : next % count;

        ResultList.SelectedIndex = next;
        ResultList.ScrollIntoView(ResultList.SelectedItem);
    }

    private void OnDragRequested(object sender, MouseButtonEventArgs e)
    {
        // 只有点在空白处（窗口背景或面板外框）才拖动；点在输入框、列表上交给它们自己
        if (ReferenceEquals(e.OriginalSource, this) || ReferenceEquals(e.OriginalSource, Shell))
        {
            DragMove();
        }
    }

    private void OnOpenSelected(object sender, MouseButtonEventArgs e) => OpenSelected();

    /// <summary>
    /// 打开选中的条目，然后**关掉自己**（SpotSearch 的做法）：搜索是"找到 → 打开 → 继续干活"，
    /// 留着一个空窗口在屏幕上只会挡事——这正是用户骂"烂标题栏放在那里"的场景。
    /// </summary>
    private void OpenSelected()
    {
        if (ResultList.SelectedItem is BoxItem item)
        {
            _open(item);
            Close();
        }
        else
        {
            StatusText.Text = $"「{_boxName}」里没有可打开的结果。";
        }
    }

    /// <summary>结果行的 shell 图标在加载时取一次（缓存层保证只查一次扩展名）。</summary>
    private void OnRowIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image && image.DataContext is BoxItem item)
        {
            image.Source = FileIconCache.For(item);
        }
    }
}
