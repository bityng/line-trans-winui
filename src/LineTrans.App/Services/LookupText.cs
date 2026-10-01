using System;
using System.Threading;
using System.Threading.Tasks;

namespace LineTrans.App.Services;

/// <summary>
/// 「一段文字」的判定与翻译。全局划词弹窗（<see cref="GlobalCaptureService"/>）与
/// 翻译页浮层（<c>WordLookupPanel</c>）共用同一套规则，避免两处对「这是单词还是整句」
/// 给出不一致的结论。
/// </summary>
public static class LookupText
{
    /// <summary>
    /// 短文本 + 全是拉丁字母/数字/连字符 → 当作单词走离线词库；其余（含中文、多词、整句）走 AI 翻译。
    /// </summary>
    public static bool IsLatinWord(string? text)
    {
        string value = text ?? string.Empty;
        if (value.Length == 0 || value.Length > 40) return false;

        bool hasLetter = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c)) return false;
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) { hasLetter = true; continue; }
            if (c >= '0' && c <= '9') continue;
            if (c == '-' || c == '\'' || c == '.' || c == '_') continue;
            return false;
        }
        return hasLetter;
    }

    /// <summary>
    /// 目标语言：优先用设置里的目标语言；但如果选中的本身就是中文、而目标语言又是中文，
    /// 那就译成英文 —— 否则「中译中」毫无意义。
    /// </summary>
    public static string ResolveTargetLanguage(string text)
    {
        string target = AppServices.Settings.TargetLang ?? "zh-CN";
        if (target.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && IsMostlyCjk(text)) return "en";
        return target;
    }

    /// <summary>非空白字符里有一半以上是汉字。</summary>
    public static bool IsMostlyCjk(string text)
    {
        int cjk = 0;
        int total = 0;
        foreach (char c in text ?? string.Empty)
        {
            if (char.IsWhiteSpace(c)) continue;
            total++;
            if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
        }
        return total > 0 && cjk * 2 >= total;
    }

    /// <summary>语言代码 → 中文标签（提示语里用）。</summary>
    public static string LanguageLabel(string? code)
    {
        string value = (code ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "auto" => "自动检测",
            "zh-cn" or "zh" => "中文（简体）",
            "zh-tw" => "中文（繁体）",
            "en" => "英语",
            "ja" => "日语",
            "ko" => "韩语",
            "fr" => "法语",
            "de" => "德语",
            "es" => "西班牙语",
            "ru" => "俄语",
            "pt" => "葡萄牙语",
            "it" => "意大利语",
            "ar" => "阿拉伯语",
            "" => "目标语言",
            _ => value,
        };
    }

    /// <summary>压成一行并截断（标题行用）。</summary>
    public static string Shorten(string? text, int max)
    {
        string flat = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
    }

    /// <summary>
    /// 把一段文字交给已配置的模型翻译。返回的内容结构与全局划词弹窗完全一致，
    /// 翻译页浮层直接照着填即可（含「未配置 AI」这类中文提示语）。
    /// </summary>
    public static async Task<LookupPopupContent> TranslateAsync(
        string text, string status, string configureHint, CancellationToken token)
    {
        string target = ResolveTargetLanguage(text);
        string system = "你是专业翻译。把用户给出的内容翻译成" + LanguageLabel(target)
            + "。只输出译文，不要解释，不要加引号，保持原有格式与换行。";

        try
        {
            var result = await AppServices.Ai.ChatAsync(system, text, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return LookupPopupContent.Loading(text);

            string translated = (result.Text ?? string.Empty).Trim();
            return new LookupPopupContent
            {
                Captured = text,
                Headline = "整句翻译（" + LanguageLabel(AppServices.Settings.SourceLang) + " → " + LanguageLabel(target) + "）",
                Badge = "AI 翻译",
                Body = translated.Length == 0 ? "模型没有返回内容，请再试一次。" : translated,
                Status = status,
            };
        }
        catch (OperationCanceledException)
        {
            return LookupPopupContent.Loading(text);
        }
        catch (Exception ex)
        {
            // AiClient 抛的都是中文提示（未配置 Base URL / 模型 / 密钥、超时、网络失败……），直接展示
            return new LookupPopupContent
            {
                Captured = text,
                Headline = "整句翻译",
                Badge = "未配置 AI",
                Body = ex.Message + Environment.NewLine + Environment.NewLine + configureHint,
                Status = status,
            };
        }
    }
}
