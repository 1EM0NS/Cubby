using System.Windows;
using System.Windows.Input;
using Cubby.Core.Model;

namespace Cubby.App;

/// <summary>
/// 盒子内即时搜索。
///
/// 为什么是独立小窗口而不是直接在盒子里放个输入框：浮层是 <c>WS_EX_NOACTIVATE</c>，
/// 按设计永远不抢焦点，因此**收不到键盘**——重命名那件事当初也是靠单独的可激活窗口解决的。
/// 这里是同一个套路，好处是搜索窗口可以正常获得焦点与输入法。
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

        HeaderText.Text = $"在「{boxName}」里搜索";
        Refresh();

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

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;

            case Key.Enter:
                OpenSelected();
                break;
        }
    }

    private void OnOpenSelected(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (ResultList.SelectedItem is BoxItem item)
        {
            _open(item);
        }
        else
        {
            StatusText.Text = $"「{_boxName}」里没有可打开的结果。";
        }
    }
}