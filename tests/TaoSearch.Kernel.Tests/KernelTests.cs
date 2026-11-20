using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.Query;
using Xunit;

namespace TaoSearch.Kernel.Tests;

public class QueryParserTests
{
    [Fact]
    public void PlainTerm_ParsesAsTerm()
    {
        var q = QueryParser.Parse("发票");
        Assert.Equal("发票", q.Term);
        Assert.False(q.IsRegex);
        Assert.False(q.CaseSensitive);
    }

    [Fact]
    public void RegexPrefix_SetsIsRegex()
    {
        var q = QueryParser.Parse("regex:^report_\\d{4}");
        Assert.True(q.IsRegex);
        Assert.Equal("^report_\\d{4}", q.Term);
    }

    [Fact]
    public void PathAndExt_Combine()
    {
        var q = QueryParser.Parse("path:D:\\docs ext:docx;pdf 报价");
        Assert.Equal("D:\\docs", q.Path!.Prefix);
        Assert.Equal(new[] { ".docx", ".pdf" }, q.Extensions);
        Assert.Equal("报价", q.Term);
    }

    [Fact]
    public void SizeRange_Parses()
    {
        var (lo, hi) = QueryParser.ParseSize("1mb-100mb");
        Assert.Equal(1024 * 1024, lo);
        Assert.Equal(100 * 1024 * 1024, hi);
    }

    [Fact]
    public void SizeGt_Parses()
    {
        var (lo, hi) = QueryParser.ParseSize(">10kb");
        Assert.Equal(10 * 1024, lo);
        Assert.Null(hi);
    }

    [Fact]
    public void EmptyInput_Throws()
    {
        Assert.Throws<QuerySyntaxException>(() => QueryParser.Parse("  "));
    }

    [Fact]
    public void UnknownPrefix_TreatedAsTerm()
    {
        var q = QueryParser.Parse("foo:bar");
        Assert.Equal("foo:bar", q.Term);
    }
}

public class TokenizerTests
{
    [Fact]
    public void English_WordsLowercased()
    {
        var tokens = TaoSearch.Kernel.FullText.Tokenizer.Tokenize("Hello World").Select(t => t.Value).ToArray();
        Assert.Equal(new[] { "hello", "world" }, tokens);
    }

    [Fact]
    public void Chinese_Bigram()
    {
        var tokens = TaoSearch.Kernel.FullText.Tokenizer.Tokenize("发票明细").Select(t => t.Value).ToArray();
        Assert.Equal(new[] { "发票", "票明", "明细" }, tokens);
    }

    [Fact]
    public void Mixed_Text()
    {
        var tokens = TaoSearch.Kernel.FullText.Tokenizer.Tokenize("2026年Q4报告").Select(t => t.Value).ToArray();
        Assert.Contains("2026", tokens);
        Assert.Contains("q4", tokens);
        Assert.Contains("报告", tokens);
    }
}

public class NameIndexTests
{
    [Fact]
    public void Upsert_Find_BuildPath()
    {
        var idx = new NameIndex();
        idx.Upsert(1, 0, "C:", 0, DateTimeOffset.UtcNow);
        idx.Upsert(2, 1, "Users", 0, DateTimeOffset.UtcNow);
        idx.Upsert(3, 2, "test.txt", 100, DateTimeOffset.UtcNow);

        Assert.True(idx.TryGet(3, out var r));
        Assert.Equal("test.txt", r.FileName);
        Assert.Equal(@"C:\Users\test.txt", idx.BuildPath(3));
    }

    [Fact]
    public void Rename_Overwrites()
    {
        var idx = new NameIndex();
        idx.Upsert(1, 0, "C:", 0, DateTimeOffset.UtcNow);
        idx.Upsert(2, 1, "old.txt", 1, DateTimeOffset.UtcNow);
        idx.Upsert(2, 1, "new.txt", 1, DateTimeOffset.UtcNow); // 同引用号重命名

        Assert.True(idx.TryGet(2, out var r));
        Assert.Equal("new.txt", r.FileName);
    }

    [Fact]
    public void Remove_ThenSearchMisses()
    {
        var idx = new NameIndex();
        idx.Upsert(1, 0, "C:", 0, DateTimeOffset.UtcNow);
        idx.Upsert(2, 1, "gone.txt", 1, DateTimeOffset.UtcNow);
        idx.Remove(2);

        Assert.Empty(idx.Search("gone"));
        Assert.False(idx.TryGet(2, out _));
    }

    [Fact]
    public void Search_Substring_Many()
    {
        var idx = new NameIndex();
        for (int i = 0; i < 5000; i++)
            idx.Upsert(i + 10, 0, $"report_{i:D4}.docx", i, DateTimeOffset.UtcNow);

        Assert.Equal(5000, idx.Search("report").Count());
        Assert.Single(idx.Search("0442")); // 仅 report_0442 命中
    }

    [Fact]
    public void HashGrows_PastLoadFactor()
    {
        var idx = new NameIndex();
        for (long i = 1; i <= 50_000; i++)
            idx.Upsert(i, 0, $"f{i}", 0, DateTimeOffset.UtcNow);
        Assert.Equal(50_000, idx.Count);
        Assert.True(idx.TryGet(49_999, out _));
    }
}
