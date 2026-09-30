using System.Text;
using System.Text.RegularExpressions;

using DictSource = LineTrans.Core.Dictionary.DictionarySource;
using DefLang = LineTrans.Core.Dictionary.DefinitionLanguage;

namespace LineTrans.Core.Dictionary;

/// <summary>释义来源（与安卓端 <c>DictionaryService.Source</c> 对齐）。</summary>
public static class DictionarySource
{
    /// <summary>本地词库。</summary>
    public const string Local = "local";

    /// <summary>牛津词典（网页）。</summary>
    public const string OxfordWeb = "oxford_web";

    /// <summary>牛津词典（API）。</summary>
    public const string OxfordApi = "oxford_api";

    /// <summary>Wiktionary。</summary>
    public const string Wiktionary = "wiktionary";

    /// <summary>AI 释义。</summary>
    public const string Ai = "ai";

    /// <summary>来源 id → 中文标签。</summary>
    public static string Label(string id) => id switch
    {
        Local => "本地词库",
        OxfordWeb => "牛津词典（网页）",
        OxfordApi => "牛津词典（API）",
        Wiktionary => "Wiktionary",
        Ai => "AI 释义",
        _ => id,
    };

    /// <summary>可选来源顺序（与安卓端 buildOrder 的 else 分支一致）。</summary>
    public static readonly string[] DefaultOrder = { Local, OxfordWeb, OxfordApi, Wiktionary, Ai };
}

/// <summary>划词查义的服务侧配置（对应安卓端 AppSettings 里与词典相关的字段）。</summary>
public sealed class DictionaryOptions
{
    /// <summary>首选来源 id，非法值回落 <see cref="DictionarySource.Local"/>。</summary>
    public string DictionarySource { get; set; } = DictSource.Local;

    /// <summary>是否启用本地词库（关闭后 local 从顺序中移除）。</summary>
    public bool LocalDictionaryEnabled { get; set; } = true;

    /// <summary>是否用 AI 补一条中文解释。</summary>
    public bool DictionaryAiExplain { get; set; }

    private string _definitionLanguage = DefLang.Zh;

    /// <summary>
    /// 释义语言：zh / both / en，非法值回落 zh。
    /// 与安卓端 SettingsRepository 在读取设置时做规范化的边界一致（SettingsRepository.kt:59），
    /// 因此配置对象里永远不会持有非法值。
    /// </summary>
    public string DefinitionLanguage
    {
        get => _definitionLanguage;
        set => _definitionLanguage = DefLang.Normalize(value);
    }

    /// <summary>牛津 API 的 app_id。</summary>
    public string OxfordAppId { get; set; } = string.Empty;

    /// <summary>牛津 API 的 app_key。</summary>
    public string OxfordAppKey { get; set; } = string.Empty;

    /// <summary>条目缓存容量（对应安卓端 removeEldestEntry size &gt; 200）。</summary>
    public int CacheCapacity { get; set; } = 200;
}

/// <summary>AI 兜底接口。本类库不直接联网，由上层注入实现。</summary>
public interface IAiExplainProvider
{
    /// <summary>用已配置的模型生成简明中文释义（词条正文）。</summary>
    Task<string?> ExplainWordAsync(string word, CancellationToken cancellationToken = default);

    /// <summary>把英文释义压缩成一行简短中文。</summary>
    Task<string?> ExplainShortAsync(string word, IReadOnlyList<string> definitions, CancellationToken cancellationToken = default);

    /// <summary>直接生成一条完整释义（对应安卓端的 AI 来源）。</summary>
    Task<DictEntry?> EntryAsync(string word, CancellationToken cancellationToken = default);
}

/// <summary>
/// 划词查义服务：与安卓端 <c>DictionaryService</c> 同构，但把联网来源抽象成可注入的取词函数，
/// 因此本类库既可以在 PC 端接真实网络，也可以在 headless 测试里完全离线运行。
/// 本地词库永远排在最前面，离线秒出。
/// </summary>
public sealed class DictionaryService
{
    private const string OxfordWebBase = "https://www.oxfordlearnersdictionaries.com/definition/english/";
    private const string OxfordApiBase = "https://od-api.oxforddictionaries.com/api/v2/entries/en-gb/";
    private const string WiktBase = "https://api.dictionaryapi.dev/api/v2/entries/en/";

    private readonly DictionaryOptions _options;
    private readonly IAiExplainProvider? _ai;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _httpFetcher;
    private readonly LruCache<DictEntry> _cache;
    private readonly Dictionary<string, long> _sourceCalls = new(StringComparer.Ordinal);
    private readonly object _statGate = new();

    /// <summary>创建服务。</summary>
    /// <param name="options">配置。</param>
    /// <param name="ai">AI 兜底实现，可为 null（离线模式）。</param>
    /// <param name="httpFetcher">联网取词函数 <c>(url, headers, ct) =&gt; text</c>，可为 null（离线模式）。</param>
    public DictionaryService(
        DictionaryOptions? options = null,
        IAiExplainProvider? ai = null,
        Func<string, string, CancellationToken, Task<string?>>? httpFetcher = null)
    {
        _options = options ?? new DictionaryOptions();
        _options.DefinitionLanguage = DefLang.Normalize(_options.DefinitionLanguage);
        _ai = ai;
        _httpFetcher = httpFetcher;
        _cache = new LruCache<DictEntry>(Math.Max(1, _options.CacheCapacity));
    }

    /// <summary>本地词库命中标签。</summary>
    public const string LocalLabel = "本地词库";

    /// <summary>缓存容量。</summary>
    public int CacheCapacity => _cache.Capacity;

    /// <summary>缓存当前条目数。</summary>
    public int CachedCount => _cache.Count;

    /// <summary>取已缓存的条目（key 为小写单词）。</summary>
    public DictEntry? Cached(string word)
    {
        var key = (word ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0) return null;
        return _cache.TryGet(key, out var entry) ? entry : null;
    }

    /// <summary>清空缓存。</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>某个来源被真正调用过的次数（测试用）。</summary>
    public long SourceCallCount(string source)
    {
        lock (_statGate) return _sourceCalls.TryGetValue(source, out var n) ? n : 0;
    }

    /// <summary>全部来源的调用统计快照。</summary>
    public IReadOnlyDictionary<string, long> SourceCallSnapshot()
    {
        lock (_statGate) return new Dictionary<string, long>(_sourceCalls, StringComparer.Ordinal);
    }

    private void CountCall(string source)
    {
        lock (_statGate)
        {
            _sourceCalls.TryGetValue(source, out var n);
            _sourceCalls[source] = n + 1;
        }
    }

    /// <summary>
    /// 划词查义主入口：按 <see cref="DictionaryOptions.DictionarySource"/> 决定的顺序依次尝试，
    /// 命中即缓存并返回；全部失败返回带 error 的空结果（不抛异常）。
    /// </summary>
    public async Task<DictEntry> LookupAsync(string word, CancellationToken cancellationToken = default)
    {
        var key = (word ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0)
        {
            return new DictEntry { Word = word ?? string.Empty, Error = "没有查到这个词的释义" };
        }

        if (_cache.TryGet(key, out var cachedEntry) && cachedEntry is not null) return cachedEntry;

        string? lastError = null;
        foreach (var source in BuildOrder(_options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DictEntry? entry = null;
            try
            {
                CountCall(source);
                entry = await FetchAsync(source, word!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                entry = null;
            }

            if (entry is not null && entry.Ok)
            {
                var finalEntry = entry;
                var needsExplain = _options.DictionaryAiExplain
                    && string.IsNullOrWhiteSpace(finalEntry.Translation)
                    && _ai is not null
                    && !string.Equals(source, DictionarySource.Ai, StringComparison.Ordinal);
                if (needsExplain)
                {
                    try
                    {
                        var explained = await _ai!.ExplainShortAsync(
                            word!, finalEntry.Senses.Select(s => s.Definition).ToList(), cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(explained))
                        {
                            finalEntry = new DictEntry
                            {
                                Word = finalEntry.Word,
                                Phonetic = finalEntry.Phonetic,
                                Senses = finalEntry.Senses,
                                Translation = explained,
                                Source = finalEntry.Source,
                                Url = finalEntry.Url,
                                Error = finalEntry.Error,
                                Match = finalEntry.Match,
                            };
                        }
                    }
                    catch
                    {
                        // AI 兜底失败不影响主结果
                    }
                }

                _cache.Set(key, finalEntry);
                return finalEntry;
            }

            if (!string.IsNullOrWhiteSpace(entry?.Error)) lastError = entry!.Error;
        }

        return new DictEntry { Word = word ?? string.Empty, Error = lastError ?? "没有查到这个词的释义" };
    }

    /// <summary>与安卓端 buildOrder 等价的来源顺序计算。</summary>
    public static IReadOnlyList<string> BuildOrder(DictionaryOptions settings)
    {
        IReadOnlyList<string> order = settings.DictionarySource switch
        {
            DictionarySource.Local => new[] { DictionarySource.Local, DictionarySource.OxfordWeb, DictionarySource.Wiktionary, DictionarySource.Ai },
            DictionarySource.OxfordApi => new[] { DictionarySource.Local, DictionarySource.OxfordApi, DictionarySource.OxfordWeb, DictionarySource.Ai, DictionarySource.Wiktionary },
            DictionarySource.OxfordWeb => new[] { DictionarySource.Local, DictionarySource.OxfordWeb, DictionarySource.OxfordApi, DictionarySource.Ai, DictionarySource.Wiktionary },
            DictionarySource.Wiktionary => new[] { DictionarySource.Local, DictionarySource.Wiktionary, DictionarySource.OxfordWeb, DictionarySource.Ai },
            DictionarySource.Ai => new[] { DictionarySource.Local, DictionarySource.Ai, DictionarySource.OxfordWeb, DictionarySource.Wiktionary },
            _ => DictionarySource.DefaultOrder,
        };
        return settings.LocalDictionaryEnabled ? order : order.Where(s => s != DictionarySource.Local).ToArray();
    }

    private async Task<DictEntry?> FetchAsync(string source, string word, CancellationToken cancellationToken)
        => source switch
        {
            DictionarySource.Local => Local(word),
            DictionarySource.Ai => await AiEntryAsync(word, cancellationToken).ConfigureAwait(false),
            DictionarySource.OxfordWeb => await OxfordWebAsync(word, cancellationToken).ConfigureAwait(false),
            DictionarySource.OxfordApi => await OxfordApiAsync(word, cancellationToken).ConfigureAwait(false),
            DictionarySource.Wiktionary => await WiktionaryAsync(word, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

    // ---------- 本地词库（离线，最快） ----------

    /// <summary>本地词库查询：未加载完成时先等待加载，保证划词能出结果。</summary>
    public static LookupResult LocalLookup(string word)
    {
        if (!LocalDictionary.IsLoaded)
        {
            try { LocalDictionary.EnsureLoadedAsync().GetAwaiter().GetResult(); }
            catch { /* 加载失败按未命中处理 */ }
        }
        return LocalDictionary.Lookup(word);
    }

    /// <summary>把本地词库命中转换成完整条目。</summary>
    public static DictEntry Local(string word)
    {
        var hit = LocalLookup(word);
        if (!hit.Found || hit.Item is null)
        {
            return new DictEntry
            {
                Word = word,
                Source = DictionarySource.Label(DictionarySource.Local),
                Error = "本地词库没有收录",
            };
        }

        return new DictEntry
        {
            Word = hit.BaseWord,
            Phonetic = FormatPhonetic(hit.Item.Phonetic),
            Senses = SplitSenses(hit.Item.Meaning),
            Source = DictionarySource.Label(DictionarySource.Local),
            Url = OxfordWebBase + word.ToLowerInvariant(),
            Match = hit.Match,
        };
    }

    /// <summary>把音标包成 /.../ 形式（已是斜杠开头则原样返回）。</summary>
    public static string? FormatPhonetic(string? phonetic)
    {
        var p = (phonetic ?? string.Empty).Trim();
        if (p.Length == 0) return null;
        return p.StartsWith('/') ? p : "/" + p + "/";
    }

    /// <summary>把释义文本拆成词义列表：\n 与「；」都算分隔符，最多 6 条。</summary>
    public static IReadOnlyList<DictSense> SplitSenses(string meaning, int max = 6)
    {
        if (string.IsNullOrWhiteSpace(meaning)) return Array.Empty<DictSense>();
        var normalized = meaning.Replace("\\n", "\n");
        var parts = normalized
            .Split(new[] { '\n', '\r', '；', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Take(max)
            .Select(SplitPos)
            .ToList();
        return parts;
    }

    // 词性前缀：「n. 」「vt. 」「[计] 」等，长度不超过 12 个字符
    private static readonly Regex PosRegex = new(@"^(?<pos>(?:[A-Za-z]+|\[[^\]]+\])[\.\s]+)\s*(?<rest>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>把「n. 名词解释」拆成词性 + 释义；<c>[网络]</c> 之类的标记也作为词性展示。</summary>
    public static DictSense SplitPos(string line)
    {
        var clean = (line ?? string.Empty).Trim();
        if (clean.Length == 0) return new DictSense { PartOfSpeech = string.Empty, Definition = string.Empty };

        var m = PosRegex.Match(clean);
        if (m.Success && m.Groups["pos"].Value.Trim().Length <= 12)
        {
            var rest = m.Groups["rest"].Value.Trim();
            if (rest.Length > 0)
            {
                return new DictSense
                {
                    PartOfSpeech = m.Groups["pos"].Value.Trim().TrimEnd('.'),
                    Definition = rest,
                };
            }
        }
        return new DictSense { PartOfSpeech = string.Empty, Definition = clean };
    }

    // ---------- 联网来源（需要上层注入 httpFetcher，未注入则视为不可用） ----------

    private async Task<DictEntry?> OxfordWebAsync(string word, CancellationToken cancellationToken)
    {
        var url = OxfordWebBase + word.ToLowerInvariant();
        var html = await FetchTextAsync(url, string.Empty, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("无法访问牛津词典网页");
        if (html.Contains("No exact results found") || html.Contains("we couldn't find"))
        {
            return new DictEntry { Word = word, Source = DictionarySource.Label(DictionarySource.OxfordWeb), Url = url, Error = "牛津词典没有收录这个词" };
        }

        var phonetic = FirstGroup(html, "class=\"phon\"[^>]*>(.*?)<");
        var poses = AllGroups(html, "class=\"pos\"[^>]*>(.*?)<");
        var defs = AllGroups(html, "class=\"def\"[^>]*>(.*?)</span>").Take(5).ToList();
        if (defs.Count == 0)
        {
            return new DictEntry { Word = word, Source = DictionarySource.Label(DictionarySource.OxfordWeb), Url = url, Error = "牛津网页没有解析到释义" };
        }

        var senses = new List<DictSense>(defs.Count);
        for (var i = 0; i < defs.Count; i++)
        {
            var pos = i < poses.Count ? poses[i] : (poses.Count > 0 ? poses[0] : string.Empty);
            senses.Add(new DictSense { PartOfSpeech = pos, Definition = defs[i] });
        }
        return new DictEntry
        {
            Word = word,
            Phonetic = (phonetic != null && phonetic.Length > 0 && phonetic.Length < 40) ? phonetic : null,
            Senses = senses,
            Source = DictionarySource.Label(DictionarySource.OxfordWeb),
            Url = url,
        };
    }

    private async Task<DictEntry?> OxfordApiAsync(string word, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.OxfordAppId) || string.IsNullOrWhiteSpace(_options.OxfordAppKey))
        {
            throw new InvalidOperationException("未配置牛津 API 的 app_id / app_key");
        }
        var url = OxfordApiBase + word.ToLowerInvariant() + "?fields=definitions,examples,pronunciations";
        var headers = "app_id:" + _options.OxfordAppId + "\napp_key:" + _options.OxfordAppKey;
        var text = await FetchTextAsync(url, headers, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("牛津 API 无响应");
        // 完整 JSON 解析留给上层（本类库零第三方依赖，接口已留好）；这里先把原始文本作为译文返回。
        return new DictEntry
        {
            Word = word,
            Translation = text,
            Source = DictionarySource.Label(DictionarySource.OxfordApi),
            Url = OxfordWebBase + word.ToLowerInvariant(),
        };
    }

    private async Task<DictEntry?> WiktionaryAsync(string word, CancellationToken cancellationToken)
    {
        var url = WiktBase + word.ToLowerInvariant();
        var text = await FetchTextAsync(url, string.Empty, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Wiktionary 无响应");
        return new DictEntry
        {
            Word = word,
            Translation = text,
            Source = DictionarySource.Label(DictionarySource.Wiktionary),
            Url = "https://en.wiktionary.org/wiki/" + word.ToLowerInvariant(),
        };
    }

    private async Task<DictEntry?> AiEntryAsync(string word, CancellationToken cancellationToken)
    {
        if (_ai is null) throw new InvalidOperationException("AI 服务不可用");
        return await _ai.EntryAsync(word, cancellationToken).ConfigureAwait(false);
    }

    private Task<string?> FetchTextAsync(string url, string headers, CancellationToken cancellationToken)
    {
        if (_httpFetcher is null) throw new InvalidOperationException("未注入联网取词函数，当前为离线模式");
        return _httpFetcher(url, headers, cancellationToken);
    }

    private static string? FirstGroup(string html, string pattern)
    {
        var m = Regex.Match(html, pattern, RegexOptions.Singleline);
        return m.Success ? StripTags(m.Groups[1].Value) : null;
    }

    private static List<string> AllGroups(string html, string pattern)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(html, pattern, RegexOptions.Singleline))
        {
            var text = StripTags(m.Groups[1].Value);
            if (text.Length > 0) list.Add(text);
        }
        return list;
    }

    /// <summary>去掉 HTML 标签与常见实体。</summary>
    public static string StripTags(string html)
    {
        var s = Regex.Replace(html ?? string.Empty, "<[^>]+>", string.Empty);
        s = s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
             .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " ");
        return Regex.Replace(s, "\\s+", " ").Trim();
    }

    /// <summary>把多条释义拼成一行（AI 提示词 / 纯文本展示用）。</summary>
    public static string JoinSenses(IEnumerable<DictSense> senses, int take = 3)
    {
        var sb = new StringBuilder();
        var i = 0;
        foreach (var s in senses)
        {
            if (i++ >= take) break;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(s.PartOfSpeech.Length > 0 ? s.PartOfSpeech + ". " + s.Definition : s.Definition);
        }
        return sb.ToString();
    }
}
