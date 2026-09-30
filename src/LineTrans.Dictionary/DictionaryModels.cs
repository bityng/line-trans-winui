namespace LineTrans.Core.Dictionary;

/// <summary>
/// 一条词典词条，对应安卓端 <c>LocalDictionary.Item</c>。
/// </summary>
public sealed class DictItem
{
    /// <summary>词条（规范化为小写）。</summary>
    public string Word { get; init; } = string.Empty;

    /// <summary>音标，可能为空字符串。</summary>
    public string Phonetic { get; init; } = string.Empty;

    /// <summary>中文释义，多条之间用「；」或 \n 分隔。</summary>
    public string Meaning { get; init; } = string.Empty;

    /// <summary>便于调试显示的短描述。</summary>
    public override string ToString()
        => $"{Word} [{Phonetic}] {Meaning}";
}

/// <summary>
/// 查词命中方式，对应安卓端「直接命中 / 词形还原 / 规则变形」三级回退。
/// </summary>
public enum LookupMatchKind
{
    /// <summary>未命中。</summary>
    None = 0,

    /// <summary>用户导入词典命中（优先级最高）。</summary>
    Import = 1,

    /// <summary>core.tsv 直接命中。</summary>
    Direct = 2,

    /// <summary>经 lemma.tsv 词形还原后命中原形（如 ran → run）。</summary>
    Lemma = 3,

    /// <summary>经规则变形后命中（如 running → run）。</summary>
    Variant = 4,
}

/// <summary>查词结果。未命中时 <see cref="Found"/> 为 false，其余字段为默认值。</summary>
public sealed class LookupResult
{
    /// <summary>是否命中。</summary>
    public bool Found { get; init; }

    /// <summary>用户传入的原始文本（未清洗）。</summary>
    public string RawWord { get; init; } = string.Empty;

    /// <summary>清洗后的查询词（小写）。</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>实际命中的词条所属原形（即命中条目自身的词头）。</summary>
    public string BaseWord { get; init; } = string.Empty;

    /// <summary>命中方式。</summary>
    public LookupMatchKind Match { get; init; } = LookupMatchKind.None;

    /// <summary>命中方式的中文标签（direct / lemma / variant / import / miss）。</summary>
    public string Via { get; init; } = "miss";

    /// <summary>命中的词条；未命中为 null。</summary>
    public DictItem? Item { get; init; }

    /// <summary>音标（未命中为空串）。</summary>
    public string Phonetic => Item?.Phonetic ?? string.Empty;

    /// <summary>释义（未命中为空串）。</summary>
    public string Meaning => Item?.Meaning ?? string.Empty;

    /// <summary>构造一个未命中结果。</summary>
    public static LookupResult Miss(string raw, string query)
        => new() { Found = false, RawWord = raw, Query = query, Via = "miss" };

    /// <summary>便于调试显示的短描述。</summary>
    public override string ToString()
        => Found ? $"{Query} -> {BaseWord} via={Via}" : $"miss({Query})";
}

/// <summary>释义语言。<see cref="Normalize"/> 把非法值回落为 <see cref="Zh"/>。</summary>
public static class DefinitionLanguage
{
    /// <summary>仅中文。</summary>
    public const string Zh = "zh";

    /// <summary>中英对照。</summary>
    public const string Both = "both";

    /// <summary>仅英文。</summary>
    public const string En = "en";

    /// <summary>合法取值集合。</summary>
    public static readonly string[] Allowed = { Zh, Both, En };

    /// <summary>规范化：null / 空 / 非法值一律回落 zh。</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Zh;
        var v = value.Trim().ToLowerInvariant();
        return v switch
        {
            Zh => Zh,
            Both => Both,
            En => En,
            _ => Zh,
        };
    }

    /// <summary>是否为合法取值。</summary>
    public static bool IsValid(string? value)
        => value is not null && Array.IndexOf(Allowed, value.Trim().ToLowerInvariant()) >= 0;
}

/// <summary>一条拆分好的释义（词性 + 释义正文）。</summary>
public sealed class DictSense
{
    /// <summary>词性，如 n. / v. / a.；无法识别时为空串。</summary>
    public string PartOfSpeech { get; init; } = string.Empty;

    /// <summary>释义正文。</summary>
    public string Definition { get; init; } = string.Empty;

    /// <summary>例句（离线词库没有，保留字段与安卓端对齐）。</summary>
    public string? Example { get; init; }

    /// <summary>便于调试显示的短描述。</summary>
    public override string ToString()
        => PartOfSpeech.Length == 0 ? Definition : $"{PartOfSpeech}: {Definition}";
}

/// <summary>
/// 一条完整词典释义结果，对应安卓端 <c>DictionaryService.Entry</c>。
/// </summary>
public sealed class DictEntry
{
    /// <summary>查询词。</summary>
    public string Word { get; init; } = string.Empty;

    /// <summary>音标，形如 /rʌn/；无音标为 null。</summary>
    public string? Phonetic { get; init; }

    /// <summary>释义条目列表。</summary>
    public IReadOnlyList<DictSense> Senses { get; init; } = Array.Empty<DictSense>();

    /// <summary>整段中文释义（AI 兜底 / 英文释义场景使用）。</summary>
    public string? Translation { get; init; }

    /// <summary>来源标签，如「本地词库」「牛津词典（网页）」。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>来源 URL（离线词库指向牛津词条页，便于联网时跳转）。</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>错误说明（未命中时非空）。</summary>
    public string? Error { get; init; }

    /// <summary>命中方式（离线词库专用，联网来源为 None）。</summary>
    public LookupMatchKind Match { get; init; } = LookupMatchKind.None;

    /// <summary>是否算「查到」。与安卓端一致：有词义或有译文即算成功。</summary>
    public bool Ok => Senses.Count > 0 || !string.IsNullOrWhiteSpace(Translation);

    /// <summary>便于调试显示的短描述。</summary>
    public override string ToString()
        => Ok ? $"{Word} ({Source}) {Senses.Count} senses" : $"{Word} ({Source}) error={Error}";
}
