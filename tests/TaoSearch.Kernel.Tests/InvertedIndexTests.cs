using TaoSearch.Kernel.FullText;
using Xunit;

namespace TaoSearch.Kernel.Tests;

public class InvertedIndexTests
{
    [Fact]
    public void Add_Lookup_Remove()
    {
        var idx = new InvertedIndex();
        idx.AddDocument(1, "hello world", DateTimeOffset.UtcNow);
        idx.AddDocument(2, "say hello", DateTimeOffset.UtcNow);

        Assert.Equal(2, idx.Lookup("hello").Count);
        Assert.Single(idx.Lookup("world"));

        idx.RemoveDocument(1);
        Assert.Single(idx.Lookup("hello"));
        Assert.Empty(idx.Lookup("world"));
    }

    [Fact]
    public void MultiTerm_And_Score()
    {
        var idx = new InvertedIndex();
        idx.AddDocument(1, "apple banana apple", DateTimeOffset.UtcNow);
        idx.AddDocument(2, "apple", DateTimeOffset.UtcNow);

        var hits = idx.LookupAll(["apple", "banana"]);
        Assert.Single(hits);
        Assert.Equal(1, hits[0].DocId);
        Assert.Equal(3, hits[0].Score); // apple×2 + banana×1
    }

    [Fact]
    public void ReAdd_UpdatesPositions()
    {
        var idx = new InvertedIndex();
        idx.AddDocument(1, "foo bar", DateTimeOffset.UtcNow);
        idx.AddDocument(1, "bar baz", DateTimeOffset.UtcNow); // 重解析覆盖

        Assert.Empty(idx.Lookup("foo"));
        Assert.Single(idx.Lookup("baz"));
        Assert.Equal(1, idx.DocCount); // 文档数仍为 1
    }

    [Fact]
    public void Positions_AvailableForPreview()
    {
        var idx = new InvertedIndex();
        idx.AddDocument(1, "find the keyword here keyword", DateTimeOffset.UtcNow);
        var pos = idx.GetPositions(1, "keyword");
        Assert.Equal(new[] { 9, 22 }, pos);
    }
}
