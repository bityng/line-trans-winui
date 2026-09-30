using System.Collections.Concurrent;

namespace LineTrans.Core.Dictionary;

/// <summary>
/// 离线英汉词库（不依赖 WinUI / Android，可 headless 测试）。
///
/// 数据来源：
///  - 内置 <c>core.tsv</c>：每行「单词 TAB 音标 TAB 中文释义」，音标可留空；
///  - 内置 <c>lemma.tsv</c>：每行「词形 TAB 原形」，用于 ran → run 之类的还原；
///  - 用户导入词典：内存字典（<see cref="ImportEntries(IEnumerable{DictItem})"/>），优先级最高。
///
/// 与安卓端 <c>LocalDictionary</c> 的等价点：
///  1. 取词清洗：Trim → 去掉首尾「既不是 Unicode 字母、也不是 - 或 '」的字符 → 小写；
///  2. 查询顺序：导入词典 → core 直接命中 → lemma 还原 → 规则变形 → 未命中；
///  3. 规则变形条件与顺序逐条一致（见 <see cref="Variants"/>）。
/// </summary>
public static class LocalDictionary
{
    /// <summary>LRU 缓存容量上限（对应 Kotlin 的 removeEldestEntry size &gt; 200）。</summary>
    public const int LookupCacheCapacity = 200;

    private const int CoreCapacityHint = 40000;
    private const int LemmaCapacityHint = 120000;

    private static readonly object LoadGate = new();
    private static readonly LruCache<LookupResult> Cache = new(LookupCacheCapacity);

    private static Dictionary<string, DictItem> _map = new(CoreCapacityHint, StringComparer.Ordinal);
    private static Dictionary<string, string> _lemmas = new(LemmaCapacityHint, StringComparer.Ordinal);
    private static Dictionary<string, DictItem> _import = new(StringComparer.Ordinal);

    private static volatile bool _storeLoaded;
    private static volatile bool _explicitFilesLoaded;
    private static Exception? _lastLoadError;

    // ---- 统计计数器（测试用，证明缓存确实回表/不回表）----
    private static long _cacheLookups;
    private static long _cacheHits;
    private static long _coreProbes;
    private static long _lemmaProbes;

    // ---------------------------------------------------------------- 状态

    /// <summary>core 与 lemma 是否都已加载完成。</summary>
    public static bool IsLoaded => _storeLoaded;

    /// <summary>core.tsv 加载到的词条数。</summary>
    public static int Size => _map.Count;

    /// <summary>lemma.tsv 加载到的可用映射条数。</summary>
    public static int LemmaSize => _lemmas.Count;

    /// <summary>用户导入词典的词条数。</summary>
    public static int ImportSize => _import.Count;

    /// <summary>LRU 缓存当前条目数。</summary>
    public static int CacheCount => Cache.Count;

    /// <summary>LRU 缓存容量。</summary>
    public static int CacheCapacity => Cache.Capacity;

    /// <summary>最近一次加载失败的异常；未失败为 null。</summary>
    public static Exception? LastLoadError => _lastLoadError;

    /// <summary>累计查词次数（含未命中）。</summary>
    public static long CacheLookups => Interlocked.Read(ref _cacheLookups);

    /// <summary>累计命中 LRU 缓存的次数。</summary>
    public static long CacheHits => Interlocked.Read(ref _cacheHits);

    /// <summary>累计真正回表探测 core/lemma 的次数（用于证明二次查询不再回表）。</summary>
    public static long CoreProbes => Interlocked.Read(ref _coreProbes);

    /// <summary>累计探测 lemma 表的次数。</summary>
    public static long LemmaProbes => Interlocked.Read(ref _lemmaProbes);

    // ---------------------------------------------------------------- 加载

    /// <summary>
    /// 后台加载：优先用显式指定的文件路径；未指定时按「exe 同目录 → 当前目录 → data 子目录」探测文件名。
    /// 加载完成前调用 <see cref="Lookup"/> 会直接返回未命中（不阻塞调用方）。
    /// </summary>
    /// <param name="corePath">core.tsv 路径，可为 null。</param>
    /// <param name="lemmaPath">lemma.tsv 路径，可为 null。</param>
    public static Task EnsureLoadedAsync(string? corePath = null, string? lemmaPath = null)
    {
        if (_storeLoaded) return Task.CompletedTask;
        if (_explicitFilesLoaded) return Task.CompletedTask;

        lock (LoadGate)
        {
            if (_storeLoaded || _explicitFilesLoaded) return Task.CompletedTask;
            return Task.Run(() =>
            {
                lock (LoadGate)
                {
                    if (_storeLoaded || _explicitFilesLoaded) return;
                    var core = corePath ?? ResolveDataFile("core.tsv");
                    var lemma = lemmaPath ?? ResolveDataFile("lemma.tsv");
                    LoadFiles(core, lemma);
                    _explicitFilesLoaded = true;
                }
            });
        }
    }

    /// <summary>
    /// 同步加载（headless 测试 / 启动预热用）。文件不存在或读取失败不会抛异常，
    /// 具体原因记在 <see cref="LastLoadError"/>。
    /// </summary>
    /// <returns>core 与 lemma 是否都读到了（文件不存在则为 false）。</returns>
    public static bool LoadFiles(string? corePath, string? lemmaPath)
    {
        var coreOk = LoadCore(corePath);
        var lemmaOk = LoadLemma(lemmaPath);
        _storeLoaded = coreOk && lemmaOk;
        return _storeLoaded;
    }

    private static bool LoadCore(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var target = new Dictionary<string, DictItem>(CoreCapacityHint, StringComparer.Ordinal);
            foreach (var line in File.ReadLines(path!))
            {
                ParseCoreLine(line, target);
            }
            lock (LoadGate)
            {
                _map = target;
                Cache.ClearEntries();
            }
            return true;
        }
        catch (Exception ex)
        {
            _lastLoadError = ex;
            return false;
        }
    }

    private static bool LoadLemma(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var target = new Dictionary<string, string>(LemmaCapacityHint, StringComparer.Ordinal);
            foreach (var line in File.ReadLines(path!))
            {
                ParseLemmaLine(line, target);
            }
            lock (LoadGate)
            {
                _lemmas = target;
                Cache.ClearEntries();
            }
            return true;
        }
        catch (Exception ex)
        {
            _lastLoadError = ex;
            return false;
        }
    }

    /// <summary>解析 core.tsv 的一行。格式：单词 TAB 音标 TAB 释义（列数不足时按「单词 TAB 释义」处理）。</summary>
    internal static void ParseCoreLine(string line, Dictionary<string, DictItem> target)
    {
        if (line.Length == 0 || line[0] == '#') return;
        var parts = line.Split('\t');
        if (parts.Length < 2) return;

        var word = parts[0].Trim().ToLowerInvariant();
        if (word.Length == 0) return;

        string phonetic;
        string meaning;
        if (parts.Length >= 3)
        {
            phonetic = parts[1].Trim();
            meaning = string.Join(" ", parts.Skip(2)).Trim();
        }
        else
        {
            phonetic = string.Empty;
            meaning = parts[1].Trim();
        }
        if (meaning.Length == 0) return;

        // 后出现的行覆盖先出现的行（对应 Kotlin 中「用户导入的词典覆盖内置条目」的赋值语义）
        target[word] = new DictItem { Word = word, Phonetic = phonetic, Meaning = meaning };
    }

    /// <summary>解析 lemma.tsv 的一行。格式：词形 TAB 原形；第二列恒为 "->" 的坏文件会被自动跳过。</summary>
    internal static void ParseLemmaLine(string line, Dictionary<string, string> target)
    {
        if (line.Length == 0 || line[0] == '#') return;
        var parts = line.Split('\t');
        if (parts.Length < 2) return;

        var form = parts[0].Trim().ToLowerInvariant();
        var baseWord = parts[1].Trim().ToLowerInvariant();
        if (form.Length == 0 || baseWord.Length == 0) return;
        // 安卓仓库那份旧 lemma.tsv 的第二列恒为 "->"，那不是有效原形，直接跳过。
        if (baseWord == "->" || baseWord == "-" || baseWord == "=>") return;
        if (form == baseWord) return;

        target[form] = baseWord;
    }

    /// <summary>按候选目录顺序探测数据文件。</summary>
    public static string? ResolveDataFile(string fileName)
    {
        foreach (var dir in CandidateDataDirs())
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>候选数据目录：exe 同目录 → 当前目录 → 其下的 data 子目录 → 向上回溯若干层的 data 目录。</summary>
    public static IEnumerable<string> CandidateDataDirs()
    {
        var bases = new List<string>();
        try { bases.Add(AppContext.BaseDirectory); } catch { /* ignore */ }
        try { bases.Add(Directory.GetCurrentDirectory()); } catch { /* ignore */ }

        foreach (var b in bases)
        {
            yield return b;
            yield return Path.Combine(b, "data");
        }

        // 源码树里跑测试时向上回溯，找到 .work/native-dict/data
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(probe); i++)
        {
            yield return Path.Combine(probe, "data");
            var parent = Path.GetDirectoryName(probe.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent) || parent == probe) break;
            probe = parent;
        }
    }

    // ---------------------------------------------------------------- 导入词典

    /// <summary>用一批词条替换用户导入词典（内存实现，上层可换成真实文件 / 数据库）。</summary>
    public static void ImportEntries(IEnumerable<DictItem> items)
    {
        var target = new Dictionary<string, DictItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = item.Word.Trim().ToLowerInvariant();
            if (key.Length == 0 || string.IsNullOrWhiteSpace(item.Meaning)) continue;
            target[key] = new DictItem { Word = key, Phonetic = item.Phonetic.Trim(), Meaning = item.Meaning.Trim() };
        }
        lock (LoadGate)
        {
            _import = target;
            Cache.ClearEntries();
        }
    }

    /// <summary>从文本导入（每行「词 TAB [音标 TAB] 释义」或「词 = 音标 = 释义」），返回导入条数。</summary>
    public static int ImportText(string text)
    {
        var list = new List<DictItem>();
        foreach (var raw in text.Split('\n'))
        {
            var parsed = ParseImportLine(raw);
            if (parsed is not null) list.Add(parsed);
        }
        ImportEntries(list);
        return list.Count;
    }

    /// <summary>清空用户导入词典。</summary>
    public static void ClearImport()
    {
        lock (LoadGate)
        {
            _import = new Dictionary<string, DictItem>(StringComparer.Ordinal);
            Cache.ClearEntries();
        }
    }

    /// <summary>解析一行导入数据，返回规范化词条；不合格返回 null。</summary>
    internal static DictItem? ParseImportLine(string raw)
    {
        var line = raw.TrimEnd('\r', '\n').TrimEnd();
        if (line.Length == 0 || line.StartsWith('#')) return null;

        string word;
        string phonetic;
        string meaning;
        if (line.Contains('\t'))
        {
            var parts = line.Split('\t').Select(p => p.Trim()).ToArray();
            if (parts.Length < 2) return null;
            word = parts[0];
            if (parts.Length >= 3)
            {
                phonetic = parts[1];
                meaning = string.Join(" ", parts.Skip(2));
            }
            else
            {
                phonetic = string.Empty;
                meaning = parts[1];
            }
        }
        else if (line.Contains('='))
        {
            var parts = line.Split('=').Select(p => p.Trim()).ToArray();
            if (parts.Length < 2) return null;
            word = parts[0];
            if (parts.Length >= 3)
            {
                phonetic = parts[1];
                meaning = string.Join(" = ", parts.Skip(2));
            }
            else
            {
                phonetic = string.Empty;
                meaning = parts[1];
            }
        }
        else
        {
            return null;
        }

        word = word.Trim().ToLowerInvariant();
        if (word.Length == 0 || meaning.Length == 0) return null;
        // 只保留带中文的释义（与安卓端一致，避免把纯英文释义也塞进来）
        if (!ContainsChinese(meaning)) return null;

        var normalized = meaning.Replace("\\n", "；").Replace("\n", "；").Trim();
        return new DictItem { Word = word, Phonetic = phonetic.Trim(), Meaning = normalized };
    }

    private static bool ContainsChinese(string s)
    {
        foreach (var ch in s)
        {
            if (ch >= '\u4e00' && ch <= '\u9fff') return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 查询

    /// <summary>
    /// 取词清洗（与 Kotlin 的 <c>raw.trim().trim { !it.isLetter() &amp;&amp; it != '-' &amp;&amp; it != '\'' }.lowercase()</c> 等价）：
    /// Trim → 去掉首尾既不是 Unicode 字母、也不是 - 或 ' 的字符 → ToLowerInvariant。
    /// </summary>
    public static string CleanWord(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var start = 0;
        var end = raw.Length;
        while (start < end && !IsKeptChar(raw[start])) start++;
        while (end > start && !IsKeptChar(raw[end - 1])) end--;
        if (end <= start) return string.Empty;
        return raw.Substring(start, end - start).ToLowerInvariant();
    }

    /// <summary>该字符是否被保留：Unicode 字母，或 - 或 '。</summary>
    private static bool IsKeptChar(char c) => char.IsLetter(c) || c == '-' || c == '\'';

    /// <summary>
    /// 规则变形（顺序与长度条件必须与安卓端逐条一致）：
    /// ies/es/s/ing/ed/er/est/ly。每个候选要求长度 ≥ 2 且不等于原词。
    /// </summary>
    public static IReadOnlyList<string> Variants(string word)
    {
        var raw = new List<string>(8);

        void Add(string candidate)
        {
            if (candidate.Length >= 2 && candidate != word) raw.Add(candidate);
        }

        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4) Add(word.Substring(0, word.Length - 3) + "y");
        if (word.EndsWith("es", StringComparison.Ordinal) && word.Length > 3) Add(word.Substring(0, word.Length - 2));
        if (word.EndsWith("s", StringComparison.Ordinal) && word.Length > 2) Add(word.Substring(0, word.Length - 1));
        if (word.EndsWith("ing", StringComparison.Ordinal) && word.Length > 5)
        {
            Add(word.Substring(0, word.Length - 3));
            Add(word.Substring(0, word.Length - 3) + "e");
        }
        if (word.EndsWith("ed", StringComparison.Ordinal) && word.Length > 4)
        {
            Add(word.Substring(0, word.Length - 2));
            Add(word.Substring(0, word.Length - 1));
        }
        if (word.EndsWith("er", StringComparison.Ordinal) && word.Length > 4)
        {
            Add(word.Substring(0, word.Length - 2));
            Add(word.Substring(0, word.Length - 1));
        }
        if (word.EndsWith("est", StringComparison.Ordinal) && word.Length > 5) Add(word.Substring(0, word.Length - 3));
        if (word.EndsWith("ly", StringComparison.Ordinal) && word.Length > 4) Add(word.Substring(0, word.Length - 2));

        // 去重但保持顺序
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(raw.Count);
        foreach (var v in raw)
        {
            if (seen.Add(v)) result.Add(v);
        }
        return result;
    }

    /// <summary>
    /// 查词：导入词典 → core 直接命中 → lemma 还原 → 规则变形 → 未命中。
    /// 命中结果进入 LRU 缓存（容量 200，key = 小写查询词）；未命中同样缓存，避免反复回表。
    /// </summary>
    public static LookupResult Lookup(string? raw)
    {
        var word = CleanWord(raw);
        if (word.Length == 0) return LookupResult.Miss(raw ?? string.Empty, string.Empty);

        Interlocked.Increment(ref _cacheLookups);
        if (Cache.TryGet(word, out var cached) && cached is not null)
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        var result = LookupUncached(word, raw ?? string.Empty);
        Cache.Set(word, result);
        return result;
    }

    private static LookupResult LookupUncached(string word, string raw)
    {
        // a. 用户导入词典
        Interlocked.Increment(ref _coreProbes);
        if (_import.TryGetValue(word, out var imported))
        {
            return Hit(raw, word, word, imported, LookupMatchKind.Import);
        }

        // b. core 直接命中
        if (_map.TryGetValue(word, out var direct))
        {
            return Hit(raw, word, word, direct, LookupMatchKind.Direct);
        }

        // c. lemma 词形还原
        Interlocked.Increment(ref _lemmaProbes);
        if (_lemmas.TryGetValue(word, out var baseWord))
        {
            Interlocked.Increment(ref _coreProbes);
            if (_map.TryGetValue(baseWord, out var restored))
            {
                return Hit(raw, word, baseWord, restored, LookupMatchKind.Lemma);
            }
        }

        // d. 规则变形（每个候选先试原形表，再试 lemma 还原后再查原形表）
        foreach (var variant in Variants(word))
        {
            Interlocked.Increment(ref _coreProbes);
            if (_map.TryGetValue(variant, out var v1))
            {
                return Hit(raw, word, variant, v1, LookupMatchKind.Variant);
            }

            Interlocked.Increment(ref _lemmaProbes);
            if (_lemmas.TryGetValue(variant, out var viaLemma))
            {
                Interlocked.Increment(ref _coreProbes);
                if (_map.TryGetValue(viaLemma, out var v2))
                {
                    return Hit(raw, word, viaLemma, v2, LookupMatchKind.Variant);
                }
            }
        }

        // e. 未命中（AI 兜底由上层注入，本类库不联网）
        return LookupResult.Miss(raw, word);
    }

    private static LookupResult Hit(string raw, string query, string baseWord, DictItem item, LookupMatchKind kind)
        => new()
        {
            Found = true,
            RawWord = raw,
            Query = query,
            BaseWord = item.Word.Length > 0 ? item.Word : baseWord,
            Match = kind,
            Via = ViaLabel(kind),
            Item = item,
        };

    /// <summary>命中方式标签（与安卓端一致：direct / lemma / variant，另加 import）。</summary>
    public static string ViaLabel(LookupMatchKind kind) => kind switch
    {
        LookupMatchKind.Import => "import",
        LookupMatchKind.Direct => "direct",
        LookupMatchKind.Lemma => "lemma",
        LookupMatchKind.Variant => "variant",
        _ => "miss",
    };

    /// <summary>清空 LRU 缓存（含统计计数）。</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>只清空 LRU 条目，保留统计计数。</summary>
    public static void ClearCacheEntries() => Cache.ClearEntries();

    /// <summary>重置统计计数器（供测试分段计时使用）。</summary>
    public static void ResetCounters()
    {
        Interlocked.Exchange(ref _cacheLookups, 0);
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _coreProbes, 0);
        Interlocked.Exchange(ref _lemmaProbes, 0);
    }

    /// <summary>清空全部数据（测试用）。</summary>
    public static void Reset()
    {
        lock (LoadGate)
        {
            _map = new Dictionary<string, DictItem>(CoreCapacityHint, StringComparer.Ordinal);
            _lemmas = new Dictionary<string, string>(LemmaCapacityHint, StringComparer.Ordinal);
            _import = new Dictionary<string, DictItem>(StringComparer.Ordinal);
            _storeLoaded = false;
            _explicitFilesLoaded = false;
            _lastLoadError = null;
            Cache.Clear();
        }
        ResetCounters();
    }
}
