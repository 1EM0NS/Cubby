using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cubby.Core.Model;

namespace Cubby.App;

/// <summary>
/// 把 Cubby **自己的窗口内容**离屏渲染成一张 PNG。
///
/// 为什么需要它：<see cref="PreviewRenderer"/> 只画盒子，而用户的抱怨里有两次都来自
/// 盒子之外的界面（右键菜单弹出系统白底、窗口顶着系统白标题栏）。
/// 窗口一旦在屏幕上闪一下就会打扰正在用电脑的人，这里同样全程**不创建可见窗口**：
/// 构造窗口 → 把它的内容摘下来 → 贴到一块离屏画布上量、排、渲染。
///
/// 注意它只能验证**内容区**：标题栏由 DWM 画在非客户区，不在 WPF 视觉树里，
/// 所以标题栏必须靠 <c>FluentChrome</c> 处理，这里看不见。
/// </summary>
internal static class WindowPreviewRenderer
{
    private const double Gap = 24;

    public static string Render(string outputDirectory)
    {
        var samples = new (string Name, Func<Window> Factory)[]
        {
            ("设置", () => new SettingsWindow(new StyleSettings(), _ => { })),
            ("同类软件共存", () => new CoexistWindow(Array.Empty<Cubby.Core.Platform.CoexistTool>())),
            ("崩溃报告", () => new CrashWindow(
                "preview",
                "预览：启动时的异常",
                "这是给设计复核用的样例文案，用来检查深色界面上的文字是否全部浅色。",
                null)),
            ("首次引导", () => new OnboardingWindow(() => "预览")),
            ("搜索", BuildSearchSample),
            ("确认对话框", () => new Window
            {
                Style = (Style)Application.Current.FindResource("Cubby.Window"),
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = CubbyDialog.BuildContent(
                    "这是确认对话框的样例文案，检查它是不是和窗口一套观感。",
                    confirm: true,
                    CubbyDialog.PrimaryButton("是"),
                    new Button { Content = "否", MinWidth = 88 }),
            }),
        };

        var cards = new List<Border>();

        foreach (var (name, factory) in samples)
        {
            try
            {
                cards.Add(BuildCard(name, factory));
            }
            catch (Exception ex)
            {
                // 一个窗口构造失败不该拖垮整张图——把它画成一张写着原因的卡片，
                // 至少能在图上看出来"这里少一块"。
                cards.Add(BuildCard(name, () => new Window
                {
                    Content = new TextBlock
                    {
                        Text = $"无法构造：{ex.GetType().Name} — {ex.Message}",
                        Foreground = Brushes.IndianRed,
                        Margin = new Thickness(12),
                        TextWrapping = TextWrapping.Wrap,
                    },
                }));
            }
        }

        var width = cards.Max(c => c.Width) + (Gap * 2);
        var height = cards.Sum(c => c.Height) + (Gap * (cards.Count + 1));

        var canvas = new Canvas { Width = width, Height = height, Background = Brushes.Black };

        var top = Gap;
        foreach (var card in cards)
        {
            Canvas.SetLeft(card, Gap);
            Canvas.SetTop(card, top);
            canvas.Children.Add(card);
            top += card.Height + Gap;
        }

        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width),
            (int)Math.Ceiling(height),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(canvas);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "window-preview.png");

        using var stream = File.Create(path);
        encoder.Save(stream);

        return path;
    }

    /// <summary>搜索窗口样例：真实索引 + 带各类扩展名的假路径（shell 按扩展名给真图标）。</summary>
    private static Window BuildSearchSample()
    {
        var search = new SearchService();
        var box = new Box("preview-search", "工作资料", new DipRect(0, 0, 372, 296))
        {
            Items =
            [
                new BoxItem("s1", "项目文档", @"C:\work\项目文档", ItemKind.Folder),
                new BoxItem("s2", "预算表.xlsx", @"C:\work\预算表.xlsx", ItemKind.File),
                new BoxItem("s3", "会议记录.docx", @"C:\work\会议记录.docx", ItemKind.File),
                new BoxItem("s4", "产品手册.pdf", @"C:\work\产品手册.pdf", ItemKind.File),
                new BoxItem("s5", "官网首页", "https://example.com", ItemKind.Url),
            ],
        };

        search.RebuildAll([box]);

        return new SearchWindow(box.Id, box.Name, search, _ => { });
    }

    private static Border BuildCard(string name, Func<Window> factory)
    {
        var window = factory();

        // 先取背景，再摘内容：内容一旦离开窗口就再也问不到它本来该是什么底色
        var background = window.Background;
        var width = double.IsNaN(window.Width) ? 560 : window.Width;
        var height = double.IsNaN(window.Height) ? 360 : window.Height;

        var content = window.Content as FrameworkElement;
        window.Content = null;

        // 内容一旦离开窗口就断了 Foreground 继承链，所有文字会退回默认黑色——
        // 那是预览工具的失真，不是真实观感（真窗口里 Cubby.Window 会给 Foreground）。
        // 这里把窗口的前景色补回宿主上，让预览如实反映继承结果。
        var foreground = window.Foreground;

        var caption = new TextBlock
        {
            Text = name,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB0)),
            FontSize = 12,
            Margin = new Thickness(12, 7, 0, 3),
        };

        var grid = new Grid();
        // Grid 不是 Control、没有 Foreground 属性，用 TextBlock 的附加属性把继承值挂上去
        grid.SetValue(TextBlock.ForegroundProperty, foreground);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(caption, 0);
        grid.Children.Add(caption);

        if (content is not null)
        {
            Grid.SetRow(content, 1);
            grid.Children.Add(content);
        }

        var card = new Border { Width = width, Background = background, Child = grid };

        // 没写死高度的窗口（如 SizeToContent 的对话框）按内容实际需要给高度，
        // 否则预览图上是一大块空白
        grid.Measure(new Size(width, double.PositiveInfinity));
        card.Height = double.IsNaN(window.Height)
            ? Math.Max(120, grid.DesiredSize.Height + 8)
            : window.Height + 26;

        return card;
    }
}
