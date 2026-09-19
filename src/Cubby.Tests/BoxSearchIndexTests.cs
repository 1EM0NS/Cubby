using System.Diagnostics;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Xunit;

namespace Cubby.Tests;

/// <summary>
/// 搜索索引。除了功能，还要证明"500 条规模下够快"与"命中优先级稳定"——
/// 前者是验收标准，后者决定了用户输入时看到的第一条是不是他想要的。
/// </summary>
public sealed class BoxSearchIndexTests
{
    private static BoxSearchIndex IndexOf(params string[] names)
    {
        var index = new BoxSearchIndex();
        index.Rebuild("box", names
            .Select((name, i) => new BoxItem($"id-{i}", name, $@"C:\dir\{name}", ItemKind.File))
            .ToList());
        return index;
    }

    [Fact]
    public void 空查询返回全部条目()
    {
        var index = IndexOf("a.txt", "b.txt", "c.txt");

        Assert.Equal(3, index.Query(null).Count);
        Assert.Equal(3, index.Query("   ").Count);
    }

    [Fact]
    public void 按展示名包含匹配()
    {
        var index = IndexOf("report.txt", "note.txt", "图片.png");

        var hits = index.Query("report");

        Assert.Single(hits);
        Assert.Equal("report.txt", hits[0].DisplayName);
    }

    [Fact]
    public void 忽略大小写()
    {
        var index = IndexOf("Report.TXT");

        Assert.Single(index.Query("REPORT"));
    }

    [Fact]
    public void 展示名前缀命中排在包含命中之前()
    {
        var index = IndexOf("我的report.txt", "report-主文件.txt");

        var hits = index.Query("report");

        Assert.Equal(2, hits.Count);
        Assert.Equal("report-主文件.txt", hits[0].DisplayName);
    }

    [Fact]
    public void 展示名命中排在路径命中之前()
    {
        var index = new BoxSearchIndex();
        index.Rebuild("box",
        [
            new BoxItem("a", "普通文件.txt", @"C:\report\普通文件.txt", ItemKind.File),
            new BoxItem("b", "report-笔记.txt", @"C:\其它\report-笔记.txt", ItemKind.File),
        ]);

        var hits = index.Query("report");

        Assert.Equal(2, hits.Count);
        Assert.Equal("report-笔记.txt", hits[0].DisplayName);
    }

    [Fact]
    public void 路径里的关键字也能搜到()
    {
        var index = new BoxSearchIndex();
        index.Rebuild("box", [new BoxItem("a", "文档.docx", @"C:\工作\季度汇报\文档.docx", ItemKind.File)]);

        Assert.Single(index.Query("季度汇报"));
    }

    [Fact]
    public void 结果数量受上限限制()
    {
        var index = IndexOf(Enumerable.Range(0, 50).Select(i => $"file-{i:00}.txt").ToArray());

        Assert.Equal(10, index.Query("file", limit: 10).Count);
        Assert.Equal(50, index.Query("file", limit: 500).Count);
    }

    [Fact]
    public void 重建会替换旧内容并计数()
    {
        var index = IndexOf("旧文件.txt");
        Assert.Equal(1, index.BuildCount);

        index.Rebuild("box", [new BoxItem("n", "新文件.txt", @"C:\新文件.txt", ItemKind.File)]);

        Assert.Equal(2, index.BuildCount);
        Assert.Equal(1, index.Count);
        Assert.Empty(index.Query("旧文件"));
        Assert.Single(index.Query("新文件"));
    }

    [Fact]
    public void 五百条规模下单次查询远快于一百毫秒()
    {
        var index = IndexOf(Enumerable.Range(0, 500)
            .Select(i => i % 5 == 0 ? $"report-{i:000}.txt" : $"note-{i:000}.txt")
            .ToArray());

        var slowest = 0.0;
        for (var i = 0; i < 50; i++)
        {
            var watch = Stopwatch.StartNew();
            var hits = index.Query("report");
            watch.Stop();

            Assert.Equal(100, hits.Count);
            slowest = Math.Max(slowest, watch.Elapsed.TotalMilliseconds);
        }

        // 留足余量：验收标准是 100ms，这里断言 20ms，真出问题时不会因为机器慢而误判
        Assert.True(slowest < 20, $"最慢一次查询用了 {slowest:0.00} ms");
    }

    [Fact]
    public void 同一个名字的多个条目顺序稳定()
    {
        var index = IndexOf("dup.txt", "dup.txt", "dup.txt");

        var first = index.Query("dup").Select(i => i.Id).ToList();
        var second = index.Query("dup").Select(i => i.Id).ToList();

        Assert.Equal(first, second);
    }
}