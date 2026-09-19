using System.Diagnostics;
using System.IO;
using System.Windows;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Core.Storage;

namespace Cubby.App;

/// <summary>
/// 布局快照管理：新建、按时间列出、还原、设置保留份数。
///
/// 还原前**自动为当前布局存一份快照**——否则"还原"本身成了一次不可逆操作，
/// 而用户往往正是在手忙脚乱的时候点它。
/// </summary>
public partial class SnapshotsWindow : Window
{
    private readonly LayoutService _layout;
    private readonly OverlayManager _manager;
    private IReadOnlyList<SnapshotInfo> _listed = [];

    internal SnapshotsWindow(LayoutService layout, OverlayManager manager)
    {
        InitializeComponent();

        _layout = layout;
        _manager = manager;

        KeepBox.Text = layout.SnapshotKeep.ToString();
        Refresh();
    }

    /// <summary>
    /// 当前列表内容（供自动化验收）。
    /// 不用 <c>SnapshotList.Items</c>：窗口没显示时 WPF 的项容器尚未生成，那个集合会是空的，
    /// 会把"列表正常"误判成失败。
    /// </summary>
    internal IReadOnlyList<SnapshotInfo> Snapshots => _listed;

    /// <summary>绑定到列表控件的那份数据量，用来证明界面拿到的就是上面这份列表。</summary>
    internal int BoundCount => SnapshotList.ItemsSource is System.Collections.ICollection collection
        ? collection.Count
        : SnapshotList.ItemsSource?.Cast<object>().Count() ?? 0;

    /// <summary>重新读取快照列表并刷新界面。用户点「新建快照」后走的就是这里。</summary>
    internal void Refresh()
    {
        var selected = (SnapshotList.SelectedItem as SnapshotInfo)?.FilePath;

        var snapshots = _layout.Snapshots.List();
        _listed = snapshots;
        SnapshotList.ItemsSource = snapshots;

        if (selected is not null)
        {
            SnapshotList.SelectedItem = snapshots.FirstOrDefault(s => s.FilePath == selected);
        }
        else if (snapshots.Count > 0)
        {
            SnapshotList.SelectedIndex = 0;
        }

        KeepBox.Text = _layout.SnapshotKeep.ToString();
        StatusText.Text = snapshots.Count == 0
            ? $"还没有快照。目录：{_layout.Snapshots.Directory}"
            : $"{snapshots.Count} 份快照，目录：{_layout.Snapshots.Directory}";
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = _layout.CreateSnapshot("手动");
            Refresh();
            StatusText.Text = $"已创建：{snapshot.Describe()}";
        }
        catch (IOException ex)
        {
            StatusText.Text = $"创建失败：{ex.Message}";
        }
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotInfo info)
        {
            StatusText.Text = "先在列表里选一份快照。";
            return;
        }

        var document = _layout.Snapshots.TryLoad(info.FilePath, out var diagnostic);
        if (document is null)
        {
            MessageBox.Show(this, $"这份快照读不了：{diagnostic}", "Cubby · 还原快照", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var diff = SnapshotDiff.Describe(document, _manager.Monitors);
        var answer = MessageBox.Show(
            this,
            $"将把布局还原为 {info.CreatedAt:yyyy-MM-dd HH:mm:ss}（{info.Label}）的版本。{Environment.NewLine}{Environment.NewLine}" +
            $"{diff}{Environment.NewLine}{Environment.NewLine}还原前会自动为当前布局存一份快照。继续？",
            "Cubby · 还原快照",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            StatusText.Text = "已取消。";
            return;
        }

        Restore(document);
        Refresh();
        StatusText.Text = $"已还原到 {info.CreatedAt:yyyy-MM-dd HH:mm:ss}；还原前的布局已存为一份快照。";
    }

    /// <summary>还原动作本身：先给自己留后路，再替换布局并重建浮层。</summary>
    internal void Restore(LayoutDocument document)
    {
        _layout.CreateSnapshot("还原前");
        _layout.Apply(document);
        _manager.Rebuild("还原快照");
    }

    private void OnApplyKeep(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(KeepBox.Text.Trim(), out var keep) || keep < 0)
        {
            StatusText.Text = "保留份数要填 0 或正整数。";
            return;
        }

        _layout.SnapshotKeep = keep;
        Refresh();
        StatusText.Text = keep == 0 ? "保留份数已设为 0：不再自动清理，需自己删。" : $"保留份数已设为 {keep}。";
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_layout.Snapshots.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", _layout.Snapshots.Directory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"打不开目录：{ex.Message}";
        }
    }
}