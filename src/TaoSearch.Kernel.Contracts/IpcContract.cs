using System.Text.Json.Serialization;

namespace TaoSearch.Kernel.Contracts;

/// <summary>
/// M4 管道契约冻结（架构 §7.2–7.4，2026-11-21 老板令「按制度落实全部做完」定版）。
/// 方法号一经发布只增不改；载荷字段只增不删（向后兼容口径：未知字段忽略）。
/// 无网络设计红线：本契约仅用于本机命名管道，不含任何网络传输语义。
/// </summary>
public static class IpcProtocol
{
    /// <summary>协议版本（帧头 [2B 版本] 字段；不兼容变更才升版本）。</summary>
    public const ushort Version = 1;
}

/// <summary>IPC 方法号（架构 §7.2 方法族 + §7.3 订阅 + §7.4 解析器注册；ushort，帧头 [2B 方法号]）。</summary>
public enum IpcMethod : ushort
{
    /// <summary>统一查询（文件名/全文/混合），载荷 QuerySpec → ResultBatch 流。</summary>
    Search = 1,

    /// <summary>索引状态，载荷空 → IndexStatus。</summary>
    GetIndexStatus = 2,

    /// <summary>重建索引，载荷 RebuildRequest → AckResponse。</summary>
    RebuildIndex = 3,

    /// <summary>暂停全文索引，载荷空 → AckResponse。</summary>
    PauseIndexing = 4,

    /// <summary>恢复全文索引，载荷空 → AckResponse。</summary>
    ResumeIndexing = 5,

    /// <summary>订阅文件事件，载荷 SubscribeRequest → SubscribeAck；后续事件以 EventNotification 推送。</summary>
    SubscribeEvents = 6,

    /// <summary>退订文件事件，载荷 UnsubscribeRequest → AckResponse。</summary>
    UnsubscribeEvents = 7,

    /// <summary>注册外部解析器（仅限本机 IPC 客户端），载荷 RegisterParserRequest → AckResponse。</summary>
    RegisterParser = 8,
}

/// <summary>重建请求（设置页「索引重建」；Scope 缺省=全量）。</summary>
public sealed record RebuildRequest
{
    public QueryScope Scope { get; init; } = QueryScope.Filename | QueryScope.Content;
}

/// <summary>事件订阅请求（预埋接口二；过滤器语义见 <see cref="FileEventFilter.Matches"/>）。</summary>
public sealed record SubscribeRequest
{
    public required FileEventFilter Filter { get; init; }
}

/// <summary>订阅确认；订阅号本进程内唯一，事件推送携带回。</summary>
public sealed record SubscribeAck
{
    public required Guid SubscriptionId { get; init; }
}

/// <summary>事件推送（服务端 → 订阅者；同一事件源只发一份，按各订阅过滤器分发）。</summary>
public sealed record EventNotification
{
    public required Guid SubscriptionId { get; init; }
    public required FileEvent Event { get; init; }
}

/// <summary>退订请求。</summary>
public sealed record UnsubscribeRequest
{
    public required Guid SubscriptionId { get; init; }
}

/// <summary>外部解析器注册请求（预埋接口三；安全边界=仅本机 IPC 客户端）。</summary>
public sealed record RegisterParserRequest
{
    /// <summary>解析器标识（宿主内唯一，如 "butler.archive-v1"）。</summary>
    public required string ParserId { get; init; }

    /// <summary>声明可处理的扩展名（带点小写，如 ".zip"）。</summary>
    public required IReadOnlyList<string> Extensions { get; init; }
}

/// <summary>通用应答（写操作与错误通道；Ok=false 时 Error 带机器可读码）。</summary>
public sealed record AckResponse
{
    public bool Ok { get; init; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    public static AckResponse Fail(string error) => new() { Ok = false, Error = error };
}
