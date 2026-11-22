using System.Collections.ObjectModel;
using System.ComponentModel;
using TaoSearch.Kernel.Contracts;
using TaoSearch.App.Services;

namespace TaoSearch.App.ViewModels;

/// <summary>
/// 主搜索窗 ViewModel：输入即搜（300ms 防抖）、文件名/内容双路、预览高亮。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SearchGateway _gateway;
    private CancellationTokenSource? _debounceCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<HitViewModel> Hits { get; } = [];

    private string _query = "";
    public string Query
    {
        get => _query;
        set { _query = value; _ = DebouncedSearchAsync(); }
    }

    private bool _isContentScope = true;
    /// <summary>true=文件名+内容混合；false=仅文件名。</summary>
    public bool IsContentScope
    {
        get => _isContentScope;
        set { _isContentScope = value; _ = SearchNowAsync(); }
    }

    /// <summary>排序方式（FR-03/FR-05）：相关度（默认）/名称/路径/大小/修改时间。</summary>
    public enum SortKind { Relevance, Name, Path, Size, Modified }

    private SortKind _sort = SortKind.Relevance;
    public SortKind Sort
    {
        get => _sort;
        set { _sort = value; _ = SearchNowAsync(); }
    }

    private string _kindFilter = "all"; // all / markdown / document / sheet / code
    public string KindFilter
    {
        get => _kindFilter;
        set { _kindFilter = value; _ = SearchNowAsync(); }
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        private set { _isSearching = value; PropertyChanged?.Invoke(this, new(nameof(IsSearching))); }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; PropertyChanged?.Invoke(this, new(nameof(StatusText))); }
    }

    public MainViewModel(SearchGateway gateway) => _gateway = gateway;

    private async Task DebouncedSearchAsync()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        try
        {
            await Task.Delay(300, token);
            await SearchCoreAsync(token);
        }
        catch (OperationCanceledException) { }
    }

    private Task SearchNowAsync() => SearchCoreAsync(CancellationToken.None);

    private async Task SearchCoreAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            Hits.Clear();
            StatusText = "";
            return;
        }
        IsSearching = true;
        try
        {
            var scope = IsContentScope ? QueryScope.Filename | QueryScope.Content : QueryScope.Filename;
            var batch = await _gateway.SearchAsync(Query, scope, 200, ct);
            Hits.Clear();
            foreach (var hit in SortAndFilter(batch.Hits))
            {
                Hits.Add(ToViewModel(hit));
            }
            StatusText = Hits.Count == 0
                ? $"未找到与「{Query}」匹配的结果"
                : $"共 {batch.TotalEstimate:0} 个结果";
        }
        catch (OperationCanceledException)
        {
            // 防抖取消属正常流转（0.1.0.9）：连续打字不再弹「搜索出错」
        }
        catch (Exception)
        {
            // 错误文案中文化（0.1.0.9）：不把原始英文异常直接抛给用户
            StatusText = "搜索出错：搜索服务暂时不可用，请稍候重试；若持续出现，请从任务栏托盘退出后重新打开 TaoSearch。";
        }
        finally { IsSearching = false; }
    }

    /// <summary>按类型筛选 + 排序（相关度沿用引擎打分顺序）。</summary>
    private IEnumerable<SearchHit> SortAndFilter(IReadOnlyList<SearchHit> hits)
    {
        IEnumerable<SearchHit> seq = hits;
        if (KindFilter != "all")
        {
            seq = seq.Where(h => KindFilter switch
            {
                "markdown" => h.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || h.FileName.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase),
                "document" => h.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) || h.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase),
                "sheet" => h.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || h.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase),
                "code" => h.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || h.FileName.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                    || h.FileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || h.FileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                    || h.FileName.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) || h.FileName.EndsWith(".h", StringComparison.OrdinalIgnoreCase),
                _ => true,
            });
        }
        return Sort switch
        {
            SortKind.Name => seq.OrderBy(h => h.FileName, StringComparer.CurrentCulture),
            SortKind.Path => seq.OrderBy(h => h.Path, StringComparer.CurrentCulture),
            SortKind.Size => seq.OrderByDescending(h => h.Size),
            SortKind.Modified => seq.OrderByDescending(h => h.ModifiedTime),
            _ => seq, // Relevance：引擎打分序
        };
    }

    /// <summary>快速搜索窗与主窗共用同一映射。</summary>
    internal static HitViewModel ToSharedViewModel(SearchHit hit) => ToViewModel(hit);

    private static HitViewModel ToViewModel(SearchHit hit)
    {
        var segments = new List<PreviewSegment>();
        if (hit.Preview is { } preview && preview.Snippet.Length > 0)
        {
            int pos = 0;
            foreach (var (start, length) in preview.MatchRanges.OrderBy(r => r.Start))
            {
                if (start > pos) segments.Add(new(preview.Snippet[pos..start], false));
                int len = Math.Min(length, preview.Snippet.Length - start);
                if (len > 0) segments.Add(new(preview.Snippet.Substring(start, len), true));
                pos = start + len;
            }
            if (pos < preview.Snippet.Length) segments.Add(new(preview.Snippet[pos..], false));
        }
        return new HitViewModel
        {
            FileReference = hit.FileReference,
            Path = hit.Path,
            FileName = hit.FileName,
            SizeText = FormatSize(hit.Size),
            ModifiedText = hit.ModifiedTime == DateTimeOffset.MinValue ? "" : hit.ModifiedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            Score = hit.Score,
            Snippet = hit.Preview?.Snippet,
            Segments = segments,
        };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.#} GB",
    };
}
