namespace TaoSearch.Kernel.Contracts;

/// <summary>查询范围与语法载体（IPC 契约，版本化）。</summary>
public sealed record QuerySpec
{
    /// <summary>用户原始输入（含高级语法）。</summary>
    public required string Text { get; init; }

    /// <summary>查询路：文件名 / 全文 / 混合。</summary>
    public QueryScope Scope { get; init; } = QueryScope.Filename | QueryScope.Content;

    /// <summary>单批最大返回数（壳层分页拉取）。</summary>
    public int BatchSize { get; init; } = 200;
}

[Flags]
public enum QueryScope
{
    Filename = 1,
    Content = 2,
}

/// <summary>单条搜索结果（跨进程传输的最小单元）。</summary>
public sealed record SearchHit
{
    /// <summary>NTFS 文件引用号（全卷唯一，稳定标识）。</summary>
    public required long FileReference { get; init; }

    /// <summary>重建后的完整路径。</summary>
    public required string Path { get; init; }

    public required string FileName { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset ModifiedTime { get; init; }

    /// <summary>全文命中时的相关度（0=纯文件名命中）。</summary>
    public double Score { get; init; }

    /// <summary>全文命中段落（含命中词位置，供壳层高亮）。</summary>
    public ContentPreview? Preview { get; init; }
}

/// <summary>命中段落预览。</summary>
public sealed record ContentPreview
{
    /// <summary>命中所在行/段文本（截断到上下文窗口）。</summary>
    public required string Snippet { get; init; }

    /// <summary>命中词在 Snippet 中的 [start, length) 区间列表。</summary>
    public required IReadOnlyList<(int Start, int Length)> MatchRanges { get; init; }
}

/// <summary>结果批次（流式返回）。</summary>
public sealed record ResultBatch
{
    public required IReadOnlyList<SearchHit> Hits { get; init; }
    /// <summary>总命中估计（截断前），-1 表示未知。</summary>
    public long TotalEstimate { get; init; } = -1;
    public bool IsFinal { get; init; }
}

/// <summary>统一查询入口：文件名与全文两路在此合并（架构 §6）。</summary>
public interface IQueryEngine
{
    IAsyncEnumerable<ResultBatch> SearchAsync(QuerySpec spec, CancellationToken ct = default);
}

/// <summary>文件事件（USN/目录监听归一后的事件，预埋接口二的数据源）。</summary>
public sealed record FileEvent
{
    public required FileChangeKind Kind { get; init; }
    public required long FileReference { get; init; }
    public required string Path { get; init; }
    public required DateTimeOffset UtcTimestamp { get; init; }

    /// <summary>NTFS 父引用号（USN 源必填，供索引父链路径重建；无此概念的源可空）。</summary>
    public long? ParentReference { get; init; }
}

[Flags]
public enum FileChangeKind
{
    /// <summary>新建（位值 M4 冻结：原非 Flags 枚举 Created=0 致按位过滤恒假，修真缺陷）。</summary>
    Created = 1,
    Renamed = 2,
    Deleted = 4,
    ContentModified = 8,
}

/// <summary>文件事件订阅（预埋接口二：未来管家/文件管理器挂接点）。</summary>
public interface IFileEventSource
{
    IAsyncEnumerable<FileEvent> SubscribeAsync(FileEventFilter filter, CancellationToken ct = default);
}

public sealed record FileEventFilter
{
    /// <summary>路径前缀白名单（空 = 全部）。</summary>
    public IReadOnlyList<string> PathPrefixes { get; init; } = [];
    public IReadOnlyList<string>? Extensions { get; init; }
    public FileChangeKind Kinds { get; init; } = FileChangeKind.Created | FileChangeKind.Renamed
        | FileChangeKind.Deleted | FileChangeKind.ContentModified;

    /// <summary>
    /// 过滤器匹配语义（M4 契约冻结，2026-11-21）：内核与外部订阅者共用单源实现。
    /// ①Kind 按位与；②前缀 OrdinalIgnoreCase 且目录边界对齐（"Downloads" 不误吞 "DownloadsBackup"，
    /// 路径等于前缀或前缀后紧跟目录分隔符才算命中）；③扩展名 OrdinalIgnoreCase
    /// （调用方传带点形式 ".zip"，内部按 Path.GetExtension 对齐，不区分 .ZIP/.zip）。
    /// 语义变更须升 IpcProtocol.Version。
    /// </summary>
    public bool Matches(FileEvent e)
    {
        if ((Kinds & e.Kind) == 0) return false;
        if (PathPrefixes.Count > 0)
        {
            bool prefixHit = PathPrefixes.Any(p =>
            {
                if (!e.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false;
                // 契约路径为 Windows 形态，分隔符显式判定（不依赖运行平台 Path API）
                return e.Path.Length == p.Length
                    || e.Path[p.Length] is '\\' or '/';
            });
            if (!prefixHit) return false;
        }
        if (Extensions is { Count: > 0 } && !Extensions.Contains(Path.GetExtension(e.Path), StringComparer.OrdinalIgnoreCase))
            return false;
        return true;
    }
}

/// <summary>解析器契约（预埋接口三：新格式注册即插，架构 §5.1）。</summary>
public interface IParser
{
    /// <summary>能否处理该文件（按扩展名与文件头魔数判断）。</summary>
    bool CanHandle(ReadOnlySpan<byte> header, string extension);

    /// <summary>抽取纯文本流；异常由管道捕获并记失败清单，绝不外抛带崩索引。</summary>
    IEnumerable<TextChunk> Parse(Stream content, CancellationToken ct = default);
}

/// <summary>
/// 事件日志写入点（M3 持久化挂点）：引擎在应用事件前先落 journal（先写后应用），
/// 强杀后按 journal 重放不丢事件；重放幂等（Upsert 同引用号覆盖、Remove 缺失即忽略）。
/// </summary>
public interface IJournalSink
{
    void Write(FileEvent e);
}

/// <summary>解析出的文本块（带偏移，供命中段落回读）。</summary>
public readonly record struct TextChunk(long Offset, string Text);

/// <summary>索引状态（设置页与 IPC GetIndexStatus 用）。</summary>
public sealed record IndexStatus
{
    public required long FileCount { get; init; }
    public required bool FilenameIndexReady { get; init; }
    public required bool ContentIndexReady { get; init; }
    public required int PendingContentQueue { get; init; }
    public required IReadOnlyList<string> FailedFiles { get; init; }
}
