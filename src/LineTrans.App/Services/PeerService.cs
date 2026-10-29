using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using LineTrans.Core;

namespace LineTrans.App.Services;

/// <summary>一端设备（局域网发现的，或手填的远程服务器）。</summary>
public sealed class PeerEndpoint
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    public bool Manual { get; set; }
    public long LastSeen { get; set; }

    public bool Online => LastSeen > 0 &&
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LastSeen < (Manual ? 30000 : 20000);
}

/// <summary>本地文档 → 对端文档的绑定（「直接编辑」用）。</summary>
public sealed class PeerBinding
{
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string RemoteDocId { get; set; } = "";
}

/// <summary>
/// 三端设备互通（v1.9.0）的 PC 侧。
///
/// 两件事：
///   1. **当设备**：本机起一个极小的 HTTP 服务（TcpListener 手写，不用 HttpListener ——
///      后者要管理员预设 urlacl，普通用户装不上），把 /api/peer、/api/docs、/api/doc、
///      /api/unit 暴露到局域网；同时 UDP 41234 广播，让手机与网页服务端能发现它。
///   2. **当客户端**：发现 / 手填对端 → 列对端文件 → 「同步到本地」复制一份，
///      或「直接编辑」建本地镜像并写回对端（每次保存把这一句推过去）。
///
/// 协议与 line-trans-web、安卓 WebApi 完全一致，三端可任意组合。
/// </summary>
public static class PeerService
{
    public const int DefaultPort = 8790;
    private const int DiscoveryPort = 41234;
    private const int ApiLevel = 1;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, PeerEndpoint> Discovered = new();
    private static readonly Dictionary<string, PeerEndpoint> Manual = new();
    private static readonly Dictionary<string, PeerBinding> Bindings = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private static TcpListener? _listener;
    private static UdpClient? _udp;
    private static CancellationTokenSource? _cts;
    private static string _peerFile = "";

    /// <summary>每次新保存译文时用来合并推送（同一句 300ms 内只推一次）。</summary>
    private static readonly Dictionary<string, (PeerBinding Binding, int Index, TranslationUnit Unit, long At)> PushQueue = new();

    public static string DeviceId { get; private set; } = Guid.NewGuid().ToString();
    public static string DeviceName { get; private set; } = Environment.MachineName;
    public static int Port { get; private set; } = DefaultPort;
    public static bool Running { get; private set; }

    /// <summary>设备列表有变化时触发，UI 用来刷新设备 chips。</summary>
    public static event Action? PeersChanged;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // ---------------- 生命周期 ----------------

    public static void Start()
    {
        if (Running) return;
        _peerFile = Path.Combine(DataDirectory(), "peer.json");
        LoadState();
        _cts = new CancellationTokenSource();

        Port = DefaultPort;
        StartServer(_cts.Token);
        StartDiscovery(_cts.Token);
        Running = true;
    }

    public static void Stop()
    {
        Running = false;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _udp?.Dispose(); } catch { /* ignore */ }
        _listener = null;
        _udp = null;
    }

    private static string DataDirectory()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LineTrans");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class Persisted
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<ManualEntry> Manual { get; set; } = new();
        public Dictionary<string, PeerBinding> Bindings { get; set; } = new();
    }

    private sealed class ManualEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Token { get; set; } = "";
    }

    private static void LoadState()
    {
        try
        {
            if (!File.Exists(_peerFile))
            {
                SaveState();
                return;
            }

            var saved = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_peerFile), JsonOptions);
            if (saved == null) return;
            if (!string.IsNullOrWhiteSpace(saved.Id)) DeviceId = saved.Id;
            if (!string.IsNullOrWhiteSpace(saved.Name)) DeviceName = saved.Name;
            foreach (var m in saved.Manual)
            {
                Manual[m.Id] = new PeerEndpoint
                {
                    Id = m.Id, Name = m.Name, Url = m.Url, Token = m.Token,
                    Manual = true, Platform = "remote", LastSeen = 0,
                };
            }
            foreach (var kv in saved.Bindings) Bindings[kv.Key] = kv.Value;
        }
        catch { /* 文件坏了就当没存过 */ }
    }

    private static void SaveState()
    {
        try
        {
            var data = new Persisted
            {
                Id = DeviceId,
                Name = DeviceName,
                Manual = Manual.Values
                    .Select(m => new ManualEntry { Id = m.Id, Name = m.Name, Url = m.Url, Token = m.Token })
                    .ToList(),
                Bindings = Bindings.ToDictionary(kv => kv.Key, kv => kv.Value),
            };
            File.WriteAllText(_peerFile, JsonSerializer.Serialize(data, JsonOptions), Encoding.UTF8);
        }
        catch { /* 只读目录也能跑 */ }
    }

    // ---------------- 当设备：极小的 HTTP 服务 ----------------

    private static void StartServer(CancellationToken token)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            _listener = listener;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(token); }
                    catch { break; }
                    _ = Task.Run(() => HandleClient(client), token);
                }
            }, token);
        }
        catch
        {
            // 端口被占：只影响「被别人访问」，本机当客户端仍然可用
        }
    }

    private static async Task HandleClient(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            var header = new MemoryStream();
            var buffer = new byte[4096];
            int headEnd = -1;

            // 读到空行为止（请求头）
            while (headEnd < 0)
            {
                int n = await stream.ReadAsync(buffer);
                if (n <= 0) return;
                header.Write(buffer, 0, n);
                var bytes = header.GetBuffer();
                int len = (int)header.Length;
                for (int i = 3; i < len; i++)
                {
                    if (bytes[i - 3] == '\r' && bytes[i - 2] == '\n' && bytes[i - 1] == '\r' && bytes[i] == '\n')
                    {
                        headEnd = i + 1;
                        break;
                    }
                }
                if (header.Length > 64 * 1024) return;
            }

            string headText = Encoding.UTF8.GetString(header.GetBuffer(), 0, headEnd);
            var lines = headText.Split("\r\n");
            var requestLine = lines.Length > 0 ? lines[0].Split(' ') : Array.Empty<string>();
            if (requestLine.Length < 2) return;
            string method = requestLine[0];
            string target = requestLine[1];

            int contentLength = 0;
            foreach (var line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line[(colon + 1)..].Trim(), out contentLength);
                }
            }

            string body = "";
            if (contentLength > 0)
            {
                var data = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = await stream.ReadAsync(data.AsMemory(read, contentLength - read));
                    if (n <= 0) break;
                    read += n;
                }
                body = Encoding.UTF8.GetString(data, 0, read);
            }

            var (status, json) = Route(method, target, body);
            var payload = Encoding.UTF8.GetBytes(json);
            var head = "HTTP/1.1 " + status + " " + (status == 200 ? "OK" : "ERROR") + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + payload.Length + "\r\n"
                + "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes);
            await stream.WriteAsync(payload);
        }
        catch { /* 单个连接出错不影响服务 */ }
    }

    private static (int Status, string Json) Route(string method, string target, string body)
    {
        string path = target;
        string query = "";
        int q = target.IndexOf('?');
        if (q >= 0)
        {
            path = target[..q];
            query = target[(q + 1)..];
        }

        // 令牌：与网页台共用同一个访问令牌
        string token = AppServices.Settings.WebServerToken?.Trim() ?? "";
        if (token.Length > 0 && !QueryHas(query, "token", token))
        {
            return (401, "{\"error\":\"未授权：请带 ?token=\"}");
        }

        switch (path)
        {
            case "/api/peer":
                return (200, PeerJson());

            case "/api/docs":
                return (200, DocsJson());

            case "/api/doc":
            {
                string id = QueryValue(query, "id");
                var doc = AppServices.Docs.Get(id ?? "");
                return doc == null ? (404, "{\"error\":\"文档不存在\"}") : (200, DocJson(doc));
            }

            case "/api/unit":
                if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase)) return (405, "{\"error\":\"只支持 POST\"}");
                return UnitPost(body);

            default:
                return (404, "{\"error\":\"unknown api\"}");
        }
    }

    private static (int, string) UnitPost(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = json.RootElement;
            string docId = root.TryGetProperty("docId", out var d) ? d.GetString() ?? "" : "";
            int index = root.TryGetProperty("index", out var i) ? i.GetInt32() : -1;
            var doc = AppServices.Docs.Get(docId);
            if (doc == null) return (404, "{\"error\":\"文档不存在\"}");
            if (index < 0 || index >= doc.Units.Count) return (404, "{\"error\":\"句子不存在\"}");

            var unit = doc.Units[index];
            if (root.TryGetProperty("translation", out var t) && t.ValueKind == JsonValueKind.String) unit.Translation = t.GetString() ?? "";
            if (root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String) unit.Source = s.GetString() ?? "";
            if (root.TryGetProperty("done", out var dn) && (dn.ValueKind == JsonValueKind.True || dn.ValueKind == JsonValueKind.False)) unit.Done = dn.GetBoolean();
            if (root.TryGetProperty("starred", out var st) && (st.ValueKind == JsonValueKind.True || st.ValueKind == JsonValueKind.False)) unit.Starred = st.GetBoolean();
            if (!string.IsNullOrWhiteSpace(unit.Translation)) unit.Done = true;
            AppServices.Docs.Save(doc, immediate: true);

            return (200, "{\"ok\":true,\"total\":" + doc.TotalCount + ",\"done\":" + doc.TranslatedCount + "}");
        }
        catch
        {
            return (400, "{\"error\":\"请求体不是合法 JSON\"}");
        }
    }

    private static string PeerJson()
    {
        var payload = new
        {
            ok = true,
            peer = new
            {
                id = DeviceId,
                name = DeviceName,
                platform = "windows",
                version = AppInfoVersion(),
                api = ApiLevel,
                port = Port,
            },
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string AppInfoVersion()
    {
        try
        {
            return typeof(PeerService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    private static string DocsJson()
    {
        var docs = AppServices.Docs.Docs
            .OrderByDescending(x => x.Pinned)
            .ThenByDescending(x => x.UpdatedAt)
            .Select(d => new
            {
                id = d.Id,
                name = d.Name,
                folder = d.Folder,
                unitMode = d.UnitMode == UnitMode.SENTENCE ? "SENTENCE" : "LINE",
                total = d.TotalCount,
                done = d.TranslatedCount,
                progress = d.TotalCount == 0 ? 0 : (int)Math.Round(d.TranslatedCount * 100.0 / d.TotalCount),
                pinned = d.Pinned,
                starred = d.StarredCount,
                updatedAt = d.UpdatedAt,
            })
            .ToList();
        return JsonSerializer.Serialize(new { ok = true, docs }, JsonOptions);
    }

    private static string DocJson(TranslationDoc doc)
    {
        var units = doc.Units.Select((u, i) => new
        {
            i,
            source = u.Source,
            translation = u.Translation ?? "",
            done = u.IsDone,
            starred = u.Starred,
        }).ToList();
        return JsonSerializer.Serialize(new
        {
            id = doc.Id,
            name = doc.Name,
            folder = doc.Folder,
            unitMode = doc.UnitMode == UnitMode.SENTENCE ? "SENTENCE" : "LINE",
            total = doc.TotalCount,
            done = doc.TranslatedCount,
            units,
        }, JsonOptions);
    }

    private static string QueryValue(string query, string key)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part[..eq] == key) return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return "";
    }

    private static bool QueryHas(string query, string key, string expected)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part[..eq] == key && Uri.UnescapeDataString(part[(eq + 1)..]) == expected) return true;
        }
        return false;
    }

    // ---------------- 当客户端 ----------------

    /// <summary>设备列表：手填的在前，自己不算。</summary>
    public static IReadOnlyList<PeerEndpoint> Peers()
    {
        lock (Gate)
        {
            var list = new List<PeerEndpoint>();
            list.AddRange(Manual.Values);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var p in Discovered.Values)
            {
                if (now - p.LastSeen > 20000) continue;
                if (list.Any(x => x.Id == p.Id)) continue;
                list.Add(p);
            }
            return list;
        }
    }

    public static PeerEndpoint? Find(string id)
    {
        lock (Gate)
        {
            if (Manual.TryGetValue(id, out var m)) return m;
            if (Discovered.TryGetValue(id, out var d)) return d;
            return null;
        }
    }

    public static bool IsBound(string localDocId, out PeerBinding? binding)
    {
        lock (Gate)
        {
            if (Bindings.TryGetValue(localDocId, out var b))
            {
                binding = b;
                return true;
            }
        }
        binding = null;
        return false;
    }

    /// <summary>手填远程服务器：先探活 /api/peer，通了才入册。</summary>
    public static async Task<(bool Ok, string Error, PeerEndpoint? Peer)> AddManualAsync(string url, string token, string name)
    {
        string clean = (url ?? "").Trim().TrimEnd('/');
        if (clean.Length == 0) return (false, "先填地址", null);
        if (!clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            clean = "http://" + clean;
        }

        var probe = new PeerEndpoint { Id = clean, Name = name, Url = clean, Token = token ?? "", Manual = true };
        var (ok, err, json) = await FetchAsync(probe, "/api/peer");
        if (!ok) return (false, "连不上：" + err, null);

        string remoteId = clean;
        string remoteName = clean;
        string platform = "remote";
        string version = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("peer", out var peerEl))
            {
                if (peerEl.TryGetProperty("id", out var idEl)) remoteId = idEl.GetString() ?? clean;
                if (peerEl.TryGetProperty("name", out var nm)) remoteName = nm.GetString() ?? clean;
                if (peerEl.TryGetProperty("platform", out var pf)) platform = pf.GetString() ?? "remote";
                if (peerEl.TryGetProperty("version", out var v)) version = v.GetString() ?? "";
            }
        }
        catch { /* 回包不是 JSON 就当探活失败 */ }

        var endpoint = new PeerEndpoint
        {
            Id = remoteId,
            Name = string.IsNullOrWhiteSpace(name) ? remoteName : name.Trim(),
            Url = clean,
            Token = token ?? "",
            Platform = platform,
            Version = version,
            Manual = true,
            LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        lock (Gate) Manual[remoteId] = endpoint;
        SaveState();
        PeersChanged?.Invoke();
        return (true, "", endpoint);
    }

    public static void RemoveManual(string id)
    {
        lock (Gate) Manual.Remove(id);
        SaveState();
        PeersChanged?.Invoke();
    }

    /// <summary>对端文件列表（只要摘要）。</summary>
    public static async Task<(bool Ok, string Error, List<PeerDocInfo> Docs)> GetDocsAsync(PeerEndpoint peer)
    {
        var (ok, err, json) = await FetchAsync(peer, "/api/docs");
        var list = new List<PeerDocInfo>();
        if (!ok) return (false, err, list);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("docs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in arr.EnumerateArray())
                {
                    list.Add(new PeerDocInfo
                    {
                        Id = d.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                        Name = d.TryGetProperty("name", out var n) ? n.GetString() ?? "未命名" : "未命名",
                        UnitMode = d.TryGetProperty("unitMode", out var m) ? m.GetString() ?? "LINE" : "LINE",
                        Total = d.TryGetProperty("total", out var t) ? t.GetInt32() : 0,
                        Done = d.TryGetProperty("done", out var dn) ? dn.GetInt32() : 0,
                    });
                }
            }
            return (true, "", list);
        }
        catch (Exception e)
        {
            return (false, e.Message, list);
        }
    }

    /// <summary>
    /// 同步到本地 / 直接编辑：把对端文档复制成本地文档。
    /// bind=true 时记下绑定，之后这台机器上每次保存都会推回对端。
    /// </summary>
    public static async Task<(bool Ok, string Error, TranslationDoc? Doc)> PullAsync(
        PeerEndpoint peer, string remoteDocId, bool bind)
    {
        var (ok, err, json) = await FetchAsync(peer, "/api/doc?id=" + Uri.EscapeDataString(remoteDocId));
        if (!ok) return (false, err, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "未命名文档" : "未命名文档";
            string mode = root.TryGetProperty("unitMode", out var m) ? m.GetString() ?? "LINE" : "LINE";
            var units = new List<TranslationUnit>();
            if (root.TryGetProperty("units", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in arr.EnumerateArray())
                {
                    units.Add(new TranslationUnit(
                        u.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "",
                        u.TryGetProperty("translation", out var t) ? t.GetString() ?? "" : "",
                        u.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True,
                        u.TryGetProperty("starred", out var st) && st.ValueKind == JsonValueKind.True));
                }
            }

            var local = new TranslationDoc(
                Guid.NewGuid().ToString(),
                name + "（来自" + peer.Name + "）",
                "同步",
                units,
                mode == "SENTENCE" ? UnitMode.SENTENCE : UnitMode.LINE,
                string.Join("\n", units.Select(x => x.Source)));
            AppServices.Docs.Save(local, immediate: true);

            if (bind)
            {
                lock (Gate)
                {
                    Bindings[local.Id] = new PeerBinding
                    {
                        PeerId = peer.Id,
                        PeerName = peer.Name,
                        RemoteDocId = remoteDocId,
                    };
                }
                SaveState();
            }

            return (true, "", local);
        }
        catch (Exception e)
        {
            return (false, e.Message, null);
        }
    }

    /// <summary>「直接编辑」时把这一句推回对端（800ms 合并，避免每个按键一个请求）。</summary>
    private static readonly Dictionary<string, (PeerEndpoint Peer, string Json)> Dirty = new();
    private static Timer? _flushTimer;

    public static void PushUnit(string localDocId, int index, TranslationUnit unit)
    {
        if (!Running) return;
        PeerBinding? binding;
        lock (Gate)
        {
            if (!Bindings.TryGetValue(localDocId, out binding)) return;
        }
        var peer = Find(binding.PeerId);
        if (peer == null) return;

        string payload = JsonSerializer.Serialize(new
        {
            docId = binding.RemoteDocId,
            index,
            translation = unit.Translation ?? "",
            done = unit.IsDone,
            starred = unit.Starred,
        });

        lock (Gate)
        {
            Dirty[localDocId + "#" + index] = (peer, payload);
            _flushTimer ??= new Timer(_ => FlushDirty(), null, 800, 800);
        }
    }

    private static void FlushDirty()
    {
        List<(PeerEndpoint Peer, string Json)> batch;
        lock (Gate)
        {
            if (Dirty.Count == 0)
            {
                _flushTimer?.Dispose();
                _flushTimer = null;
                return;
            }
            batch = Dirty.Values.ToList();
            Dirty.Clear();
        }
        foreach (var (peer, json) in batch)
        {
            _ = Task.Run(async () => await FetchAsync(peer, "/api/unit", json));
        }
    }

    /// <summary>调一次对端接口；失败不抛异常，只把原因带回去。</summary>
    private static async Task<(bool Ok, string Error, string Json)> FetchAsync(
        PeerEndpoint peer, string pathAndQuery, string? body = null)
    {
        string url = peer.Url.TrimEnd('/') + pathAndQuery;
        if (!string.IsNullOrWhiteSpace(peer.Token))
        {
            url += (pathAndQuery.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(peer.Token);
        }
        try
        {
            using var req = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url);
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req);
            string text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                string snippet = text.Length > 120 ? text[..120] : text;
                return (false, "对端返回 " + (int)res.StatusCode + "：" + snippet, "");
            }
            return (true, "", text);
        }
        catch (TaskCanceledException)
        {
            return (false, "对端超时：" + peer.Url, "");
        }
        catch (Exception e)
        {
            return (false, e.Message, "");
        }
    }

    // ---------------- 发现 ----------------

    private static void StartDiscovery(CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            UdpClient udp;
            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                udp.EnableBroadcast = true;
                _udp = udp;
            }
            catch
            {
                return;   // 端口被占：只影响互相发现
            }

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udp.ReceiveAsync(token);
                        string text = Encoding.UTF8.GetString(result.Buffer);
                        using var json = JsonDocument.Parse(text);
                        var root = json.RootElement;
                        if (!root.TryGetProperty("lt", out var lt) || lt.GetString() != "peer") continue;
                        string id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(id) || id == DeviceId) continue;
                        int port = root.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 0;
                        if (port <= 0) continue;

                        bool isNew;
                        lock (Gate) isNew = !Discovered.ContainsKey(id);
                        var endpoint = new PeerEndpoint
                        {
                            Id = id,
                            Name = root.TryGetProperty("name", out var nm) ? nm.GetString() ?? "另一台设备" : "另一台设备",
                            Platform = root.TryGetProperty("platform", out var pf) ? pf.GetString() ?? "device" : "device",
                            Version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                            Url = "http://" + result.RemoteEndPoint.Address + ":" + port,
                            Manual = false,
                            LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        };
                        lock (Gate) Discovered[id] = endpoint;
                        if (isNew) PeersChanged?.Invoke();
                    }
                    catch { /* 单包坏了忽略 */ }
                }
            }, token);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        lt = "peer",
                        v = ApiLevel,
                        id = DeviceId,
                        name = DeviceName,
                        platform = "windows",
                        version = AppInfoVersion(),
                        port = Port,
                    }, JsonOptions);
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), token);
                }
                catch { /* 广播失败不致命 */ }
                try { await Task.Delay(3000, token); } catch { break; }
            }
        }, token);
    }

    /// <summary>对端文档摘要（给列表用）。</summary>
    public sealed class PeerDocInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string UnitMode { get; set; } = "LINE";
        public int Total { get; set; }
        public int Done { get; set; }
    }
}
