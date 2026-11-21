using System.Text;
using System.Text.RegularExpressions;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.FullText;
using TaoSearch.Kernel.Query;

namespace TaoSearch.Service;

/// <summary>
/// 统一查询引擎（架构 §6，M4 引擎接线）：文件名路（FsIndexEngine + QueryParser 语法）
/// 与全文路（InvertedIndex 多词 AND + 词频相关度）在此合并，按引用号去重、相关度排序、
/// 分批流式返回。全文命中预览按决议「位置不落盘」走文档重扫：读文件头部窗口构建
/// Snippet 与 MatchRanges（读不到文件时 Preview 置空，不影响命中返回）。
/// </summary>
public sealed class SearchEngine : IQueryEngine
{
    /// <summary>预览重扫读取上限（字节）：片段窗口足够，避免大文件整读。</summary>
    public const int PreviewScanBytes = 64 * 1024;

    /// <summary>片段窗口：命中点前后各取字符数。</summary>
    public const int SnippetContextChars = 80;

    /// <summary>片段内最多标注的命中区间数。</summary>
    public const int SnippetMaxRanges = 8;

    /// <summary>正则/空词全枚举扫描上限（轻量口径：防全量正则拖垮前台；超限即止，结果如实截断）。</summary>
    public const int ScanCap = 100_000;

    private readonly FsIndexEngine _fs;
    private readonly InvertedIndex _fullText;

    public SearchEngine(FsIndexEngine fs, InvertedIndex fullText)
    {
        _fs = fs;
        _fullText = fullText;
    }

    public async IAsyncEnumerable<ResultBatch> SearchAsync(QuerySpec spec, CancellationToken ct = default)
    {
        ParsedQuery parsed;
        try
        {
            parsed = QueryParser.Parse(spec.Text);
        }
        catch (QuerySyntaxException)
        {
            // 语法错误：返回空终批（错误通道由服务层以 AckResponse 帧先行告知，见 ServiceHost）
            yield return new ResultBatch { Hits = [], TotalEstimate = 0, IsFinal = true };
            yield break;
        }

        ct.ThrowIfCancellationRequested();
        var merged = new Dictionary<long, (string Path, double Score, ContentPreview? Preview)>();

        if (spec.Scope.HasFlag(QueryScope.Filename))
            CollectFilenameHits(parsed, merged, ct);

        if (spec.Scope.HasFlag(QueryScope.Content))
            CollectContentHits(parsed, merged, ct);

        // 排序：相关度降序，同分按路径序（稳定可预期）
        var ordered = merged
            .Select(kv => (FileReference: kv.Key, kv.Value.Path, kv.Value.Score, kv.Value.Preview))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Path, StringComparer.Ordinal)
            .ToArray();

        int batchSize = Math.Max(1, spec.BatchSize);
        long total = ordered.Length;
        for (int offset = 0; offset < ordered.Length; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var hits = new List<SearchHit>(Math.Min(batchSize, ordered.Length - offset));
            foreach (var h in ordered.Skip(offset).Take(batchSize))
            {
                _fs.TryGetInfo(h.FileReference, out _, out long size, out DateTimeOffset modified);
                hits.Add(new SearchHit
                {
                    FileReference = h.FileReference,
                    Path = h.Path,
                    FileName = Path.GetFileName(h.Path),
                    Size = size,
                    ModifiedTime = modified,
                    Score = h.Score,
                    Preview = h.Preview,
                });
            }
            bool final = offset + batchSize >= ordered.Length;
            yield return new ResultBatch { Hits = hits, TotalEstimate = total, IsFinal = final };
            if (final) yield break;
            await Task.Yield(); // 分批间让出，保持流式语义
        }
    }

    private void CollectFilenameHits(ParsedQuery parsed, Dictionary<long, (string Path, double Score, ContentPreview?)> merged, CancellationToken ct)
    {
        StringComparison comparison = parsed.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (parsed.IsRegex)
        {
            Regex regex;
            try { regex = new Regex(parsed.Term, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (ArgumentException) { return; } // 非法正则：文件名路零命中（语法错误已在解析层拦截）

            int scanned = 0;
            foreach (var (fileRef, path) in _fs.EnumerateLivePaths())
            {
                if (++scanned > ScanCap || ct.IsCancellationRequested) return; // 超限即止，结果如实截断
                if (!regex.IsMatch(path)) continue;
                if (!PassesFilters(parsed, path, fileRef)) continue;
                merged.TryAdd(fileRef, (path, 0, null));
            }
            return;
        }

        if (parsed.Term.Length == 0)
        {
            // 空词 = 仅按过滤器浏览（Everything 行为）：全枚举 + 过滤
            int scanned = 0;
            foreach (var (fileRef, path) in _fs.EnumerateLivePaths())
            {
                if (++scanned > ScanCap || ct.IsCancellationRequested) return;
                if (!PassesFilters(parsed, path, fileRef)) continue;
                merged.TryAdd(fileRef, (path, 0, null));
            }
            return;
        }

        foreach (var (fileRef, path) in _fs.Search(parsed.Term, comparison))
        {
            if (ct.IsCancellationRequested) return;
            if (parsed.WholeWord && !IsWholeWordMatch(path, parsed.Term, comparison)) continue;
            if (!PassesFilters(parsed, path, fileRef)) continue;
            merged.TryAdd(fileRef, (path, 0, null));
        }
    }

    private void CollectContentHits(ParsedQuery parsed, Dictionary<long, (string Path, double Score, ContentPreview?)> merged, CancellationToken ct)
    {
        var terms = Tokenizer.Tokenize(parsed.Term).Select(t => t.Value).Distinct().ToArray();
        if (terms.Length == 0) return; // 纯过滤器查询不进全文路

        foreach (var (docId, score) in _fullText.LookupAll(terms))
        {
            if (ct.IsCancellationRequested) return;
            if (!_fs.TryGetInfo(docId, out string path, out _, out _)) continue; // 文件已删，倒排待清理
            if (!PassesFilters(parsed, path, docId)) continue;

            var preview = BuildPreview(path, terms);
            if (merged.TryGetValue(docId, out var existing))
            {
                // 文件名+全文双命中：保留全文相关度与预览
                merged[docId] = (path, Math.Max(existing.Score, score), preview);
            }
            else
            {
                merged[docId] = (path, score, preview);
            }
        }
    }

    private bool PassesFilters(ParsedQuery parsed, string path, long fileRef)
    {
        if (parsed.Path is { } pf)
        {
            bool prefixHit = path.StartsWith(pf.Prefix, StringComparison.OrdinalIgnoreCase);
            if (pf.Exclude ? prefixHit : !prefixHit) return false;
        }

        if (parsed.Extensions is { Count: > 0 })
        {
            string ext = Path.GetExtension(path);
            if (!parsed.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;
        }

        if (parsed.MinSize is { } min || parsed.MaxSize is { } max)
        {
            _fs.TryGetInfo(fileRef, out _, out long size, out _);
            if (min is { } lo && size < lo) return false;
            if (max is { } hi && size > hi) return false;
        }

        if (parsed.Modified is { } dr)
        {
            _fs.TryGetInfo(fileRef, out _, out _, out DateTimeOffset modified);
            if (dr.From is { } from && modified < from) return false;
            if (dr.To is { } to && modified > to) return false;
        }

        return true;
    }

    /// <summary>文档重扫构建命中预览（决议口径：位置不落盘）。读不到文件返回 null，不影响命中。</summary>
    private static ContentPreview? BuildPreview(string path, string[] terms)
    {
        string text;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(PreviewScanBytes, (long)fs.Length)];
            if (buffer.Length == 0) return null;
            int read = fs.Read(buffer);
            text = Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception)
        {
            return null; // 文件被占用/已删/非文本：预览置空，命中照常返回
        }

        // 首个命中点定窗口
        int first = -1;
        foreach (var term in terms)
        {
            int pos = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (pos >= 0 && (first < 0 || pos < first)) first = pos;
        }
        if (first < 0) return null;

        int start = Math.Max(0, first - SnippetContextChars);
        int length = Math.Min(text.Length - start, SnippetContextChars * 2 + terms.Max(t => t.Length));
        string snippet = text.Substring(start, length);

        var ranges = new List<(int Start, int Length)>();
        foreach (var term in terms)
        {
            int idx = snippet.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0 && ranges.Count < SnippetMaxRanges)
            {
                ranges.Add((idx, term.Length));
                idx = snippet.IndexOf(term, idx + term.Length, StringComparison.OrdinalIgnoreCase);
            }
            if (ranges.Count >= SnippetMaxRanges) break;
        }

        return new ContentPreview { Snippet = snippet, MatchRanges = ranges };
    }

    private static bool IsWholeWordMatch(string path, string term, StringComparison comparison)
    {
        string name = Path.GetFileName(path);
        foreach (var segment in name.Split([' ', '.', '_', '-', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, term, comparison)) return true;
        }
        return false;
    }
}
