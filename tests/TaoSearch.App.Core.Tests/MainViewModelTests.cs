using System.Collections.ObjectModel;
using TaoSearch.App.Core;
using TaoSearch.Kernel.Contracts;
using Xunit;

namespace TaoSearch.App.Core.Tests;

/// <summary>可注入假网关：按脚本返回结果/异常，覆盖 ViewModel 全部分支。</summary>
internal sealed class FakeGateway : ISearchGateway
{
    public ResultBatch? SearchBatch { get; set; }
    public Exception? SearchError { get; set; }
    public IndexStatus? Status { get; set; }
    public Exception? StatusError { get; set; }
    public AckResponse? RebuildAck { get; set; }
    public Exception? RebuildError { get; set; }
    public AckResponse? PauseAck { get; set; }
    public AckResponse? ResumeAck { get; set; }
    public Exception? ToggleError { get; set; }

    public int SearchCalls { get; private set; }
    public QueryScope LastScope { get; private set; }

    public Task<ResultBatch> SearchAsync(string text, QueryScope scope, int batchSize, CancellationToken ct = default)
    {
        SearchCalls++;
        LastScope = scope;
        if (SearchError is not null) throw SearchError;
        return Task.FromResult(SearchBatch ?? new ResultBatch { Hits = [] });
    }

    public Task<IndexStatus> GetIndexStatusAsync(CancellationToken ct = default)
    {
        if (StatusError is not null) throw StatusError;
        return Task.FromResult(Status ?? new IndexStatus
        {
            FileCount = 253, FilenameIndexReady = true, ContentIndexReady = true,
            PendingContentQueue = 0, FailedFiles = [],
        });
    }

    public Task<AckResponse> RebuildAsync(QueryScope scope, CancellationToken ct = default)
    {
        if (RebuildError is not null) throw RebuildError;
        return Task.FromResult(RebuildAck ?? new AckResponse());
    }

    public Task<AckResponse> PauseAsync(CancellationToken ct = default)
    {
        if (ToggleError is not null) throw ToggleError;
        return Task.FromResult(PauseAck ?? new AckResponse());
    }

    public Task<AckResponse> ResumeAsync(CancellationToken ct = default)
    {
        if (ToggleError is not null) throw ToggleError;
        return Task.FromResult(ResumeAck ?? new AckResponse());
    }

    public void Dispose() { }
}

internal static class Poll
{
    public static async Task UntilAsync(Func<bool> done, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(20);
        }
    }
}

/// <summary>MainViewModel 全分支：空查询清空、成功/空结果/异常/取消、筛选与排序矩阵、预览高亮切片、大小格式化。</summary>
public class MainViewModelTests
{
    private static SearchHit Hit(string path, string name, long size = 1024, double score = 1.0,
        DateTimeOffset? modified = null, ContentPreview? preview = null) => new()
    {
        FileReference = 42,
        Path = path,
        FileName = name,
        Size = size,
        ModifiedTime = modified ?? new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(8)),
        Score = score,
        Preview = preview,
    };

    [Fact]
    public async Task Query_Empty_ClearsHitsAndStatus()
    {
        var vm = new MainViewModel(new FakeGateway()) { Query = "先有词" };
        await Poll.UntilAsync(() => vm.Hits.Count > 0 || vm.StatusText.Length > 0);
        vm.Hits.Add(new HitViewModel { FileReference = 1, Path = "p", FileName = "n", SizeText = "", ModifiedText = "" });
        vm.Query = "   ";
        await Poll.UntilAsync(() => vm.Hits.Count == 0);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public async Task Search_Success_PopulatesHits_AndStatusShowsCount()
    {
        var gw = new FakeGateway
        {
            SearchBatch = new ResultBatch
            {
                Hits = [Hit(@"D:\a.txt", "a.txt")],
                TotalEstimate = 1,
            },
        };
        var vm = new MainViewModel(gw);
        vm.PropertyChanged += (_, _) => { }; // 订阅者在场 → 覆盖事件非空侧
        vm.Query = "abc";
        await Poll.UntilAsync(() => vm.Hits.Count == 1);
        Assert.Contains("共 1 个结果", vm.StatusText);
        Assert.False(vm.IsSearching);
        Assert.Equal(QueryScope.Filename | QueryScope.Content, gw.LastScope);
    }

    [Fact]
    public async Task Search_Over200Hits_ListCappedAt200_TotalShowsFull()
    {
        // 流式聚合后命中可能上万：列表只展示前 200 条，总数以状态栏为准
        var hits = Enumerable.Range(1, 250).Select(i => Hit($@"D:\f{i}.txt", $"f{i}.txt")).ToList();
        var gw = new FakeGateway { SearchBatch = new ResultBatch { Hits = hits, TotalEstimate = 250 } };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 200);
        Assert.Contains("共 250 个结果", vm.StatusText);
    }

    [Fact]
    public async Task Search_NoHits_ShowsNotFound()
    {
        var gw = new FakeGateway { SearchBatch = new ResultBatch { Hits = [], TotalEstimate = 0 } };
        var vm = new MainViewModel(gw) { Query = "没有的东西" };
        await Poll.UntilAsync(() => vm.StatusText.Contains("未找到"));
        Assert.Empty(vm.Hits);
    }

    [Fact]
    public async Task Search_Exception_ShowsChineseMessage_NotRawException()
    {
        var gw = new FakeGateway { SearchError = new IOException("pipe boom") };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.StatusText.Contains("搜索出错"));
        Assert.DoesNotContain("pipe boom", vm.StatusText);
    }

    [Fact]
    public async Task Debounce_Cancel_IsSilent()
    {
        var gw = new FakeGateway();
        var vm = new MainViewModel(gw);
        vm.Query = "第一笔";
        vm.Query = "第二笔"; // 取消第一笔防抖
        await Task.Delay(600);
        Assert.DoesNotContain("搜索出错", vm.StatusText);
    }

    [Fact]
    public async Task Scope_FilenameOnly_WhenContentScopeOff()
    {
        var gw = new FakeGateway { SearchBatch = new ResultBatch { Hits = [Hit(@"D:\a.txt", "a.txt")] } };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 1);
        vm.IsContentScope = false; // 立即触发一次
        await Poll.UntilAsync(() => gw.LastScope == QueryScope.Filename);
    }

    [Fact]
    public async Task KindFilter_Markdown_MatchesMdAndMarkdown()
    {
        var gw = new FakeGateway
        {
            SearchBatch = new ResultBatch
            {
                Hits = [
                    Hit(@"D:\a.md", "a.md"),
                    Hit(@"D:\b.markdown", "b.markdown"),
                    Hit(@"D:\c.txt", "c.txt"),
                ],
            },
        };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 3);
        vm.KindFilter = "markdown";
        await Poll.UntilAsync(() => vm.Hits.Count == 2);
    }

    [Fact]
    public async Task KindFilter_Document_MatchesDocxAndPdf()
    {
        var gw = new FakeGateway
        {
            SearchBatch = new ResultBatch
            {
                Hits = [
                    Hit(@"D:\a.docx", "a.docx"),
                    Hit(@"D:\b.pdf", "b.pdf"),
                    Hit(@"D:\c.txt", "c.txt"),
                ],
            },
        };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 3);
        vm.KindFilter = "document";
        await Poll.UntilAsync(() => vm.Hits.Count == 2);
    }

    [Fact]
    public async Task KindFilter_Sheet_MatchesXlsxAndCsv()
    {
        var gw = new FakeGateway
        {
            SearchBatch = new ResultBatch
            {
                Hits = [
                    Hit(@"D:\a.xlsx", "a.xlsx"),
                    Hit(@"D:\b.csv", "b.csv"),
                    Hit(@"D:\c.txt", "c.txt"),
                ],
            },
        };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 3);
        vm.KindFilter = "sheet";
        await Poll.UntilAsync(() => vm.Hits.Count == 2);
    }

    [Fact]
    public async Task KindFilter_Code_MatchesSixExtensions()
    {
        var gw = new FakeGateway
        {
            SearchBatch = new ResultBatch
            {
                Hits = [
                    Hit(@"D:\a.cs", "a.cs"),
                    Hit(@"D:\b.py", "b.py"),
                    Hit(@"D:\c.js", "c.js"),
                    Hit(@"D:\d.ts", "d.ts"),
                    Hit(@"D:\e.cpp", "e.cpp"),
                    Hit(@"D:\f.h", "f.h"),
                    Hit(@"D:\g.txt", "g.txt"),
                ],
            },
        };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 7);
        vm.KindFilter = "code";
        await Poll.UntilAsync(() => vm.Hits.Count == 6);
    }

    [Fact]
    public async Task KindFilter_UnknownValue_PassesAll()
    {
        var gw = new FakeGateway
        {
            SearchBatch = new ResultBatch
            {
                Hits = [Hit(@"D:\a.txt", "a.txt"), Hit(@"D:\b.md", "b.md")],
            },
        };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 2);
        vm.KindFilter = "unknown-kind";
        await Poll.UntilAsync(() => vm.Hits.Count == 2);
    }

    [Fact]
    public async Task Sort_Name_OrdersByFileName()
    {
        var gw = new FakeGateway
        {
            SearchBatch = new ResultBatch
            {
                Hits = [Hit(@"D:\b.txt", "b.txt"), Hit(@"D:\a.txt", "a.txt")],
            },
        };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 2);
        vm.Sort = MainViewModel.SortKind.Name;
        await Poll.UntilAsync(() => vm.Hits[0].FileName == "a.txt");
    }

    [Fact]
    public async Task Sort_Path_OrdersByPath()
    {
        var gw = new FakeGateway
        {
            SearchBatch = new ResultBatch
            {
                Hits = [Hit(@"D:\z\a.txt", "a.txt"), Hit(@"D:\a\b.txt", "b.txt")],
            },
        };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 2);
        vm.Sort = MainViewModel.SortKind.Path;
        await Poll.UntilAsync(() => vm.Hits[0].Path == @"D:\a\b.txt");
    }

    [Fact]
    public async Task Sort_Size_Descending()
    {
        var gw = new FakeGateway
        {
            SearchBatch = new ResultBatch
            {
                Hits = [Hit(@"D:\small.txt", "small.txt", size: 1), Hit(@"D:\big.txt", "big.txt", size: 99999)],
            },
        };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 2);
        vm.Sort = MainViewModel.SortKind.Size;
        await Poll.UntilAsync(() => vm.Hits[0].FileName == "big.txt");
    }

    [Fact]
    public async Task Sort_Modified_Descending()
    {
        var older = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gw = new FakeGateway
        {
            SearchBatch = new ResultBatch
            {
                Hits = [Hit(@"D:\old.txt", "old.txt", modified: older), Hit(@"D:\new.txt", "new.txt", modified: newer)],
            },
        };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 2);
        vm.Sort = MainViewModel.SortKind.Modified;
        await Poll.UntilAsync(() => vm.Hits[0].FileName == "new.txt");
    }

    [Fact]
    public async Task Sort_Relevance_KeepsEngineOrder()
    {
        var low = Hit(@"D:\low.txt", "low.txt", score: 0.1);
        var high = Hit(@"D:\high.txt", "high.txt", score: 9.9);
        var gw = new FakeGateway { SearchBatch = new ResultBatch { Hits = [high, low] } };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.Hits.Count == 2);
        vm.Sort = MainViewModel.SortKind.Relevance;
        await Poll.UntilAsync(() => vm.Hits[0].FileName == "high.txt");
    }

    [Fact]
    public void ToSharedViewModel_PreviewNull_SegmentsEmpty()
    {
        var vm = MainViewModel.ToSharedViewModel(Hit(@"D:\a.txt", "a.txt", preview: null));
        Assert.Null(vm.Snippet);
        Assert.Empty(vm.Segments);
    }

    [Fact]
    public void ToSharedViewModel_EmptySnippet_SegmentsEmpty()
    {
        var preview = new ContentPreview { Snippet = "", MatchRanges = [(0, 1)] };
        var vm = MainViewModel.ToSharedViewModel(Hit(@"D:\a.txt", "a.txt", preview: preview));
        Assert.Empty(vm.Segments);
    }

    [Fact]
    public void ToSharedViewModel_MatchRanges_ProduceGapAndMatchSegments()
    {
        // "hello world"，命中 [6,5) → 前导 gap "hello " + 命中 "world"
        var preview = new ContentPreview { Snippet = "hello world", MatchRanges = [(6, 5)] };
        var vm = MainViewModel.ToSharedViewModel(Hit(@"D:\a.txt", "a.txt", preview: preview));
        Assert.Equal(2, vm.Segments.Count);
        Assert.False(vm.Segments[0].IsMatch);
        Assert.Equal("hello ", vm.Segments[0].Text);
        Assert.True(vm.Segments[1].IsMatch);
        Assert.Equal("world", vm.Segments[1].Text);
    }

    [Fact]
    public void ToSharedViewModel_MatchLengthClampedToSnippetEnd()
    {
        // 命中长度越界被截断，不产生越界段；越界前有 gap
        var preview = new ContentPreview { Snippet = "abc", MatchRanges = [(1, 100)] };
        var vm = MainViewModel.ToSharedViewModel(Hit(@"D:\a.txt", "a.txt", preview: preview));
        Assert.Equal(2, vm.Segments.Count);
        Assert.False(vm.Segments[0].IsMatch);
        Assert.Equal("a", vm.Segments[0].Text);
        Assert.True(vm.Segments[1].IsMatch);
        Assert.Equal("bc", vm.Segments[1].Text);
    }

    [Fact]
    public void ToSharedViewModel_TrailingRemainderAfterLastMatch()
    {
        var preview = new ContentPreview { Snippet = "abcdef", MatchRanges = [(0, 3)] };
        var vm = MainViewModel.ToSharedViewModel(Hit(@"D:\a.txt", "a.txt", preview: preview));
        Assert.Equal(2, vm.Segments.Count);
        Assert.True(vm.Segments[0].IsMatch);
        Assert.Equal("abc", vm.Segments[0].Text);
        Assert.False(vm.Segments[1].IsMatch);
        Assert.Equal("def", vm.Segments[1].Text);
    }

    [Fact]
    public void ToSharedViewModel_ZeroLengthRange_ProducesNoSegment()
    {
        var preview = new ContentPreview { Snippet = "abc", MatchRanges = [(1, 0)] };
        var vm = MainViewModel.ToSharedViewModel(Hit(@"D:\a.txt", "a.txt", preview: preview));
        // len=0 → 不加命中段；前导 gap "a" + 尾段 "bc"
        Assert.Equal(2, vm.Segments.Count);
        Assert.False(vm.Segments[0].IsMatch);
        Assert.Equal("a", vm.Segments[0].Text);
        Assert.False(vm.Segments[1].IsMatch);
        Assert.Equal("bc", vm.Segments[1].Text);
    }

    [Fact]
    public void ToSharedViewModel_SizeAndTime_Formatting()
    {
        var vm = MainViewModel.ToSharedViewModel(Hit(@"D:\a.txt", "a.txt", size: 512));
        Assert.Equal("512 B", vm.SizeText);
        Assert.Contains("2026-11-23", vm.ModifiedText);

        var kb = MainViewModel.ToSharedViewModel(Hit(@"D:\a.txt", "a.txt", size: 2048));
        Assert.Equal("2 KB", kb.SizeText);

        var mb = MainViewModel.ToSharedViewModel(Hit(@"D:\a.txt", "a.txt", size: 3 * 1024 * 1024));
        Assert.Equal("3 MB", mb.SizeText);

        var gb = MainViewModel.ToSharedViewModel(Hit(@"D:\a.txt", "a.txt", size: 5L * 1024 * 1024 * 1024));
        Assert.Equal("5 GB", gb.SizeText);
    }

    [Fact]
    public void ToSharedViewModel_MinModifiedTime_ShowsEmpty()
    {
        var vm = MainViewModel.ToSharedViewModel(Hit(@"D:\a.txt", "a.txt", modified: DateTimeOffset.MinValue));
        Assert.Equal("", vm.ModifiedText);
    }
}
