using System.Text.Json;
using TaoSearch.Kernel.Contracts;
using TaoSearch.App.Ipc;

namespace TaoSearch.App.Services;

/// <summary>
/// 搜索服务门面：壳层 ViewModel 的唯一数据源，包装管道客户端并做错误转译。
/// </summary>
public sealed class SearchGateway : IDisposable
{
    private readonly PipeClient _pipe = new();

    public async Task<ResultBatch> SearchAsync(string text, QueryScope scope, int batchSize, CancellationToken ct = default) =>
        await _pipe.CallAsync<QuerySpec, ResultBatch>(IpcMethod.Search,
            new QuerySpec { Text = text, Scope = scope, BatchSize = batchSize }, ct);

    public async Task<IndexStatus> GetIndexStatusAsync(CancellationToken ct = default) =>
        await _pipe.CallAsync<object, IndexStatus>(IpcMethod.GetIndexStatus, null, ct);

    public async Task<AckResponse> RebuildAsync(QueryScope scope, CancellationToken ct = default) =>
        await _pipe.CallAsync<RebuildRequest, AckResponse>(IpcMethod.RebuildIndex, new RebuildRequest { Scope = scope }, ct);

    public async Task<AckResponse> PauseAsync(CancellationToken ct = default) =>
        await _pipe.CallAsync<object, AckResponse>(IpcMethod.PauseIndexing, null, ct);

    public async Task<AckResponse> ResumeAsync(CancellationToken ct = default) =>
        await _pipe.CallAsync<object, AckResponse>(IpcMethod.ResumeIndexing, null, ct);

    public void Dispose() => _pipe.Dispose();
}
