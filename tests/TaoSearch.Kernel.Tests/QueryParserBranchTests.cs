using TaoSearch.Kernel.Query;
using Xunit;

namespace TaoSearch.Kernel.Tests;

/// <summary>
/// 分支覆盖补测（测试规范九.11）：QueryParser.ParseSize / ParseDateRange 全分支。
/// </summary>
public sealed class QueryParserBranchTests
{
    // ---- ParseSize：区间、比较符、单位、非法输入 ----

    [Theory]
    [InlineData("5", 5L, null)]
    [InlineData("5b", 5L, null)]
    [InlineData("5k", 5L * 1024, null)]
    [InlineData("5kb", 5L * 1024, null)]
    [InlineData("5m", 5L * 1024 * 1024, null)]
    [InlineData("5mb", 5L * 1024 * 1024, null)]
    [InlineData("5g", 5L * 1024 * 1024 * 1024, null)]
    [InlineData("5gb", 5L * 1024 * 1024 * 1024, null)]
    [InlineData("1.5mb", 1572864L, null)]
    public void ParseSize_UnitVariants_LowerBound(string arg, long? min, long? max)
        => Assert.Equal((min, max), QueryParser.ParseSize(arg));

    [Fact]
    public void ParseSize_LessThan_SetsUpperOnly()
    {
        var (min, max) = QueryParser.ParseSize("<10kb");
        Assert.Null(min);
        Assert.Equal(10 * 1024, max);
    }

    [Fact]
    public void ParseSize_GreaterThan_SetsLowerOnly()
    {
        var (min, max) = QueryParser.ParseSize(">2");
        Assert.Equal(2, min);
        Assert.Null(max);
    }

    [Fact]
    public void ParseSize_Range_SetsBoth()
    {
        var (min, max) = QueryParser.ParseSize("1mb-100mb");
        Assert.Equal(1024 * 1024, min);
        Assert.Equal(100L * 1024 * 1024, max);
    }

    [Fact]
    public void ParseSize_OpenRange_OnlyLower()
    {
        var (min, max) = QueryParser.ParseSize("1-");
        Assert.Equal(1, min);
        Assert.Null(max);
    }

    [Fact]
    public void ParseSize_EmptyParts_NoBounds()
    {
        var (min, max) = QueryParser.ParseSize("-");
        Assert.Null(min);
        Assert.Null(max);
    }

    [Fact]
    public void ParseSize_ThreeSegments_Throws()
        => Assert.Throws<QuerySyntaxException>(() => QueryParser.ParseSize("1-2-3"));

    [Fact]
    public void ParseSize_UnparsableNumber_Throws()
        => Assert.Throws<QuerySyntaxException>(() => QueryParser.ParseSize("abc"));

    [Fact]
    public void ParseSize_UnknownUnit_Throws()
        => Assert.Throws<QuerySyntaxException>(() => QueryParser.ParseSize("5zb"));

    [Fact]
    public void Parse_SizePrefix_WiresBoundsIntoQuery()
    {
        var q = QueryParser.Parse("size:>2 report");
        Assert.Equal(2, q.MinSize);
        Assert.Null(q.MaxSize);
        Assert.Equal("report", q.Term);
    }

    // ---- ParseDateRange：单日、区间、非法段 ----

    [Fact]
    public void ParseDateRange_SingleDay_ThreeSegments()
    {
        var r = QueryParser.ParseDateRange("2026-11-01");
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), r.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), r.To);
    }

    [Fact]
    public void ParseDateRange_SingleDay_FourSegments_SecondDateWellFormed()
    {
        // 4 段且第二段可解析：按单日处理（现行为），不进 6 段区间分支
        var r = QueryParser.ParseDateRange("2026-11-01-05");
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), r.From);
    }

    [Fact]
    public void ParseDateRange_FourSegments_SecondMonthInvalid_NoCrash()
    {
        var r = QueryParser.ParseDateRange("2026-xx-01-05");
        Assert.Null(r.From);
        Assert.Null(r.To);
    }

    [Fact]
    public void ParseDateRange_ThreeSegments_NonNumeric_NullRange()
    {
        var r = QueryParser.ParseDateRange("xxxx-yy-zz");
        Assert.Null(r.From);
        Assert.Null(r.To);
    }

    [Fact]
    public void ParseDateRange_SixSegments_BothDates()
    {
        var r = QueryParser.ParseDateRange("2026-11-01-2026-11-05");
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), r.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), r.To);
    }

    [Fact]
    public void ParseDateRange_SixSegments_AllInvalid_NullRange()
    {
        var r = QueryParser.ParseDateRange("aa-bb-cc-dd-ee-ff");
        Assert.Null(r.From);
        Assert.Null(r.To);
    }

    [Fact]
    public void ParseDateRange_SixSegments_FirstValidSecondInvalid()
    {
        var r = QueryParser.ParseDateRange("2026-11-01-xx-ee-ff");
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), r.From);
        Assert.Null(r.To);
    }

    [Fact]
    public void Parse_DmPrefix_WiresRangeIntoQuery()
    {
        var q = QueryParser.Parse("dm:2026-11-01-2026-11-05 log");
        Assert.NotNull(q.Modified);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), q.Modified!.Value.From);
        Assert.Equal("log", q.Term);
    }

    // ---- 其余前缀分支 ----

    [Fact]
    public void Parse_PathMissingArg_Throws()
        => Assert.Throws<QuerySyntaxException>(() => QueryParser.Parse("path:"));

    [Fact]
    public void Parse_ExtMissingArg_Throws()
        => Assert.Throws<QuerySyntaxException>(() => QueryParser.Parse("ext:"));

    [Fact]
    public void Parse_ExtDotsNormalizedOnce()
    {
        var q = QueryParser.Parse("ext:pdf;.docx");
        Assert.Equal(new[] { ".pdf", ".docx" }, q.Extensions);
    }

    [Fact]
    public void Parse_ColonLeading_TreatedAsTerm()
    {
        var q = QueryParser.Parse(":weird");
        Assert.Equal(":weird", q.Term);
    }
}
