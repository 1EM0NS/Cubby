using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 管理「每台显示器一个浮层窗口」，把布局分配到各屏，并处理显示器变化后的重建。
///
/// 当前重建策略是**整体重建**：显示器数量/分辨率/DPI 变化时，枚举当前显示器、
/// 重新规划盒子归属、销毁旧窗口并按新显示器创建新窗口。
/// 代价是切换瞬间有一次闪烁（事件本身很稀有）；增量复用窗口留待后续优化。
/// </summary>
internal sealed class OverlayManager : IBoxChangeSink
{
    private readonly LayoutService _layout;
    private readonly List<OverlayWindow> _windows = [];

    /// <summary>盒子是否可见。重建浮层后要按这个状态恢复，否则显示器一变盒子就自己冒出来。</summary>
    private bool _boxesVisible = true;

    /// <summary>映射文件夹的同步器：目录一变就把新内容推给盒子。</summary>
    private readonly MappedFolderService _mapping;

    /// <summary>每个盒子一份搜索索引（事件驱动重建，空闲不跑）。</summary>
    private readonly SearchService _search = new();

    /// <summary>每个盒子最多开一个搜索窗口。</summary>
    private readonly Dictionary<string, SearchWindow> _searchWindows = new(StringComparer.Ordinal);

    /// <summary>自动归类的规则宿主（规则在 Core 里算，这里只落引用）。</summary>
    private readonly RuleService _rules;

    /// <summary>桌面图标显隐的唯一入口（托盘 / 热键 / 盒子按钮都走它）。</summary>
    private readonly DesktopIconController _desktopIcons;

    public OverlayManager(LayoutService layout)
    {
        _layout = layout;
        _mapping = new MappedFolderService(layout, ApplyBoxUpdate);
        _rules = new RuleService(layout, ApplyBoxUpdate, () => _layout.Boxes.FirstOrDefault()?.Id);
        _desktopIcons = new DesktopIconController(layout);
    }

    /// <summary>桌面图标控制器。</summary>
    public DesktopIconController DesktopIconToggle => _desktopIcons;

    /// <summary>归类规则（界面与验收都用它）。</summary>
    public RuleService Rules => _rules;

    /// <summary>搜索索引状态（诊断面板与状态报告）。</summary>
    public string SearchDescription => _search.Describe();

    public SearchService Search => _search;

    /// <summary>映射同步的诊断（供诊断面板与状态报告）。</summary>
    public IReadOnlyList<string> MappingDiagnostics => _mapping.Diagnostics;

    public int MappingWatcherCount => _mapping.WatcherCount;

    /// <summary>映射同步累计重扫次数与溢出重建次数（诊断/验收）。</summary>
    public int MappingRescanCount => _mapping.TotalRescanCount;

    public int MappingOverflowCount => _mapping.TotalOverflowCount;

    public IReadOnlyList<OverlayWindow> Windows => _windows;

    public IReadOnlyList<MonitorSurface> Monitors { get; private set; } = [];

    public IReadOnlyList<MonitorPlan> Plans { get; private set; } = [];

    public int RebuildCount { get; private set; }

    public string LastRebuildReason { get; private set; } = "(尚未发生)";

    public string? LastRebuildAt { get; private set; }

    /// <summary>最近一次打开条目失败的原因（正常为 null）。</summary>
    public string? LastOpenError { get; private set; }

    /// <summary>最近一次桌面图标吸附的结果摘要（正常为 null）。</summary>
    public string? LastAdoptSummary { get; private set; }

    public OverlayWindow? PrimaryWindow =>
        _windows.FirstOrDefault(w => w.Surface?.IsPrimary == true) ?? _windows.FirstOrDefault();

    public void Start() => Rebuild("启动");

    /// <summary>重新枚举显示器并按布局重建浮层。可由界面按钮手动触发，也会被显示变化事件触发。</summary>
    public void Rebuild(string reason) => Rebuild(reason, MonitorSurfaces.Enumerate());

    /// <summary>
    /// 用给定的显示器集合重建。生产路径始终传系统枚举结果，这个重载是为**验收**准备的：
    /// 要验证「拔掉副屏」「副屏回来」这类拓扑变化，正确做法不是去改系统的显示设置
    /// （那会动到用户的桌面），而是只传入系统枚举结果的**子集**来复刻那一刻的真实拓扑。
    /// 也就是说传进来的必须是真实存在的显示器——窗口仍然是真的，只是少了或多了几块屏。
    /// </summary>
    public void Rebuild(string reason, IReadOnlyList<MonitorSurface> monitors)
    {
        CloseAll();

        Monitors = monitors;
        _layout.EnsureDefaults(Monitors);
        _layout.UpdateMonitors(Monitors);

        Plans = OverlayPlanner.Plan(Monitors, _layout.Boxes);

        // 先把归属与坐标的修正落盘，再建窗口：窗口拿到的是修正后的盒子，
        // 而落盘必须发生在建窗口之前，否则中途一旦出错，界面与文件就会不一致
        PersistPlacement();

        foreach (var plan in Plans)
        {
            var window = new OverlayWindow(
                plan.Monitor,
                plan.Boxes.Select(planned => planned.Box).ToList(),
                _layout.Style,
                this);

            window.DisplayChanged += OnWindowDisplayChanged;
            _windows.Add(window);
        }

        foreach (var window in _windows)
        {
            // Show 之后才有 HWND，窗口在 OnSourceInitialized 里完成浮层初始化
            window.Show();
            window.Host?.SetVisible(_boxesVisible);
        }

        RebuildCount++;
        LastRebuildReason = reason;
        LastRebuildAt = DateTime.Now.ToString("HH:mm:ss.fff");

        // 重建后按新布局重挂映射监视器（盒子可能被挪到别的显示器，但映射关系不变）
        _mapping.Sync(_layout.Boxes);

        // 索引跟着布局一起重建：换显示器、启动、还原快照都走这里
        _search.RebuildAll(_layout.Boxes);

        AppendRebuildLog(reason);
    }

    /// <summary>
    /// 把这一轮的「盒子归哪块屏」「坐标要不要挪」写回布局。
    ///
    /// 写回策略里有一条是刻意的取舍：**原显示器不在了（Fallback）时只挪坐标、不改归属**。
    /// 因为「枚举不到某块屏」既可能是它真的被拔了，也可能只是它睡着了/正在重排——
    /// 若立刻把归属改成主屏，屏一回来盒子就永远留在主屏了，而那正是 issue #5
    /// 「副屏唤醒后盒子不跑到主屏」要防的事。
    /// 归属真正改掉的时机是**用户自己动手拖它**（见 <see cref="OnBoxChanged"/>）：
    /// 那是唯一能确定"这就是我要的位置"的信号。
    /// </summary>
    private void PersistPlacement()
    {
        foreach (var plan in Plans)
        {
            foreach (var planned in plan.Boxes)
            {
                var current = _layout.Boxes.FirstOrDefault(b => b.Id == planned.Box.Id);
                if (current is null)
                {
                    continue;
                }

                // 原屏已不在：只采纳坐标修正，归属与记录的屏幕尺寸都保持不动（等它回来）
                var updated = planned.Kind == MonitorMatchKind.Fallback
                    ? current with { Bounds = planned.Box.Bounds }
                    : current with
                    {
                        Bounds = planned.Box.Bounds,
                        MonitorId = plan.Monitor.Id,
                        MonitorWidth = plan.Monitor.Bounds.Width,
                        MonitorHeight = plan.Monitor.Bounds.Height,
                    };

                if (updated != current)
                {
                    _layout.UpdateBox(updated);
                }
            }
        }
    }

    public void Stop()
    {
        _mapping.Dispose();
        CloseAll();
        _layout.SaveNow();
    }

    /// <summary>把某个盒子的最新模型推给它所在的浮层（映射同步、外部修改都走这里）。</summary>
    public void ApplyBoxUpdate(Box box)
    {
        foreach (var window in _windows)
        {
            window.ApplyBoxUpdate(box);
        }

        // 条目变了就只重建这一个盒子的索引（不整表重建，避免无关盒子跟着抖）
        _search.RebuildBox(box);
    }

    /// <summary>让某个映射盒子强制整体重扫（监视器报错 / 缓冲区溢出的路径）。</summary>
    public bool RequestMappingRescan(string boxId, string reason) =>
        _mapping.RequestRescan(boxId, reason);

    /// <summary>盒子当前是否显示。</summary>
    public bool BoxesVisible => _boxesVisible;

    /// <summary>显示 / 隐藏所有盒子（托盘菜单）。隐藏期间浮层不参与命中测试。</summary>
    public void SetBoxesVisible(bool visible)
    {
        _boxesVisible = visible;

        foreach (var window in _windows)
        {
            window.Host?.SetVisible(visible);
        }
    }

    /// <summary>把全局样式套用到所有浮层（设置窗口拖动滑块时调用）。</summary>
    public void ApplyStyle(StyleSettings style)
    {
        _layout.UpdateStyle(style);

        foreach (var window in _windows)
        {
            window.ChangeStyle(_layout.Style);
        }
    }

    /// <summary>
    /// 首次引导的「创建第一个盒子」：保证**主屏上有一个盒子**，并让它立刻显示出来。
    ///
    /// 已经有盒子就不重复建（幂等）——真实首次运行其实已经由 <see cref="Rebuild"/> 里的
    /// <see cref="LayoutService.EnsureDefaults"/> 建好了一个，这里主要是兜住"用户把盒子全删了"的情况，
    /// 所以返回的是一句**如实的说明**而不是硬造一个盒子出来。
    /// 只动 Cubby 自己的布局文件，不碰用户文件（P4）。
    /// </summary>
    public string CreateFirstBoxOnPrimary()
    {
        var primary = Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
        if (primary is null)
        {
            return "没有枚举到任何显示器，无法创建盒子。";
        }

        var created = _layout.CreateDefaultBox(primary);

        // 新建 / 已存在都要重建一次：引导窗打开的这几秒里浮层可能还没把盒子画出来
        Rebuild("首次引导：创建第一个盒子");

        // 盒子若被用户在托盘菜单里关过（SetBoxesVisible(false)），刚点了「创建盒子」却看不到会以为没生效
        SetBoxesVisible(true);

        return created is null
            ? $"主屏（{primary.Id}）上已经有盒子了，直接用它：{_layout.Boxes.Count} 个盒子。"
            : $"已在主屏（{primary.Id}）创建盒子「{created.Name}」（{created.Bounds.Width:0}×{created.Bounds.Height:0} DIP），" +
              $"现有 {_layout.Boxes.Count} 个盒子。";
    }

    /// <summary>某个窗口句柄是否属于我们的任一浮层（多屏下不能只比一个句柄）。</summary>
    public bool IsOurOverlay(nint hwnd) =>
        hwnd != 0 && _windows.Any(w => DesktopProbe.BelongsTo(hwnd, w.Host?.Handle ?? 0));

    // ---- IBoxChangeSink ----

    public void OnBoxChanged(Box box)
    {
        // 用户动手了（拖动 / 改尺寸）——这时把盒子归属到它**实际所在的**那块屏。
        // 这是唯一能确定"它就该在这块屏上"的信号；自动回退时不动归属，
        // 否则副屏睡一觉回来，盒子就被永久留在主屏了（见 PersistPlacement）。
        var pinned = PinToActualMonitor(box);
        _layout.UpdateBox(pinned);

        // 条目可能变了（拖入 / 移出 / 重命名），索引跟上
        _search.RebuildBox(pinned);

        // 映射关系可能刚被建立或解除，让同步器跟上
        _mapping.Sync(_layout.Boxes);
    }

    /// <summary>把盒子归属到当前承载它的那块屏；没找到（比如盒子刚被删）就原样返回。</summary>
    private Box PinToActualMonitor(Box box)
    {
        foreach (var window in _windows)
        {
            var surface = window.Surface;
            if (surface is null || !window.Boxes.Any(b => b.Id == box.Id))
            {
                continue;
            }

            return box with
            {
                MonitorId = surface.Id,
                MonitorWidth = surface.Bounds.Width,
                MonitorHeight = surface.Bounds.Height,
            };
        }

        return box;
    }

    public void OnItemOpen(BoxItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.TargetPath) { UseShellExecute = true });
            LastOpenError = null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            LastOpenError = $"{item.DisplayName}: {ex.Message}";
            MessageBox.Show(
                $"打不开「{item.DisplayName}」：{ex.Message}",
                "Cubby",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>在资源管理器中定位条目。只读操作，不改变磁盘上的任何东西。</summary>
    public void OnItemReveal(BoxItem item)
    {
        try
        {
            // /select 需要目标已存在；不存在时退回打开其所在目录
            var target = File.Exists(item.TargetPath) || Directory.Exists(item.TargetPath)
                ? item.TargetPath
                : Path.GetDirectoryName(item.TargetPath);

            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            LastOpenError = null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            LastOpenError = $"{item.DisplayName}: {ex.Message}";
        }
    }

    public void OnAdoptReport(string summary) => LastAdoptSummary = summary;

    /// <summary>盒子标题栏按钮 / 全局热键都汇到这里，保证"藏了要记标记"这件事只写一次。</summary>
    public void OnDesktopIconToggleRequested() => _desktopIcons.Toggle();

    /// <summary>打开某个盒子的搜索窗口。同一个盒子只开一个，重复点就激活已有的那个。</summary>
    public void OnSearchRequested(Box box)
    {
        if (_searchWindows.TryGetValue(box.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }

        var window = new SearchWindow(box.Id, box.Name, _search, OnItemOpen);
        window.Closed += (_, _) => _searchWindows.Remove(box.Id);
        _searchWindows[box.Id] = window;
        window.Show();
    }

    /// <summary>某个盒子当前的搜索窗口（自动化验收用；没开则为 null）。</summary>
    internal SearchWindow? SearchWindowOf(string boxId) =>
        _searchWindows.TryGetValue(boxId, out var window) ? window : null;

    /// <summary>诊断用的显示器摘要。</summary>
    public string DescribeMonitors()
    {
        if (Monitors.Count == 0)
        {
            return "（未枚举到显示器）";
        }

        return string.Join(
            Environment.NewLine,
            Monitors.Select(m =>
                $"  {m.Id,-16} {m.Bounds}  像素 {m.Bounds.Width}×{m.Bounds.Height}  " +
                $"DPI 缩放 {m.DpiScale:0.##}{(m.IsPrimary ? "  [主屏]" : string.Empty)}"));
    }

    private void OnWindowDisplayChanged(object? sender, int message)
    {
        // 显示器变化是在窗口过程里收到的，此时不能立刻销毁窗口（还在处理这条消息），
        // 因此派发到消息队列末尾再重建。
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => Rebuild(message switch
            {
                0x007E => "WM_DISPLAYCHANGE",
                0x02E0 => "WM_DPICHANGED",
                _ => $"消息 0x{message:X4}",
            })));
    }

    /// <summary>
    /// 把每次重建追加到诊断日志。既方便现场排查，也让「显示器变化真的被处理了」这件事可被自动取证。
    /// （M3 做崩溃日志时会统一搬到 %AppData%。）
    /// </summary>
    private void AppendRebuildLog(string reason)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(directory);

            var notes = Plans
                .SelectMany(plan => plan.Notes.Select(note => $"{plan.Monitor.Id}: {note}"))
                .ToList();

            var line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {reason,-22} | " +
                $"显示器 {Monitors.Count} 台 | 浮层窗口 {_windows.Count} 个 | " +
                $"盒子 {_layout.Boxes.Count} 个 | " +
                string.Join(" ; ", Monitors.Select(m => $"{m.Id} {m.Bounds.Width}x{m.Bounds.Height}@{m.DpiScale:0.##}")) +
                (notes.Count > 0 ? " || " + string.Join(" ; ", notes) : string.Empty) +
                Environment.NewLine;

            File.AppendAllText(
                Path.Combine(directory, "overlay-rebuilds.log"),
                line,
                new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // 日志写不进去不影响主流程
        }
    }

    private void CloseAll()
    {
        foreach (var window in _windows)
        {
            window.DisplayChanged -= OnWindowDisplayChanged;
            window.Close();
        }

        _windows.Clear();
    }
}