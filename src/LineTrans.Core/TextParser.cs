using System.Text;
using System.Text.RegularExpressions;

namespace LineTrans.Core;

/// <summary>
/// 文本切分：逐行 / 逐句，以及智能清理、CSV 拆分等工具。
///
/// 本文件是安卓端 <c>app/src/main/java/com/linetrans/app/util/TextParser.kt</c> 的逐条等价移植，
/// Kotlin 版是主实现：凡是网页端 server.js 与 Kotlin 版行为不一致的地方，一律以 Kotlin 版为准。
/// 规则细节见《项目说明与开发指南》§5。
///
/// 移植时特别注意 JVM 与 .NET 的差异：
///   · Java 正则的 \d / \s 都是 ASCII 语义，.NET 的 \d 会匹配 Unicode 数字、\s 会匹配 Unicode 空白，
///     因此这里把 \d 写成 [0-9]、把 \s 写成显式的 ASCII 空白字符集。
///   · Kotlin 的 Char.isWhitespace 是 Character.isWhitespace || Character.isSpaceChar 的并集，
///     与 .NET 的 char.IsWhiteSpace 不完全相同（见 <see cref="IsKotlinWhitespace"/>）。
///   · Kotlin 的 CharSequence.lines() 会同时按 CRLF / LF / CR 拆行，见 <see cref="SplitLines"/>。
/// </summary>
public static class TextParser
{
    /// <summary>SRT / VTT 时间轴行，例如 00:00:01,000 --&gt; 00:00:02,500。</summary>
    private static readonly Regex TimecodeRegex =
        new(@"^[0-9]{1,2}:[0-9]{2}:[0-9]{2}[,.][0-9]{1,3}[ \t\x0B\f\r]*-->[ \t\x0B\f\r]*.*$", RegexOptions.Compiled);

    /// <summary>纯数字序号行（字幕序号、有序列表）。</summary>
    private static readonly Regex IndexOnlyRegex =
        new(@"^[0-9]{1,4}[.、)]?$", RegexOptions.Compiled);

    /// <summary>Markdown 行首标记。</summary>
    private static readonly Regex MdPrefixRegex =
        new(@"^(#{1,6}[ \t\x0B\f\r]+|>[ \t\x0B\f\r]+|[-*+][ \t\x0B\f\r]+|[0-9]+\.[ \t\x0B\f\r]+)", RegexOptions.Compiled);

    /// <summary>连续空白压缩（Kotlin 里是 Regex("[ \t]{2,}")）。</summary>
    private static readonly Regex MultiSpaceRegex = new("[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>段落分隔：一个空行（中间允许夹空格 / 制表符）。</summary>
    private static readonly Regex ParagraphSplitRegex = new("\n[ \t]*\n+", RegexOptions.Compiled);

    /// <summary>缩写识别用的尾部 token。</summary>
    private static readonly Regex AbbrevTokenRegex = new("([A-Za-z][A-Za-z.]*)$", RegexOptions.Compiled);

    /// <summary>句末收尾符：这些字符跟着上一句。</summary>
    private const string Closers = "”’\"'）)]》〉」』】〕｝}";

    /// <summary>软换行合并时，视为“前一行是拉丁文”的尾部字符。</summary>
    private const string LatinPrevChars = ".,;:!?)]}\"'”’";

    /// <summary>软换行合并时，视为“后一行是拉丁文”的首部字符。</summary>
    private const string LatinNextChars = "([{\"'“‘";

    /// <summary>常见缩写，避免 "Mr." "U.S." 被当成句末。</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        "mr", "mrs", "ms", "dr", "prof", "st", "jr", "sr", "vs", "etc", "e.g", "i.e", "a.m", "p.m",
        "no", "fig", "inc", "ltd", "co", "dept", "univ", "approx", "cf", "al", "ibid", "eg", "ie",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
        "mon", "tue", "wed", "thu", "fri", "sat", "sun", "u.s", "u.k", "u.n", "d.c",
        "ph.d", "b.a", "m.a", "b.sc", "m.sc", "vol", "pp", "ed", "eds", "trans",
    };

    // ------------------------------------------------------------------
    // 对外 API（与 Kotlin object TextParser 一一对应）
    // ------------------------------------------------------------------

    /// <summary>
    /// 智能清理：去掉字幕时间轴、序号行与 Markdown 行首标记，并压缩多余空行。
    /// 仅在用户勾选“智能清理”时使用。
    /// </summary>
    public static string SmartClean(string text)
    {
        var kept = new List<string>();
        foreach (string raw in SplitLines(text))
        {
            string line = KotlinTrim(raw);
            if (line.Length == 0) continue;
            if (TimecodeRegex.IsMatch(line)) continue;
            if (IndexOnlyRegex.IsMatch(line)) continue;
            if (line == "WEBVTT" || line.StartsWith("WEBVTT", StringComparison.Ordinal)) continue;
            line = MdPrefixRegex.Replace(line, "");
            if (line.Length == 0) continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    /// <summary>判断文本是否像 CSV / 制表符对照表：大部分行都能切成两段。</summary>
    public static bool LooksLikeTable(string text)
    {
        var lines = new List<string>();
        foreach (string l in SplitLines(text))
        {
            if (TranslationUnit.IsNotBlank(l)) lines.Add(l);
            if (lines.Count == 20) break;
        }
        if (lines.Count < 2) return false;
        int matched = lines.Count(l => SplitSourceTranslation(l) != null);
        return matched >= lines.Count * 0.6;
    }

    /// <summary>按模式切分文本。</summary>
    public static List<TranslationUnit> Parse(string text, UnitMode mode) =>
        mode == UnitMode.LINE ? ParseLines(text) : ParseSentences(text);

    /// <summary>等价 Kotlin 的 String.lines()（按 CRLF / LF / CR 拆行）。</summary>
    public static List<string> SplitLines(string text) => AppSettings.SplitLines(text);

    /// <summary>
    /// 语言探测：返回 auto / ja / ko / zh-CN / en。
    /// 与 Kotlin 版一致：latin 只统计 ASCII 字母。
    /// </summary>
    public static string DetectLanguage(string text)
    {
        if (TranslationUnit.IsBlank(text)) return "auto";
        int cjk = 0, kana = 0, hangul = 0, latin = 0;
        foreach (char c in text)
        {
            if (c >= '\u4e00' && c <= '\u9fff') cjk++;
            else if (c >= '\u3040' && c <= '\u30ff') kana++;
            else if (c >= '\uac00' && c <= '\ud7af') hangul++;
            if (char.IsLetter(c) && c < 128) latin++;
        }
        if (kana > latin) return "ja";
        if (hangul > latin) return "ko";
        if (cjk > latin) return "zh-CN";
        if (latin > cjk) return "en";
        return "auto";
    }

    /// <summary>解析一行里已经存在的「原文 ⇥ 译文」（Tab、,、=&gt;、| 分隔）。</summary>
    public static (string Source, string Translation)? SplitSourceTranslation(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed.Contains('\t'))
        {
            var parts = SplitLimit2(trimmed, '\t');
            if (parts.Count == 2 && TranslationUnit.IsNotBlank(parts[0]) && TranslationUnit.IsNotBlank(parts[1]))
            {
                return (parts[0].Trim(), parts[1].Trim());
            }
        }

        foreach (string sep in new[] { "=>", "⇥", "|" })
        {
            int idx = trimmed.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                string s = trimmed.Substring(0, idx).Trim();
                string t = trimmed.Substring(idx + sep.Length).Trim();
                if (TranslationUnit.IsNotBlank(s) && TranslationUnit.IsNotBlank(t)) return (s, t);
            }
        }

        // CSV：仅当恰好两列且第二列不像纯英文原文时采用
        if (trimmed.Contains(',') && !trimmed.Contains('，'))
        {
            var parts = SplitCsvLine(trimmed);
            if (parts.Count == 2 && TranslationUnit.IsNotBlank(parts[0]) && TranslationUnit.IsNotBlank(parts[1]))
            {
                return (parts[0].Trim().Trim('"'), parts[1].Trim().Trim('"'));
            }
        }

        return null;
    }

    /// <summary>简易 CSV 行拆分，支持双引号包裹与转义引号。</summary>
    public static List<string> SplitCsvLine(string line)
    {
        var outList = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                outList.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
            i++;
        }
        outList.Add(sb.ToString());
        return outList;
    }

    /// <summary>CSV 字段转义（与 Kotlin 版一致）。</summary>
    public static string CsvEscape(string value)
    {
        bool needsQuote = value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0 ||
                          value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0;
        string escaped = value.Replace("\"", "\"\"");
        return needsQuote ? "\"" + escaped + "\"" : escaped;
    }

    /// <summary>构造一条逐行单元。</summary>
    public static TranslationUnit BuildLineUnit(string source, string translation = "") =>
        new(source, translation);

    // ------------------------------------------------------------------
    // 内部实现
    // ------------------------------------------------------------------

    private static List<TranslationUnit> ParseLines(string text)
    {
        var result = new List<TranslationUnit>();
        foreach (string piece in NormalizeNewlines(text).Split('\n'))
        {
            string line = KotlinTrim(piece);
            if (line.Length == 0) continue;
            result.Add(new TranslationUnit(line));
        }
        return result;
    }

    /// <summary>
    /// 逐句切分（与 Kotlin parseSentences 等价）：
    ///  · 空行分段；段落内的软换行先合并（英文补空格、中日韩不补）
    ///  · 只在真正的句末标点断开：。！？!?…；不在英文分号处断开
    ///  · 保护小数点（3.14）、网址（example.com）、缩写（Mr. / U.S. / e.g.）与首字母缩写（J. K.）
    ///  · 省略号连续的点会压成一个 …，句末的引号/括号跟着上一句
    ///  · 只有标点或单字的碎片会并回上一句
    /// </summary>
    private static List<TranslationUnit> ParseSentences(string text)
    {
        var raw = new List<string>();
        foreach (string paragraph in ParagraphSplitRegex.Split(NormalizeNewlines(text)))
        {
            var lines = new List<string>();
            foreach (string piece in paragraph.Split('\n'))
            {
                string line = KotlinTrim(piece);
                if (line.Length > 0) lines.Add(line);
            }
            if (lines.Count == 0) continue;
            SplitInto(raw, JoinSoftLines(lines));
        }

        var merged = new List<string>();
        foreach (string sentence in raw)
        {
            int core = 0;
            foreach (char c in sentence)
            {
                if (char.IsLetterOrDigit(c)) core++;
            }
            if (merged.Count > 0 && core <= 1)
            {
                merged[^1] = merged[^1] + sentence;
            }
            else
            {
                merged.Add(sentence);
            }
        }

        var result = new List<TranslationUnit>();
        foreach (string s in merged)
        {
            string cleaned = KotlinTrim(MultiSpaceRegex.Replace(s, " "));
            if (cleaned.Length == 0) continue;
            result.Add(new TranslationUnit(cleaned));
        }
        return result;
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>段落内的换行多为排版软换行，合并成一行再断句。</summary>
    private static string JoinSoftLines(List<string> lines)
    {
        var sb = new StringBuilder(lines[0]);
        for (int i = 1; i < lines.Count; i++)
        {
            char prev = sb[sb.Length - 1];
            char next = lines[i][0];
            bool latinPrev = (prev >= 'A' && prev <= 'Z') || (prev >= 'a' && prev <= 'z') ||
                             char.IsDigit(prev) || LatinPrevChars.IndexOf(prev) >= 0;
            bool latinNext = (next >= 'A' && next <= 'Z') || (next >= 'a' && next <= 'z') ||
                             char.IsDigit(next) || LatinNextChars.IndexOf(next) >= 0;
            bool cjk = IsCjk(prev) || IsCjk(next);
            if (latinPrev && latinNext && !cjk) sb.Append(' ');
            sb.Append(lines[i]);
        }
        return sb.ToString();
    }

    private static bool IsCjk(char ch)
    {
        int code = ch;
        return (code >= 0x3040 && code <= 0x30FF) || (code >= 0x3400 && code <= 0x4DBF) ||
               (code >= 0x4E00 && code <= 0x9FFF) || (code >= 0xF900 && code <= 0xFAFF) ||
               (code >= 0xFF66 && code <= 0xFF9F);
    }

    private static bool IsSentenceEnd(char ch) =>
        ch == '。' || ch == '！' || ch == '？' || ch == '!' || ch == '?' || ch == '…' || ch == '；';

    private static bool IsAbbreviation(string prefix)
    {
        var match = AbbrevTokenRegex.Match(prefix);
        if (!match.Success) return false;
        string rawToken = match.Groups[1].Value;
        string token = rawToken.ToLowerInvariant().TrimEnd('.');
        string trimmedRaw = rawToken.Trim();
        if (token.Length == 1 && trimmedRaw.Length > 0 && char.IsUpper(trimmedRaw[0])) return true;
        return Abbreviations.Contains(token);
    }

    private static void SplitInto(List<string> outList, string text)
    {
        var buffer = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            buffer.Append(ch);
            bool isDot = ch == '.';
            bool isEllipsis = ch == '…';
            bool isTripleDot = isDot && i >= 2 && string.CompareOrdinal(text.Substring(i - 2, 3), "...") == 0;
            if (!IsSentenceEnd(ch) && !isDot)
            {
                i++;
                continue;
            }

            if (isEllipsis || isTripleDot)
            {
                int start = i;
                while (start > 0 && (text[start - 1] == '.' || text[start - 1] == '…')) start--;
                int end = i + 1;
                while (end < text.Length && (text[end] == '.' || text[end] == '…')) end++;
                int drop = i - start + 1;
                buffer.Length -= drop;
                buffer.Append('…');
                while (end < text.Length && Closers.IndexOf(text[end]) >= 0)
                {
                    buffer.Append(text[end]);
                    end++;
                }
                outList.Add(KotlinTrim(buffer.ToString()));
                buffer = new StringBuilder();
                i = end;
                continue;
            }

            if (isDot)
            {
                char? prev = i - 1 >= 0 ? text[i - 1] : null;
                char? next = i + 1 < text.Length ? text[i + 1] : null;
                if (prev != null && next != null && char.IsDigit(prev.Value) && char.IsDigit(next.Value))
                {
                    i++;
                    continue;
                }
                if (next != null && char.IsLetterOrDigit(next.Value))
                {
                    i++;
                    continue;
                }
                if (IsAbbreviation(buffer.ToString(0, buffer.Length - 1)))
                {
                    i++;
                    continue;
                }
                if (next != null && !IsKotlinWhitespace(next.Value) && Closers.IndexOf(next.Value) < 0)
                {
                    i++;
                    continue;
                }
            }

            int sentenceEnd = i + 1;
            while (sentenceEnd < text.Length && Closers.IndexOf(text[sentenceEnd]) >= 0)
            {
                buffer.Append(text[sentenceEnd]);
                sentenceEnd++;
            }
            string sentence = KotlinTrim(buffer.ToString());
            if (sentence.Length > 0) outList.Add(sentence);
            buffer = new StringBuilder();
            i = sentenceEnd;
        }

        string tail = KotlinTrim(buffer.ToString());
        if (tail.Length > 0) outList.Add(tail);
    }

    /// <summary>
    /// 等价 Kotlin 的 CharSequence.trim()：JVM 上用的是
    /// Character.isWhitespace(c) || Character.isSpaceChar(c)，与 .NET 的 char.IsWhiteSpace 有差异
    /// （.NET 会把 U+0085 当空白，且不认为 U+001C-U+001F 是空白；Kotlin 反过来）。
    /// </summary>
    public static string KotlinTrim(string value)
    {
        int start = 0;
        int end = value.Length;
        while (start < end && IsKotlinWhitespace(value[start])) start++;
        while (end > start && IsKotlinWhitespace(value[end - 1])) end--;
        return value.Substring(start, end - start);
    }

    /// <summary>Character.isWhitespace(c) || Character.isSpaceChar(c)。</summary>
    public static bool IsKotlinWhitespace(char c)
    {
        switch (c)
        {
            case '\t':
            case '\n':
            case '\x0B':
            case '\f':
            case '\r':
            case '\x1C':
            case '\x1D':
            case '\x1E':
            case '\x1F':
            case ' ':
            case '\u00A0':
            case '\u1680':
            case '\u2028':
            case '\u2029':
            case '\u202F':
            case '\u205F':
            case '\u3000':
                return true;
        }
        return c >= '\u2000' && c <= '\u200A';
    }

    /// <summary>等价 Kotlin 的 String.split(delimiter, limit = 2)。</summary>
    private static List<string> SplitLimit2(string text, char delimiter)
    {
        int idx = text.IndexOf(delimiter);
        if (idx < 0) return new List<string> { text };
        return new List<string> { text.Substring(0, idx), text.Substring(idx + 1) };
    }
}
