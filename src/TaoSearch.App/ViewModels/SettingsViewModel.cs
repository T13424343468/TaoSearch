using System.Collections.ObjectModel;
using System.ComponentModel;
using TaoSearch.App.Services;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.App.ViewModels;

/// <summary>设置页 ViewModel：索引状态、重建、暂停恢复、开机自启、存储位置。</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly SearchGateway _gateway;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<KeyValuePair<string, string>> IndexInfo { get; } = [];

    private string _fileCount = "—";
    public string FileCount
    {
        get => _fileCount;
        private set { _fileCount = value; PropertyChanged?.Invoke(this, new(nameof(FileCount))); }
    }

    private string _indexState = "—";
    public string IndexState
    {
        get => _indexState;
        private set { _indexState = value; PropertyChanged?.Invoke(this, new(nameof(IndexState))); }
    }

    private string _pendingQueue = "—";
    public string PendingQueue
    {
        get => _pendingQueue;
        private set { _pendingQueue = value; PropertyChanged?.Invoke(this, new(nameof(PendingQueue))); }
    }

    private bool _paused;
    public bool Paused
    {
        get => _paused;
        private set { _paused = value; PropertyChanged?.Invoke(this, new(nameof(Paused))); }
    }

    public string StorageLocation { get; } = @"D:\TaoSearch\Index（不可用时自动改用本机数据目录）";

    private string _indexLocation = AppSettings.Instance.IndexLocation;
    public string IndexLocation
    {
        get => _indexLocation;
        set { _indexLocation = value; PropertyChanged?.Invoke(this, new(nameof(IndexLocation))); }
    }

    public SettingsViewModel(SearchGateway gateway) => _gateway = gateway;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await _gateway.GetIndexStatusAsync(ct);
            FileCount = status.FileCount.ToString("N0");
            IndexState = status.FilenameIndexReady ? "就绪" : "建索引中";
            PendingQueue = status.PendingContentQueue.ToString("N0");
        }
        catch (Exception ex)
        {
            IndexState = $"服务未连接：{ex.Message}";
        }
    }

    public async Task<string> RebuildAsync()
    {
        var ack = await _gateway.RebuildAsync(QueryScope.Filename | QueryScope.Content);
        return ack.Ok ? "已开始重建索引" : $"重建失败：{ack.Error}";
    }

    public async Task<string> TogglePauseAsync()
    {
        var ack = Paused
            ? await _gateway.ResumeAsync()
            : await _gateway.PauseAsync();
        if (ack.Ok) Paused = !Paused;
        return ack.Ok ? (Paused ? "已暂停全文索引" : "已恢复全文索引") : $"操作失败：{ack.Error}";
    }
}
