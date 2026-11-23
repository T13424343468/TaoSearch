namespace TaoSearch.App.Core;

using TaoSearch.Kernel.Contracts;
/// <summary>搜索服务门面契约：ViewModel 只依赖此接口，管道实现可替换（壳层核心可测化，2026-11-23 终裁路线）。</summary>
public interface ISearchGateway : IDisposable
{
    Task<ResultBatch> SearchAsync(string text, QueryScope scope, int batchSize, CancellationToken ct = default);
    Task<IndexStatus> GetIndexStatusAsync(CancellationToken ct = default);
    Task<AckResponse> RebuildAsync(QueryScope scope, CancellationToken ct = default);
    Task<AckResponse> PauseAsync(CancellationToken ct = default);
    Task<AckResponse> ResumeAsync(CancellationToken ct = default);
}
