using System.Reflection;
using System.Text;
using System.Text.Json;
using LineTrans.Core;
using LineTrans.Core.Dictionary;

namespace LineTrans.App.Services;

/// <summary>
/// PC 端内置「网页翻译台」（v0.4.0）。
///
/// 安卓端与独立服务端一直有这一层，PC 端此前只把 /api/peer、/api/docs、/api/doc、/api/unit
/// 暴露出去当「被同步的对象」，自己却没有任何界面 —— 手机连过来看不到东西，别人也打不开 PC 的地址。
///
/// 这一层把与 line-trans-web / 安卓内置网页台 **同一套界面 + 同一套 API** 挂在 PeerService 的端口上：
///   GET  /                   网页翻译台（内嵌资源，与网页端 public/ 同源）
///   GET  /api/info | /api/docs | /api/doc?id=
///   POST /api/doc            改名 / 文件夹 / 置顶 / 切换逐行逐句
///   POST /api/unit           保存一句（带 peer 时转发给对端）
///   POST /api/ai             用 PC 端当前模型翻这一句
///   GET  /api/lookup?word=   查词（复用 PC 端离线词库）
///   GET  /api/dict           词库条目数
///   GET  /api/export?id=&format=
///   GET  /api/peer | POST 改名；GET/POST/DELETE /api/peers（发现 + 手填远程服务器）
///   GET  /api/remote/docs|doc；POST /api/remote/pull|unit
///   POST /api/import         新建文档
/// </summary>
internal static class WebConsole
{
    internal sealed record Reply(int Status, string ContentType, byte[] Body, string? ExtraHeaders = null);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Dictionary<string, string> StaticTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["index.html"] = "text/html; charset=utf-8",
        ["app.js"] = "application/javascript; charset=utf-8",
        ["style.css"] = "text/css; charset=utf-8",
        ["icon.svg"] = "image/svg+xml",
        ["manifest.json"] = "application/json; charset=utf-8",
        ["icon-192.png"] = "image/png",
        ["icon-512.png"] = "image/png",
    };

    private static readonly Dictionary<string, byte[]> StaticCache = new();

    // ---------------- 入口 ----------------

    internal static async Task<Reply?> HandleAsync(string method, string path, string query, string body)
    {
        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            return StaticReply(path);
        }

        bool post = method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        bool delete = method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);

        switch (path)
        {
            case "/api/info":
                return Json(200, InfoJson());

            case "/api/docs":
                return Json(200, DocsJson());

            case "/api/doc":
                return post ? DocPatch(body) : DocDetail(Query(query, "id"));

            case "/api/unit":
                return post ? await UnitSaveAsync(body) : Err(405, "只支持 POST");

            case "/api/ai":
                return post ? await AiAsync(body) : Err(405, "只支持 POST");

            case "/api/lookup":
                return await LookupAsync(query);

            case "/api/dict":
                return Json(200, DictJson());

            case "/api/export":
                return ExportReply(query);

            case "/api/peer":
                if (post) return Json(200, PeerJson(PeerService.SetDeviceName(BodyString(body, "name"))));
                return Json(200, PeerJson(null));

            case "/api/peers":
                if (post) return await AddPeerAsync(body);
                if (delete) return RemovePeer(Query(query, "id"));
                return Json(200, PeersJson());

            case "/api/remote/docs":
                return await RemoteDocsAsync(query);

            case "/api/remote/doc":
                return await RemoteDocAsync(query);

            case "/api/remote/pull":
                return post ? await RemotePullAsync(body) : Err(405, "只支持 POST");

            case "/api/remote/unit":
                return post ? await RemoteUnitAsync(body) : Err(405, "只支持 POST");

            case "/api/import":
                return post ? Import(body) : Err(405, "只支持 POST");

            default:
                return null;
        }
    }

    internal static Reply NotFound() => Err(404, "unknown api");

    internal static Reply Unauthorized() => Err(401, "未授权：请带 ?token=");

    // ---------------- 静态资源 ----------------

    private static Reply? StaticReply(string path)
    {
        string name = path is "" or "/" ? "index.html" : path.TrimStart('/');
        if (name.Contains('/') || name.Contains("..")) return null;
        if (!StaticTypes.TryGetValue(name, out var type)) return null;

        if (StaticCache.TryGetValue(name, out var cached)) return new Reply(200, type, cached);

        try
        {
            using var stream = typeof(WebConsole).Assembly.GetManifestResourceStream("web." + name);
            if (stream == null)
            {
                return new Reply(404, "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes("缺少网页资源：" + name + "（构建时没有把 web/ 打包进来）"));
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            StaticCache[name] = bytes;
            return new Reply(200, type, bytes);
        }
        catch (Exception e)
        {
            return new Reply(500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("读取网页资源失败：" + e.Message));
        }
    }

    // ---------------- 文档 ----------------

    private static string InfoJson()
    {
        var settings = AppServices.Settings;
        return Serialize(new
        {
            ok = true,
            name = "LineTrans",
            version = PeerService.Version,
            mode = "PC 客户端",
            targetLang = settings.TargetLang,
            provider = settings.ActiveModel?.Name,
            docCount = AppServices.Docs.Docs.Count,
            canTranslate = settings.ActiveProvider != null && settings.ActiveModel != null,
        });
    }

    private static string DocsJson()
    {
        var docs = AppServices.Docs.Docs
            .OrderByDescending(d => d.Pinned)
            .ThenByDescending(d => d.UpdatedAt)
            .Select(DocSummary)
            .ToList();
        return Serialize(new { ok = true, docs });
    }

    private static object DocSummary(TranslationDoc d) => new
    {
        id = d.Id,
        name = d.Name,
        folder = d.Folder,
        unitMode = ModeText(d.UnitMode),
        total = d.TotalCount,
        done = d.TranslatedCount,
        progress = d.TotalCount == 0 ? 0 : (int)Math.Round(d.TranslatedCount * 100.0 / d.TotalCount),
        pinned = d.Pinned,
        starred = d.StarredCount,
        updatedAt = d.UpdatedAt,
    };

    private static Reply DocDetail(string id)
    {
        var doc = AppServices.Docs.Get(id);
        if (doc == null) return Err(404, "文档不存在");

        var units = doc.Units.Select((u, i) => new
        {
            i,
            source = u.Source,
            translation = u.Translation ?? "",
            done = u.IsDone,
            starred = u.Starred,
        }).ToList();

        return Json(200, Serialize(new
        {
            ok = true,
            id = doc.Id,
            name = doc.Name,
            folder = doc.Folder,
            unitMode = ModeText(doc.UnitMode),
            total = doc.TotalCount,
            done = doc.TranslatedCount,
            lastIndex = doc.LastIndex,
            units,
        }));
    }

    private static Reply DocPatch(string body)
    {
        using var json = Parse(body);
        var root = json.RootElement;
        string id = Str(root, "docId");
        var doc = AppServices.Docs.Get(id);
        if (doc == null) return Err(404, "文档不存在");

        string name = Str(root, "name");
        if (name.Trim().Length > 0) AppServices.Docs.Rename(doc.Id, name.Trim());
        if (root.TryGetProperty("folder", out var folderEl) && folderEl.ValueKind == JsonValueKind.String)
        {
            AppServices.Docs.Move(doc.Id, folderEl.GetString() ?? "");
        }
        if (root.TryGetProperty("pinned", out var pinnedEl) &&
            (pinnedEl.ValueKind == JsonValueKind.True || pinnedEl.ValueKind == JsonValueKind.False))
        {
            AppServices.Docs.SetPinned(doc.Id, pinnedEl.GetBoolean());
        }
        string mode = Str(root, "unitMode");
        if (mode == "LINE" || mode == "SENTENCE")
        {
            AppServices.Docs.ChangeMode(doc.Id, mode == "SENTENCE" ? UnitMode.SENTENCE : UnitMode.LINE);
        }

        var updated = AppServices.Docs.Get(doc.Id) ?? doc;
        AppServices.Docs.Save(updated, immediate: true);
        return Json(200, Serialize(new { ok = true, total = updated.TotalCount, done = updated.TranslatedCount }));
    }

    private static async Task<Reply> UnitSaveAsync(string body)
    {
        using var json = Parse(body);
        var root = json.RootElement;

        // 「直接编辑对端」：网页台把 peer 带上，这里转发给那台设备
        string peerId = Str(root, "peer");
        if (peerId.Length > 0)
        {
            var peer = PeerService.Find(peerId);
            if (peer == null) return Err(404, "设备不存在");
            string forward = Serialize(new
            {
                docId = Str(root, "docId"),
                index = Int(root, "index", -1),
                translation = StrOrNull(root, "translation"),
                source = StrOrNull(root, "source"),
                done = BoolOrNull(root, "done"),
                starred = BoolOrNull(root, "starred"),
            });
            var (ok, error, text) = await PeerService.FetchAsync(peer, "api/unit", forward);
            return ok ? Json(200, text) : Json(200, Serialize(new { ok = false, error }));
        }

        var doc = AppServices.Docs.Get(Str(root, "docId"));
        if (doc == null) return Err(404, "文档不存在");
        int index = Int(root, "index", -1);
        if (index < 0 || index >= doc.Units.Count) return Err(404, "句子不存在");

        var unit = doc.Units[index];
        if (root.TryGetProperty("translation", out var t) && t.ValueKind == JsonValueKind.String) unit.Translation = t.GetString() ?? "";
        if (root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String && (s.GetString() ?? "").Trim().Length > 0) unit.Source = s.GetString()!;
        if (root.TryGetProperty("done", out var d) && (d.ValueKind == JsonValueKind.True || d.ValueKind == JsonValueKind.False)) unit.Done = d.GetBoolean();
        if (root.TryGetProperty("starred", out var st) && (st.ValueKind == JsonValueKind.True || st.ValueKind == JsonValueKind.False)) unit.Starred = st.GetBoolean();
        if (unit.Translation.Trim().Length > 0) unit.Done = true;

        doc.LastIndex = index;
        AppServices.Docs.Save(doc, immediate: true);
        return Json(200, Serialize(new { ok = true, total = doc.TotalCount, done = doc.TranslatedCount }));
    }

    private static async Task<Reply> AiAsync(string body)
    {
        using var json = Parse(body);
        var root = json.RootElement;
        var doc = AppServices.Docs.Get(Str(root, "docId"));
        if (doc == null) return Err(404, "文档不存在");
        int index = Int(root, "index", -1);
        if (index < 0 || index >= doc.Units.Count) return Err(404, "句子不存在");

        var settings = AppServices.Settings;
        if (settings.ActiveProvider == null || settings.ActiveModel == null)
        {
            return Json(200, Serialize(new { ok = false, error = "还没配置模型：请在 PC 端「设置 → AI」里选一个" }));
        }

        try
        {
            var result = await AppServices.Ai.TranslateAsync(doc, index);
            var unit = doc.Units[index];
            unit.Translation = result.Text;
            unit.Done = true;
            doc.LastIndex = index;
            AppServices.Docs.Save(doc, immediate: true);

            double cost = AppServices.Ai.CostOf(result.PromptTokens, result.CompletionTokens);
            return Json(200, Serialize(new
            {
                ok = true,
                text = result.Text,
                promptTokens = result.PromptTokens,
                completionTokens = result.CompletionTokens,
                cachedTokens = result.CachedTokens,
                cost,
                total = doc.TotalCount,
                done = doc.TranslatedCount,
            }));
        }
        catch (Exception e)
        {
            return Json(200, Serialize(new { ok = false, error = e.Message }));
        }
    }

    private static Reply Import(string body)
    {
        using var json = Parse(body);
        var root = json.RootElement;
        if (!root.TryGetProperty("units", out var unitsEl) || unitsEl.ValueKind != JsonValueKind.Array)
        {
            return Err(400, "缺少 units");
        }

        var units = new List<TranslationUnit>();
        foreach (var item in unitsEl.EnumerateArray())
        {
            string source = Str(item, "source");
            string translation = Str(item, "translation");
            bool done = item.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            bool starred = item.TryGetProperty("starred", out var s) && s.ValueKind == JsonValueKind.True;
            units.Add(new TranslationUnit(source, translation, done, starred));
        }
        if (units.Count == 0) return Err(400, "units 为空");

        string mode = Str(root, "unitMode") == "SENTENCE" ? "SENTENCE" : "LINE";
        string name = Str(root, "name");
        string folder = Str(root, "folder");
        var doc = new TranslationDoc(
            DocRepository.NewId(),
            name.Trim().Length == 0 ? "未命名文档" : name.Trim(),
            folder.Trim().Length == 0 ? "同步" : folder.Trim(),
            null,
            mode == "SENTENCE" ? UnitMode.SENTENCE : UnitMode.LINE,
            string.Join("\n", units.Select(u => u.Source)));
        doc.Units.Clear();
        doc.Units.AddRange(units);
        AppServices.Docs.Save(doc, immediate: true);

        return Json(200, Serialize(new { ok = true, id = doc.Id, name = doc.Name, total = doc.TotalCount, done = doc.TranslatedCount }));
    }

    // ---------------- 查词 / 导出 ----------------

    private static async Task<Reply> LookupAsync(string query)
    {
        string word = Query(query, "word").Trim();
        if (word.Length == 0) return Json(200, Serialize(new { ok = false, error = "word 不能为空" }));
        if (word.Length > 64) word = word[..64];

        try
        {
            var entry = await AppServices.LookupWordAsync(word);
            if (entry == null || !entry.Ok)
            {
                return Json(200, Serialize(new { ok = true, word, found = false, matched = (string?)null, via = (string?)null, source = "none" }));
            }

            string meaning = entry.Translation ?? string.Empty;
            if (string.IsNullOrWhiteSpace(meaning))
            {
                meaning = string.Join("\n", entry.Senses.Select(s =>
                    string.IsNullOrEmpty(s.PartOfSpeech) ? s.Definition : s.PartOfSpeech + " " + s.Definition));
            }

            return Json(200, Serialize(new
            {
                ok = true,
                word,
                found = true,
                matched = entry.Word,
                via = entry.Source,
                phonetic = entry.Phonetic ?? "",
                meaning,
                source = entry.Source,
            }));
        }
        catch (Exception e)
        {
            return Json(200, Serialize(new { ok = false, error = e.Message }));
        }
    }

    private static string DictJson() => Serialize(new
    {
        ok = true,
        ready = LocalDictionary.IsLoaded,
        entries = LocalDictionary.Size,
        lemma = LocalDictionary.LemmaSize,
        imported = LocalDictionary.ImportSize,
    });

    private static Reply ExportReply(string query)
    {
        var doc = AppServices.Docs.Get(Query(query, "id"));
        if (doc == null) return Err(404, "文档不存在");

        var format = FormatOf(Query(query, "format"));
        string text = ExportManager.BuildText(doc, format);
        string fileName = ExportManager.BuildFileName(doc, format);
        string header = "Content-Disposition: attachment; filename=\"" + fileName.Replace("\"", "") + "\"; "
            + "filename*=UTF-8''" + Uri.EscapeDataString(fileName) + "\r\n";
        return new Reply(200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), header);
    }

    private static ExportFormat FormatOf(string format) => format switch
    {
        "txt_translated" => ExportFormat.TXT_TRANSLATED_ONLY,
        "txt_source" => ExportFormat.TXT_SOURCE_FALLBACK,
        "md" => ExportFormat.MARKDOWN_TABLE,
        "csv" => ExportFormat.CSV,
        "json" => ExportFormat.JSON,
        _ => ExportFormat.TXT_BILINGUAL,
    };

    // ---------------- 设备 ----------------

    private static string PeerJson(string? renamed)
    {
        return Serialize(new
        {
            ok = true,
            peer = new
            {
                id = PeerService.DeviceId,
                name = renamed ?? PeerService.DeviceName,
                platform = "windows",
                version = PeerService.Version,
                api = 2,
                port = PeerService.Port,
            },
        });
    }

    private static string PeersJson()
    {
        var peers = PeerService.Peers().Select(p => new
        {
            id = p.Id,
            name = p.Name,
            platform = p.Platform,
            version = p.Version,
            url = p.Url,
            hasToken = !string.IsNullOrWhiteSpace(p.Token),
            manual = p.Manual,
            lastSeen = p.LastSeen,
            online = p.Online,
        }).ToList();

        return Serialize(new
        {
            ok = true,
            self = new
            {
                id = PeerService.DeviceId,
                name = PeerService.DeviceName,
                platform = "windows",
                version = PeerService.Version,
                api = 2,
                port = PeerService.Port,
            },
            peers,
            discovery = true,
        });
    }

    private static async Task<Reply> AddPeerAsync(string body)
    {
        string url = BodyString(body, "url");
        string token = BodyString(body, "token");
        string name = BodyString(body, "name");
        if (url.Trim().Length == 0) return Err(400, "缺少 url");

        var (ok, error, peer) = await PeerService.AddManualAsync(url, token, name);
        if (!ok || peer == null) return Json(200, Serialize(new { ok = false, error }));

        return Json(200, Serialize(new
        {
            ok = true,
            peer = new { id = peer.Id, name = peer.Name, url = peer.Url },
            peers = PeerService.Peers().Select(p => new { id = p.Id, name = p.Name, url = p.Url }),
        }));
    }

    private static Reply RemovePeer(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Err(400, "缺少 id");
        var peer = PeerService.Find(id);
        bool removed = peer != null && peer.Manual;
        if (removed) PeerService.RemoveManual(id);
        return Json(200, Serialize(new
        {
            ok = removed,
            error = removed ? (string?)null : "没有这个远程服务器",
            peers = PeerService.Peers().Select(p => new { id = p.Id, name = p.Name, url = p.Url }),
        }));
    }

    private static async Task<Reply> RemoteDocsAsync(string query)
    {
        var peer = PeerService.Find(Query(query, "peer"));
        if (peer == null) return Err(404, "设备不存在");

        var (ok, error, docs) = await PeerService.GetDocsAsync(peer);
        if (!ok) return Json(200, Serialize(new { ok = false, error }));

        return Json(200, Serialize(new
        {
            ok = true,
            peer = new { id = peer.Id, name = peer.Name },
            docs = docs.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                unitMode = d.UnitMode,
                total = d.Total,
                done = d.Done,
                progress = d.Total == 0 ? 0 : (int)Math.Round(d.Done * 100.0 / d.Total),
            }),
        }));
    }

    private static async Task<Reply> RemoteDocAsync(string query)
    {
        var peer = PeerService.Find(Query(query, "peer"));
        if (peer == null) return Err(404, "设备不存在");
        string id = Query(query, "id");
        if (id.Trim().Length == 0) return Err(400, "缺少 id");

        var (ok, error, text) = await PeerService.FetchAsync(peer, "api/doc?id=" + Uri.EscapeDataString(id));
        return ok ? Json(200, text) : Json(200, Serialize(new { ok = false, error }));
    }

    private static async Task<Reply> RemotePullAsync(string body)
    {
        string peerId = BodyString(body, "peer");
        string docId = BodyString(body, "docId");
        var peer = PeerService.Find(peerId);
        if (peer == null) return Err(404, "设备不存在");
        if (docId.Trim().Length == 0) return Err(400, "缺少 docId");

        var (ok, error, doc) = await PeerService.PullAsync(peer, docId, bind: false);
        if (!ok || doc == null) return Json(200, Serialize(new { ok = false, error }));

        return Json(200, Serialize(new
        {
            ok = true,
            id = doc.Id,
            name = doc.Name,
            total = doc.TotalCount,
            done = doc.TranslatedCount,
        }));
    }

    private static async Task<Reply> RemoteUnitAsync(string body)
    {
        using var json = Parse(body);
        var root = json.RootElement;
        var peer = PeerService.Find(Str(root, "peer"));
        if (peer == null) return Err(404, "设备不存在");

        string forward = Serialize(new
        {
            docId = Str(root, "docId"),
            index = Int(root, "index", -1),
            translation = StrOrNull(root, "translation"),
            source = StrOrNull(root, "source"),
            done = BoolOrNull(root, "done"),
            starred = BoolOrNull(root, "starred"),
        });
        var (ok, error, text) = await PeerService.FetchAsync(peer, "api/unit", forward);
        return ok ? Json(200, text) : Json(200, Serialize(new { ok = false, error }));
    }

    // ---------------- 小工具 ----------------

    private static JsonDocument Parse(string body)
    {
        string text = string.IsNullOrWhiteSpace(body) ? "{}" : body;
        return JsonDocument.Parse(text);
    }

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, JsonOpts);

    private static Reply Json(int status, string json) =>
        new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));

    private static Reply Err(int status, string message) =>
        Json(status, Serialize(new { ok = false, error = message }));

    private static string ModeText(UnitMode mode) => mode == UnitMode.SENTENCE ? "SENTENCE" : "LINE";

    private static string Query(string query, string key)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part[..eq] == key) return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return "";
    }

    private static string Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static object? StrOrNull(JsonElement root, string key) =>
        root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static object? BoolOrNull(JsonElement root, string key) =>
        root.TryGetProperty(key, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean()
            : null;

    private static int Int(JsonElement root, string key, int fallback) =>
        root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int value) ? value : fallback;

    private static string BodyString(string body, string key)
    {
        try
        {
            using var json = Parse(body);
            return Str(json.RootElement, key);
        }
        catch
        {
            return "";
        }
    }
}
