using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LineTrans.Core;

/// <summary>文档排序方式（对应安卓端 <c>DocRepository.DocSort</c>）。</summary>
public enum DocSort
{
    /// <summary>最近更新（默认，置顶优先）。</summary>
    UPDATED,

    /// <summary>按名称（置顶优先）。</summary>
    NAME,

    /// <summary>按进度（置顶优先）。</summary>
    PROGRESS,
}

/// <summary>
/// 文档仓库：内存里持有全部文档，磁盘写入做防抖（默认 700ms）并放到后台线程，
/// 避免打字时每个字符都同步写文件卡住输入。
///
/// 数据位置：<c>%APPDATA%\LineTrans\docs\&lt;uuid&gt;.json</c>，一个文档一个文件。
/// 移植自安卓端 <c>data/DocRepository.kt</c>，文件字段与网页端 <c>server.js</c> 的 state.json 保持一致。
///
/// 五个关键保证：
///   1. 原子落盘：先写 <c>&lt;id&gt;.json.tmp</c>，fsync 之后再覆盖改名成目标文件；进程被强杀也不会留下半截 JSON，
///      断电也不会出现「名字已经改了、内容还在缓存里」；
///   2. 单写者：同一个文档同一时刻只有一个写者。防抖写盘 / <see cref="FlushAsync"/> / 立即写盘都从同一个写闸门排队，
///      不会出现两个写者抢同一个 .tmp、慢的那个把新的那个盖掉（那会让磁盘永久停在旧版本）；
///   3. 退出前 <see cref="FlushAsync"/> 会把所有待写内容同步刷到磁盘，返回后数据一定在盘上；
///   4. 加载容错：单个文件损坏只跳过并记录到 <see cref="LoadErrors"/>，不影响其余文档；
///   5. 读写争用容错：覆盖改名期间目标文件名会短暂打不开（Windows 语义，本机实测 30~230ms），
///      所以 <see cref="Load"/> 会先等该文档的写闸门、再对瞬时共享冲突做有限次重试。
///
/// 注意：读者侧的瞬时共享冲突无法靠改写法根除——<c>File.Move(…, overwrite: true)</c> 与
/// <c>File.Replace</c> 都会让目标名字短暂不可打开（实测后者窗口更长，最长 2s）。所以读方必须容忍重试。
/// </summary>
public sealed class DocRepository : IDisposable
{
    /// <summary>默认防抖间隔（毫秒），与安卓端一致。</summary>
    public const int DefaultDebounceMs = 700;

    /// <summary>防抖下限（毫秒）。</summary>
    public const int MinDebounceMs = 200;

    /// <summary>防抖上限（毫秒）。</summary>
    public const int MaxDebounceMs = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly List<TranslationDoc> _docs = new();
    private readonly List<string> _loadErrors = new();
    private readonly List<string> _writeErrors = new();

    /// <summary>待落盘的文档（id → 已序列化好的 JSON），由 <see cref="_gate"/> 保护。</summary>
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);

    /// <summary>每个文档的防抖计时器，由 <see cref="_gate"/> 保护。</summary>
    private readonly Dictionary<string, CancellationTokenSource> _timers = new(StringComparer.Ordinal);

    /// <summary>
    /// 每个文档的「写闸门」：同一个文档同一时刻只允许一个写者，由 <see cref="_gate"/> 保护。
    /// 防抖写盘 / <see cref="FlushAsync"/> / <see cref="Save"/> 的立即写盘统统排队进 <see cref="WritePending"/>；
    /// 不排队的话两个写者会同时写同一个 &lt;id&gt;.json.tmp 和同一个目标文件：
    /// 轻则报共享冲突，重则旧内容盖掉新内容、磁盘永久停在旧版本（都是实测到过的）。
    /// 取闸门时只在 <see cref="_gate"/> 里做一次字典查找就退出来，绝不在持有 <see cref="_gate"/> 时去等闸门（防死锁）。
    /// </summary>
    private readonly Dictionary<string, object> _writeGates = new(StringComparer.Ordinal);

    private int _debounceMs;
    private bool _disposed;

    /// <param name="directory">文档目录；传 null 用 <see cref="DefaultDirectory"/>（测试里可传临时目录）。</param>
    /// <param name="debounceMs">防抖间隔（毫秒），会 clamp 到 200~5000。</param>
    public DocRepository(string? directory = null, int? debounceMs = null)
    {
        DirectoryPath = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory!;
        _debounceMs = ClampDebounce(debounceMs ?? DefaultDebounceMs);
        System.IO.Directory.CreateDirectory(DirectoryPath);
    }

    /// <summary>默认文档目录：<c>%APPDATA%\LineTrans\docs</c>。</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LineTrans", "docs");

    /// <summary>实际文档目录。</summary>
    public string DirectoryPath { get; }

    /// <summary>防抖间隔（毫秒）。界面改设置后可以直接同步过来。</summary>
    public int DebounceMs
    {
        get => _debounceMs;
        set => _debounceMs = ClampDebounce(value);
    }

    /// <summary>当前排序方式（恒为「置顶优先」）。</summary>
    public DocSort SortMode { get; private set; } = DocSort.UPDATED;

    /// <summary>按当前排序返回的文档只读快照。</summary>
    public IReadOnlyList<TranslationDoc> Docs
    {
        get { lock (_gate) return _docs.ToList(); }
    }

    /// <summary>加载时被跳过的文件（损坏 / 缺 id / 读不动），形如「文件名（原因）」。</summary>
    public IReadOnlyList<string> LoadErrors
    {
        get { lock (_gate) return _loadErrors.ToList(); }
    }

    /// <summary>写盘失败的记录（磁盘满 / 无权限等），失败内容会留在待写队列里等下次重试。</summary>
    public IReadOnlyList<string> WriteErrors
    {
        get { lock (_gate) return _writeErrors.ToList(); }
    }

    /// <summary>还有多少文档没落盘（测试与退出提示用）。</summary>
    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>把防抖间隔 clamp 到合法区间。</summary>
    public static int ClampDebounce(int value) => Math.Clamp(value, MinDebounceMs, MaxDebounceMs);

    // ------------------------------------------------------------------
    // 加载
    // ------------------------------------------------------------------

    /// <summary>
    /// 扫描文档目录并加载全部文档；单个文件坏掉只记录不抛出。
    /// 重复调用会清空当前内存状态重新加载（未落盘的改动会丢失，调用前请先 FlushAsync）。
    /// </summary>
    public void Load()
    {
        var loaded = new List<TranslationDoc>();
        var errors = new List<string>();

        string[] files;
        try
        {
            files = System.IO.Directory.GetFiles(DirectoryPath, "*.json");
        }
        catch (Exception ex)
        {
            errors.Add("无法读取文档目录：" + ex.Message);
            files = Array.Empty<string>();
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            // 崩溃可能留下 <id>.json.tmp：它不是文档（正常的 "*.json" 选不中，这里再兜一道）。
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

            string text;
            try
            {
                // 同一个实例里这个文档可能正好在写盘：先排队进它的写闸门等写完，再读；
                // 闸门保证的是「本实例不再有人在写」，闸门外还有别的进程（另一个实例 / 同步盘）在换这个文件，
                // 那种瞬时冲突由 ReadAllTextWithRetry 吸收。
                lock (WriteGateFor(Path.GetFileNameWithoutExtension(name)))
                {
                    text = ReadAllTextWithRetry(file);
                }
            }
            catch (Exception ex)
            {
                errors.Add(name + "（读取失败：" + ex.Message + "）");
                continue;
            }

            TranslationDoc? doc;
            try
            {
                doc = ParseDoc(text);
            }
            catch (Exception ex)
            {
                errors.Add(name + "（解析失败：" + ex.Message + "）");
                continue;
            }

            if (doc == null || TranslationUnit.IsBlank(doc.Id))
            {
                errors.Add(name + "（缺少 id）");
                continue;
            }

            loaded.Add(doc);
        }

        lock (_gate)
        {
            _docs.Clear();
            _docs.AddRange(loaded);
            _loadErrors.Clear();
            _loadErrors.AddRange(errors);
            SortLocked();
        }
    }

    /// <summary>
    /// 读文件，容忍瞬时共享冲突。
    /// 覆盖式落盘期间目标文件名会短暂打不开（本机实测 30~230ms），窗口可能来自另一个实例 / 同步盘 / 杀毒扫描；
    /// 撞上只说明「这一刻正好在换文件」，退避重试即可。真正读不动（文件不存在、路径非法、没有权限）不重试。
    /// </summary>
    private static string ReadAllTextWithRetry(string file, int attempts = 5)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception ex) when (attempt < attempts && IsTransientSharingError(ex))
            {
                Thread.Sleep(30 * attempt);   // 30 / 60 / 90 / 120ms 退避
            }
        }
    }

    /// <summary>按 id 取文档（取不到返回 null）。</summary>
    public TranslationDoc? Get(string id)
    {
        lock (_gate) return _docs.FirstOrDefault(d => d.Id == id);
    }

    /// <summary>全部文件夹名（去重后按序数排序，与安卓端一致）。</summary>
    public List<string> Folders()
    {
        lock (_gate) return _docs.Select(d => d.Folder).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>生成新的文档 id（uuid）。</summary>
    public static string NewId() => Guid.NewGuid().ToString("D");

    // ------------------------------------------------------------------
    // 新建 / 修改 / 删除
    // ------------------------------------------------------------------

    /// <summary>
    /// 新建文档：按 <paramref name="mode"/> 切分正文后立即落盘（用户刚建的东西不能因为崩溃丢掉）。
    /// </summary>
    public TranslationDoc Create(
        string name = "未命名文档",
        string sourceText = "",
        UnitMode mode = UnitMode.LINE,
        string folder = TranslationDoc.DefaultFolder)
    {
        var doc = new TranslationDoc(
            NewId(),
            TranslationUnit.IsBlank(name) ? "未命名文档" : name.Trim(),
            TranslationUnit.IsBlank(folder) ? TranslationDoc.DefaultFolder : folder.Trim(),
            null,
            mode,
            sourceText ?? "");
        doc.Units.AddRange(TextParser.Parse(doc.SourceText, mode));
        Save(doc, immediate: true);
        return doc;
    }

    /// <summary>
    /// 保存到内存并（防抖）写入磁盘；<paramref name="immediate"/> 用于离开编辑页等必须立刻落盘的场景。
    /// </summary>
    public void Save(TranslationDoc doc, bool immediate = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_disposed) return;

        doc.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string payload = SerializeDoc(doc);

        lock (_gate)
        {
            int idx = _docs.FindIndex(d => d.Id == doc.Id);
            if (idx >= 0)
            {
                if (!ReferenceEquals(_docs[idx], doc)) _docs[idx] = doc;
            }
            else
            {
                _docs.Add(doc);
            }
            SortLocked();

            _pending[doc.Id] = payload;
            CancelTimerLocked(doc.Id);
            if (!immediate) ScheduleLocked(doc.Id);
        }

        if (immediate) WritePending(doc.Id);
    }

    /// <summary>
    /// 立刻把所有待写内容刷到磁盘；返回后文件一定在盘上（退出前务必调用）。
    /// 与还来得及触发的防抖写盘共用每个文档的写闸门：要么这次刷盘等它写完，要么它已经被取消，不会两个写者同时写一份文件。
    /// </summary>
    public Task FlushAsync()
    {
        List<string> ids;
        lock (_gate)
        {
            ids = _pending.Keys.ToList();
            foreach (string id in ids) CancelTimerLocked(id);
        }
        foreach (string id in ids) WritePending(id);
        return Task.CompletedTask;
    }

    /// <summary>删除文档（内存 + 磁盘 + 待写队列）。</summary>
    public bool Delete(string id)
    {
        bool removed;
        lock (_gate)
        {
            CancelTimerLocked(id);
            _pending.Remove(id);
            removed = _docs.RemoveAll(d => d.Id == id) > 0;
        }

        if (removed) DeleteFile(Path.Combine(DirectoryPath, id + ".json"));
        return removed;
    }

    /// <summary>改名。</summary>
    public bool Rename(string id, string name)
    {
        var doc = Get(id);
        if (doc == null) return false;
        doc.Name = TranslationUnit.IsBlank(name) ? doc.Name : name.Trim();
        Save(doc);
        return true;
    }

    /// <summary>移动文件夹。</summary>
    public bool Move(string id, string folder)
    {
        var doc = Get(id);
        if (doc == null) return false;
        doc.Folder = TranslationUnit.IsBlank(folder) ? TranslationDoc.DefaultFolder : folder.Trim();
        Save(doc);
        return true;
    }

    /// <summary>置顶 / 取消置顶。</summary>
    public bool SetPinned(string id, bool pinned)
    {
        var doc = Get(id);
        if (doc == null) return false;
        doc.Pinned = pinned;
        Save(doc);
        return true;
    }

    /// <summary>记住阅读位置。</summary>
    public bool SetLastIndex(string id, int index)
    {
        var doc = Get(id);
        if (doc == null) return false;
        doc.LastIndex = index < 0 ? 0 : index;
        Save(doc);
        return true;
    }

    /// <summary>
    /// 切换逐行 / 逐句：按切换后的切分结果重建单元，原文完全相同的单元保留已有译文 / 完成 / 收藏
    /// （语义与网页端 <c>server.js</c> 的 applyMode 一致）。切分方式没变时直接返回 false。
    /// </summary>
    public bool ChangeMode(string id, UnitMode target)
    {
        var doc = Get(id);
        if (doc == null || doc.UnitMode == target) return false;

        var rebuilt = RebuildUnits(SourceTextOf(doc), target, doc.Units);
        doc.Units.Clear();
        doc.Units.AddRange(rebuilt);
        doc.UnitMode = target;
        if (doc.LastIndex >= doc.Units.Count) doc.LastIndex = Math.Max(0, doc.Units.Count - 1);
        Save(doc);
        return true;
    }

    /// <summary>
    /// 按新切分方式重建单元并尽量保留老译文：以「原文」为键对齐，命中即搬运译文 / done / starred。
    /// 纯函数，方便自测与复用。
    /// </summary>
    public static List<TranslationUnit> RebuildUnits(string sourceText, UnitMode mode, IReadOnlyList<TranslationUnit> previous)
    {
        // 与 server.js 的 new Map(units.map(u => [u.source, u])) 一致：重复原文取最后一次出现的那个。
        var old = new Dictionary<string, TranslationUnit>(StringComparer.Ordinal);
        if (previous != null)
        {
            foreach (var unit in previous) old[unit.Source] = unit;
        }

        var result = new List<TranslationUnit>();
        foreach (var unit in TextParser.Parse(sourceText ?? "", mode))
        {
            if (old.TryGetValue(unit.Source, out var prev))
            {
                result.Add(new TranslationUnit(unit.Source, prev.Translation, prev.Done, prev.Starred));
            }
            else
            {
                result.Add(new TranslationUnit(unit.Source));
            }
        }
        return result;
    }

    /// <summary>切换排序方式并重新排序。</summary>
    public void SetSort(DocSort mode)
    {
        lock (_gate)
        {
            SortMode = mode;
            SortLocked();
        }
    }

    /// <summary>按当前排序方式重新排序。</summary>
    public void Sort()
    {
        lock (_gate) SortLocked();
    }

    /// <summary>文档正文：优先用 sourceText，缺失时退化为把各单元原文拼回去（与 server.js 一致）。</summary>
    public static string SourceTextOf(TranslationDoc doc) =>
        string.IsNullOrEmpty(doc.SourceText) ? string.Join("\n", doc.Units.Select(u => u.Source)) : doc.SourceText;

    // ------------------------------------------------------------------
    // 内部：排序 / 计时器 / 落盘
    // ------------------------------------------------------------------

    private void SortLocked()
    {
        List<TranslationDoc> sorted = SortMode switch
        {
            DocSort.NAME => _docs.OrderByDescending(d => d.Pinned).ThenBy(d => d.Name, StringComparer.Ordinal).ToList(),
            DocSort.PROGRESS => _docs.OrderByDescending(d => d.Pinned).ThenByDescending(d => d.Progress).ToList(),
            _ => _docs.OrderByDescending(d => d.Pinned).ThenByDescending(d => d.UpdatedAt).ToList(),
        };
        _docs.Clear();
        _docs.AddRange(sorted);
    }

    private void ScheduleLocked(string id)
    {
        var cts = new CancellationTokenSource();
        _timers[id] = cts;
        int delay = _debounceMs;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            WritePending(id);
        });
    }

    private void CancelTimerLocked(string id)
    {
        if (_timers.Remove(id, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* 已经释放，忽略 */ }
            cts.Dispose();
        }
    }

    private void WritePending(string id)
    {
        // 顺序很重要：先排队进这个文档的写闸门，再读待写内容。
        // 反过来的话会出现「旧内容正在写、新内容已经被别人写完并出队」——旧内容落在新内容之后，
        // 待写队列又是空的，磁盘就永久停在旧版本了（实测复现过）。
        lock (WriteGateFor(id))
        {
            string payload;
            lock (_gate)
            {
                CancelTimerLocked(id);
                if (!_pending.TryGetValue(id, out var current)) return;
                payload = current;
            }

            try
            {
                WriteAtomic(Path.Combine(DirectoryPath, id + ".json"), payload);
                // 写盘期间可能又有新改动进来，只有内容仍是同一份时才从待写队列移除。
                lock (_gate)
                {
                    if (_pending.TryGetValue(id, out var latest) && latest == payload) _pending.Remove(id);
                }
            }
            catch (Exception ex)
            {
                // 失败的内容留在待写队列里，等下一次编辑或退出前的 FlushAsync 再试。
                lock (_gate)
                {
                    string message = id + "（写盘失败：" + ex.Message + "）";
                    if (!_writeErrors.Contains(message)) _writeErrors.Add(message);
                }
            }
        }
    }

    /// <summary>取某个文档的写闸门（不持有 <see cref="_gate"/> 去等它，避免锁序反转）。</summary>
    private object WriteGateFor(string id)
    {
        lock (_gate)
        {
            if (!_writeGates.TryGetValue(id, out var gate))
            {
                gate = new object();
                _writeGates[id] = gate;
            }
            return gate;
        }
    }

    /// <summary>
    /// 原子写：先把完整内容写进 &lt;path&gt;.tmp（显式 <see cref="FileShare.Read"/>，不独占），
    /// fsync 落盘后再覆盖改名成目标文件——既不留下半截 JSON，也不会出现「名字已经改了、内容还在缓存里」。
    ///
    /// 覆盖改名（<c>File.Move(…, overwrite: true)</c>）期间，目标文件名会短暂打不开：Windows 的覆盖改名要求
    /// 目标文件没有别的手柄占着，而这个窗口本机实测有 30~230ms（杀毒 / 同步盘 / 机械盘还会放大）。
    /// 所以这里对「共享冲突」做有限次退避重试——外部读方（备份工具、同步盘、另一个实例）只是短暂占着文件，
    /// 退避一下就能写进去；不重试的话这次改动只记一条 <see cref="WriteErrors"/>，然后一直躺在待写队列里等下次编辑。
    ///
    /// 不要改用 <c>File.Replace</c>：实测它的不可读窗口更长（最长 2s），对读者更不友好。
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);   // 编码一次，重试时直接复用
        const int attempts = 4;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < attempts && IsTransientSharingError(ex))
            {
                Thread.Sleep(20 * attempt);   // 20 / 40 / 60ms 退避
            }
        }
    }

    /// <summary>
    /// 瞬时共享冲突：文件正被别的手柄占着导致打不开（杀毒扫描 / 同步盘 / 外部读方 / 另一个实例）。
    /// 「文件不存在」「路径太长」这类是硬错误，重试没有意义。
    /// </summary>
    private static bool IsTransientSharingError(Exception ex) =>
        (ex is IOException || ex is UnauthorizedAccessException)
        && ex is not FileNotFoundException
        && ex is not DirectoryNotFoundException
        && ex is not PathTooLongException;

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
        catch
        {
            // 删不掉（被占用等）不影响内存状态，下次删除或清理时再处理。
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var cts in _timers.Values)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { /* 忽略 */ }
                cts.Dispose();
            }
            _timers.Clear();
        }
    }

    // ------------------------------------------------------------------
    // 内部：JSON 读写（字段名与网页端 / 安卓端保持一致）
    // ------------------------------------------------------------------

    private static string SerializeDoc(TranslationDoc doc)
    {
        var dto = new DocDto
        {
            Id = doc.Id,
            Name = doc.Name,
            Folder = doc.Folder,
            UnitMode = doc.UnitMode.ToString(),
            SourceText = doc.SourceText,
            Pinned = doc.Pinned,
            LastIndex = doc.LastIndex,
            CreatedAt = doc.CreatedAt,
            UpdatedAt = doc.UpdatedAt,
            Units = doc.Units.Select(u => (UnitDto?)new UnitDto
            {
                Source = u.Source,
                Translation = u.Translation,
                Done = u.Done,
                Starred = u.Starred,
            }).ToList(),
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static TranslationDoc ParseDoc(string json)
    {
        var dto = JsonSerializer.Deserialize<DocDto>(json, JsonOptions)
                  ?? throw new JsonException("内容为空");

        var doc = new TranslationDoc(
            dto.Id ?? "",
            TranslationUnit.IsBlank(dto.Name ?? "") ? "未命名文档" : dto.Name!.Trim(),
            TranslationUnit.IsBlank(dto.Folder ?? "") ? TranslationDoc.DefaultFolder : dto.Folder!.Trim(),
            null,
            ParseMode(dto.UnitMode),
            dto.SourceText ?? "",
            dto.Pinned,
            dto.LastIndex < 0 ? 0 : dto.LastIndex,
            dto.CreatedAt is > 0 ? dto.CreatedAt : null,
            dto.UpdatedAt is > 0 ? dto.UpdatedAt : null);

        if (dto.Units != null)
        {
            foreach (var unit in dto.Units)
            {
                if (unit == null) continue;
                doc.Units.Add(new TranslationUnit(unit.Source ?? "", unit.Translation ?? "", unit.Done, unit.Starred));
            }
        }
        return doc;
    }

    private static UnitMode ParseMode(string? raw) =>
        string.Equals(raw, "SENTENCE", StringComparison.OrdinalIgnoreCase) ? UnitMode.SENTENCE : UnitMode.LINE;

    private sealed class DocDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }

        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("folder")] public string? Folder { get; set; }

        [JsonPropertyName("unitMode")] public string? UnitMode { get; set; }

        [JsonPropertyName("sourceText")] public string? SourceText { get; set; }

        [JsonPropertyName("pinned")] public bool Pinned { get; set; }

        [JsonPropertyName("lastIndex")] public int LastIndex { get; set; }

        [JsonPropertyName("createdAt")] public long? CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")] public long? UpdatedAt { get; set; }

        [JsonPropertyName("units")] public List<UnitDto?>? Units { get; set; }
    }

    private sealed class UnitDto
    {
        [JsonPropertyName("source")] public string? Source { get; set; }

        [JsonPropertyName("translation")] public string? Translation { get; set; }

        [JsonPropertyName("done")] public bool Done { get; set; }

        [JsonPropertyName("starred")] public bool Starred { get; set; }
    }
}
