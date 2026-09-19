using System.Diagnostics;
using System.IO;
using System.Windows;
using Cubby.Core.Diagnostics;

namespace Cubby.App;

/// <summary>
/// 崩溃提示窗（issue #39）。
///
/// 不做成 <c>MessageBox</c> 的原因和 <see cref="CoexistWindow"/> 一样：这里要给出**可操作的出口**
/// ——把日志路径摆出来、能一键复制、能直接打开日志目录。只告诉用户"出错了"等于什么也没说。
///
/// 它必须能被程序化驱动（自动化验收要断言"复制路径"与"打开日志目录"真的接上了），
/// 因此两个动作都做成 internal 方法，验收走的是与用户点击同一个处理函数。
/// </summary>
public partial class CrashWindow : Window
{
    private readonly string _logPath;

    internal CrashWindow(string source, string headline, string summary, string? logPath)
    {
        InitializeComponent();

        _logPath = logPath ?? string.Empty;

        SourceText.Text = $"来源：{source}";
        HeadlineText.Text = headline;
        SummaryBox.Text = summary;
        LogPathBox.Text = _logPath.Length == 0 ? "（日志写入失败，未能留下文件）" : _logPath;

        CopyButton.IsEnabled = _logPath.Length > 0;
        OpenFolderButton.IsEnabled = _logPath.Length > 0;
    }

    /// <summary>界面上真实显示出来的日志路径（供自动化验收）。</summary>
    internal string ShownLogPath => LogPathBox.Text;

    /// <summary>界面上真实显示出来的异常摘要（供自动化验收）。</summary>
    internal string ShownSummary => SummaryBox.Text;

    /// <summary>程序化点击「复制路径」（与用户点击走同一个处理函数）。</summary>
    internal void CopyPath() => OnCopyPath(this, new RoutedEventArgs());

    /// <summary>程序化点击「打开日志目录」。</summary>
    internal void OpenFolder() => OnOpenFolder(this, new RoutedEventArgs());

    /// <summary>程序化点击「知道了」。</summary>
    internal void Dismiss() => OnClose(this, new RoutedEventArgs());

    /// <summary>状态提示文字（复制成功 / 打开失败等）。</summary>
    internal string? Status => string.IsNullOrEmpty(StatusText.Text) ? null : StatusText.Text;

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_logPath);
            ShowStatus("已复制日志路径。");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // 剪贴板被别的进程占着时会失败；退一步把路径选中，用户自己按 Ctrl+C
            LogPathBox.SelectAll();
            LogPathBox.Focus();
            ShowStatus($"复制失败（{ex.GetType().Name}），路径已选中，请按 Ctrl+C。");
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logPath);
            if (string.IsNullOrEmpty(directory))
            {
                ShowStatus("日志路径无效，无法定位目录。");
                return;
            }

            // 用 /select 直接选中日志文件：目录里可能有很多天，省得用户自己找
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_logPath}\"")
            {
                UseShellExecute = true,
            });

            ShowStatus("已在资源管理器中定位日志文件。");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ShowStatus($"打不开资源管理器：{ex.Message}");
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }
}
