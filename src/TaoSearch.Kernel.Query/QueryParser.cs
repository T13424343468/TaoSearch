using System.Buffers.Text;
using System.Text;

namespace TaoSearch.Kernel.Query;

/// <summary>解析后的查询：主词 + 过滤器链（架构 §6，语法对齐 Everything 核心子集）。</summary>
public sealed record ParsedQuery
{
    /// <summary>主匹配词（空 = 仅按过滤器过滤）。</summary>
    public string Term { get; init; } = string.Empty;

    /// <summary>主词是否按正则解释（regex: 前缀）。</summary>
    public bool IsRegex { get; init; }

    /// <summary>是否大小写敏感（case: 前缀）。</summary>
    public bool CaseSensitive { get; init; }

    /// <summary>全词匹配（wholeword: 前缀）。</summary>
    public bool WholeWord { get; init; }

    public PathFilter? Path { get; init; }
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateRange? Modified { get; init; }
}

public sealed record PathFilter
{
    public required string Prefix { get; init; }
    public bool Exclude { get; init; }
}

public readonly record struct DateRange(DateTimeOffset? From, DateTimeOffset? To);

/// <summary>语法解析异常（用户输入不合法时给出可读原因）。</summary>
public sealed class QuerySyntaxException(string message) : Exception(message);

/// <summary>
/// 高级语法解析器：纯函数、可单测（架构 §10.2，沙箱可跑）。
/// 支持前缀：regex: case: wholeword: path: ext: size: dm:（修改时间）。
/// 多前缀可空格组合；其余文本视为主匹配词（仅取最后一段非前缀词，与 Everything 行为一致）。
/// </summary>
public static class QueryParser
{
    public static ParsedQuery Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new QuerySyntaxException("查询为空");

        var q = new ParsedQuery();
        var terms = new List<string>();

        foreach (var raw in input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0)
            {
                terms.Add(raw);
                continue;
            }

            var head = raw[..colon].ToLowerInvariant();
            var arg = raw[(colon + 1)..];
            switch (head)
            {
                case "regex": q = q with { IsRegex = true }; terms.Add(arg); break;
                case "case": q = q with { CaseSensitive = true }; terms.Add(arg); break;
                case "wholeword": q = q with { WholeWord = true }; terms.Add(arg); break;
                case "path":
                    if (arg.Length == 0) throw new QuerySyntaxException("path: 缺少参数");
                    q = q with { Path = new PathFilter { Prefix = arg } };
                    break;
                case "ext":
                    if (arg.Length == 0) throw new QuerySyntaxException("ext: 缺少参数");
                    q = q with { Extensions = arg.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(e => e.StartsWith('.') ? e : "." + e).ToArray() };
                    break;
                case "size":
                    q = q with { MinSize = q.MinSize, MaxSize = q.MaxSize };
                    var (lo, hi) = ParseSize(arg);
                    q = q with { MinSize = lo ?? q.MinSize, MaxSize = hi ?? q.MaxSize };
                    break;
                case "dm":
                    q = q with { Modified = ParseDateRange(arg) };
                    break;
                default:
                    terms.Add(raw); // 未知前缀按普通词处理
                    break;
            }
        }

        return q with { Term = string.Join(' ', terms) };
    }

    /// <summary>size: 语法 [下限]-[上限]，支持 kb/mb/gb 后缀，如 size:1mb-100mb、size:&gt;10kb。</summary>
    public static (long? Min, long? Max) ParseSize(string arg)
    {
        long? lo = null, hi = null;
        var parts = arg.Split('-');
        if (parts.Length > 2) throw new QuerySyntaxException($"size: 语法不合法：{arg}");

        foreach (var (part, isUpper) in new[] { (parts[0], false), (parts.Length > 1 ? parts[1] : null, true) })
        {
            if (part is null || part.Length == 0) continue;
            var s = part.TrimStart('>', '<').TrimEnd('=');
            long v = ParseSizeBytes(s) ?? throw new QuerySyntaxException($"size: 无法识别：{part}");
            if (part.StartsWith('<')) hi = v;
            else if (part.StartsWith('>')) lo = v;
            else if (isUpper) hi = v;
            else lo = v;
        }
        return (lo, hi);
    }

    private static long? ParseSizeBytes(string s)
    {
        if (s.Length == 0) return null;
        int split = s.Length;
        while (split > 0 && !char.IsAsciiDigit(s[split - 1]) && s[split - 1] != '.') split--;
        var numPart = s[..split];
        var unit = s[split..].Trim().ToLowerInvariant();
        if (!double.TryParse(numPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
            return null;
        return unit switch
        {
            "" or "b" => (long)value,
            "kb" or "k" => (long)(value * 1024),
            "mb" or "m" => (long)(value * 1024 * 1024),
            "gb" or "g" => (long)(value * 1024 * 1024 * 1024),
            _ => null,
        };
    }

    /// <summary>dm: 语法 yyyy-MM-dd 或 yyyy-MM-dd-yyyy-MM-dd（区间）。</summary>
    public static DateRange ParseDateRange(string arg)
    {
        var parts = arg.Split('-');
        DateTimeOffset? from = null, to = null;
        if (parts.Length is 3 or 4)
        {
            var y = parts[0]; var m = parts[1]; var d = parts[2];
            if (int.TryParse(y, out var yy) && int.TryParse(m, out var mm) && int.TryParse(d, out var dd))
            {
                from = new DateTimeOffset(yy, mm, dd, 0, 0, 0, TimeSpan.Zero);
                to = from + TimeSpan.FromDays(1);
            }
            if (parts.Length == 4 && int.TryParse(parts[1], out var y2m) == false)
            {
                // 形如 dm:2026-11-01-2026-11-05：第二段日期
            }
        }
        if (parts.Length == 6)
        {
            if (TryDate(parts[0], parts[1], parts[2], out var f)) from = f;
            if (TryDate(parts[3], parts[4], parts[5], out var t)) { to = t; }
        }
        return new DateRange(from, to);
    }

    private static bool TryDate(string y, string m, string d, out DateTimeOffset value)
    {
        value = default;
        if (int.TryParse(y, out var yy) && int.TryParse(m, out var mm) && int.TryParse(d, out var dd))
        {
            value = new DateTimeOffset(yy, mm, dd, 0, 0, 0, TimeSpan.Zero);
            return true;
        }
        return false;
    }
}
