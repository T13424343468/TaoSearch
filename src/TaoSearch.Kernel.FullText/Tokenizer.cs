using System.Text;

namespace TaoSearch.Kernel.FullText;

/// <summary>
/// 分词器（架构 §5.2）：中文按字二元组（bigram），英文/数字按词归一小写。
/// 零依赖，纯函数可单测；分词器本身可替换（做成独立组件）。
/// </summary>
public static class Tokenizer
{
    /// <summary>产出词元序列（去重由索引层负责）。</summary>
    public static IEnumerable<Token> Tokenize(string text)
    {
        var sb = new StringBuilder();
        int start = 0;
        bool prevCjk = false;

        static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '#';

        static bool IsCjk(char c) => c is >= '\u4e00' and <= '\u9fff';

        for (int i = 0; i <= text.Length; i++)
        {
            bool inWord = i < text.Length && IsWordChar(text[i]);
            if (inWord)
            {
                if (sb.Length == 0) start = i;
                sb.Append(char.ToLowerInvariant(text[i]));
                prevCjk = false;
                continue;
            }
            if (sb.Length > 0)
            {
                yield return new Token(sb.ToString(), start, sb.Length);
                sb.Clear();
            }
            // 中文（含 CJK）按字二元组；末字已被前一 bigram 覆盖时不再补单字
            if (i < text.Length && IsCjk(text[i]))
            {
                if (i + 1 < text.Length && IsCjk(text[i + 1]))
                    yield return new Token(text.Substring(i, 2), i, 2);
                else if (!prevCjk)
                    yield return new Token(text[i].ToString(), i, 1);
            }
            prevCjk = i < text.Length && IsCjk(text[i]);
        }
    }

    public readonly record struct Token(string Value, int Offset, int Length);
}
