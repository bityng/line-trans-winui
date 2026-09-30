using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LineTrans.Core;

/// <summary>
/// 设置仓库：整份设置以一个 JSON 文件保存，读写在后台线程做防抖（默认 700ms，可配置）。
///
/// 数据位置：<c>%APPDATA%\LineTrans\settings.json</c>。
/// 文件里的 provider 块与网页端 <c>config.json</c> 的 provider 字段一一对应
/// （type / baseUrl / apiKey / model / temperature / maxTokens / inputPrice / outputPrice），
/// 内存里则映射回 <see cref="AppSettings"/>，供界面与 <see cref="AiClient"/> 复用同一套模型。
///
/// 老配置兼容（对应安卓端 <c>SettingsRepository.sanitized()</c>）：
///   · 字段缺失 → 用 <see cref="AppSettings"/> 的默认值，绝不抛异常；
///   · 取值非法 → 回落（definitionLanguage 只能 zh / both / en，非法回落 zh）；
///   · 数值越界 → clamp（contextUnits 0~10、autoSaveMs 200~5000、uiScale 0.8~1.5 …）；
///   · 整份文件损坏 → 备份成 settings.json.broken-&lt;时间戳&gt; 后回落到默认设置。
/// </summary>
public sealed class SettingsRepository : IDisposable
{
    /// <summary>默认写盘防抖间隔（毫秒）。</summary>
    public const int DefaultDebounceMs = 700;

    /// <summary>内存里固定使用的提供方 id（PC 端只有一个当前提供方，扁平存盘）。</summary>
    public const string DefaultProviderId = "default";

    /// <summary>内存里固定使用的模型 id（模型名保存在 <see cref="ModelConfig.Name"/> 里）。</summary>
    public const string DefaultModelId = "default";

    /// <summary>合法的释义语言。</summary>
    public static readonly IReadOnlyList<string> DefinitionLanguages = new[] { "zh", "both", "en" };

    /// <summary>合法的查词来源，与安卓端一致。</summary>
    public static readonly IReadOnlyList<string> DictionarySources = new[]
    {
        "auto", "local", "oxford_web", "oxford_api", "wiktionary", "ai",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly CancellationTokenSource? _lifetime;
    private int _debounceMs;
    private string? _pending;
    private CancellationTokenSource? _timer;
    private bool _disposed;

    /// <param name="filePath">设置文件路径；传 null 用 <see cref="DefaultFilePath"/>（测试里可传临时文件）。</param>
    /// <param name="debounceMs">写盘防抖间隔（毫秒），会 clamp 到 200~5000；传 null 则跟随设置里的 autoSaveMs。</param>
    public SettingsRepository(string? filePath = null, int? debounceMs = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath!;
        _debounceMs = debounceMs.HasValue ? DocRepository.ClampDebounce(debounceMs.Value) : 0;
        _lifetime = new CancellationTokenSource();
        string? dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>默认设置文件：<c>%APPDATA%\LineTrans\settings.json</c>。</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LineTrans", "settings.json");

    /// <summary>设置文件路径。</summary>
    public string FilePath { get; }

    /// <summary>当前设置（永远非空；改完记得 <see cref="Save"/>）。</summary>
    public AppSettings Settings { get; private set; } = new AppSettings();

    /// <summary>设置变化通知（界面订阅后刷新）。</summary>
    public event Action? Changed;

    /// <summary>加载时的告警（文件损坏等），没有问题时为 null。</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>是否有内容等待落盘。</summary>
    public bool IsDirty
    {
        get { lock (_gate) return _pending != null; }
    }

    /// <summary>
    /// 防抖间隔（毫秒）。构造时给了固定值就用固定值，否则跟随设置里的 autoSaveMs（clamp 后）。
    /// </summary>
    public int DebounceMs
    {
        get
        {
            lock (_gate)
            {
                return _debounceMs > 0 ? _debounceMs : DocRepository.ClampDebounce(Settings.AutoSaveMs);
            }
        }
        set
        {
            lock (_gate) _debounceMs = DocRepository.ClampDebounce(value);
        }
    }

    /// <summary>当前提供方（永远非空：老配置缺 provider 时会补一个空的默认提供方）。</summary>
    public ProviderConfig? ActiveProvider => Settings.ActiveProvider;

    /// <summary>当前模型（未填模型名时为 null，<see cref="AiClient"/> 会据此抛中文异常）。</summary>
    public ModelConfig? ActiveModel => Settings.ActiveModel;

    // ------------------------------------------------------------------
    // 读写
    // ------------------------------------------------------------------

    /// <summary>读盘：文件不存在 / 字段缺失 / 整份损坏都不会抛异常。</summary>
    public void Load()
    {
        LoadWarning = null;
        AppSettings loaded;
        if (!File.Exists(FilePath))
        {
            loaded = new AppSettings();
        }
        else
        {
            try
            {
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                loaded = FromDto(JsonSerializer.Deserialize<SettingsDto>(json, JsonOptions));
            }
            catch (Exception ex)
            {
                LoadWarning = "settings.json 解析失败，已备份并回落默认设置：" + ex.Message;
                BackupBrokenFile();
                loaded = new AppSettings();
            }
        }

        Settings = Sanitize(loaded);
        Changed?.Invoke();
    }

    /// <summary>当前设置的 JSON 文本（备份 / 排查用）。</summary>
    public string ToJson() => JsonSerializer.Serialize(ToDto(Settings), JsonOptions);

    /// <summary>把设置写成 JSON 落盘；<paramref name="immediate"/> 为 true 时立刻写，否则走防抖。</summary>
    public void Save(bool immediate = false)
    {
        if (_disposed) return;
        Sanitize(Settings);
        string payload = ToJson();
        lock (_gate)
        {
            _pending = payload;
            _timer?.Cancel();
            _timer?.Dispose();
            _timer = null;
            if (!immediate) ScheduleLocked();
        }
        if (immediate) WritePending();
        Changed?.Invoke();
    }

    /// <summary>改设置再保存（界面最常用的入口）。</summary>
    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        mutate(Settings);
        Save();
    }

    /// <summary>立刻把待写的设置刷到磁盘，返回后文件一定是最新的。</summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            _timer?.Cancel();
            _timer?.Dispose();
            _timer = null;
        }
        WritePending();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 一次性写入当前提供方与模型（设置页的扁平入口，字段与网页端 config.json 的 provider 一致）。
    /// </summary>
    public void SetProvider(
        string type,
        string baseUrl,
        string apiKey,
        string model,
        double temperature = 0.2,
        int maxTokens = 4096,
        double inputPrice = 0.0,
        double outputPrice = 0.0,
        bool save = true)
    {
        var providerType = ParseProviderType(type);
        var s = Settings;
        s.Providers.Clear();
        var provider = new ProviderConfig(DefaultProviderId, ProviderTypeInfo.Label(providerType))
        {
            Type = providerType,
            BaseUrl = baseUrl ?? "",
            ApiKey = apiKey ?? "",
        };
        var modelConfig = new ModelConfig(DefaultModelId, model ?? "", DefaultProviderId)
        {
            Temperature = temperature,
            MaxTokens = maxTokens,
        };
        modelConfig.Billing.InputPrice = inputPrice;
        modelConfig.Billing.OutputPrice = outputPrice;
        provider.Models.Add(modelConfig);
        s.Providers.Add(provider);
        s.ActiveProviderId = DefaultProviderId;
        s.ActiveModelId = DefaultModelId;
        Sanitize(s);
        if (save) Save();
        else Changed?.Invoke();
    }

    /// <summary>缺字段时补默认值、非法值回落、越界值 clamp；返回同一个实例（对应安卓端 sanitized()）。</summary>
    public static AppSettings Sanitize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.TargetLang = TranslationUnit.IsBlank(settings.TargetLang) ? "zh-CN" : settings.TargetLang.Trim();
        settings.SourceLang = TranslationUnit.IsBlank(settings.SourceLang) ? "auto" : settings.SourceLang.Trim();
        settings.DefinitionLanguage = SanitizeDefinitionLanguage(settings.DefinitionLanguage);
        if (!DictionarySources.Contains(settings.DictionarySource)) settings.DictionarySource = "auto";
        if (PromptTemplates.All.All(t => t.Id != settings.PromptTemplateId)) settings.PromptTemplateId = AppSettings.DefaultPromptId;
        if (!Enum.IsDefined(typeof(ThemeMode), settings.ThemeMode)) settings.ThemeMode = ThemeMode.SYSTEM;
        if (!Enum.IsDefined(typeof(ExportFormat), settings.DefaultExportFormat))
        {
            settings.DefaultExportFormat = ExportFormat.TXT_BILINGUAL;
        }

        settings.ContextUnits = Math.Clamp(settings.ContextUnits, 0, 10);
        settings.AutoSaveMs = DocRepository.ClampDebounce(settings.AutoSaveMs);
        settings.RequestTimeoutSec = Math.Clamp(settings.RequestTimeoutSec, 10, 600);
        settings.MaxRetries = Math.Clamp(settings.MaxRetries, 0, 5);
        settings.WebServerPort = Math.Clamp(settings.WebServerPort, 1024, 65535);
        settings.UiScale = Math.Clamp(settings.UiScale, 0.8f, 1.5f);
        settings.DailyGoal = Math.Clamp(settings.DailyGoal, 0, 100000);

        foreach (var provider in settings.Providers)
        {
            if (provider.BaseUrl == null) provider.BaseUrl = "";
            if (provider.ApiKey == null) provider.ApiKey = "";
            if (!Enum.IsDefined(typeof(ProviderType), provider.Type)) provider.Type = ProviderType.OPENAI_COMPAT;
            foreach (var model in provider.Models)
            {
                if (model.Name == null) model.Name = "";
                model.Temperature = double.IsFinite(model.Temperature) ? Math.Clamp(model.Temperature, 0.0, 2.0) : 0.2;
                model.TopP = double.IsFinite(model.TopP) ? Math.Clamp(model.TopP, 0.0, 1.0) : 1.0;
                model.MaxTokens = Math.Clamp(model.MaxTokens, 0, 1_000_000);
                var billing = model.Billing;
                billing.InputPrice = double.IsFinite(billing.InputPrice) ? Math.Max(0.0, billing.InputPrice) : 0.0;
                billing.OutputPrice = double.IsFinite(billing.OutputPrice) ? Math.Max(0.0, billing.OutputPrice) : 0.0;
                billing.PeakMultiplier = double.IsFinite(billing.PeakMultiplier) ? Math.Max(0.0, billing.PeakMultiplier) : 1.0;
                billing.PeakStartHour = Math.Clamp(billing.PeakStartHour, 0, 23);
                billing.PeakEndHour = Math.Clamp(billing.PeakEndHour, 0, 23);
            }
        }

        // 只有一个提供方时给它建出来，保证界面永远有可绑定的对象（字段为空由 AiClient 报中文异常）。
        if (settings.Providers.Count == 0) settings.Providers.Add(NewDefaultProvider());
        foreach (var provider in settings.Providers)
        {
            if (provider.Models.Count == 0) provider.Models.Add(new ModelConfig(DefaultModelId, "", provider.Id));
        }

        if (settings.Providers.All(p => p.Id != settings.ActiveProviderId)) settings.ActiveProviderId = settings.Providers[0].Id;
        if (settings.ActiveProvider?.FindModel(settings.ActiveModelId) == null)
        {
            settings.ActiveModelId = settings.ActiveProvider?.Models[0].Id ?? "";
        }
        return settings;
    }

    /// <summary>释义语言回落：只能是 zh / both / en，其余（含 null 与大小写混写）一律回落 zh。</summary>
    public static string SanitizeDefinitionLanguage(string? value)
    {
        string raw = (value ?? "").Trim().ToLowerInvariant();
        return DefinitionLanguages.Contains(raw) ? raw : "zh";
    }

    /// <summary>把配置里的 provider.type 解析成枚举（openai / anthropic / custom，认不出按 OpenAI 兼容）。</summary>
    public static ProviderType ParseProviderType(string? raw)
    {
        string value = (raw ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "anthropic" => ProviderType.ANTHROPIC,
            "custom" => ProviderType.CUSTOM,
            _ => ProviderType.OPENAI_COMPAT,
        };
    }

    /// <summary>枚举写回配置时的字符串（与网页端 config.json 一致）。</summary>
    public static string ProviderTypeToId(ProviderType type) => type switch
    {
        ProviderType.ANTHROPIC => "anthropic",
        ProviderType.CUSTOM => "custom",
        _ => "openai",
    };

    private static ProviderConfig NewDefaultProvider() =>
        new(DefaultProviderId, ProviderTypeInfo.Label(ProviderType.OPENAI_COMPAT));

    // ------------------------------------------------------------------
    // 内部：防抖写盘
    // ------------------------------------------------------------------

    private void ScheduleLocked()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime!.Token);
        _timer = cts;
        int delay = DebounceMs;
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
            WritePending();
        });
    }

    private void WritePending()
    {
        string? payload;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            payload = _pending;
            if (payload == null) return;
        }

        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, payload, new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
            lock (_gate)
            {
                if (_pending == payload) _pending = null;
            }
        }
        catch (Exception ex)
        {
            // 写盘失败时保留 _pending，下次 Save / FlushAsync 还会再试一次。
            LoadWarning = "设置写盘失败：" + ex.Message;
        }
    }

    private void BackupBrokenFile()
    {
        try
        {
            string backup = FilePath + ".broken-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.Copy(FilePath, backup, overwrite: true);
        }
        catch
        {
            // 备份失败不影响继续用默认设置启动。
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _timer?.Cancel();
            _timer?.Dispose();
            _timer = null;
        }
        _lifetime?.Cancel();
        _lifetime?.Dispose();
    }

    // ------------------------------------------------------------------
    // 内部：JSON 映射（扁平 provider ↔ AppSettings）
    // ------------------------------------------------------------------

    private static AppSettings FromDto(SettingsDto? dto)
    {
        var s = new AppSettings();
        if (dto == null) return s;

        if (dto.Provider != null)
        {
            var type = ParseProviderType(dto.Provider.Type);
            var provider = new ProviderConfig(DefaultProviderId, ProviderTypeInfo.Label(type))
            {
                Type = type,
                BaseUrl = dto.Provider.BaseUrl ?? "",
                ApiKey = dto.Provider.ApiKey ?? "",
            };
            var model = new ModelConfig(DefaultModelId, dto.Provider.Model ?? "", DefaultProviderId);
            if (dto.Provider.Temperature.HasValue) model.Temperature = dto.Provider.Temperature.Value;
            if (dto.Provider.MaxTokens.HasValue) model.MaxTokens = dto.Provider.MaxTokens.Value;
            if (dto.Provider.InputPrice.HasValue) model.Billing.InputPrice = dto.Provider.InputPrice.Value;
            if (dto.Provider.OutputPrice.HasValue) model.Billing.OutputPrice = dto.Provider.OutputPrice.Value;
            if (dto.Provider.PeakMultiplier.HasValue) model.Billing.PeakMultiplier = dto.Provider.PeakMultiplier.Value;
            if (dto.Provider.PeakStartHour.HasValue) model.Billing.PeakStartHour = dto.Provider.PeakStartHour.Value;
            if (dto.Provider.PeakEndHour.HasValue) model.Billing.PeakEndHour = dto.Provider.PeakEndHour.Value;
            provider.Models.Add(model);
            s.Providers.Add(provider);
            s.ActiveProviderId = DefaultProviderId;
            s.ActiveModelId = DefaultModelId;
        }

        if (dto.TargetLang != null) s.TargetLang = dto.TargetLang;
        if (dto.SourceLang != null) s.SourceLang = dto.SourceLang;
        if (dto.DetectLanguage.HasValue) s.DetectLanguage = dto.DetectLanguage.Value;
        if (dto.ContextUnits.HasValue) s.ContextUnits = dto.ContextUnits.Value;
        if (dto.SystemPrompt != null) s.SystemPrompt = dto.SystemPrompt;
        if (dto.PromptTemplateId != null) s.PromptTemplateId = dto.PromptTemplateId;
        if (dto.Glossary != null) s.Glossary = dto.Glossary;
        if (dto.DefinitionLanguage != null) s.DefinitionLanguage = dto.DefinitionLanguage;
        if (dto.LookupAiFallback.HasValue) s.LookupAiFallback = dto.LookupAiFallback.Value;
        if (dto.WordLookupEnabled.HasValue) s.WordLookupEnabled = dto.WordLookupEnabled.Value;
        if (dto.DictionarySource != null) s.DictionarySource = dto.DictionarySource;
        if (dto.LocalDictionaryEnabled.HasValue) s.LocalDictionaryEnabled = dto.LocalDictionaryEnabled.Value;
        if (dto.DictionaryAiExplain.HasValue) s.DictionaryAiExplain = dto.DictionaryAiExplain.Value;
        if (dto.OxfordAppId != null) s.OxfordAppId = dto.OxfordAppId;
        if (dto.OxfordAppKey != null) s.OxfordAppKey = dto.OxfordAppKey;
        if (dto.Theme != null) s.ThemeMode = ParseTheme(dto.Theme);
        if (dto.DynamicColor.HasValue) s.DynamicColor = dto.DynamicColor.Value;
        if (dto.UiScale.HasValue) s.UiScale = (float)dto.UiScale.Value;
        if (dto.Animations.HasValue) s.Animations = dto.Animations.Value;
        if (dto.ShowProgressRing.HasValue) s.ShowProgressRing = dto.ShowProgressRing.Value;
        if (dto.KeepScreenOn.HasValue) s.KeepScreenOn = dto.KeepScreenOn.Value;
        if (dto.SwipeToSwitch.HasValue) s.SwipeToSwitch = dto.SwipeToSwitch.Value;
        if (dto.AutoAdvance.HasValue) s.AutoAdvance = dto.AutoAdvance.Value;
        if (dto.TranslationMemory.HasValue) s.TranslationMemory = dto.TranslationMemory.Value;
        if (dto.AutoSaveMs.HasValue) s.AutoSaveMs = dto.AutoSaveMs.Value;
        if (dto.RequestTimeoutSec.HasValue) s.RequestTimeoutSec = dto.RequestTimeoutSec.Value;
        if (dto.MaxRetries.HasValue) s.MaxRetries = dto.MaxRetries.Value;
        if (dto.ProxyUrl != null) s.ProxyUrl = dto.ProxyUrl;
        if (dto.UserAgent != null) s.UserAgent = dto.UserAgent;
        if (dto.StorageDirUri != null) s.StorageDirUri = dto.StorageDirUri;
        if (dto.DailyGoal.HasValue) s.DailyGoal = dto.DailyGoal.Value;
        if (dto.DefaultExportFormat != null) s.DefaultExportFormat = ParseExportFormat(dto.DefaultExportFormat);
        if (dto.WebServerPort.HasValue) s.WebServerPort = dto.WebServerPort.Value;
        if (dto.WebServerEnabled.HasValue) s.WebServerEnabled = dto.WebServerEnabled.Value;
        if (dto.WebServerToken != null) s.WebServerToken = dto.WebServerToken;
        if (dto.WebServerAutoStart.HasValue) s.WebServerAutoStart = dto.WebServerAutoStart.Value;
        if (dto.DailyDate != null) s.DailyDate = dto.DailyDate;
        if (dto.DailyCount.HasValue) s.DailyCount = dto.DailyCount.Value;

        if (dto.UsageByModel != null)
        {
            foreach (var usage in dto.UsageByModel)
            {
                if (usage == null) continue;
                s.UsageByModel.Add(new UsageRecord(usage.ModelId ?? "", usage.ModelName ?? "")
                {
                    Calls = usage.Calls,
                    InputTokens = usage.InputTokens,
                    CachedTokens = usage.CachedTokens,
                    OutputTokens = usage.OutputTokens,
                    Cost = usage.Cost,
                });
            }
        }

        if (dto.DailyStats != null)
        {
            foreach (var stat in dto.DailyStats)
            {
                if (stat == null) continue;
                s.DailyStats.Add(new DailyStat
                {
                    Date = stat.Date ?? "",
                    Units = stat.Units,
                    Tokens = stat.Tokens,
                    Cost = stat.Cost,
                });
            }
        }

        return s;
    }

    private static SettingsDto ToDto(AppSettings s)
    {
        var provider = s.ActiveProvider;
        var model = s.ActiveModel;
        var dto = new SettingsDto
        {
            TargetLang = s.TargetLang,
            SourceLang = s.SourceLang,
            DetectLanguage = s.DetectLanguage,
            ContextUnits = s.ContextUnits,
            SystemPrompt = s.SystemPrompt,
            PromptTemplateId = s.PromptTemplateId,
            Glossary = s.Glossary,
            DefinitionLanguage = s.DefinitionLanguage,
            LookupAiFallback = s.LookupAiFallback,
            WordLookupEnabled = s.WordLookupEnabled,
            DictionarySource = s.DictionarySource,
            LocalDictionaryEnabled = s.LocalDictionaryEnabled,
            DictionaryAiExplain = s.DictionaryAiExplain,
            OxfordAppId = s.OxfordAppId,
            OxfordAppKey = s.OxfordAppKey,
            Theme = s.ThemeMode.ToString().ToLowerInvariant(),
            DynamicColor = s.DynamicColor,
            UiScale = s.UiScale,
            Animations = s.Animations,
            ShowProgressRing = s.ShowProgressRing,
            KeepScreenOn = s.KeepScreenOn,
            SwipeToSwitch = s.SwipeToSwitch,
            AutoAdvance = s.AutoAdvance,
            TranslationMemory = s.TranslationMemory,
            AutoSaveMs = s.AutoSaveMs,
            RequestTimeoutSec = s.RequestTimeoutSec,
            MaxRetries = s.MaxRetries,
            ProxyUrl = s.ProxyUrl,
            UserAgent = s.UserAgent,
            StorageDirUri = s.StorageDirUri,
            DailyGoal = s.DailyGoal,
            DefaultExportFormat = s.DefaultExportFormat.ToString(),
            WebServerPort = s.WebServerPort,
            WebServerEnabled = s.WebServerEnabled,
            WebServerToken = s.WebServerToken,
            WebServerAutoStart = s.WebServerAutoStart,
            DailyDate = s.DailyDate,
            DailyCount = s.DailyCount,
            Provider = provider == null
                ? null
                : new ProviderDto
                {
                    Type = ProviderTypeToId(provider.Type),
                    BaseUrl = provider.BaseUrl,
                    ApiKey = provider.ApiKey,
                    Model = model?.Name ?? "",
                    Temperature = model?.Temperature,
                    MaxTokens = model?.MaxTokens,
                    InputPrice = model?.Billing.InputPrice,
                    OutputPrice = model?.Billing.OutputPrice,
                    PeakMultiplier = model?.Billing.PeakMultiplier,
                    PeakStartHour = model?.Billing.PeakStartHour,
                    PeakEndHour = model?.Billing.PeakEndHour,
                },
        };

        dto.UsageByModel = s.UsageByModel
            .Select(u => (UsageDto?)new UsageDto
            {
                ModelId = u.ModelId,
                ModelName = u.ModelName,
                Calls = u.Calls,
                InputTokens = u.InputTokens,
                CachedTokens = u.CachedTokens,
                OutputTokens = u.OutputTokens,
                Cost = u.Cost,
            })
            .ToList();

        dto.DailyStats = s.DailyStats
            .Select(d => (DailyStatDto?)new DailyStatDto { Date = d.Date, Units = d.Units, Tokens = d.Tokens, Cost = d.Cost })
            .ToList();

        return dto;
    }

    private static ThemeMode ParseTheme(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.LIGHT,
        "dark" => ThemeMode.DARK,
        _ => ThemeMode.SYSTEM,
    };

    private static ExportFormat ParseExportFormat(string raw) =>
        Enum.TryParse<ExportFormat>(raw, ignoreCase: true, out var format) ? format : ExportFormat.TXT_BILINGUAL;

    private sealed class SettingsDto
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;

        [JsonPropertyName("provider")] public ProviderDto? Provider { get; set; }

        [JsonPropertyName("targetLang")] public string? TargetLang { get; set; }

        [JsonPropertyName("sourceLang")] public string? SourceLang { get; set; }

        [JsonPropertyName("detectLanguage")] public bool? DetectLanguage { get; set; }

        [JsonPropertyName("contextUnits")] public int? ContextUnits { get; set; }

        [JsonPropertyName("systemPrompt")] public string? SystemPrompt { get; set; }

        [JsonPropertyName("promptTemplateId")] public string? PromptTemplateId { get; set; }

        [JsonPropertyName("glossary")] public string? Glossary { get; set; }

        [JsonPropertyName("definitionLanguage")] public string? DefinitionLanguage { get; set; }

        [JsonPropertyName("lookupAiFallback")] public bool? LookupAiFallback { get; set; }

        [JsonPropertyName("wordLookupEnabled")] public bool? WordLookupEnabled { get; set; }

        [JsonPropertyName("dictionarySource")] public string? DictionarySource { get; set; }

        [JsonPropertyName("localDictionaryEnabled")] public bool? LocalDictionaryEnabled { get; set; }

        [JsonPropertyName("dictionaryAiExplain")] public bool? DictionaryAiExplain { get; set; }

        [JsonPropertyName("oxfordAppId")] public string? OxfordAppId { get; set; }

        [JsonPropertyName("oxfordAppKey")] public string? OxfordAppKey { get; set; }

        [JsonPropertyName("theme")] public string? Theme { get; set; }

        [JsonPropertyName("dynamicColor")] public bool? DynamicColor { get; set; }

        [JsonPropertyName("uiScale")] public double? UiScale { get; set; }

        [JsonPropertyName("animations")] public bool? Animations { get; set; }

        [JsonPropertyName("showProgressRing")] public bool? ShowProgressRing { get; set; }

        [JsonPropertyName("keepScreenOn")] public bool? KeepScreenOn { get; set; }

        [JsonPropertyName("swipeToSwitch")] public bool? SwipeToSwitch { get; set; }

        [JsonPropertyName("autoAdvance")] public bool? AutoAdvance { get; set; }

        [JsonPropertyName("translationMemory")] public bool? TranslationMemory { get; set; }

        [JsonPropertyName("autoSaveMs")] public int? AutoSaveMs { get; set; }

        [JsonPropertyName("requestTimeoutSec")] public int? RequestTimeoutSec { get; set; }

        [JsonPropertyName("maxRetries")] public int? MaxRetries { get; set; }

        [JsonPropertyName("proxyUrl")] public string? ProxyUrl { get; set; }

        [JsonPropertyName("userAgent")] public string? UserAgent { get; set; }

        [JsonPropertyName("storageDirUri")] public string? StorageDirUri { get; set; }

        [JsonPropertyName("dailyGoal")] public int? DailyGoal { get; set; }

        [JsonPropertyName("defaultExportFormat")] public string? DefaultExportFormat { get; set; }

        [JsonPropertyName("webServerPort")] public int? WebServerPort { get; set; }

        [JsonPropertyName("webServerEnabled")] public bool? WebServerEnabled { get; set; }

        [JsonPropertyName("webServerToken")] public string? WebServerToken { get; set; }

        [JsonPropertyName("webServerAutoStart")] public bool? WebServerAutoStart { get; set; }

        [JsonPropertyName("dailyDate")] public string? DailyDate { get; set; }

        [JsonPropertyName("dailyCount")] public int? DailyCount { get; set; }

        [JsonPropertyName("usageByModel")] public List<UsageDto?>? UsageByModel { get; set; }

        [JsonPropertyName("dailyStats")] public List<DailyStatDto?>? DailyStats { get; set; }
    }

    private sealed class ProviderDto
    {
        [JsonPropertyName("type")] public string? Type { get; set; }

        [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }

        [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }

        [JsonPropertyName("model")] public string? Model { get; set; }

        [JsonPropertyName("temperature")] public double? Temperature { get; set; }

        [JsonPropertyName("maxTokens")] public int? MaxTokens { get; set; }

        [JsonPropertyName("inputPrice")] public double? InputPrice { get; set; }

        [JsonPropertyName("outputPrice")] public double? OutputPrice { get; set; }

        [JsonPropertyName("peakMultiplier")] public double? PeakMultiplier { get; set; }

        [JsonPropertyName("peakStartHour")] public int? PeakStartHour { get; set; }

        [JsonPropertyName("peakEndHour")] public int? PeakEndHour { get; set; }
    }

    private sealed class UsageDto
    {
        [JsonPropertyName("modelId")] public string? ModelId { get; set; }

        [JsonPropertyName("modelName")] public string? ModelName { get; set; }

        [JsonPropertyName("calls")] public int Calls { get; set; }

        [JsonPropertyName("inputTokens")] public long InputTokens { get; set; }

        [JsonPropertyName("cachedTokens")] public long CachedTokens { get; set; }

        [JsonPropertyName("outputTokens")] public long OutputTokens { get; set; }

        [JsonPropertyName("cost")] public double Cost { get; set; }
    }

    private sealed class DailyStatDto
    {
        [JsonPropertyName("date")] public string? Date { get; set; }

        [JsonPropertyName("units")] public int Units { get; set; }

        [JsonPropertyName("tokens")] public long Tokens { get; set; }

        [JsonPropertyName("cost")] public double Cost { get; set; }
    }
}
