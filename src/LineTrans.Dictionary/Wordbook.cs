using DefLang = LineTrans.Core.Dictionary.DefinitionLanguage;

namespace LineTrans.Core.Dictionary;

/// <summary>一条自定义词库记录，对应安卓端 <c>WordEntry</c>。</summary>
public sealed class WordEntry
{
    /// <summary>词条。</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>音标 / 读音。</summary>
    public string Reading { get; set; } = string.Empty;

    /// <summary>释义。</summary>
    public string Meaning { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>最近更新时间（Unix 毫秒）。</summary>
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>转为离线词库条目。</summary>
    public DictItem ToItem() => new() { Word = Term, Phonetic = Reading, Meaning = Meaning };
}

/// <summary>
/// 自定义词库：划词查义时优先命中这里，也可以把查到的释义一键存进来。
/// 数据用纯文本持久化（<c>词=音标=释义</c> 每行一条），并做 500ms 防抖写入。
/// 与安卓端 <c>WordbookRepository</c> 语义一致，去掉了 Compose / Gson 依赖。
/// </summary>
public sealed class Wordbook
{
    private const long DebounceMs = 500;

    private readonly object _gate = new();
    private readonly Dictionary<string, WordEntry> _map = new(StringComparer.Ordinal);
    private readonly string? _filePath;
    private CancellationTokenSource? _debounce;
    private string _definitionLanguage = DefLang.Zh;

    /// <summary>创建词库；<paramref name="filePath"/> 为 null 时只在内存中工作。</summary>
    public Wordbook(string? filePath = null)
    {
        _filePath = filePath;
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath)) LoadFromText(File.ReadAllText(filePath!));
    }

    /// <summary>词条数。</summary>
    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    /// <summary>持久化文件路径。</summary>
    public string? FilePath => _filePath;

    /// <summary>释义语言：zh / both / en，非法值一律回落 zh。</summary>
    public string DefinitionLanguage
    {
        get { lock (_gate) return _definitionLanguage; }
        set { lock (_gate) _definitionLanguage = DefLang.Normalize(value); }
    }

    /// <summary>按 term 精确查找（大小写不敏感、忽略首尾空白）。</summary>
    public WordEntry? Find(string term)
    {
        var key = (term ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0) return null;
        lock (_gate) return _map.TryGetValue(key, out var entry) ? entry : null;
    }

    /// <summary>是否已收录。</summary>
    public bool Contains(string term) => Find(term) is not null;

    /// <summary>全部词条（按词条排序）。</summary>
    public IReadOnlyList<WordEntry> All()
    {
        lock (_gate) return _map.Values.OrderBy(e => e.Term, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>新增或覆盖一条词条（按 term 去重），并安排一次防抖持久化。</summary>
    public void Upsert(string term, string meaning, string reading = "", string note = "")
    {
        var key = (term ?? string.Empty).Trim();
        if (key.Length == 0) return;
        lock (_gate)
        {
            if (_map.TryGetValue(key.ToLowerInvariant(), out var existing))
            {
                if (!string.IsNullOrWhiteSpace(reading)) existing.Reading = reading;
                if (!string.IsNullOrWhiteSpace(meaning)) existing.Meaning = meaning;
                if (!string.IsNullOrWhiteSpace(note)) existing.Note = note;
                existing.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            else
            {
                _map[key.ToLowerInvariant()] = new WordEntry
                {
                    Term = key,
                    Reading = reading ?? string.Empty,
                    Meaning = meaning ?? string.Empty,
                    Note = note ?? string.Empty,
                };
            }
        }
        Schedule();
    }

    /// <summary>删除一条词条。</summary>
    public void Remove(string term)
    {
        var key = (term ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0) return;
        lock (_gate) _map.Remove(key);
        Schedule();
    }

    /// <summary>清空词库。</summary>
    public void Clear()
    {
        lock (_gate) _map.Clear();
        Schedule();
    }

    /// <summary>把当前词库同步写给 <see cref="LocalDictionary"/> 作为最高优先级的导入词典。</summary>
    public void ApplyToLocalDictionary() => LocalDictionary.ImportEntries(All().Select(e => e.ToItem()));

    // ---------- 文本导入 / 导出 ----------

    /// <summary>
    /// 从文本导入，每行一条：<c>词=释义</c> / <c>词=音标=释义</c> / <c>词 TAB 释义</c>。
    /// 以 # 开头的行视为注释。返回导入条数（同一词条重复出现会被覆盖，但仍计入返回值，与安卓端一致）。
    /// </summary>
    public int ImportText(string text)
    {
        var added = 0;
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string[] parts;
            if (line.Contains('\t'))
            {
                parts = line.Split('\t').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            }
            else if (line.Contains('='))
            {
                parts = line.Split('=').Select(p => p.Trim()).ToArray();
            }
            else
            {
                continue;
            }
            if (parts.Length < 2) continue;

            var term = parts[0];
            if (term.Length == 0) continue;

            var reading = parts.Length >= 3 ? parts[1] : string.Empty;
            var meaning = parts.Length >= 3
                ? string.Join(" = ", parts.Skip(2))
                : string.Join(" = ", parts.Skip(1));
            if (meaning.Length == 0) continue;

            Upsert(term, meaning, reading);
            added++;
        }
        return added;
    }

    /// <summary>导出为文本（每行 <c>词=释义</c> 或 <c>词=音标=释义</c>）。</summary>
    public string ExportText()
    {
        var lines = All().Select(e => string.IsNullOrWhiteSpace(e.Reading)
            ? e.Term + "=" + e.Meaning
            : e.Term + "=" + e.Reading + "=" + e.Meaning);
        return string.Join("\n", lines);
    }

    // ---------- 持久化（防抖） ----------

    /// <summary>取消未决的防抖任务并立即写盘。</summary>
    public void Flush()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _debounce;
            _debounce = null;
        }
        cts?.Cancel();
        WriteNow();
    }

    private void Schedule()
    {
        if (string.IsNullOrWhiteSpace(_filePath)) return;
        CancellationTokenSource cts;
        lock (_gate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            cts = new CancellationTokenSource();
            _debounce = cts;
        }
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)DebounceMs, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested) WriteNow();
            }
            catch (OperationCanceledException)
            {
                // 被后续写入取消，属正常路径
            }
        });
    }

    private void WriteNow()
    {
        if (string.IsNullOrWhiteSpace(_filePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(_filePath!);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath!, ExportText());
        }
        catch
        {
            // 持久化失败不抛出，避免影响划词主流程
        }
    }

    /// <summary>从 <see cref="ExportText"/> 生成的文本载入词库。</summary>
    public int LoadFromText(string text)
    {
        lock (_gate) _map.Clear();
        return ImportText(text);
    }
}
