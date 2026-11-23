using TaoSearch.App.Core;
using Xunit;

namespace TaoSearch.App.Core.Tests;

/// <summary>LaunchArgs 全分支：--quick / --open-index / --search 各路径。</summary>
public class LaunchArgsTests
{
    [Fact]
    public void HasQuick_True_WhenPresent()
    {
        Assert.True(LaunchArgs.HasQuick(["--quick"]));
    }

    [Fact]
    public void HasQuick_False_WhenAbsent()
    {
        Assert.False(LaunchArgs.HasQuick(["--search", "x"]));
        Assert.False(LaunchArgs.HasQuick([]));
    }

    [Fact]
    public void HasOpenIndex_True_WhenPresent()
    {
        Assert.True(LaunchArgs.HasOpenIndex(["--open-index"]));
    }

    [Fact]
    public void HasOpenIndex_False_WhenAbsent()
    {
        Assert.False(LaunchArgs.HasOpenIndex(["--quick"]));
        Assert.False(LaunchArgs.HasOpenIndex([]));
    }

    [Fact]
    public void TryGetSearch_False_WhenNoSearchFlag()
    {
        Assert.False(LaunchArgs.TryGetSearch(["--quick"], out _));
    }

    [Fact]
    public void TryGetSearch_False_WhenSearchIsLastToken()
    {
        Assert.False(LaunchArgs.TryGetSearch(["--search"], out _));
    }

    [Fact]
    public void TryGetSearch_True_SingleWord()
    {
        Assert.True(LaunchArgs.TryGetSearch(["--search", "发票"], out var kw));
        Assert.Equal("发票", kw);
    }

    [Fact]
    public void TryGetSearch_True_MultiWord_Joined()
    {
        Assert.True(LaunchArgs.TryGetSearch(["--search", "年度", "总结"], out var kw));
        Assert.Equal("年度 总结", kw);
    }

    [Fact]
    public void TryGetSearch_False_WhenKeywordWhitespaceOnly()
    {
        Assert.False(LaunchArgs.TryGetSearch(["--search", "   "], out _));
    }

    [Fact]
    public void TryGetSearch_True_TrimsKeyword()
    {
        Assert.True(LaunchArgs.TryGetSearch(["--search", "  abc  "], out var kw));
        Assert.Equal("abc", kw);
    }
}
