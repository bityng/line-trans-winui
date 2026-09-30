using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LineTrans.Core.Tests;

/// <summary>假服务捕获到的一条 HTTP 请求。</summary>
internal sealed class CapturedRequest
{
    public string Method { get; init; } = "POST";

    /// <summary>原始请求目标（形如 <c>/v1/chat/completions</c>，可能带查询串）。</summary>
    public string Target { get; init; } = "/";

    public string Body { get; set; } = "";

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>去掉查询串的路径。</summary>
    public string Path => Target.Split('?')[0];

    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";

    /// <summary>把请求体当 JSON 解析（JsonElement 持有自己的文档，无需释放）。</summary>
    public JsonElement Json() => JsonDocument.Parse(Body).RootElement;
}

/// <summary>假服务要回的一条响应。</summary>
internal sealed record FakeResponse(int Status, string Body, int DelayMs = 0);

/// <summary>
/// 自测用的本地假 HTTP 服务：监听 127.0.0.1 的随机端口（loopback 不需要管理员权限，
/// 也不用 HttpListener 的 URL ACL），按 <see cref="Handler"/> 返回固定响应，
/// 并把收到的请求原样记下来供断言（请求头 / 请求体 / 路径）。
/// </summary>
internal sealed class FakeHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Thread _acceptThread;
    private readonly List<CapturedRequest> _requests = new();
    private readonly object _gate = new();
    private volatile bool _disposed;

    public FakeHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = "http://127.0.0.1:" + port;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "FakeHttpServer" };
        _acceptThread.Start();
    }

    /// <summary>服务地址，例如 <c>http://127.0.0.1:54321</c>。</summary>
    public string BaseUrl { get; }

    /// <summary>响应策略（默认 200 + 空对象）。</summary>
    public Func<CapturedRequest, FakeResponse> Handler { get; set; } = _ => new FakeResponse(200, "{}");

    /// <summary>收到的全部请求（按到达顺序）。</summary>
    public IReadOnlyList<CapturedRequest> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    /// <summary>最近一条请求。</summary>
    public CapturedRequest? LastRequest
    {
        get
        {
            lock (_gate) return _requests.Count == 0 ? null : _requests[^1];
        }
    }

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch
            {
                break;
            }
            var worker = new Thread(() => Handle(client)) { IsBackground = true, Name = "FakeHttpServer-conn" };
            worker.Start();
        }
    }

    private void Handle(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 15000;
                var stream = client.GetStream();
                var request = ReadRequest(stream);
                if (request == null) return;
                lock (_gate) _requests.Add(request);

                FakeResponse response;
                try
                {
                    response = Handler(request);
                }
                catch
                {
                    response = new FakeResponse(500, "{\"error\":{\"message\":\"fake handler failed\"}}");
                }

                // 模拟慢响应：分片睡眠，Dispose 时能尽快退出。
                for (int waited = 0; waited < response.DelayMs && !_disposed; waited += 50) Thread.Sleep(50);
                if (_disposed) return;

                byte[] body = Encoding.UTF8.GetBytes(response.Body);
                string head = "HTTP/1.1 " + response.Status + " " + Reason(response.Status) + "\r\n" +
                              "Content-Type: application/json; charset=utf-8\r\n" +
                              "Content-Length: " + body.Length + "\r\n" +
                              "Connection: close\r\n\r\n";
                byte[] headBytes = Encoding.ASCII.GetBytes(head);
                stream.Write(headBytes, 0, headBytes.Length);
                stream.Write(body, 0, body.Length);
                stream.Flush();
            }
        }
        catch
        {
            // 客户端中途取消（CancellationToken 用例）会导致写响应失败，自测里直接忽略。
        }
    }

    private static CapturedRequest? ReadRequest(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int read = stream.Read(chunk, 0, chunk.Length);
            if (read <= 0) return null;
            buffer.Write(chunk, 0, read);
            headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
        }

        byte[] all = buffer.GetBuffer();
        int total = (int)buffer.Length;
        string headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        string[] lines = headerText.Split("\r\n");
        string[] startLine = lines[0].Split(' ');
        var request = new CapturedRequest
        {
            Method = startLine.Length > 0 ? startLine[0] : "POST",
            Target = startLine.Length > 1 ? startLine[1] : "/",
        };

        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            string name = lines[i].Substring(0, colon).Trim();
            string value = lines[i].Substring(colon + 1).Trim();
            request.Headers[name] = value;
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out contentLength);
        }

        int bodyStart = headerEnd + 4;
        var bodyBytes = new MemoryStream();
        if (total > bodyStart) bodyBytes.Write(all, bodyStart, total - bodyStart);
        while (bodyBytes.Length < contentLength)
        {
            int read = stream.Read(chunk, 0, chunk.Length);
            if (read <= 0) break;
            bodyBytes.Write(chunk, 0, read);
        }

        int bodyLength = (int)Math.Min(bodyBytes.Length, contentLength);
        request.Body = Encoding.UTF8.GetString(bodyBytes.ToArray(), 0, bodyLength);
        return request;
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "OK",
    };

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _listener.Stop();
        }
        catch
        {
            // 已经停掉了，忽略。
        }
    }
}
