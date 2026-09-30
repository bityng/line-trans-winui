using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using LineTrans.App.Interop;
using LineTrans.App.Views;

namespace LineTrans.App.Services;

/// <summary>
/// 全局划词：按下热键（或点托盘菜单）之后跑完整条链路
///   保存剪贴板 → 模拟 Ctrl+C → 读剪贴板 → 立刻还原剪贴板 → 弹窗查词/翻译。
///
/// 线程：全程在 UI 线程（剪贴板是 STA API，弹窗也要 UI 线程）。
/// 托盘线程只负责把事件派发过来。
/// </summary>
public static class GlobalCaptureService
{
    /// <summary>等目标程序把内容写进剪贴板的最长时间。</summary>
    private const int ClipboardWaitMs = 900;

    private static bool _running;
    private static CancellationTokenSource? _cts;

    /// <summary>是否正在处理一次划词（连续快速按热键时用它挡住叠加）。</summary>
    public static bool IsRunning => _running;

    /// <summary>最近一次的触发来源（热键 / 托盘菜单 / 设置页 / 自检）。</summary>
    public static string LastTriggerSource { get; private set; } = string.Empty;

    /// <summary>最近一次捕获到的文字。</summary>
    public static string LastCapturedText { get; private set; } = string.Empty;

    /// <summary>最近一次是否把剪贴板还原成功了。</summary>
    public static bool LastClipboardRestored { get; private set; }

    /// <summary>最近一次触发时剪贴板里原本是什么（用于验证「非文本内容没被破坏」）。</summary>
    public static ClipboardKind LastOriginalClipboardKind { get; private set; } = ClipboardKind.Unknown;

    /// <summary>最近一次触发是否真的检测到剪贴板被改写（= 有东西被复制）。</summary>
    public static bool LastClipboardChanged { get; private set; }

    /// <summary>跑一次全局划词。重复触发会被忽略并记日志。</summary>
    public static async Task TriggerAsync(string source)
    {
        if (_running)
        {
            AppServices.Log("全局划词：上一次还在处理（本次来源 " + source + "），忽略这次触发");
            return;
        }

        _running = true;
        LastTriggerSource = source;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        try
        {
            await RunAsync(source, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AppServices.Log("全局划词：被新的一次触发打断");
        }
        catch (Exception ex)
        {
            AppServices.Log("全局划词失败：" + ex);
            LookupPopupWindow.ShowOrUpdate(new LookupPopupContent
            {
                Headline = "划词失败",
                Badge = "错误",
                Body = ex.Message,
            });
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>关掉当前弹窗（自检与 Esc 处理用）。</summary>
    public static void ClosePopup() => LookupPopupWindow.CloseCurrent();

    private static async Task RunAsync(string source, CancellationToken token)
    {
        // 1) 先给剪贴板拍快照
        ClipboardSnapshot snapshot = await ClipboardGuard.CaptureAsync().ConfigureAwait(true);
        LastOriginalClipboardKind = snapshot.Kind;

        // 2) 记下序列号，再模拟 Ctrl+C
        uint sequenceBefore = NativeMethods.GetClipboardSequenceNumber();
        bool sent = InputSender.SendCtrlC();
        bool changed = sent && await WaitForClipboardChangeAsync(sequenceBefore, ClipboardWaitMs, token)
            .ConfigureAwait(true);
        LastClipboardChanged = changed;

        // 判定完立刻再取一次序列号用于日志：拖到后面取的话，还原剪贴板本身也会让它变大，容易误读
        uint sequenceAtCheck = NativeMethods.GetClipboardSequenceNumber();

        // 3) 读走被复制的内容（不改动剪贴板本身）
        string captured = string.Empty;
        if (changed)
        {
            captured = (await ClipboardGuard.ReadTextAsync().ConfigureAwait(true)).Trim();
        }
        LastCapturedText = captured;

        // 4) 立刻还原剪贴板，别污染用户的剪贴板
        bool restored = await ClipboardGuard.RestoreAsync(snapshot).ConfigureAwait(true);
        LastClipboardRestored = restored;

        string clipboardNote = DescribeClipboardStep(snapshot, changed, restored);
        AppServices.Log("全局划词（" + source + "）：剪贴板原内容=" + snapshot.Describe()
            + "，序列号 " + sequenceBefore + " → " + sequenceAtCheck
            + "（" + (changed ? "已改写" : "未改写") + "），捕获文字="
            + (captured.Length == 0 ? "（空）" : "\"" + Shorten(captured, 60) + "\"")
            + "，还原=" + (restored ? "成功" : "未完成"));

        if (token.IsCancellationRequested) return;

        // 5) 没抓到东西：如果弹窗正开着，就当作「再按一次收起」；否则提示未检测到选中文字
        if (captured.Length == 0)
        {
            if (LookupPopupWindow.ActivePopup != null)
            {
                LookupPopupWindow.CloseCurrent();
                AppServices.Log("全局划词：没有新的选中内容，弹窗已收起");
                return;
            }

            LookupPopupWindow.ShowOrUpdate(LookupPopupContent.NoSelection(clipboardNote));
            return;
        }

        // 6) 先弹一个「查询中」的窗，再把结果填进去——弹窗要秒开
        LookupPopupWindow.ShowOrUpdate(new LookupPopupContent
        {
            Captured = captured,
            Headline = Shorten(captured, 80),
            Badge = "查询中",
            Body = "正在查询…",
            IsLoading = true,
            Status = clipboardNote,
        });

        LookupPopupContent result = await BuildContentAsync(captured, clipboardNote, token).ConfigureAwait(true);
        if (token.IsCancellationRequested) return;

        LookupPopupWindow.ShowOrUpdate(result);
    }

    /// <summary>轮询剪贴板序列号，直到它变化或超时。</summary>
    private static async Task<bool> WaitForClipboardChangeAsync(uint sequenceBefore, int timeoutMs, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (NativeMethods.GetClipboardSequenceNumber() != sequenceBefore) return true;
            try
            {
                await Task.Delay(25, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return NativeMethods.GetClipboardSequenceNumber() != sequenceBefore;
            }
        }
        return NativeMethods.GetClipboardSequenceNumber() != sequenceBefore;
    }

    private static async Task<LookupPopupContent> BuildContentAsync(
        string captured, string clipboardNote, CancellationToken token)
    {
        return IsLatinWord(captured)
            ? await BuildWordAsync(captured, clipboardNote, token).ConfigureAwait(true)
            : await BuildTranslationAsync(captured, clipboardNote, token).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 单词：离线词库优先
    // ------------------------------------------------------------------

    private static async Task<LookupPopupContent> BuildWordAsync(
        string word, string clipboardNote, CancellationToken token)
    {
        try
        {
            var entry = await AppServices.LookupWordAsync(word, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return LookupPopupContent.Loading(word);

            if (entry.Ok)
            {
                return new LookupPopupContent
                {
                    Captured = word,
                    Headline = entry.Word.Length > 0 ? entry.Word : word,
                    Phonetic = entry.Phonetic ?? string.Empty,
                    Badge = entry.Source.Length > 0 ? entry.Source : "本地词库",
                    Body = LookupPopupContent.FormatSenses(entry),
                    Status = clipboardNote,
                    CanAddToWordbook = true,
                };
            }

            bool aiFallback = AppServices.Settings.LookupAiFallback;
            return new LookupPopupContent
            {
                Captured = word,
                Headline = word,
                Badge = aiFallback ? "未命中" : "本地词库",
                Body = (string.IsNullOrWhiteSpace(entry.Error) ? "没有查到这个词的释义。" : entry.Error!)
                       + Environment.NewLine + Environment.NewLine
                       + (aiFallback
                           ? "本地词库与 AI 都没给出释义，可以到「设置 → AI 服务」检查模型配置。"
                           : "想要 AI 兜底，请到「设置 → 划词查义」打开「AI 兜底」。"),
                Status = clipboardNote,
            };
        }
        catch (OperationCanceledException)
        {
            return LookupPopupContent.Loading(word);
        }
        catch (Exception ex)
        {
            return new LookupPopupContent
            {
                Captured = word,
                Headline = word,
                Badge = "查词失败",
                Body = ex.Message,
                Status = clipboardNote,
            };
        }
    }

    // ------------------------------------------------------------------
    // 整句 / 多词：AI 翻译
    // ------------------------------------------------------------------

    private static async Task<LookupPopupContent> BuildTranslationAsync(
        string text, string clipboardNote, CancellationToken token)
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
                Status = clipboardNote,
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
                Body = ex.Message + Environment.NewLine + Environment.NewLine
                       + "配好之后回到「设置 → 托盘与全局划词」，或用托盘菜单的「全局划词」再试一次。",
                Status = clipboardNote,
            };
        }
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static string DescribeClipboardStep(ClipboardSnapshot snapshot, bool changed, bool restored)
    {
        string head = changed ? "已复制选中文字，剪贴板原内容（" + snapshot.Describe() + "）" : "剪贴板未变化，原内容（" + snapshot.Describe() + "）";
        return head + (restored ? "已还原。" : "未能完整还原。");
    }

    /// <summary>短文本 + 全是拉丁字母/数字 → 当作单词走离线词库；其余（含中文、多词、整句）走 AI 翻译。</summary>
    private static bool IsLatinWord(string text)
    {
        if (text.Length == 0 || text.Length > 40) return false;

        bool hasLetter = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) return false;
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                hasLetter = true;
                continue;
            }
            if (c >= '0' && c <= '9') continue;
            if (c == '-' || c == '\'' || c == '.' || c == '_') continue;
            return false;
        }
        return hasLetter;
    }

    /// <summary>
    /// 目标语言：优先用设置里的目标语言；但如果选中的本身就是中文、而目标语言又是中文，
    /// 那就译成英文——否则「中译中」毫无意义。
    /// </summary>
    private static string ResolveTargetLanguage(string text)
    {
        string target = AppServices.Settings.TargetLang ?? "zh-CN";
        if (target.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && IsMostlyCjk(text)) return "en";
        return target;
    }

    private static bool IsMostlyCjk(string text)
    {
        int cjk = 0;
        int total = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            total++;
            if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
        }
        return total > 0 && cjk * 2 >= total;
    }

    private static string LanguageLabel(string? code)
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

    private static string Shorten(string text, int max)
    {
        string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
    }
}
