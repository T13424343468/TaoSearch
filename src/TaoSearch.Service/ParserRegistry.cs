using System.Collections.Concurrent;

namespace TaoSearch.Service;

/// <summary>
/// 外部解析器注册表（预埋接口三，架构 §5.1）：IPC RegisterParser 只登记「解析器标识 + 声明的扩展名」，
/// 内容索引管线把登记扩展名纳入候选集（文本尽力读取）；跨进程传输解析器实现不在本版范围
/// （契约冻结决议：解析器注册语义=扩展名声明，实际抽取仍走内置文本管线，新格式接入走后续版本）。
/// 线程安全；ParserId 宿主内唯一，重复注册返回 false。
/// </summary>
public sealed class ParserRegistry
{
    private readonly ConcurrentDictionary<string, byte> _parserIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _extensions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册外部解析器声明。成功返回 true；ParserId 重复返回 false（幂等注册由调用方处理）。</summary>
    public bool Register(string parserId, IReadOnlyList<string> extensions)
    {
        if (!_parserIds.TryAdd(parserId, 0)) return false;
        foreach (var ext in extensions)
        {
            if (!ext.StartsWith('.')) continue;
            _extensions.TryAdd(ext, 0);
        }
        return true;
    }

    /// <summary>扩展名（带点）是否已被任一解析器声明。</summary>
    public bool IsKnownExtension(string extension) => _extensions.ContainsKey(extension);

    /// <summary>已注册解析器标识快照（诊断用）。</summary>
    public IReadOnlyList<string> ParserIds => [.. _parserIds.Keys];
}
