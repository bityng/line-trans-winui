using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LineTrans.Core;

/// <summary>一次 AI 调用的结果。</summary>
/// <param name="Text">模型返回的文本（已 trim）。</param>
/// <param name="PromptTokens">输入 token 数（Anthropic 的 input_tokens）。</param>
/// <param name="CompletionTokens">输出 token 数（Anthropic 的 output_tokens）。</param>
/// <param name="CachedTokens">命中缓存的输入 token（部分服务商才返回）。</param>
public sealed record AiResult(string Text, int PromptTokens, int CompletionTokens, int CachedTokens = 0)
{
    /// <summary>本次调用的费用估算（按当前模型的单价，等价网页端 costOf）。</summary>
    public double Cost { get; init; }
}

/// <summary>
/// AI 调用客户端：支持 OpenAI 兼容（<c>POST {baseUrl}/v1/chat/completions</c>）与
/// Anthropic（<c>POST {baseUrl}/v1/messages</c>）两种协议，判断方式与网页端 <c>server.js</c> 的
/// <c>callModel()</c> 一致（provider.type 为 anthropic，或 baseUrl 里含 anthropic.com）。
///
/// 费用口径同样与 <c>server.js</c> 的 <c>costOf()</c> 一致，并复用 <see cref="CostCalculator"/>：
///   费用 = promptTokens / 1e6 * 输入单价 + completionTokens / 1e6 * 输出单价
/// （高峰倍率默认为 1.0，因此未配置倍率时两者完全一致）。
///
/// 未配置 Base URL 或模型名时抛明确的中文 <see cref="InvalidOperationException"/>，不静默失败；
/// 所有请求都接受 <see cref="CancellationToken"/>，用户随时可以中止批量翻译。
/// </summary>
public sealed class AiClient : IDisposable
{
    /// <summary>Anthropic 必须带的版本头。</summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>未配置 maxTokens 时的默认值（与 server.js 的 <c>|| 4096</c> 一致）。</summary>
    public const int DefaultMaxTokens = 4096;

    /// <summary>未配置温度时的默认值（与 server.js 的 <c>?? 0.2</c> 一致）。</summary>
    public const double DefaultTemperature = 0.2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private bool _disposed;

    /// <param name="settings">设置对象（内部持有引用，设置改动后下一次调用即生效）。</param>
    /// <param name="httpClient">可注入的 HttpClient（自测里指向本地假服务）；传 null 则自己建一个。</param>
    public AiClient(AppSettings settings, HttpClient? httpClient = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (httpClient != null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
    }

    /// <summary>当前设置。</summary>
    public AppSettings Settings { get; }

    /// <summary>当前提供方。</summary>
    public ProviderConfig? Provider => Settings.ActiveProvider;

    /// <summary>当前模型。</summary>
    public ModelConfig? Model => Settings.ActiveModel;

    /// <summary>当前是否走 Anthropic 协议。</summary>
    public bool IsAnthropic => Provider != null && IsAnthropicProvider(Provider);

    /// <summary>按 provider.type / baseUrl 判断是否走 Anthropic 协议。</summary>
    public static bool IsAnthropicProvider(ProviderConfig provider) =>
        provider.Type == ProviderType.ANTHROPIC ||
        provider.BaseUrl.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 拼接接口地址：先把 baseUrl 尾部的斜杠去掉，没有 <c>/v1</c> 就补上（与 server.js / 安卓端一致）。
    /// </summary>
    public static string ResolveUrl(string baseUrl, string path)
    {
        string trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed + path
            : trimmed + "/v1" + path;
    }

    /// <summary>按当前模型估算费用（未配置模型返回 0）。</summary>
    public double CostOf(int promptTokens, int completionTokens)
    {
        var model = Settings.ActiveModel;
        return model == null ? 0.0 : CostCalculator.CostFor(model, promptTokens, completionTokens);
    }

    /// <summary>按指定模型估算费用（复用 Core 的 <see cref="CostCalculator"/>，口径同 server.js 的 costOf）。</summary>
    public static double CostOf(ModelConfig? model, int promptTokens, int completionTokens) =>
        model == null ? 0.0 : CostCalculator.CostFor(model, promptTokens, completionTokens);

    /// <summary>翻译文档里的第 <paramref name="index"/> 条单元（提示词与网页端 /api/ai 一致）。</summary>
    public async Task<AiResult> TranslateAsync(TranslationDoc doc, int index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (index < 0 || index >= doc.Units.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "单元下标越界：" + index + "（共 " + doc.Units.Count + " 条）");
        }

        var unit = doc.Units[index];
        var previous = new List<TranslationUnit>();
        for (int i = 0; i < index; i++) previous.Add(doc.Units[i]);

        string system = PromptBuilder.BuildSystemPrompt(Settings, unit.Source, doc.Name, doc.UnitMode);
        string user = PromptBuilder.BuildUserPrompt(Settings, doc.Name, unit, doc.UnitMode, index, doc.Units.Count, previous);
        return await ChatAsync(system, user, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 发一次对话请求。未配置 Base URL / 模型名时抛中文异常；
    /// <paramref name="cancellationToken"/> 被触发时原样抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public async Task<AiResult> ChatAsync(string system, string user, CancellationToken cancellationToken = default)
    {
        var provider = Provider
            ?? throw new InvalidOperationException("尚未配置 API 提供商：请先在「设置 → AI」里填写 Base URL、模型与密钥");
        string baseUrl = (provider.BaseUrl ?? "").Trim();
        if (baseUrl.Length == 0)
        {
            throw new InvalidOperationException("尚未配置 Base URL：请先在「设置 → AI」里填写接口地址（例如 https://api.deepseek.com）");
        }

        var model = Model;
        string modelName = (model?.Name ?? "").Trim();
        if (modelName.Length == 0)
        {
            throw new InvalidOperationException("尚未配置模型：请先在「设置 → AI」里填写模型名（例如 deepseek-chat）");
        }

        bool anthropic = IsAnthropicProvider(provider);
        string url = ResolveUrl(baseUrl, anthropic ? "/messages" : "/chat/completions");
        int maxTokens = model!.MaxTokens > 0 ? model.MaxTokens : DefaultMaxTokens;
        double temperature = model.Temperature > 0 ? model.Temperature : DefaultTemperature;
        string body = anthropic
            ? BuildAnthropicBody(modelName, system ?? "", user ?? "", maxTokens, temperature)
            : BuildOpenAiBody(modelName, system ?? "", user ?? "", maxTokens, temperature);

        int timeoutSec = Math.Clamp(Settings.RequestTimeoutSec, 10, 600);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, new UTF8Encoding(false), "application/json"),
        };
        if (anthropic)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey ?? "");
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (provider.ApiKey ?? ""));
        }
        if (!TranslationUnit.IsBlank(Settings.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", Settings.UserAgent.Trim());
        }

        string responseText;
        int statusCode;
        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);
            statusCode = (int)response.StatusCode;
            responseText = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException("请求超时（已等待 " + timeoutSec + " 秒）：可在「设置 → 高级」里调大超时时间", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("网络请求失败：" + ex.Message, ex);
        }

        if (statusCode < 200 || statusCode > 299)
        {
            throw new InvalidOperationException("API 错误 (" + statusCode + ")：" + ExtractError(responseText));
        }

        var result = ParseResponse(responseText, anthropic);
        return result with { Cost = CostOf(model, result.PromptTokens, result.CompletionTokens) };
    }

    private static string BuildOpenAiBody(string model, string system, string user, int maxTokens, double temperature)
    {
        var root = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        return root.ToJsonString(JsonOptions);
    }

    private static string BuildAnthropicBody(string model, string system, string user, int maxTokens, double temperature)
    {
        var root = new JsonObject
        {
            ["model"] = model,
            ["system"] = system,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        return root.ToJsonString(JsonOptions);
    }

    private static AiResult ParseResponse(string body, bool anthropic)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("接口返回内容无法解析：" + Truncate(body, 300), ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("接口返回内容不是 JSON 对象：" + Truncate(body, 300));
            }

            string text = anthropic ? ExtractAnthropicText(root) : ExtractOpenAiText(root);
            var usage = root.TryGetProperty("usage", out var usageValue) ? usageValue : default;
            int promptTokens = IntOrZero(usage, anthropic ? "input_tokens" : "prompt_tokens");
            int completionTokens = IntOrZero(usage, anthropic ? "output_tokens" : "completion_tokens");
            int cachedTokens = anthropic
                ? IntOrZero(usage, "cache_read_input_tokens")
                : IntOrZero(root.TryGetProperty("usage", out var u2) && u2.TryGetProperty("prompt_tokens_details", out var details) ? details : default, "cached_tokens");

            if (TranslationUnit.IsBlank(text))
            {
                string? error = ErrorMessage(root);
                if (error != null) throw new InvalidOperationException("接口返回错误：" + error);
                throw new InvalidOperationException("接口返回内容为空：" + Truncate(body, 300));
            }

            return new AiResult(text.Trim(), promptTokens, completionTokens, cachedTokens);
        }
    }

    /// <summary>OpenAI 兼容：取 choices[0].message.content（字符串或分段数组）。</summary>
    private static string ExtractOpenAiText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return "";
        var first = default(JsonElement);
        foreach (var choice in choices.EnumerateArray())
        {
            first = choice;
            break;
        }
        if (first.ValueKind != JsonValueKind.Object) return "";
        if (!first.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";

        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("text", out var piece) &&
                piece.ValueKind == JsonValueKind.String)
            {
                builder.Append(piece.GetString());
            }
        }
        return builder.ToString();
    }

    /// <summary>Anthropic：把 content 数组里 type=text 的块拼起来。</summary>
    private static string ExtractAnthropicText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";

        var builder = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (!block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
            if (!string.Equals(type.GetString(), "text", StringComparison.Ordinal)) continue;
            if (block.TryGetProperty("text", out var piece) && piece.ValueKind == JsonValueKind.String)
            {
                builder.Append(piece.GetString());
            }
        }
        return builder.ToString();
    }

    /// <summary>从错误响应里挖出人话：error.message / error（字符串），挖不到就截断原文。</summary>
    private static string ExtractError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            string? message = ErrorMessage(document.RootElement);
            if (!string.IsNullOrEmpty(message)) return message!;
        }
        catch
        {
            // 不是 JSON，走下面的截断兜底。
        }
        return Truncate(body, 300);
    }

    private static string? ErrorMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("error", out var error)) return null;
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }
        if (error.ValueKind == JsonValueKind.String) return error.GetString();
        if (error.ValueKind == JsonValueKind.Object || error.ValueKind == JsonValueKind.Array) return error.ToString();
        return null;
    }

    /// <summary>安全读取一个整数统计字段：缺失 / 类型不符 / 浮点数都返回 0。</summary>
    private static int IntOrZero(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return 0;
        if (!parent.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out int number) ? number : (int)Math.Round(value.GetDouble()),
            JsonValueKind.String => int.TryParse(value.GetString(), out int parsed) ? parsed : 0,
            _ => 0,
        };
    }

    private static string Truncate(string value, int max)
    {
        string text = value ?? "";
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>
/// 系统提示词与用户提示词构建。语义与网页端 <c>server.js</c> 的 buildSystemPrompt / buildUserPrompt、
/// 安卓端 <c>TranslationService.kt</c> 的 PromptBuilder 保持一致：
///   · 系统提示词替换 {sourceLang} {targetLang} {docName} {mode}，并注入术语表 {glossary}；
///   · 逐句模式额外追一句「请保证译文是完整通顺的句子」；
///   · 用户提示词带上文档名、模式、进度、前文参考（最近 N 条已译）与待翻译原文。
/// </summary>
public static class PromptBuilder
{
    /// <summary>构建系统提示词。</summary>
    public static string BuildSystemPrompt(AppSettings settings, string sourceText, string docName, UnitMode mode)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string sourceLang = settings.DetectLanguage ? TextParser.DetectLanguage(sourceText ?? "") : settings.SourceLang;
        string template = settings.EffectivePrompt;
        string glossaryBlock = GlossaryBlock(settings);

        string prompt = template
            .Replace("{sourceLang}", sourceLang == "auto" ? "原语言" : sourceLang)
            .Replace("{targetLang}", TranslationUnit.IsBlank(settings.TargetLang) ? "zh-CN" : settings.TargetLang)
            .Replace("{docName}", docName ?? "")
            .Replace("{mode}", mode == UnitMode.SENTENCE ? "逐句" : "逐行");

        if (template.Contains("{glossary}", StringComparison.Ordinal))
        {
            prompt = prompt.Replace("{glossary}", glossaryBlock);
        }
        else if (glossaryBlock.Length > 0)
        {
            prompt = prompt + "\n" + glossaryBlock;
        }

        if (mode == UnitMode.SENTENCE)
        {
            prompt += "\n当前按句翻译，请保证译文是完整通顺的句子。";
        }
        return prompt;
    }

    /// <summary>构建用户提示词。</summary>
    public static string BuildUserPrompt(
        AppSettings settings,
        string docName,
        TranslationUnit unit,
        UnitMode mode,
        int index,
        int total,
        IReadOnlyList<TranslationUnit>? previousUnits)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(unit);

        var builder = new StringBuilder();
        builder.Append("文档：").Append(docName ?? "");
        builder.Append("  |  当前模式：").Append(mode == UnitMode.SENTENCE ? "逐句" : "逐行");
        builder.Append("  |  当前进度：").Append(index + 1).Append('/').Append(total);

        int contextSize = Math.Clamp(settings.ContextUnits, 0, 10);
        if (contextSize > 0 && previousUnits != null)
        {
            var previous = previousUnits.Where(u => TranslationUnit.IsNotBlank(u.Translation)).ToList();
            if (previous.Count > contextSize) previous = previous.Skip(previous.Count - contextSize).ToList();
            if (previous.Count > 0)
            {
                builder.Append("  |  前文参考：");
                foreach (var item in previous)
                {
                    builder.Append("【原文：").Append(item.Source)
                        .Append(" → 译文：").Append(item.Translation).Append('】');
                }
            }
        }

        builder.Append("  |  待翻译原文：").Append(unit.Source);
        return builder.ToString();
    }

    /// <summary>术语表文本块（没有术语时返回空串），与 server.js 的 glossaryBlock 一致。</summary>
    public static string GlossaryBlock(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var entries = settings.GlossaryEntries;
        if (entries.Count == 0) return "";

        var builder = new StringBuilder("术语表（必须严格使用以下译法）：");
        foreach (var entry in entries)
        {
            builder.Append("\n- ").Append(entry.Key).Append(" → ").Append(entry.Value);
        }
        return builder.ToString();
    }
}
