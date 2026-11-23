using System.Text.Json;
using TaoSearch.Kernel.Contracts;
using TaoSearch.App.Core;

namespace TaoSearch.App.Core;

/// <summary>
/// 搜索服务门面：壳层 ViewModel 的唯一数据源，包装管道客户端并做错误转译。
/// </summary>
public sealed class SearchGateway : ISearchGateway
{
    private readonly PipeClient _pipe = new();

    /// <summary>真机红灯修复：首次 IPC 前确保索引服务在运行（未运行则自动拉起，进程级只试一次）。</summary>
    private async Task EnsureServiceAsync(CancellationToken ct) =>
        await ServiceSupervisor.EnsureRunningAsync(ct).ConfigureAwait(false);

    public async Task<ResultBatch> SearchAsync(string text, QueryScope scope, int batchSize, CancellationToken ct = default)
    {
        await EnsureServiceAsync(ct).ConfigureAwait(false);
        return await _pipe.CallAsync<QuerySpec, ResultBatch>(IpcMethod.Search,
            new QuerySpec { Text = text, Scope = scope, BatchSize = batchSize }, ct);
    }

    public async Task<IndexStatus> GetIndexStatusAsync(CancellationToken ct = default)
    {
        await EnsureServiceAsync(ct).ConfigureAwait(false);
        return await _pipe.CallAsync<object, IndexStatus>(IpcMethod.GetIndexStatus, null, ct);
    }

    public async Task<AckResponse> RebuildAsync(QueryScope scope, CancellationToken ct = default)
    {
        await EnsureServiceAsync(ct).ConfigureAwait(false);
        return await _pipe.CallAsync<RebuildRequest, AckResponse>(IpcMethod.RebuildIndex, new RebuildRequest { Scope = scope }, ct);
    }

    public async Task<AckResponse> PauseAsync(CancellationToken ct = default)
    {
        await EnsureServiceAsync(ct).ConfigureAwait(false);
        return await _pipe.CallAsync<object, AckResponse>(IpcMethod.PauseIndexing, null, ct);
    }

    public async Task<AckResponse> ResumeAsync(CancellationToken ct = default)
    {
        await EnsureServiceAsync(ct).ConfigureAwait(false);
        return await _pipe.CallAsync<object, AckResponse>(IpcMethod.ResumeIndexing, null, ct);
    }

    public void Dispose() => _pipe.Dispose();
}
