using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LineTrans.App.Controls;
using LineTrans.App.Services;
using LineTrans.App.ViewModels;
using LineTrans.Core;
using LineTrans.Core.Dictionary;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace LineTrans.App.Views;

/// <summary>
/// 翻译工作台：左边全部句子，中间原文，右边译文，中间可拖拽分割。
/// 单句 AI 翻译 / 复制 / 粘贴原文 / 收藏 / 标记完成，以及可随时停止的批量翻译。
/// </summary>
public sealed partial class TranslationPage : Page
{
    private TranslationDoc? _doc;
    private List<UnitRow> _rows = new List<UnitRow>();
    private int _index;

    private bool _ready;
    private bool _syncingSelection;
    private bool _suppressTargetChanged;

    private CancellationTokenSource? _cts;

    private long _promptTokens;
    private long _cachedTokens;
    private long _completionTokens;
    private double _cost;

    private WordLookupPanel? _lookupPanel;
    private Flyout? _lookupFlyout;

    public TranslationPage()
    {
        InitializeComponent();

        FontSize = AppServices.BodyFontSize;

        Splitter.Host = BodyGrid;
        Splitter.RatioChanged += OnSplitterRatioChanged;
        Splitter.Ratio = 0.5;
        ApplySplit(0.5);

        _ready = true;
        ShowEmptyState();
    }

    /// <summary>
    /// 当前文档。所有调用点都先判过空，这里统一收口，
    /// 避免编译器在方法调用之后丢失字段的非空推断而产生可空性告警。
    /// </summary>
    private TranslationDoc Doc => _doc!;

    // ------------------------------------------------------------------
    // 生命周期
    // ------------------------------------------------------------------

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.SettingsRepo.Changed += OnSettingsChanged;

        var doc = ResolveDocument(e.Parameter as string);
        if (doc == null)
        {
            ShowEmptyState();
        }
        else
        {
            LoadDocument(doc);
        }

        ApplyFontScale();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        AppServices.SettingsRepo.Changed -= OnSettingsChanged;

        _cts?.Cancel();

        // 离开页面前强制落盘：DocRepository 平时是防抖写盘，不刷会丢掉最后一次编辑。
        var doc = _doc;
        if (doc != null)
        {
            doc.LastIndex = _index;
            AppServices.Docs.Save(doc, immediate: true);
        }
        _ = AppServices.Docs.FlushAsync();
    }

    private static TranslationDoc? ResolveDocument(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = AppServices.Docs.Get(id!);
            if (byId != null) return byId;
        }

        var all = AppServices.Docs.Docs;
        return all.Count == 0 ? null : all[0];
    }

    private void OnSettingsChanged()
    {
        RefreshStatusBar();
        ApplyFontScale();
    }

    private void ApplyFontScale()
    {
        double size = AppServices.BodyFontSize + 1;
        SourceBox.FontSize = size;
        TargetBox.FontSize = size;
        FontSize = AppServices.BodyFontSize;
    }

    // ------------------------------------------------------------------
    // 载入 / 展示
    // ------------------------------------------------------------------

    private void LoadDocument(TranslationDoc doc)
    {
        _cts?.Cancel();
        _doc = doc;
        ExportButton.IsEnabled = true;

        _rows = new List<UnitRow>(doc.Units.Count);
        for (int i = 0; i < doc.Units.Count; i++)
        {
            _rows.Add(new UnitRow(doc.Units[i], i));
        }

        UnitList.ItemsSource = _rows;

        _ready = false;
        ModeBox.SelectedIndex = doc.UnitMode == UnitMode.LINE ? 0 : 1;
        _ready = true;

        if (_rows.Count == 0)
        {
            _index = 0;
            SourceBox.Text = doc.SourceText;
            _suppressTargetChanged = true;
            try { TargetBox.Text = string.Empty; } finally { _suppressTargetChanged = false; }
            RefreshHeader();
            ShowInfo(InfoBarSeverity.Warning, "文档没有可翻译的内容", "这篇文档切分后是空的，请到「文档」页检查正文。");
            return;
        }

        SelectIndex(doc.LastIndex, true);
    }

    private void ShowEmptyState()
    {
        _doc = null;
        _rows = new List<UnitRow>();
        UnitList.ItemsSource = null;

        DocTitle.Text = "翻译";
        DocMeta.Text = "还没有打开任何文档。请先到「文档」页新建或打开一篇文档。";
        IndexText.Text = "第 0 / 0 句";
        ProgressHost.Value = 0;
        SourceTitle.Text = "原文（双击任意单词可查词）";
        TargetTitle.Text = "译文（可直接编辑）";
        ExportButton.IsEnabled = false;

        _suppressTargetChanged = true;
        try
        {
            SourceBox.Text = string.Empty;
            TargetBox.Text = string.Empty;
        }
        finally
        {
            _suppressTargetChanged = false;
        }

        RefreshStatusBar();
    }

    private void SelectIndex(int index, bool scroll = true)
    {
        if (_doc == null || _rows.Count == 0) return;

        index = Math.Clamp(index, 0, _rows.Count - 1);
        _index = index;

        _syncingSelection = true;
        try
        {
            UnitList.SelectedIndex = index;
            if (scroll)
            {
                try { UnitList.ScrollIntoView(_rows[index]); } catch { /* 列表还没布局好，忽略 */ }
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        var unit = Doc.Units[index];

        SourceBox.Text = unit.Source ?? string.Empty;

        _suppressTargetChanged = true;
        try { TargetBox.Text = unit.Translation ?? string.Empty; } finally { _suppressTargetChanged = false; }

        SourceTitle.Text = "原文 · 第 " + (index + 1) + " 句（双击任意单词可查词）";
        TargetTitle.Text = "译文 · 第 " + (index + 1) + " 句（可直接编辑）";

        UpdateToggleButtons();
        RefreshHeader();

        Doc.LastIndex = index;
        AppServices.Docs.Save(Doc);
    }

    private void RefreshHeader()
    {
        if (_doc == null)
        {
            RefreshStatusBar();
            return;
        }

        bool line = Doc.UnitMode == UnitMode.LINE;
        DocTitle.Text = Doc.Name;
        DocMeta.Text = Doc.Folder + "　·　" + (line ? "逐行" : "逐句")
            + "　·　已完成 " + Doc.TranslatedCount + " / " + Doc.TotalCount
            + "　·　收藏 " + Doc.StarredCount + " 条";
        IndexText.Text = "第 " + (_rows.Count == 0 ? 0 : _index + 1) + " / " + _rows.Count + " 句";
        ProgressHost.Value = Math.Round(Doc.Progress * 100.0, 1);
        RefreshStatusBar();
    }

    private void RefreshStatusBar()
    {
        string modelName = (AppServices.Settings.ActiveModel?.Name ?? string.Empty).Trim();
        if (modelName.Length == 0) modelName = "未配置";

        var provider = AppServices.Settings.ActiveProvider;
        string providerLabel = provider == null ? "未配置" : ProviderTypeInfo.Label(provider.Type);

        ModelText.Text = "模型：" + modelName + "　·　" + providerLabel;
        long uncached = Math.Max(0, _promptTokens - _cachedTokens);
        UsageText.Text = "用量：未命中 " + uncached + " · 命中 " + _cachedTokens + " · 输出 " + _completionTokens + " token";
        CostText.Text = "估算费用：￥" + _cost.ToString("F6");
    }

    private void UpdateToggleButtons()
    {
        if (_doc == null || _rows.Count == 0) return;
        var unit = Doc.Units[_index];
        StarButton.Content = unit.Starred ? "取消收藏" : "收藏";
        DoneButton.Content = unit.Done ? "取消完成标记" : "标记完成";
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message, Button? action = null)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.ActionButton = action;
        MessageBar.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        TranslateButton.IsEnabled = !busy;
        BatchButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        PrevButton.IsEnabled = !busy;
        NextButton.IsEnabled = !busy;
        ModeBox.IsEnabled = !busy;
    }

    // ------------------------------------------------------------------
    // 导航
    // ------------------------------------------------------------------

    private void OnPrevClick(object sender, RoutedEventArgs e) => SelectIndex(_index - 1);

    private void OnNextClick(object sender, RoutedEventArgs e) => SelectIndex(_index + 1);

    private void OnJumpClick(object sender, RoutedEventArgs e)
    {
        double value = JumpBox.Value;
        if (double.IsNaN(value) || value < 1)
        {
            ShowInfo(InfoBarSeverity.Warning, "跳转失败", "请输入要跳到的句号（从 1 开始）。");
            return;
        }
        SelectIndex((int)Math.Round(value) - 1);
    }

    private void OnUnitSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || !_ready) return;
        if (UnitList.SelectedIndex < 0) return;
        SelectIndex(UnitList.SelectedIndex, false);
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _doc == null) return;

        var doc = Doc;
        var target = ModeBox.SelectedIndex == 1 ? UnitMode.SENTENCE : UnitMode.LINE;
        if (doc.UnitMode == target) return;

        int before = doc.TranslatedCount;
        if (!AppServices.Docs.ChangeMode(doc.Id, target))
        {
            _ready = false;
            try { ModeBox.SelectedIndex = doc.UnitMode == UnitMode.LINE ? 0 : 1; } finally { _ready = true; }
            return;
        }

        LoadDocument(doc);
        ShowInfo(
            InfoBarSeverity.Informational,
            "切分方式已切换为「" + (target == UnitMode.LINE ? "逐行" : "逐句") + "」",
            "按新方式重建了 " + doc.TotalCount + " 个单元；原文相同的条目已保留译文（切换前 " + before + " 条 → 现在 " + doc.TranslatedCount + " 条）。");
    }

    // ------------------------------------------------------------------
    // 划词查义
    //
    // 方案选型：TextBox（原文只读 / 译文可编辑）+ 双击取词。
    //   1) 只读 TextBox 天然支持选中与 Ctrl+C，正好满足「原文区可选中/复制」的要求；
    //   2) 双击时 WinUI 输入栈会直接给出该词的 SelectedText，
    //      等价于安卓端 TextLayoutResult.getWordBoundary 的效果，不用自己分词；
    //   3) 相比「把整句拆成一堆 Hyperlink 内联元素」，它不需要覆写链接样式、
    //      不会破坏复制粘贴，也避免每次翻页都重建上百个内联元素。
    //   局限：CJK 没有词边界，双击会选中一整串汉字，本地词库通常查不到（会走 AI 兜底或提示未收录）；
    //         也没有 hover 取词，必须先双击。
    // ------------------------------------------------------------------

    private void OnEditorDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!AppServices.Settings.WordLookupEnabled) return;
        if (sender is not TextBox box) return;

        var position = e.GetPosition(box);

        string raw = (box.SelectedText ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            // 双击后取词点已经是插入符位置，直接按词边界扩展
            raw = WordAt(box.Text, box.SelectionStart);
        }
        if (raw.Length == 0) return;

        string normalized = LocalDictionary.CleanWord(raw);
        string word = normalized.Length == 0 ? raw : normalized;

        e.Handled = true;
        _ = ShowLookupAsync(box, position, word);
    }

    private static string WordAt(string? text, int index)
    {
        if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length) return string.Empty;

        static bool IsWordChar(char c) =>
            char.IsLetterOrDigit(c) || c == '\'' || c == '-' || c == '_';

        if (!IsWordChar(text[index]))
        {
            int probe = index;
            while (probe < text.Length && !IsWordChar(text[probe])) probe++;
            if (probe >= text.Length) return string.Empty;
            index = probe;
        }

        int start = index;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        int end = index;
        while (end + 1 < text.Length && IsWordChar(text[end + 1])) end++;

        return text.Substring(start, end - start + 1);
    }

    private async Task ShowLookupAsync(FrameworkElement target, Point position, string word)
    {
        try
        {
            if (_lookupPanel == null)
            {
                _lookupPanel = new WordLookupPanel();
                _lookupFlyout = new Flyout
                {
                    Content = _lookupPanel,
                    Placement = FlyoutPlacementMode.Bottom,
                    ShouldConstrainToRootBounds = true,
                };
            }

            var flyout = _lookupFlyout;
            var panel = _lookupPanel;
            if (flyout == null || panel == null) return;

            if (flyout.IsOpen) flyout.Hide();
            flyout.ShowAt(target, new FlyoutShowOptions { Position = position });

            await panel.LookupAsync(word);
        }
        catch (Exception ex)
        {
            AppServices.Log("划词查义失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // 编辑
    // ------------------------------------------------------------------

    private void OnTargetTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTargetChanged || _doc == null || _rows.Count == 0) return;

        string text = TargetBox.Text ?? string.Empty;
        var unit = Doc.Units[_index];
        if (unit.Translation == text) return;

        unit.Translation = text;
        AppServices.Docs.Save(Doc);
        _rows[_index].Refresh();
        RefreshHeader();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        string text = TargetBox.Text ?? string.Empty;
        if (text.Trim().Length == 0) text = SourceBox.Text ?? string.Empty;

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            ShowInfo(InfoBarSeverity.Success, "已复制", "当前内容已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "复制失败", ex.Message);
        }
    }

    private void OnPasteSourceClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _rows.Count == 0) return;
        TargetBox.Text = SourceBox.Text ?? string.Empty;
        TargetBox.Focus(FocusState.Programmatic);
    }

    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _rows.Count == 0) return;

        var unit = Doc.Units[_index];
        unit.Starred = !unit.Starred;
        AppServices.Docs.Save(Doc);
        _rows[_index].Refresh();
        UpdateToggleButtons();
        RefreshHeader();
    }

    private void OnDoneClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _rows.Count == 0) return;

        var unit = Doc.Units[_index];
        unit.Done = !unit.Done;
        AppServices.Docs.Save(Doc);
        _rows[_index].Refresh();
        UpdateToggleButtons();
        RefreshHeader();
    }

    // ------------------------------------------------------------------
    // 翻译
    // ------------------------------------------------------------------

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _rows.Count == 0 || _cts != null) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);

        int index = _index;
        var doc = Doc;
        try
        {
            var result = await AppServices.Ai.TranslateAsync(doc, index, _cts.Token);

            var unit = doc.Units[index];
            unit.Translation = (result.Text ?? string.Empty).Trim();
            AppServices.Docs.Save(doc);
            _rows[index].Refresh();

            if (index == _index)
            {
                _suppressTargetChanged = true;
                try { TargetBox.Text = unit.Translation; } finally { _suppressTargetChanged = false; }
            }

            Accumulate(result);
            RefreshHeader();

            double cost = result.Cost > 0 ? result.Cost : AppServices.Ai.CostOf(result.PromptTokens, result.CompletionTokens);
            ShowInfo(
                InfoBarSeverity.Success,
                "第 " + (index + 1) + " 句翻译完成",
                "token：输入 " + result.PromptTokens + "（命中缓存 " + result.CachedTokens + "）、输出 "
                + result.CompletionTokens + "　·　本次估算费用 ￥" + cost.ToString("F6"));
        }
        catch (OperationCanceledException)
        {
            ShowInfo(InfoBarSeverity.Warning, "已停止", "本次翻译被取消。");
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "翻译失败", ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            await AppServices.Docs.FlushAsync();
        }
    }

    private async void OnBatchClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _rows.Count == 0 || _cts != null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var doc = Doc;
        SetBusy(true);

        int done = 0;
        bool stopped = false;
        bool failed = false;

        try
        {
            for (int i = _index; i < doc.Units.Count; i++)
            {
                if (token.IsCancellationRequested) { stopped = true; break; }

                var unit = doc.Units[i];
                if (unit.IsTranslated) continue;

                SelectIndex(i);

                try
                {
                    var result = await AppServices.Ai.TranslateAsync(doc, i, token);
                    unit.Translation = (result.Text ?? string.Empty).Trim();
                    AppServices.Docs.Save(doc);
                    _rows[i].Refresh();
                    Accumulate(result);
                    done++;

                    _suppressTargetChanged = true;
                    try { TargetBox.Text = unit.Translation; } finally { _suppressTargetChanged = false; }

                    RefreshHeader();
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                    break;
                }
                catch (Exception ex)
                {
                    failed = true;
                    ShowInfo(InfoBarSeverity.Error, "批量翻译已停止（第 " + (i + 1) + " 句出错）", ex.Message);
                    break;
                }
            }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            await AppServices.Docs.FlushAsync();
            RefreshHeader();

            if (!failed)
            {
                ShowInfo(
                    stopped ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                    stopped ? "批量翻译已停止" : "批量翻译完成",
                    "本次共翻译 " + done + " 句。");
            }
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ShowInfo(InfoBarSeverity.Warning, "正在停止…", "已发出停止信号，当前这一句结束后就会停下。");
    }

    private void Accumulate(AiResult result)
    {
        _promptTokens += result.PromptTokens;
        _cachedTokens += result.CachedTokens;
        _completionTokens += result.CompletionTokens;
        _cost += result.Cost > 0 ? result.Cost : AppServices.Ai.CostOf(result.PromptTokens, result.CompletionTokens);
        RefreshStatusBar();
    }

    // ------------------------------------------------------------------
    // 导出
    //
    // 六种格式的生成逻辑全在 Core 的 ExportManager 里（已自测），这里只负责
    // 「弹菜单 -> 选位置 -> 落盘 -> 给中文提示」。选位置走 FileSavePicker，
    // 打不开对话框时由 ExportService 自动退到数据目录，结果不会丢。
    // ------------------------------------------------------------------

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null)
        {
            ShowInfo(InfoBarSeverity.Warning, "还没有打开文档", "请先到「文档」页新建或打开一篇文档，再导出。");
            return;
        }

        ExportService.BuildMenu(Doc, ShowExportResultAsync).ShowAt(ExportButton);
    }

    private async Task ShowExportResultAsync(ExportOutcome? outcome)
    {
        if (outcome == null) return; // 用户在选择对话框里按了取消，不打扰

        if (!outcome.Ok)
        {
            ShowInfo(InfoBarSeverity.Error, "导出失败", outcome.Error ?? "未知错误");
            return;
        }

        string path = outcome.FilePath;
        var action = new Button { Content = "打开所在文件夹" };
        action.Click += (s, e) => AppServices.OpenDirectory(Path.GetDirectoryName(path) ?? path);

        string message = "格式：" + outcome.FormatLabel
            + "　·　" + outcome.Units + " 条　·　" + outcome.Bytes + " 字节"
            + "\n" + path;
        if (outcome.Warning is { Length: > 0 })
        {
            message = outcome.Warning + "\n" + message;
        }

        ShowInfo(
            outcome.Warning is { Length: > 0 } ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
            "导出成功",
            message,
            action);

        AppServices.Log("导出完成：" + path);
    }

    // ------------------------------------------------------------------
    // 分割条
    // ------------------------------------------------------------------

    private void OnSplitterRatioChanged(object? sender, double ratio) => ApplySplit(ratio);

    private void ApplySplit(double ratio)
    {
        double value = DragSplitter.Clamp(ratio);
        var columns = BodyGrid.ColumnDefinitions;
        if (columns.Count < 4) return;

        columns[1].Width = new GridLength(value, GridUnitType.Star);
        columns[3].Width = new GridLength(1.0 - value, GridUnitType.Star);
    }
}
