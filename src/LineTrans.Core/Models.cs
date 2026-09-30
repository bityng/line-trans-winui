using System.Globalization;
using System.Text;

namespace LineTrans.Core;

/// <summary>切分模式：逐行 / 逐句。</summary>
public enum UnitMode
{
    LINE,
    SENTENCE,
}

/// <summary>主题模式。</summary>
public enum ThemeMode
{
    SYSTEM,
    LIGHT,
    DARK,
}

/// <summary>导出格式（对应安卓端 model/Models.kt 的 ExportFormat）。</summary>
public enum ExportFormat
{
    TXT_TRANSLATED_ONLY,
    TXT_BILINGUAL,
    TXT_SOURCE_FALLBACK,
    MARKDOWN_TABLE,
    CSV,
    JSON,
}

/// <summary>导出格式的中文名与扩展名，等价 Kotlin 枚举的 label / extension。</summary>
public static class ExportFormatInfo
{
    public static string Label(ExportFormat format) => format switch
    {
        ExportFormat.TXT_TRANSLATED_ONLY => "仅译文 TXT",
        ExportFormat.TXT_BILINGUAL => "原文 + 译文 TXT",
        ExportFormat.TXT_SOURCE_FALLBACK => "已译替换源文 TXT",
        ExportFormat.MARKDOWN_TABLE => "Markdown 表格",
        ExportFormat.CSV => "CSV 表格",
        ExportFormat.JSON => "JSON（含元数据）",
        _ => format.ToString(),
    };

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.MARKDOWN_TABLE => "md",
        ExportFormat.CSV => "csv",
        ExportFormat.JSON => "json",
        _ => "txt",
    };
}

/// <summary>一条翻译单元（原文 + 译文 + 状态）。</summary>
public sealed class TranslationUnit
{
    public TranslationUnit(string source, string translation = "", bool done = false, bool starred = false)
    {
        Source = source;
        Translation = translation;
        Done = done;
        Starred = starred;
    }

    public string Source { get; set; }

    public string Translation { get; set; }

    public bool Done { get; set; }

    /// <summary>收藏 / 星标，便于回看重点句。</summary>
    public bool Starred { get; set; }

    /// <summary>是否真的有译文（用于导出时决定“已翻译”）。</summary>
    public bool IsTranslated => IsNotBlank(Translation);

    /// <summary>是否已处理（翻译或标记为“不需要翻译/跳过”），用于进度统计。</summary>
    public bool IsDone => Done || IsNotBlank(Translation);

    /// <summary>Kotlin 的 isNotBlank()：非空且含非空白字符。</summary>
    internal static bool IsNotBlank(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsWhiteSpace(c)) return true;
        }
        return false;
    }

    /// <summary>Kotlin 的 isBlank()。</summary>
    internal static bool IsBlank(string value) => !IsNotBlank(value);
}

/// <summary>一篇文档。</summary>
public sealed class TranslationDoc
{
    public const string DefaultFolder = "默认";

    public TranslationDoc(
        string id,
        string name,
        string folder = DefaultFolder,
        List<TranslationUnit>? units = null,
        UnitMode unitMode = UnitMode.LINE,
        string sourceText = "",
        bool pinned = false,
        int lastIndex = 0,
        long? createdAt = null,
        long? updatedAt = null)
    {
        Id = id;
        Name = name;
        Folder = folder;
        Units = units ?? new List<TranslationUnit>();
        UnitMode = unitMode;
        SourceText = sourceText;
        Pinned = pinned;
        LastIndex = lastIndex;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        CreatedAt = createdAt ?? now;
        UpdatedAt = updatedAt ?? now;
    }

    public string Id { get; }

    public string Name { get; set; }

    public string Folder { get; set; }

    public List<TranslationUnit> Units { get; }

    public UnitMode UnitMode { get; set; }

    public string SourceText { get; set; }

    /// <summary>置顶显示。</summary>
    public bool Pinned { get; set; }

    /// <summary>上次编辑到的位置，重新打开文档时回到这里。</summary>
    public int LastIndex { get; set; }

    public long CreatedAt { get; }

    public long UpdatedAt { get; set; }

    public int TotalCount => Units.Count;

    public int TranslatedCount => Units.Count(u => u.IsDone);

    public int RemainingCount => Units.Count(u => !u.IsDone);

    public int StarredCount => Units.Count(u => u.Starred);

    public float Progress => TotalCount == 0 ? 0f : (float)TranslatedCount / TotalCount;

    public bool IsFinished => TotalCount > 0 && TranslatedCount >= TotalCount;

    /// <summary>从 from 开始按环形顺序找下一个未完成的单元，全部完成时返回 null。</summary>
    public int? NextUndoneIndex(int from = 0)
    {
        if (Units.Count == 0) return null;
        for (int offset = 0; offset < Units.Count; offset++)
        {
            int i = (((from + offset) % Units.Count) + Units.Count) % Units.Count;
            if (!Units[i].IsDone) return i;
        }
        return null;
    }

    /// <summary>命中翻译记忆：返回已有译文的相同原文（排除 excludeIndex）。</summary>
    public string? MemoryTranslation(string source, int excludeIndex = -1)
    {
        string key = source.Trim();
        if (key.Length == 0) return null;
        for (int i = 0; i < Units.Count; i++)
        {
            if (i == excludeIndex) continue;
            var u = Units[i];
            if (u.Source.Trim() == key && TranslationUnit.IsNotBlank(u.Translation)) return u.Translation;
        }
        return null;
    }
}

/// <summary>AI 提供商类型。</summary>
public enum ProviderType
{
    OPENAI_COMPAT,
    ANTHROPIC,
    CUSTOM,
}

public static class ProviderTypeInfo
{
    public static string Label(ProviderType type) => type switch
    {
        ProviderType.OPENAI_COMPAT => "OpenAI 兼容",
        ProviderType.ANTHROPIC => "Anthropic",
        ProviderType.CUSTOM => "自定义",
        _ => type.ToString(),
    };
}

/// <summary>计费配置（每百万 token 单价 + 高峰倍率）。</summary>
public sealed class BillingConfig
{
    public double InputPrice { get; set; }

    public double OutputPrice { get; set; }

    public double PeakMultiplier { get; set; } = 1.0;

    public int PeakStartHour { get; set; } = 8;

    public int PeakEndHour { get; set; } = 22;

    /// <summary>当前小时（0-23）。默认取本机本地时间，测试里可以注入固定值。</summary>
    public Func<int> HourProvider { get; set; } = () => DateTime.Now.Hour;

    /// <summary>等价 Kotlin 的 currentMultiplier（非高峰时段倍率为 1.0）。</summary>
    public double CurrentMultiplier
    {
        get
        {
            if (PeakMultiplier == 1.0) return 1.0;
            int h = HourProvider();
            bool inPeak = PeakStartHour <= PeakEndHour
                ? h >= PeakStartHour && h < PeakEndHour
                : h >= PeakStartHour || h < PeakEndHour;
            return inPeak ? PeakMultiplier : 1.0;
        }
    }

    /// <summary>是否配置了价格（未配置时不做费用估算）。</summary>
    public bool HasPrice => InputPrice > 0.0 || OutputPrice > 0.0;
}

public sealed class ModelConfig
{
    public ModelConfig(string id, string name, string providerId)
    {
        Id = id;
        Name = name;
        ProviderId = providerId;
    }

    public string Id { get; }

    public string Name { get; set; }

    public string ProviderId { get; set; }

    public BillingConfig Billing { get; set; } = new BillingConfig();

    public int MaxTokens { get; set; } = 4096;

    public double Temperature { get; set; } = 0.2;

    public double TopP { get; set; } = 1.0;
}

public sealed class ProviderConfig
{
    public ProviderConfig(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public string Name { get; set; }

    public ProviderType Type { get; set; } = ProviderType.OPENAI_COMPAT;

    public string BaseUrl { get; set; } = "";

    public string ApiKey { get; set; } = "";

    public List<ModelConfig> Models { get; } = new List<ModelConfig>();

    public ModelConfig? FindModel(string id) => Models.FirstOrDefault(m => m.Id == id);
}

/// <summary>单个模型的累计用量。</summary>
public sealed class UsageRecord
{
    public UsageRecord(string modelId, string modelName)
    {
        ModelId = modelId;
        ModelName = modelName;
    }

    public string ModelId { get; set; }

    public string ModelName { get; set; }

    public int Calls { get; set; }

    public long InputTokens { get; set; }

    public long CachedTokens { get; set; }

    public long OutputTokens { get; set; }

    public double Cost { get; set; }
}

/// <summary>每日统计，用于进度图表。</summary>
public sealed class DailyStat
{
    public string Date { get; set; } = "";

    public int Units { get; set; }

    public long Tokens { get; set; }

    public double Cost { get; set; }
}

/// <summary>应用设置（Android 专有字段已去掉，只保留纯数据）。</summary>
public sealed class AppSettings
{
    public const string DefaultPromptId = "default";

    // —— AI ——
    public List<ProviderConfig> Providers { get; } = new List<ProviderConfig>();

    public string ActiveProviderId { get; set; } = "";

    public string ActiveModelId { get; set; } = "";

    public bool DetectLanguage { get; set; } = true;

    public string SourceLang { get; set; } = "auto";

    public string TargetLang { get; set; } = "zh-CN";

    /// <summary>用户可编辑的系统提示词，支持 {sourceLang} {targetLang} {docName} {mode} {glossary} 占位符。</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>使用的提示词模板 id（default / literal / literary / technical / subtitle / custom）。</summary>
    public string PromptTemplateId { get; set; } = DefaultPromptId;

    /// <summary>术语表，每行一条“原文=译文”。</summary>
    public string Glossary { get; set; } = "";

    /// <summary>AI 翻译时携带的前文参考句数。</summary>
    public int ContextUnits { get; set; } = 3;

    public bool AutoAdvance { get; set; } = true;

    public bool TranslationMemory { get; set; } = true;

    public bool WordLookupEnabled { get; set; } = true;

    public string DictionarySource { get; set; } = "auto";

    public bool LocalDictionaryEnabled { get; set; } = true;

    public string DefinitionLanguage { get; set; } = "zh";

    public bool DictionaryAiExplain { get; set; } = true;

    public string OxfordAppId { get; set; } = "";

    public string OxfordAppKey { get; set; } = "";

    // —— 界面 ——
    public ThemeMode ThemeMode { get; set; } = ThemeMode.SYSTEM;

    public bool DynamicColor { get; set; } = true;

    public float UiScale { get; set; } = 1.0f;

    public bool Animations { get; set; } = true;

    public bool ShowProgressRing { get; set; } = true;

    // —— 翻译行为 ——
    public bool KeepScreenOn { get; set; }

    public bool SwipeToSwitch { get; set; } = true;

    public int AutoSaveMs { get; set; } = 700;

    // —— 网络 ——
    public int RequestTimeoutSec { get; set; } = 120;

    public int MaxRetries { get; set; } = 2;

    public string ProxyUrl { get; set; } = "";

    public string UserAgent { get; set; } = "";

    // —— 数据 ——
    public string StorageDirUri { get; set; } = "";

    public int DailyGoal { get; set; }

    public ExportFormat DefaultExportFormat { get; set; } = ExportFormat.TXT_BILINGUAL;

    // —— Web 终端 ——
    public int WebServerPort { get; set; } = 8080;

    public bool WebServerEnabled { get; set; }

    public string WebServerToken { get; set; } = "";

    public bool WebServerAutoStart { get; set; }

    // —— 统计 ——
    public string DailyDate { get; set; } = "";

    public int DailyCount { get; set; }

    public List<UsageRecord> UsageByModel { get; } = new List<UsageRecord>();

    public List<DailyStat> DailyStats { get; } = new List<DailyStat>();

    public ProviderConfig? ActiveProvider => Providers.FirstOrDefault(p => p.Id == ActiveProviderId);

    public ModelConfig? ActiveModel => ActiveProvider?.FindModel(ActiveModelId);

    public IEnumerable<ModelConfig> AllModels => Providers.SelectMany(p => p.Models);

    /// <summary>实际使用的系统提示词（未自定义时用默认模板）。</summary>
    public string EffectivePrompt => TranslationUnit.IsBlank(SystemPrompt) ? PromptTemplates.Default : SystemPrompt;

    /// <summary>术语表逐行解析（等价 Kotlin 的 glossaryEntries）。</summary>
    public List<KeyValuePair<string, string>> GlossaryEntries
    {
        get
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (string line in SplitLines(Glossary))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                int sep = -1;
                for (int i = 0; i < t.Length; i++)
                {
                    char c = t[i];
                    if (c == '=' || c == '\t' || c == '：' || c == ':') { sep = i; break; }
                }
                if (sep <= 0) continue;
                string key = t.Substring(0, sep).Trim();
                string value = t.Substring(sep + 1).Trim();
                if (key.Length == 0 || value.Length == 0) continue;
                result.Add(new KeyValuePair<string, string>(key, value));
            }
            return result;
        }
    }

    /// <summary>等价 Kotlin 的 CharSequence.lines()：按 CRLF / LF / CR 拆分。</summary>
    internal static List<string> SplitLines(string text)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                parts.Add(text.Substring(start, i - start));
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            else if (c == '\n')
            {
                parts.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(text.Substring(start));
        return parts;
    }
}

/// <summary>内置系统提示词模板。</summary>
public static class PromptTemplates
{
    public const string Default =
        "你是专业翻译。请把用户提供的内容从 {sourceLang} 翻译成 {targetLang}。" +
        "只输出译文，不要解释，不要添加多余内容，保持原有格式与换行。";

    public sealed record Template(string Id, string Name, string Description, string Prompt);

    public static readonly IReadOnlyList<Template> All = new List<Template>
    {
        new Template("default", "通用（默认）", "忠实直译，保持格式，适合大多数场景", Default),
        new Template(
            "literal",
            "逐字对照",
            "尽量保留原文语序与结构，适合精读",
            "你是严谨的对照翻译工具。把 {sourceLang} 内容逐句翻译成 {targetLang}，" +
            "尽量保留原文语序与结构，术语前后一致。只输出译文，不要解释。"),
        new Template(
            "literary",
            "文学润色",
            "译文自然流畅，适合小说、散文",
            "你是文学翻译。把 {sourceLang} 内容翻译成 {targetLang}，" +
            "译文要自然流畅、符合目标语言表达习惯，保留原文语气与情感。" +
            "只输出译文，不要解释。"),
        new Template(
            "technical",
            "技术文档",
            "术语准确、语句简洁，保留代码与标记",
            "你是技术文档翻译。把 {sourceLang} 内容翻译成 {targetLang}，" +
            "术语准确、语句简洁；代码、命令、变量名、占位符保持原样不翻译。" +
            "只输出译文，不要解释。"),
        new Template(
            "subtitle",
            "字幕口语",
            "口语化、长度贴近原文，适合字幕",
            "你是字幕翻译。把 {sourceLang} 内容翻译成 {targetLang}，" +
            "使用口语化表达，长度尽量贴近原文，不要出现换行。" +
            "只输出译文，不要解释。"),
    };

    public static Template ById(string id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
