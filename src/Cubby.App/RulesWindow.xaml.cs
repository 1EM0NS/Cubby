using System.Diagnostics;
using System.IO;
using System.Windows;
using Cubby.Core.Rules;

namespace Cubby.App;

/// <summary>
/// 归类规则界面：看规则、预览、应用、撤销上次归类。
///
/// 规则本身**不在这里编辑**——它是 JSON 文件，用任意编辑器改完点「重新加载」即可。
/// 这样做换来的是：规则可导入导出、可版本管理、可被脚本生成，而界面代码只有几十行。
/// </summary>
public partial class RulesWindow : Window
{
    private readonly RuleService _rules;
    private readonly Func<IReadOnlyList<RuleCandidate>> _candidates;

    /// <param name="candidatesSource">
    /// 候选来源。默认是"桌面上还没入盒的条目"（也就是用户真正会归类的那批）；
    /// 验收会注入自己的临时目录，这样既覆盖了界面这条路径，又不动用户的桌面。
    /// </param>
    internal RulesWindow(RuleService rules, Func<IReadOnlyList<RuleCandidate>>? candidatesSource = null)
    {
        InitializeComponent();

        _rules = rules;
        _candidates = candidatesSource ?? rules.Candidates;
        Refresh();
    }

    /// <summary>供自动化验收读取：当前规则明细。</summary>
    internal IReadOnlyList<string> RuleLines => RuleList.Items.Cast<string>().ToList();

    internal string Status => StatusText.Text;

    internal string Plan => PlanText.Text;

    private void Refresh()
    {
        RuleList.ItemsSource = _rules.DescribeRules();
        PathText.Text = $"规则文件：{_rules.FilePath}";
        UndoButton.IsEnabled = _rules.CanUndo;
        StatusText.Text = _rules.Describe() + (_rules.CanUndo ? $"；可撤销：{_rules.LastUndoDescription}" : string.Empty);
    }

    /// <summary>供自动化验收驱动：走与按钮完全相同的路径。</summary>
    internal void Preview() => OnPreview(this, new RoutedEventArgs());

    internal void ApplyToDesktop() => OnApply(this, new RoutedEventArgs());

    internal void UndoLast() => OnUndo(this, new RoutedEventArgs());

    internal void ReloadRules() => OnReload(this, new RoutedEventArgs());

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        _rules.EnsureOnDisk();
        var plan = _rules.Preview(_candidates());

        PlanText.Text = _rules.DescribePlan(plan);
        StatusText.Text = $"预览：命中 {plan.Matches.Count} 条，未命中 {plan.Unmatched.Count} 条" +
                          (plan.MissingTargets.Count > 0 ? $"，{plan.MissingTargets.Count} 个目标盒子不存在" : string.Empty) +
                          "。预览不会改动任何东西。";
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var plan = _rules.Apply(_candidates());

        PlanText.Text = _rules.DescribePlan(plan);
        Refresh();

        StatusText.Text = plan.Matches.Count == 0
            ? "没有可归类的条目（桌面上的东西都已经在盒子里，或没有规则命中）。"
            : $"已归类 {plan.Matches.Count} 条到对应的盒子；随时可以点「撤销上次归类」还原。";
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        var restored = _rules.Undo();

        Refresh();
        PlanText.Text = restored == 0
            ? "没有可撤销的归类。"
            : $"已撤销上一次归类，恢复了 {restored} 个盒子的条目清单。磁盘上的文件一直没被动过。";
        StatusText.Text = restored == 0 ? "没有可撤销的归类。" : $"已撤销 {restored} 个盒子的归类。";
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        _rules.Reload();
        Refresh();
        StatusText.Text = $"已重新加载规则。{_rules.Describe()}";
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        try
        {
            _rules.EnsureOnDisk();
            Process.Start(new ProcessStartInfo(_rules.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"打不开规则文件：{ex.Message}";
        }
    }
}