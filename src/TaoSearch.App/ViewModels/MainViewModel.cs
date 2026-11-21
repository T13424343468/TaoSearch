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
            foreach (var hit in batch.Hits)
            {
                Hits.Add(ToViewModel(hit));
            }
            StatusText = batch.Hits.Count == 0
                ? "没有找到匹配的文件"
                : $"共 {batch.TotalEstimate:0} 个结果";
        }
        catch (Exception ex)
        {
            StatusText = $"搜索出错：{ex.Message}";
        }
        finally { IsSearching = false; }
    }

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
