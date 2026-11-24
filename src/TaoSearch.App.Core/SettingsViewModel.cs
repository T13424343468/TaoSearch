using System.Collections.ObjectModel;
using System.ComponentModel;
using TaoSearch.App.Core;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.App.Core;

/// <summary>设置页 ViewModel：索引状态、重建、暂停恢复、开机自启、存储位置。</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly ISearchGateway _gateway;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<KeyValuePair<string, string>> IndexInfo { get; } = [];

    private string _fileCount = "获取中…";
    public string FileCount
    {
        get => _fileCount;
        private set { _fileCount = value; PropertyChanged?.Invoke(this, new(nameof(FileCount))); }
    }

    private string _indexState = "获取中…";
    public string IndexState
    {
        get => _indexState;
        private set { _indexState = value; PropertyChanged?.Invoke(this, new(nameof(IndexState))); }
    }

    private string _pendingQueue = "获取中…";
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

    public SettingsViewModel(ISearchGateway gateway) => _gateway = gateway;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await _gateway.GetIndexStatusAsync(ct);
            FileCount = status.FileCount.ToString("N0");
            IndexState = status.FilenameIndexReady ? "就绪"
                : status.FilenamePaused ? "已暂停"
                : "建索引中";
            PendingQueue = status.PendingContentQueue.ToString("N0");
        }
        catch (Exception)
        {
            // 文案中文化（0.1.0.9）：不展示原始英文异常
            IndexState = "服务未连接";
            FileCount = "—";
            PendingQueue = "—";
        }
    }

    public async Task<string> RebuildAsync()
    {
        try
        {
            var ack = await _gateway.RebuildAsync(QueryScope.Filename | QueryScope.Content);
            return ack.Ok ? "已开始重建索引" : $"重建失败：{ErrorText(ack.Error)}";
        }
        catch (Exception)
        {
            return "重建失败：索引服务未连接，请确认服务已启动后重试。";
        }
    }

    public async Task<string> TogglePauseAsync()
    {
        try
        {
            var ack = Paused
                ? await _gateway.ResumeAsync()
                : await _gateway.PauseAsync();
            if (ack.Ok) Paused = !Paused;
            // 0.1.0.18：暂停现在同时停文件名与内容两路，文案不再只说全文
            return ack.Ok ? (Paused ? "已暂停索引" : "已恢复索引") : $"操作失败：{ErrorText(ack.Error)}";
        }
        catch (Exception)
        {
            return "操作失败：索引服务未连接，请确认服务已启动后重试。";
        }
    }

    /// <summary>服务侧错误转中文提示（0.1.0.9）：不再透传英文原文。</summary>
    private static string ErrorText(string? error) => string.IsNullOrWhiteSpace(error)
        ? "未知错误" : "服务返回错误，请查看服务日志或重试。";
}
