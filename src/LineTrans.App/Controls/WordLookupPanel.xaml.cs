using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using LineTrans.App.Services;
using LineTrans.Core.Dictionary;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.ApplicationModel.DataTransfer;

namespace LineTrans.App.Controls;

/// <summary>
/// 划词查义浮层的内容面板：单词 / 音标 / 来源徽章 / 中文释义（可滚动）/ 复制 / 加入我的词库。
///
/// 面板自己完成查询与两个动作，宿主页面只需要把它塞进一个 Flyout 并调用 <see cref="LookupAsync"/>。
/// </summary>
public sealed partial class WordLookupPanel : UserControl
{
    private CancellationTokenSource? _cts;
    private DictEntry? _entry;
    private string _word = string.Empty;

    public WordLookupPanel()
    {
        InitializeComponent();
    }

    /// <summary>当前展示的词。</summary>
    public string CurrentWord => _word;

    // —— 取证入口（--lookupprobe 要把浮层里真正显示出来的字读出来当证据）：只读，不参与业务逻辑 ——

    public string ProbeWord => WordText.Text;

    public string ProbePhonetic => PhoneticText.Text;

    public string ProbeMeaning => MeaningText.Text;

    public string ProbeSourceBadge => SourceText.Text;

    public string ProbeMatchLabel => MatchText.Text;

    public string ProbeStatus => StatusText.Text;

    /// <summary>查询并展示一个词。</summary>
    public async Task LookupAsync(string word)
    {
        _word = (word ?? string.Empty).Trim();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _entry = null;
        WordText.Text = _word;
        PhoneticText.Text = string.Empty;
        MatchText.Text = string.Empty;
        SourceText.Text = "查询中";
        SourceBadge.Visibility = Visibility.Visible;
        MeaningText.Text = "正在查询…";
        StatusText.Text = string.Empty;

        if (_word.Length == 0)
        {
            ApplyMiss("没有选中任何文字。");
            return;
        }

        try
        {
            var entry = await AppServices.LookupWordAsync(_word, token);
            if (token.IsCancellationRequested) return;
            Apply(entry);
        }
        catch (OperationCanceledException)
        {
            // 用户又划了下一个词，忽略
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) ApplyMiss("查询失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 整段划词：把选中的一段文字翻译出来。
    /// 单个拉丁词仍然走离线词库（那样才有音标与释义），其余交给已配置的模型；
    /// 「这是单词还是整句」与全局划词弹窗共用 <see cref="LookupText"/> 的同一套判定，不会两边打架。
    /// </summary>
    public async Task LookupSelectionAsync(string text)
    {
        _word = (text ?? string.Empty).Trim();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _entry = null;
        WordText.Text = LookupText.Shorten(_word, 120);
        PhoneticText.Text = string.Empty;
        MatchText.Text = string.Empty;
        SourceText.Text = "查询中";
        SourceBadge.Visibility = Visibility.Visible;
        MeaningText.Text = "正在翻译…";
        StatusText.Text = string.Empty;

        if (_word.Length == 0)
        {
            ApplyMiss("没有选中任何文字。");
            return;
        }

        if (LookupText.IsLatinWord(_word))
        {
            await LookupAsync(_word);
            return;
        }

        try
        {
            var content = await LookupText.TranslateAsync(
                _word,
                string.Empty,
                "配好之后回到「设置 → AI」填好接口地址与模型，再回到翻译页重新划一次。",
                token).ConfigureAwait(true);

            if (token.IsCancellationRequested) return;

            WordText.Text = LookupText.Shorten(_word, 120);
            MatchText.Text = content.Headline;
            SourceText.Text = content.Badge.Length > 0 ? content.Badge : "AI 翻译";
            MeaningText.Text = content.Body;
            StatusText.Text = content.Status;
        }
        catch (OperationCanceledException)
        {
            // 用户又划了下一段，忽略
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) ApplyMiss("翻译失败：" + ex.Message);
        }
    }

    private void Apply(DictEntry entry)
    {
        _entry = entry;

        if (!entry.Ok)
        {
            ApplyMiss(string.IsNullOrWhiteSpace(entry.Error) ? "没有查到这个词的释义。" : entry.Error!);
            return;
        }

        WordText.Text = entry.Word.Length > 0 ? entry.Word : _word;
        PhoneticText.Text = entry.Phonetic ?? string.Empty;
        SourceText.Text = entry.Source.Length > 0 ? entry.Source : "本地词库";
        MatchText.Text = entry.Match == LookupMatchKind.None
            ? string.Empty
            : LocalDictionary.ViaLabel(entry.Match);

        var lines = new List<string>();
        foreach (var sense in entry.Senses)
        {
            string pos = sense.PartOfSpeech ?? string.Empty;
            string def = sense.Definition ?? string.Empty;
            if (def.Length == 0) continue;
            lines.Add(pos.Length == 0 ? def : pos + ". " + def);
        }
        if (!string.IsNullOrWhiteSpace(entry.Translation)) lines.Add(entry.Translation!.Trim());

        MeaningText.Text = lines.Count == 0 ? "没有查到这个词的释义。" : string.Join("\n", lines);
        StatusText.Text = string.Empty;
    }

    private void ApplyMiss(string message)
    {
        _entry = null;
        PhoneticText.Text = string.Empty;
        MatchText.Text = string.Empty;
        SourceText.Text = AppServices.Settings.LookupAiFallback ? "未命中" : "本地词库";
        MeaningText.Text = message;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        string text = _entry?.Word is { Length: > 0 } w ? w : _word;
        if (text.Length == 0)
        {
            StatusText.Text = "还没有可复制的内容。";
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            StatusText.Text = "已复制：" + text;
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        string term = _entry?.Word is { Length: > 0 } w ? w : _word;
        if (term.Length == 0)
        {
            StatusText.Text = "还没有可收藏的词。";
            return;
        }

        string meaning = MeaningText.Text;
        if (meaning.Length == 0 || meaning.StartsWith("正在", StringComparison.Ordinal))
        {
            StatusText.Text = "还没有查到释义，先等查询完成。";
            return;
        }

        try
        {
            AppServices.Words.Upsert(term, meaning, _entry?.Phonetic ?? string.Empty);
            AppServices.Words.Flush();
            AppServices.Words.ApplyToLocalDictionary();
            StatusText.Text = "已加入我的词库：" + term;
        }
        catch (Exception ex)
        {
            StatusText.Text = "加入失败：" + ex.Message;
        }
    }
}
