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

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace LineTrans.App.Views;

/// <summary>
/// 翻译工作台：左边全部句子，其余区域按设置摆成【左右式】（左原文右译文）
/// 或【上下式】（上原文下译文），两种布局下分隔条都能拖。
/// 单句 AI 翻译 / 复制 / 粘贴原文 / 收藏 / 标记完成，以及可随时停止的批量翻译。
/// </summary>
public sealed partial class TranslationPage : Page
{
    /// <summary>句子列表的固定宽度（DIP）。</summary>
    private const double ListPaneWidth = 240;

    /// <summary>
    /// 左右式在窄于这个宽度时给出降级提示（DIP）。
    /// 取 820：两侧各留出约 285 DIP 的编辑区，再窄就真的挤了；
    /// 最小窗口 900 DIP（导航栏收成图标条）时工作区约 803 DIP，正好落进提示区间。
    /// </summary>
    private const double NarrowThreshold = 820;

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
    private EditorWordLookup? _sourceLookup;
    private EditorWordLookup? _targetLookup;

    /// <summary>鼠标离开编辑框之后延迟收起浮层（给用户时间把鼠标移进浮层点「复制」）。</summary>
    private DispatcherQueueTimer? _lookupCloseTimer;

    /// <summary>上一条计时器的回调（退出时要能摘掉，所以不能写成匿名委托）。</summary>
    private Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object>? _lookupCloseTick;

    /// <summary>划词查义那套交互是否已经解绑（退出流程问一次，避免重复摘）。</summary>
    private bool _lookupReleased;

    /// <summary>两个编辑框是否已经从视觉树上摘下来过。</summary>
    private bool _editorsUnloaded;

    /// <summary>鼠标当前是否停在浮层上（停着就不收）。</summary>
    private bool _pointerOverLookup;

    /// <summary>浮层里当前显示的词 / 文本，用来避免同一个词反复 Hide + Show。</summary>
    private string _shownLookupKey = string.Empty;

    /// <summary>划词一次最多送多少字给模型（避免误选整篇文档）。</summary>
    private const int MaxSelectionChars = 800;

    /// <summary>当前布局：true = 上下式。</summary>
    private bool _topBottom;

    /// <summary>第一栏（原文）占比，两种布局共用。</summary>
    private double _ratio = 0.5;

    public TranslationPage()
    {
        InitializeComponent();

        FontSize = AppServices.BodyFontSize;

        Splitter.Host = WorkGrid;
        Splitter.RatioChanged += OnSplitterRatioChanged;
        Splitter.Ratio = _ratio;

        WorkGrid.SizeChanged += OnWorkGridSizeChanged;

        // 先按设置把工作区装好，再放空状态文案
        ApplyLayoutFromSettings(preserveScroll: false, updateCombo: true);

        SetupWordLookup();

        _ready = true;
        ShowEmptyState();
    }

    /// <summary>
    /// 当前文档。所有调用点都先判过空，这里统一收口，
    /// 避免编译器在方法调用之后丢失字段的非空推断而产生可空性告警。
    /// </summary>
    private TranslationDoc Doc => _doc!;

    /// <summary>当前是否上下式布局（自检 / 截图取证用）。</summary>
    public bool IsTopBottomLayout => _topBottom;

    /// <summary>当前第一栏占比（自检 / 截图取证用）。</summary>
    public double SplitRatio => _ratio;

    // —— 取证入口：x:Name 生成的字段在 WinUI 3 里是 private，
    //    --uiprobe 需要量四个区域的位置、读编辑框内容、看窄窗口提示条，
    //    这里开一组只读出口，不参与任何业务逻辑。 ——
    public FrameworkElement ProbeSourcePane => SourcePane;

    public FrameworkElement ProbeTargetPane => TargetPane;

    public FrameworkElement ProbeListPane => ListPane;

    public FrameworkElement ProbeWorkGrid => WorkGrid;

    public DragSplitter ProbeSplitter => Splitter;

    public InfoBar ProbeNarrowBar => NarrowBar;

    public RichEditBox ProbeTargetBox => TargetBox;

    public Button ProbeBatchButton => BatchButton;

    // —— 划词查义取证入口（--lookupprobe）—— //

    public RichEditBox ProbeSourceBox => SourceBox;

    /// <summary>原文框里的纯文本（RichEditBox 没有 .Text，必须走 ITextDocument）。</summary>
    public string ProbeSourceText => EditorWordLookup.DocumentText(SourceBox);

    /// <summary>译文框里的纯文本。</summary>
    public string ProbeTargetText => EditorWordLookup.DocumentText(TargetBox);

    /// <summary>取证用：把译文框整段替换成指定文本。</summary>
    public void ProbeSetTargetText(string text)
    {
        _suppressTargetChanged = true;
        try { EditorWordLookup.SetDocumentText(TargetBox, text); }
        finally { _suppressTargetChanged = false; }
    }

    /// <summary>浮层当前是否打开。</summary>
    public bool ProbeLookupOpen => LookupPopup.IsOpen;

    /// <summary>取证用：强制收起浮层（等价于「移开鼠标一会儿」）。</summary>
    public void ProbeHideLookup() => HideLookup();

    /// <summary>取证用：浮层累计被真正显示过多少次（同一个词重复触发不会重复计数）。</summary>
    public int ProbeLookupShowCount { get; private set; }

    /// <summary>取证用：浮层当前的位置与尺寸（证明它不是被摆在屏幕外或者尺寸为 0）。</summary>
    public string ProbeLookupPlacement =>
        "打开=" + LookupPopup.IsOpen
        + " 偏移=(" + LookupPopup.HorizontalOffset.ToString("0.#") + "," + LookupPopup.VerticalOffset.ToString("0.#") + ")"
        + " 面板尺寸=" + (_lookupPanel == null
            ? "（还没建）"
            : _lookupPanel.ActualWidth.ToString("0.#") + "x" + _lookupPanel.ActualHeight.ToString("0.#"));

    /// <summary>浮层面板实例（没建过就是 null）。</summary>
    public WordLookupPanel? ProbeLookupPanel => _lookupPanel;

    /// <summary>最近一次取词的原始事实（没触发过就是空串）。</summary>
    public string ProbeLastTriggerReport { get; private set; } = string.Empty;

    /// <summary>取证用：原文框上三种交互的原始事件过程。</summary>
    public string ProbeSourceLookupTrace => DescribeLookupTrace(_sourceLookup);

    /// <summary>取证用：译文框上三种交互的原始事件过程。</summary>
    public string ProbeTargetLookupTrace => DescribeLookupTrace(_targetLookup);

    private static string DescribeLookupTrace(EditorWordLookup? lookup)
    {
        if (lookup == null) return "（还没装上）";
        return "事件计数：移动=" + lookup.MovedCount + " 按下=" + lookup.PressedCount
            + " 抬起=" + lookup.ReleasedCount + " 离开=" + lookup.ExitedCount
            + " 悬浮触发=" + lookup.HoverFiredCount + " 取词触发=" + lookup.WordFiredCount
            + " 划词触发=" + lookup.SelectionFiredCount
            + "；过程：" + string.Join(" ｜ ", lookup.Trace);
    }

    /// <summary>
    /// 取证入口：页面是否已经把文档装进来了。
    /// Frame.Navigate 是异步的 —— 页面对象会先出现，OnNavigatedTo 之后 _doc / _rows 才有值，
    /// 这中间往译文框里写内容是会被丢掉的（OnTargetTextChanged 会提前返回），取证必须先等这个为 true。
    /// </summary>
    public bool ProbeDocLoaded => _doc != null && _rows.Count > 0;

    /// <summary>取证入口：页面当前承载的文档 id（空串表示还没载入）。</summary>
    public string ProbeDocId => _doc?.Id ?? string.Empty;

    /// <summary>取证入口：页面当前的下标。</summary>
    public int ProbeIndex => _index;

    /// <summary>取证入口：等价于点一次「下一句」（走的是和真实点击完全相同的处理函数）。</summary>
    public void ProbeRaiseNext() => OnNextClick(NextButton, new RoutedEventArgs());

    /// <summary>取证入口：等价于点一次「收藏」。</summary>
    public void ProbeRaiseStar() => OnStarClick(StarButton, new RoutedEventArgs());

    /// <summary>取证入口：等价于点一次「标记完成」。</summary>
    public void ProbeRaiseDone() => OnDoneClick(DoneButton, new RoutedEventArgs());

    // ------------------------------------------------------------------
    // 生命周期
    // ------------------------------------------------------------------

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.SettingsRepo.Changed += OnSettingsChanged;
        UiSettingsStore.Changed += OnUiSettingsChanged;

        // 上一轮 OnNavigatedFrom / 退出流程把交互摘掉过的话，这里重新装一套
        // （Frame 默认不缓存页面，正常每次都是新实例；这条只是防御）。
        SetupWordLookup();

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
        ApplyLayoutFromSettings(preserveScroll: false, updateCombo: true);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        AppServices.SettingsRepo.Changed -= OnSettingsChanged;
        UiSettingsStore.Changed -= OnUiSettingsChanged;

        _cts?.Cancel();
        HideLookup();
        // 换页 = 这个页面连同两个 RichEditBox 一起被扔掉，交互资源要跟着摘干净，
        // 免得原生文本控件在页面拆掉之后还回调进来。
        ReleaseWordLookup();

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
        // 三个开关随时可能被改；关掉之后浮层不该还挂在那儿
        HideLookup();
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
    // 布局：左右式 / 上下式
    //
    // 实现方式：只重建 WorkGrid 的行列定义 + 给四个已有元素重新贴 Grid.Row / Grid.Column，
    // 控件本身（两个 TextBox、句子列表）自始至终是同一批实例，
    // 所以输入内容、编辑状态一定不会丢；滚动位置在切换前后手动存取一次。
    // ------------------------------------------------------------------

    private void OnUiSettingsChanged()
    {
        ApplyLayoutFromSettings(preserveScroll: true, updateCombo: true);
    }

    /// <summary>按设置里的翻译页布局装配工作区。</summary>
    private void ApplyLayoutFromSettings(bool preserveScroll, bool updateCombo)
    {
        bool topBottom = AppSettings.NormalizeTranslationLayout(UiSettingsStore.Current.TranslationLayout)
            == AppSettings.LayoutTopBottom;
        ApplyLayout(topBottom, preserveScroll, updateCombo);
    }

    /// <summary>切换布局（保留当前输入与滚动位置）。</summary>
    public void ApplyLayout(bool topBottom, bool preserveScroll = true, bool updateCombo = false)
    {
        if (updateCombo)
        {
            int index = topBottom ? 1 : 0;
            if (LayoutBox.SelectedIndex != index)
            {
                _ready = false;
                try { LayoutBox.SelectedIndex = index; } finally { _ready = true; }
            }
        }

        // 记下切换前的滚动位置（切换后要还原，用户的阅读位置不能丢）
        double sourceOffset = 0;
        double targetOffset = 0;
        double listOffset = 0;
        if (preserveScroll)
        {
            sourceOffset = CurrentOffset(SourceBox);
            targetOffset = CurrentOffset(TargetBox);
            listOffset = CurrentOffset(UnitList);
        }

        _topBottom = topBottom;
        Splitter.Direction = topBottom ? SplitDirection.Rows : SplitDirection.Columns;

        var columns = WorkGrid.ColumnDefinitions;
        var rows = WorkGrid.RowDefinitions;
        columns.Clear();
        rows.Clear();

        if (!topBottom)
        {
            columns.Add(new ColumnDefinition { Width = new GridLength(ListPaneWidth) });
            columns.Add(new ColumnDefinition { Width = new GridLength(_ratio, GridUnitType.Star) });
            columns.Add(new ColumnDefinition { Width = new GridLength(DragSplitter.Thickness) });
            columns.Add(new ColumnDefinition { Width = new GridLength(1.0 - _ratio, GridUnitType.Star) });
            rows.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Place(ListPane, row: 0, column: 0);
            Place(SourcePane, row: 0, column: 1);
            Place(SplitterHost, row: 0, column: 2);
            Place(TargetPane, row: 0, column: 3);
        }
        else
        {
            columns.Add(new ColumnDefinition { Width = new GridLength(ListPaneWidth) });
            columns.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rows.Add(new RowDefinition { Height = new GridLength(_ratio, GridUnitType.Star) });
            rows.Add(new RowDefinition { Height = new GridLength(DragSplitter.Thickness) });
            rows.Add(new RowDefinition { Height = new GridLength(1.0 - _ratio, GridUnitType.Star) });

            Place(ListPane, row: 0, column: 0, rowSpan: 3);
            Place(SourcePane, row: 0, column: 1);
            Place(SplitterHost, row: 1, column: 1);
            Place(TargetPane, row: 2, column: 1);
        }

        // 分隔条上的那条细线要跟着转 90 度
        if (topBottom)
        {
            SplitterLine.Width = double.NaN;
            SplitterLine.Height = 1;
            SplitterLine.HorizontalAlignment = HorizontalAlignment.Stretch;
            SplitterLine.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            SplitterLine.Width = 1;
            SplitterLine.Height = double.NaN;
            SplitterLine.HorizontalAlignment = HorizontalAlignment.Center;
            SplitterLine.VerticalAlignment = VerticalAlignment.Stretch;
        }

        UpdateNarrowHint();

        if (preserveScroll)
        {
            UpdateLayout();
            // 等这一轮布局跑完（新的尺寸已经生效）再恢复滚动位置
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                RestoreOffset(SourceBox, sourceOffset);
                RestoreOffset(TargetBox, targetOffset);
                RestoreOffset(UnitList, listOffset);
            });
        }
    }

    private static void Place(FrameworkElement element, int row, int column, int rowSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetRowSpan(element, rowSpan);
        Grid.SetColumnSpan(element, 1);
    }

    /// <summary>取控件内部 ScrollViewer 的纵向偏移（拿不到就返回 0）。</summary>
    private static double CurrentOffset(DependencyObject root)
    {
        var viewer = FindScrollViewer(root);
        return viewer?.VerticalOffset ?? 0;
    }

    /// <summary>把控件内部 ScrollViewer 恢复到指定纵向偏移。</summary>
    private static void RestoreOffset(DependencyObject root, double offset)
    {
        if (offset <= 0) return;
        var viewer = FindScrollViewer(root);
        if (viewer == null) return;
        try { viewer.ChangeView(null, offset, null, disableAnimation: true); }
        catch { /* 布局还没就绪，忽略 */ }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private void OnWorkGridSizeChanged(object sender, SizeChangedEventArgs e) => UpdateNarrowHint();

    /// <summary>
    /// 窄窗口降级：左右式在窄窗口下会把两栏挤成一团，这里给一条可一键切换的提示。
    /// 不擅自改用户的设置。
    /// </summary>
    private void UpdateNarrowHint()
    {
        bool narrow = !_topBottom && WorkGrid.ActualWidth > 0 && WorkGrid.ActualWidth < NarrowThreshold;
        NarrowBar.IsOpen = narrow;
    }

    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;

        string layout = LayoutBox.SelectedIndex == 1 ? AppSettings.LayoutTopBottom : AppSettings.LayoutLeftRight;
        var current = UiSettingsStore.Current;

        if (string.Equals(current.TranslationLayout, layout, StringComparison.Ordinal)) return;

        // 写进设置（同时镜像到 AppSettings），Changed 回调里会应用新布局
        UiSettingsStore.Update(s => s.TranslationLayout = layout);
    }

    private void OnSwitchToTopBottomClick(object sender, RoutedEventArgs e)
    {
        UiSettingsStore.Update(s => s.TranslationLayout = AppSettings.LayoutTopBottom);
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
            EditorWordLookup.SetDocumentText(SourceBox, doc.SourceText);
            _suppressTargetChanged = true;
            try { EditorWordLookup.SetDocumentText(TargetBox, string.Empty); } finally { _suppressTargetChanged = false; }
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

        HideLookup();
        DocTitle.Text = "翻译";
        DocMeta.Text = "还没有打开任何文档。请先到「文档」页新建或打开一篇文档。";
        IndexText.Text = "第 0 / 0 句";
        ProgressHost.Value = 0;
        SourceTitle.Text = "原文（悬浮或单击单词查词）";
        TargetTitle.Text = "译文（可直接编辑）";
        ExportButton.IsEnabled = false;

        _suppressTargetChanged = true;
        try
        {
            EditorWordLookup.SetDocumentText(SourceBox, string.Empty);
            EditorWordLookup.SetDocumentText(TargetBox, string.Empty);
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
        HideLookup();

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

        EditorWordLookup.SetDocumentText(SourceBox, unit.Source ?? string.Empty);

        _suppressTargetChanged = true;
        try { EditorWordLookup.SetDocumentText(TargetBox, unit.Translation ?? string.Empty); } finally { _suppressTargetChanged = false; }

        SourceTitle.Text = "原文 · 第 " + (index + 1) + " 句（悬浮或单击单词查词）";
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

        // 图标用 Segoe Fluent Icons 的收藏 / 实心收藏字形
        StarText.Text = unit.Starred ? "已收藏" : "收藏";
        StarIcon.Glyph = unit.Starred ? "\uE735" : "\uE734";
        DoneText.Text = unit.Done ? "已完成" : "标记完成";
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
        LayoutBox.IsEnabled = !busy;
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
    // 划词查义：悬浮 / 单击 / 划词 三种方式并存，各自有开关
    //
    // 【取词方案选型】两个编辑区都换成 RichEditBox，用 ITextDocument API 取词。
    //   1) 悬浮与单击都要「按坐标取到字符」，而 WinUI3 的 TextBox 没有
    //      GetCharacterIndexFromPoint（那是 UWP 的），只有 RichEditBox 有
    //      ITextDocument.GetRangeFromPoint(Point, PointOptions.ClientCoordinates)；
    //   2) 反向的「字符 → 控件坐标」用 ITextRange.GetPoint(...)，正好当浮层锚点，
    //      不用自己估字号 / 行高 / DPI；
    //   3) 相比「把整句拆成一堆 Hyperlink 内联元素」，它不破坏复制、粘贴与编辑，
    //      也不用每次翻页重建上百个内联元素。
    // 代价：RichEditBox 没有 .Text / .SelectedText，读写文本得走 Document.GetText /
    //      SetText（见 EditorWordLookup.DocumentText / SetDocumentText）；
    //      它默认还会把 RTF 一起塞进剪贴板，已在样式里固定成 PlainText。
    //
    // 【根因记录】所有交互处理器都由 EditorWordLookup 用
    //   AddHandler(..., handledEventsToo: true) 注册。
    //   修复前这里写的是 <TextBox ... DoubleTapped="OnEditorDoubleTapped" />，
    //   --lookupprobe 实测（2026-10-01，见 lookupprobe-report.txt）：
    //     · 同一个控件上用 handledEventsToo: true 挂的计数器数到了 DoubleTapped=1、
    //       PointerPressed=2，说明事件确实发出来了、也确实到了控件；
    //     · 但 XAML 上挂的处理函数一次都没进（它的取证字段始终是空串），
    //       而同一个页面实例的另一个取证字段能正常更新（绕开输入栈直接调链路那次是 ok）。
    //   结论：DoubleTapped 在冒泡到控件自己的处理器之前就被内部输入栈标成了 Handled，
    //   而 XAML 生成的 AddHandler 等价于 handledEventsToo: false，收到已处理事件时会被跳过。
    //   这就是「划词查义完全不能用」的根因（不是词库、不是浮层、不是坐标注入）。
    // ------------------------------------------------------------------

    /// <summary>
    /// 装三种取词交互。三个开关每次触发时现读设置，所以在「设置」页改完立刻生效，
    /// 不需要重建页面。
    /// </summary>
    private void SetupWordLookup()
    {
        if (_sourceLookup != null || _targetLookup != null) return;

        _sourceLookup = AttachLookup(SourceBox);
        _targetLookup = AttachLookup(TargetBox);

        _lookupCloseTimer = DispatcherQueue.CreateTimer();
        _lookupCloseTimer.Interval = TimeSpan.FromMilliseconds(400);
        _lookupCloseTimer.IsRepeating = false;
        _lookupCloseTick = OnLookupCloseTick;
        _lookupCloseTimer.Tick += _lookupCloseTick;

        _lookupReleased = false;
    }

    private void OnLookupCloseTick(DispatcherQueueTimer sender, object args)
    {
        _lookupCloseTimer?.Stop();
        if (!_pointerOverLookup) HideLookup();
    }

    /// <summary>
    /// 把划词查义这一整套交互从窗口上摘干净：停掉两个延时器、解绑两个 RichEditBox 上的
    /// AddHandler 回调、收起 Popup 并卸掉它里面的自建面板。
    ///
    /// <para><b>为什么退出前必须做</b>：RichEditBox 的实体是原生控件（WinUIEdit.dll），
    /// 窗口销毁过程中它还会回调进 XAML；回调进来时页面若还挂着处理器、Popup 里还夹着
    /// 一个代码创建的面板，就会摸到正在拆的对象 —— 实测退出期在
    /// Microsoft.UI.Xaml.dll 里读空指针（0xC0000005）。</para>
    ///
    /// <para>可重复调用；解绑之后 <see cref="OnNavigatedTo"/> 会重新装一套，
    /// 所以「退出」与「换页」两种调用场合都安全。</para>
    /// </summary>
    public void ReleaseWordLookup(bool unloadEditors = false)
    {
        if (!_lookupReleased)
        {
            ReleaseLookupCore();
        }

        if (!unloadEditors || _editorsUnloaded) return;
        _editorsUnloaded = true;
        UnloadEditor(SourceBox);
        UnloadEditor(TargetBox);
    }

    private void ReleaseLookupCore()
    {
        _lookupReleased = true;

        try
        {
            if (_lookupCloseTimer != null)
            {
                _lookupCloseTimer.Stop();
                if (_lookupCloseTick != null) _lookupCloseTimer.Tick -= _lookupCloseTick;
                _lookupCloseTimer = null;
            }
            _lookupCloseTick = null;

            _sourceLookup?.Detach();
            _targetLookup?.Detach();
            _sourceLookup = null;
            _targetLookup = null;

            if (_lookupPanel != null)
            {
                _lookupPanel.PointerEntered -= OnPanelPointerEntered;
                _lookupPanel.PointerExited -= OnPanelPointerExited;
                _lookupPanel = null;
            }

            if (LookupPopup.IsOpen) LookupPopup.IsOpen = false;
            LookupPopup.Child = null;
            LookupPopup.HorizontalOffset = 0;
            LookupPopup.VerticalOffset = 0;

            _shownLookupKey = string.Empty;
            _pointerOverLookup = false;
        }
        catch (Exception ex)
        {
            AppServices.Log("释放划词查义资源失败：" + ex.Message);
        }
    }

    private EditorWordLookup AttachLookup(RichEditBox box)
    {
        var lookup = new EditorWordLookup(box)
        {
            WindowHandleProvider = () => MainWindow.Instance?.Handle ?? IntPtr.Zero,
            HoverEnabled = () => LookupAllowed() && AppServices.Settings.LookupHoverEnabled,
            ClickEnabled = () => LookupAllowed() && AppServices.Settings.LookupClickEnabled,
            SelectionEnabled = () => LookupAllowed() && AppServices.Settings.LookupSelectionEnabled,
        };

        lookup.WordRequested += (word, anchor, trigger) => OnWordRequested(box, word, anchor, trigger);
        lookup.SelectionRequested += (text, anchor) => OnSelectionRequested(box, text, anchor);
        lookup.PointerLeft += OnEditorPointerLeft;
        lookup.PointerEntered += OnEditorPointerEntered;
        return lookup;
    }

    /// <summary>总开关（「设置 → 划词查义」里第一个开关）。</summary>
    private static bool LookupAllowed() => AppServices.Settings.WordLookupEnabled;

    private void OnWordRequested(RichEditBox box, string word, Point anchor, WordLookupTrigger trigger)
    {
        if (!LookupAllowed()) return;
        if (trigger == WordLookupTrigger.Hover && !AppServices.Settings.LookupHoverEnabled) return;
        if (trigger == WordLookupTrigger.Click && !AppServices.Settings.LookupClickEnabled) return;

        string normalized = LocalDictionary.CleanWord(word);
        string query = normalized.Length == 0 ? word : normalized;
        if (query.Length == 0) return;

        ProbeLastTriggerReport = trigger + "：取到 \"" + word + "\" → 查 \"" + query + "\""
            + "；锚点=(" + anchor.X.ToString("0.#") + "," + anchor.Y.ToString("0.#") + ")";
        _ = ShowWordLookupAsync(box, query, anchor, trigger + "：" + query);
    }

    private void OnSelectionRequested(RichEditBox box, string text, Point anchor)
    {
        if (!LookupAllowed() || !AppServices.Settings.LookupSelectionEnabled) return;
        if (text.Length == 0) return;

        if (text.Length > MaxSelectionChars)
        {
            ProbeLastTriggerReport = "划词：选中 " + text.Length + " 字，超过上限 " + MaxSelectionChars + "，已忽略";
            ShowInfo(InfoBarSeverity.Warning, "选中的内容太长了",
                "划词翻译一次最多处理 " + MaxSelectionChars + " 个字，请少选一点再试。");
            return;
        }

        ProbeLastTriggerReport = "划词：选中 " + text.Length + " 字 → \"" + LookupText.Shorten(text, 60) + "\""
            + "；锚点=(" + anchor.X.ToString("0.#") + "," + anchor.Y.ToString("0.#") + ")";
        _ = ShowSelectionLookupAsync(box, text, anchor);
    }

    private void OnEditorPointerLeft()
    {
        if (!LookupAllowed()) return;
        _lookupCloseTimer?.Stop();
        _lookupCloseTimer?.Start();
    }

    /// <summary>指针又回到编辑框里：取消「准备收起浮层」的计时。</summary>
    private void OnEditorPointerEntered()
    {
        _pointerOverLookup = false;
        _lookupCloseTimer?.Stop();
    }

    /// <summary>
    /// 显示浮层。
    /// 锚点往下挪一行：浮层如果正好盖住光标，底下的编辑框就收不到指针事件了（表现是「一闪而过」）。
    /// 已经开着的时候只更新内容、不重复开关，避免 Popup 反复开关造成闪烁与状态错乱。
    /// </summary>
    private void ShowLookupAt(RichEditBox box, Point anchor)
    {
        var panel = _lookupPanel;
        if (panel == null) return;

        if (!ReferenceEquals(LookupPopup.Child, panel)) LookupPopup.Child = panel;

        Point point = new Point(anchor.X + 2, anchor.Y + 20);
        try
        {
            if (Content is UIElement root) point = box.TransformToVisual(root).TransformPoint(point);
        }
        catch (Exception ex)
        {
            AppServices.Log("换算浮层位置失败：" + ex.Message);
        }

        LookupPopup.HorizontalOffset = Math.Max(0, point.X);
        LookupPopup.VerticalOffset = Math.Max(0, point.Y);
        LookupPopup.IsOpen = true;
    }

    private async Task ShowWordLookupAsync(RichEditBox box, string word, Point anchor, string key)
    {
        try
        {
            var panel = EnsureLookupPanel();
            if (panel == null) return;

            // 之前那次「准备收起」的计时必须作废，否则它会在浮层刚弹出来的 0.4 秒里把它收掉
            _lookupCloseTimer?.Stop();

            // 同一个词已经在浮层里了就别再动它
            if (LookupPopup.IsOpen && string.Equals(_shownLookupKey, key, StringComparison.Ordinal)) return;

            ShowLookupAt(box, anchor);
            _shownLookupKey = key;
            ProbeLookupShowCount++;

            await panel.LookupAsync(word);
        }
        catch (Exception ex)
        {
            AppServices.Log("查词失败：" + ex.Message);
        }
    }

    private async Task ShowSelectionLookupAsync(RichEditBox box, string text, Point anchor)
    {
        try
        {
            var panel = EnsureLookupPanel();
            if (panel == null) return;

            _lookupCloseTimer?.Stop();

            ShowLookupAt(box, anchor);
            _shownLookupKey = "selection:" + text;
            ProbeLookupShowCount++;

            await panel.LookupSelectionAsync(text);
        }
        catch (Exception ex)
        {
            AppServices.Log("划词翻译失败：" + ex.Message);
        }
    }

    private WordLookupPanel? EnsureLookupPanel()
    {
        if (_lookupPanel == null)
        {
            _lookupPanel = new WordLookupPanel();
            // 鼠标移进浮层就别收（否则用户永远点不到「复制 / 加入我的词库」）
            _lookupPanel.PointerEntered += OnPanelPointerEntered;
            _lookupPanel.PointerExited += OnPanelPointerExited;
        }

        return _lookupPanel;
    }

    /// <summary>
    /// 把一个 RichEditBox 从视觉树上摘下来（退出前用）。
    ///
    /// <para>RichEditBox 的原生实体（WinUIEdit.dll 里的控件 + 它自己的子窗口 + TSF 输入上下文）
    /// 是在【离开视觉树】时销毁的。让它跟着顶层窗口一起被拆的话，销毁过程中它还会回调进 XAML，
    /// 而那一刻 XAML 核心本身已经在拆了 —— 回调踩到已经释放的对象，实测退出期在
    /// Microsoft.UI.Xaml.dll 里读空指针（0xC0000005）。先卸载，等于让它在一个健康的核心里拆完。</para>
    /// </summary>
    private static void UnloadEditor(RichEditBox box)
    {
        if (box == null) return;

        try
        {
            switch (box.Parent)
            {
                case Panel panel:
                    panel.Children.Remove(box);
                    break;
                case Border border:
                    border.Child = null;
                    break;
                case ContentControl content:
                    content.Content = null;
                    break;
                default:
                    AppServices.Log("卸载编辑框：父元素是 "
                        + (box.Parent?.GetType().Name ?? "null") + "，没有可用的摘除方式");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("卸载编辑框失败：" + ex.Message);
        }
    }

    private void OnPanelPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverLookup = true;
        _lookupCloseTimer?.Stop();
    }

    private void OnPanelPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverLookup = false;
        _lookupCloseTimer?.Stop();
        _lookupCloseTimer?.Start();
    }

    /// <summary>收起浮层并复位（换句、切页、改设置、滚动、鼠标移开时都要收）。</summary>
    private void HideLookup()
    {
        try
        {
            _lookupCloseTimer?.Stop();
            _sourceLookup?.CancelHover();
            _targetLookup?.CancelHover();
            if (LookupPopup.IsOpen) LookupPopup.IsOpen = false;
            _shownLookupKey = string.Empty;
        }
        catch (Exception ex)
        {
            AppServices.Log("收起查词浮层失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // 编辑
    // ------------------------------------------------------------------

    // RichEditBox 的 TextChanged 是 RoutedEventHandler；TextBox 那个才是 TextChangedEventHandler
    private void OnTargetTextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressTargetChanged || _doc == null || _rows.Count == 0) return;

        string text = EditorWordLookup.DocumentText(TargetBox);
        var unit = Doc.Units[_index];
        if (unit.Translation == text) return;

        unit.Translation = text;
        AppServices.Docs.Save(Doc);
        _rows[_index].Refresh();
        RefreshHeader();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        string text = EditorWordLookup.DocumentText(TargetBox);
        if (text.Trim().Length == 0) text = EditorWordLookup.DocumentText(SourceBox);

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
        EditorWordLookup.SetDocumentText(TargetBox, EditorWordLookup.DocumentText(SourceBox));
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
                try { EditorWordLookup.SetDocumentText(TargetBox, unit.Translation); } finally { _suppressTargetChanged = false; }
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
                    try { EditorWordLookup.SetDocumentText(TargetBox, unit.Translation); } finally { _suppressTargetChanged = false; }

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

    /// <summary>把第一栏占比写进行列定义；两种布局都走这里。</summary>
    private void ApplySplit(double ratio)
    {
        double value = DragSplitter.Clamp(ratio);
        _ratio = value;

        if (!_topBottom)
        {
            var columns = WorkGrid.ColumnDefinitions;
            if (columns.Count < 4) return;
            columns[1].Width = new GridLength(value, GridUnitType.Star);
            columns[3].Width = new GridLength(1.0 - value, GridUnitType.Star);
        }
        else
        {
            var rows = WorkGrid.RowDefinitions;
            if (rows.Count < 3) return;
            rows[0].Height = new GridLength(value, GridUnitType.Star);
            rows[2].Height = new GridLength(1.0 - value, GridUnitType.Star);
        }
    }
}
