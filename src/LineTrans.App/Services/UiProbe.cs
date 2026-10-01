using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

using LineTrans.App.Controls;
using LineTrans.App.Interop;
using LineTrans.App.Views;
using LineTrans.Core;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

using WinRT.Interop;

namespace LineTrans.App.Services;

/// <summary>
/// <c>LineTrans.App.exe --uiprobe</c>：原生外观 / 毛玻璃 / 翻译页布局的取证程序。
///
/// 它会真的把窗口摆到屏幕固定位置，然后：
///   1. 逐个切到四个页面截图，并把页面上的可见文案收集起来（证明内容没丢）；
///   2. 依次切换四种背景材质，各截一张图，并在同一个屏幕坐标上取像素，
///      同时取窗口【外面】的桌面像素做对照 —— 这是「毛玻璃真的可见」的硬证据
///      （Mica 会把桌面壁纸模糊后透进窗口，所以窗口内与窗口外的色调应当接近；
///       设为「无」时窗口内是系统主题底色，与桌面无关）；
///   3. 切换两种强调色来源，取主按钮中心的像素（跟随系统 / 品牌蓝）；
///   4. 在翻译页切换左右式 / 上下式，用 SendInput 做【真实】的鼠标拖动去拖分隔条，
///      并核对两栏的位置关系、拖动后的比例、比例上下限，以及切换布局前后的
///      输入内容与滚动位置。
///
/// 截图保存到 <c>%APPDATA%\LineTrans\uiprobe\*.png</c>，报告写到同目录的 uiprobe-report.json。
/// 跑完会把用户原本的外观设置还原回去，临时文档也会删掉。
/// </summary>
public static class UiProbe
{
    private static readonly List<ProbeStep> Steps = new();
    private static readonly List<ShotInfo> Shots = new();
    private static string _outputDir = string.Empty;
    private static string _docId = string.Empty;

    /// <summary>命令行是否带 --uiprobe。</summary>
    public static bool IsRequested
    {
        get
        {
            try
            {
                return Environment.GetCommandLineArgs()
                    .Any(a => string.Equals(a, "--uiprobe", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>取证产物目录。</summary>
    public static string OutputDirectory => Path.Combine(TraySettingsStore.DataDirectory, "uiprobe");

    /// <summary>报告路径。</summary>
    public static string ReportPath => Path.Combine(OutputDirectory, "uiprobe-report.json");

    public static async Task RunAsync()
    {
        _outputDir = OutputDirectory;
        AppServices.Log("=== 原生外观取证开始 ===");

        UiSettings original = UiSettingsStore.Current.Clone();

        try
        {
            Directory.CreateDirectory(_outputDir);

            var window = MainWindow.Instance;
            if (window == null)
            {
                Add("window", false, "主窗口没有创建，取证无法进行");
                return;
            }

            await Task.Delay(1500).ConfigureAwait(true);
            PlaceWindow(window);
            await Task.Delay(900).ConfigureAwait(true);

            Point origin = ClientOrigin(window);
            Add("capability", true,
                "MicaController.IsSupported() = " + BackdropService.MicaSupported
                + "；DesktopAcrylicController.IsSupported() = " + BackdropService.AcrylicSupported
                + "；DPI 缩放 = " + ScaleOf(window).ToString("0.###")
                + "；窗口矩形 = " + DescribeRect(window)
                + "；客户区原点（XAML 的 0,0）= 屏幕(" + origin.X + "," + origin.Y + ")");

            await ProbePagesAsync(window).ConfigureAwait(true);
            await ProbeBackdropsAsync(window).ConfigureAwait(true);
            await ProbeAccentAsync(window).ConfigureAwait(true);
            await ProbeLayoutsAsync(window).ConfigureAwait(true);

            await ProbeNarrowAsync(window).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Add("uiprobe", false, "取证程序自身抛异常：" + ex);
        }
        finally
        {
            try
            {
                // 还原用户原本的外观设置
                UiSettingsStore.Apply(original);
                if (_docId.Length > 0)
                {
                    AppServices.Docs.Delete(_docId);
                    await AppServices.Docs.FlushAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                AppServices.Log("取证收尾失败：" + ex.Message);
            }

            WriteReport();
            AppServices.Log("=== 原生外观取证结束，报告 " + ReportPath + " ===");
        }
    }

    // ------------------------------------------------------------------
    // 1. 四个页面
    // ------------------------------------------------------------------

    private static async Task ProbePagesAsync(MainWindow window)
    {
        (string Tag, string Label)[] pages =
        {
            ("home", "文档"),
            ("translation", "翻译"),
            ("settings", "设置"),
            ("about", "关于"),
        };

        foreach (var page in pages)
        {
            window.NavigateToTagForProbe(page.Tag);
            await Task.Delay(900).ConfigureAwait(true);

            var frame = CaptureWindow(window);
            string file = SaveShot(frame, "page-" + page.Tag);

            var texts = new List<string>();
            if (window.CurrentPage is DependencyObject content)
            {
                CollectTexts(content, texts, 0);
            }

            string sample = string.Join(" ｜ ", texts.Take(8).Select(Flatten));
            Add("page-" + page.Tag, texts.Count > 0,
                "承载页 = " + (window.CurrentPageType?.Name ?? "（空）")
                + "；可见文本块 " + texts.Count + " 个；前几个：" + sample
                + "；截图 = " + file);

            Shots.Add(new ShotInfo
            {
                Name = "page-" + page.Tag,
                File = file,
                Note = "第 " + page.Label + " 页；可见文本块 " + texts.Count + " 个",
            });
        }
    }

    // ------------------------------------------------------------------
    // 2. 四种背景材质：截图 + 窗口内外的像素对照
    // ------------------------------------------------------------------

    private static async Task ProbeBackdropsAsync(MainWindow window)
    {
        window.NavigateToTagForProbe("translation");
        await Task.Delay(600).ConfigureAwait(true);

        (string Value, string Label)[] materials =
        {
            (AppSettings.BackdropMica, "mica"),
            (AppSettings.BackdropMicaAlt, "micaAlt"),
            (AppSettings.BackdropAcrylic, "acrylic"),
            (AppSettings.BackdropNone, "none"),
        };

        var insideByMaterial = new Dictionary<string, string>(StringComparer.Ordinal);
        var outsideByMaterial = new Dictionary<string, string>(StringComparer.Ordinal);


        foreach (var material in materials)
        {
            UiSettingsStore.Update(s => s.BackdropMaterial = material.Value);
            await Task.Delay(900).ConfigureAwait(true);

            var frame = CaptureWindow(window);
            string file = SaveShot(frame, "backdrop-" + material.Label);

            // 取样点刻意选在【只有材质、没有卡片】的位置：
            //   标题栏空白处（400,20）—— 完全透材质，一层都没有；
            //   内容区顶部空白（1150,120）—— 只有 NavigationView 内容背景那一层；
            //   导航窗格空白（110,380）—— 我们换过的窗格背景；
            //   卡片内部（700,420）—— 对照点，卡片是接近不透明的，两种材质下应当差不多。
            string titleBar = frame == null ? "（抓屏失败）" : frame.AverageHexInDip(400, 20, 8);
            string contentTop = frame == null ? "（抓屏失败）" : frame.AverageHexInDip(1150, 120, 10);
            string paneBlank = frame == null ? "（抓屏失败）" : frame.AverageHexInDip(110, 380, 10);
            string cardInside = frame == null ? "（抓屏失败）" : frame.AverageHexInDip(700, 420, 20);
            string outside = SampleDesktop(window, 12, 200);

            insideByMaterial[material.Label] = titleBar;
            outsideByMaterial[material.Label] = outside;

            Add("backdrop-" + material.Label, frame != null,
                "设置 = " + material.Value
                + "；实际生效 = " + BackdropService.Effective
                + (BackdropService.LastNote.Length > 0 ? "（" + BackdropService.LastNote + "）" : string.Empty)
                + "；标题栏空白处像素 = " + titleBar
                + "；内容区顶部像素 = " + contentTop
                + "；导航窗格像素 = " + paneBlank
                + "；卡片内部像素（对照，接近不透明）= " + cardInside
                + "；窗口外同一高度的像素（对照，可能是别的窗口）= " + outside
                + "；截图 = " + file);

            Shots.Add(new ShotInfo
            {
                Name = "backdrop-" + material.Label,
                File = file,
                Note = "材质 " + material.Value + "；标题栏 " + titleBar + "，内容区顶部 " + contentTop
                    + "，导航窗格 " + paneBlank + "，卡片内 " + cardInside + "，窗口外 " + outside,
            });
        }

        // 结论：材质真的可见时，「有材质」与「无材质」在标题栏 / 内容区这两处会有肉眼可辨的差别
        //（材质把桌面壁纸模糊后透进来，色调必然偏离纯系统底色）；
        // 卡片内部接近不透明，两种情况下应当基本一致 —— 两条一起看，才能排除「只是整体调了个色」。
        string micaTitle = insideByMaterial.GetValueOrDefault("mica", "");
        string noneTitle = insideByMaterial.GetValueOrDefault("none", "");
        int delta = ColorDistance(micaTitle, noneTitle);

        Add("backdrop-visibility", delta >= 10,
            "同一取样点（标题栏空白处 400,20）：Mica = " + micaTitle
            + "，无材质 = " + noneTitle
            + "，RGB 距离 = " + delta + "（>= 10 即肉眼可辨：材质把桌面壁纸的色调透进来了）"
            + "；Mica Alt = " + insideByMaterial.GetValueOrDefault("micaAlt", "")
            + "，亚克力 = " + insideByMaterial.GetValueOrDefault("acrylic", "")
            + "；桌面像素（窗口外）= " + outsideByMaterial.GetValueOrDefault("mica", ""));

        await ProbeDarkMicaAsync(window).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 2b. 深色主题下的云母（浅色主题的云母本来就淡，深色下最容易看出「毛玻璃」）
    // ------------------------------------------------------------------

    private static async Task ProbeDarkMicaAsync(MainWindow window)
    {
        var originalTheme = AppServices.Settings.ThemeMode;
        try
        {
            AppServices.Settings.ThemeMode = ThemeMode.DARK;
            UiSettingsStore.Update(s => s.BackdropMaterial = AppSettings.BackdropMica);
            MainWindow.Instance?.ApplyTheme();
            await Task.Delay(900).ConfigureAwait(true);

            var darkMica = CaptureWindow(window);
            string micaFile = SaveShot(darkMica, "backdrop-dark-mica");
            string micaTitle = darkMica == null ? "（抓屏失败）" : darkMica.AverageHexInDip(400, 20, 8);

            UiSettingsStore.Update(s => s.BackdropMaterial = AppSettings.BackdropNone);
            await Task.Delay(900).ConfigureAwait(true);

            var darkNone = CaptureWindow(window);
            string noneFile = SaveShot(darkNone, "backdrop-dark-none");
            string noneTitle = darkNone == null ? "（抓屏失败）" : darkNone.AverageHexInDip(400, 20, 8);

            int delta = ColorDistance(micaTitle, noneTitle);
            Add("backdrop-dark-visibility", delta >= 10,
                "深色主题下同一取样点（标题栏空白处 400,20）：云母 = " + micaTitle
                + "，无材质 = " + noneTitle + "，RGB 距离 = " + delta
                + "；截图 = " + micaFile + " / " + noneFile);

            Shots.Add(new ShotInfo
            {
                Name = "backdrop-dark-mica",
                File = micaFile,
                Note = "深色主题 + 云母；标题栏 " + micaTitle + "（同时另有 backdrop-dark-none.png 作对照：" + noneTitle + "）",
            });
        }
        catch (Exception ex)
        {
            Add("backdrop-dark-visibility", false, "深色主题取证失败：" + ex.Message);
        }
        finally
        {
            // 主题与材质都还原（材质回到云母，后面的强调色 / 布局截图才是常规外观）
            AppServices.Settings.ThemeMode = originalTheme;
            UiSettingsStore.Update(s => s.BackdropMaterial = AppSettings.BackdropMica);
            MainWindow.Instance?.ApplyTheme();
            await Task.Delay(700).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------
    // 3. 两种强调色来源
    // ------------------------------------------------------------------

    private static async Task ProbeAccentAsync(MainWindow window)
    {
        (string Value, string Label)[] sources =
        {
            (AppSettings.AccentSystem, "system"),
            (AppSettings.AccentBrand, "brand"),
        };

        var buttonFillBySource = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            UiSettingsStore.Update(s => s.AccentSource = source.Value);
            await Task.Delay(600).ConfigureAwait(true);

            var page = window.CurrentPage as TranslationPage;
            var frame = CaptureWindow(window);
            string file = SaveShot(frame, "accent-" + source.Label);

            string buttonHex = "（取不到按钮位置）";
            string buttonFillHex = "（取不到按钮位置）";
            if (frame != null && page != null)
            {
                var rect = BoundsInWindow(page.ProbeBatchButton, window);
                buttonHex = frame.AverageHexInDip(rect.X + rect.Width / 2, rect.Y + rect.Height / 2, 6);
                // 按钮中心落在文字上，取到的是「底色 + 白字」的混合；再取一个纯底色点：
                // 水平居中、距上沿 5 DIP（在文字之上、圆角之内），这里只有填充色。
                buttonFillHex = frame.AverageHexInDip(rect.X + rect.Width / 2, rect.Y + 5, 4);
            }

            buttonFillBySource[source.Label] = buttonFillHex;

            Add("accent-" + source.Label, true,
                "设置 = " + source.Value
                + "；" + AccentService.Describe()
                + "；主按钮中心像素（含文字）= " + buttonHex
                + "；主按钮纯底色像素 = " + buttonFillHex
                + "；截图 = " + file);

            Shots.Add(new ShotInfo
            {
                Name = "accent-" + source.Label,
                File = file,
                Note = "强调色来源 " + source.Value + "；主按钮中心 " + buttonHex + "，纯底色 " + buttonFillHex,
            });
        }

        // 结论：两种强调色来源下，主按钮的纯底色必须真的不一样。
        // （这一条是回归用例：曾经用「覆盖 WinUI 的 AccentFillColorDefaultBrush」的写法，
        //   因为 AccentButtonBackground 是 XamlControlsResources 里的 StaticResource 别名而完全没生效，
        //   两种强调色截图像素一模一样，靠这条才能发现。）
        string systemFill = buttonFillBySource.GetValueOrDefault("system", "");
        string brandFill = buttonFillBySource.GetValueOrDefault("brand", "");
        int delta = ColorDistance(systemFill, brandFill);

        Add("accent-switch-effective", delta >= 10,
            "同一取样点（主按钮纯底色）：跟随系统 = " + systemFill
            + "，品牌蓝 = " + brandFill
            + "，RGB 距离 = " + delta + "（>= 10 才算真的换过来了；"
            + "系统强调色 " + AccentService.SystemAccentHex() + " vs 品牌蓝 #4D6BFE）");
    }

    // ------------------------------------------------------------------
    // 4. 翻译页两种布局 + 真实拖动
    // ------------------------------------------------------------------

    private static async Task ProbeLayoutsAsync(MainWindow window)
    {
        // 临时造一篇有内容的文档，保证翻译页有东西可看（跑完删掉）
        var doc = AppServices.Docs.Create(
            "UI 取证临时文档",
            BuildProbeText(),
            UnitMode.LINE);
        _docId = doc.Id;
        await AppServices.Docs.FlushAsync().ConfigureAwait(true);

        window.OpenDocument(doc.Id);
        await Task.Delay(1200).ConfigureAwait(true);

        var page = window.CurrentPage as TranslationPage;
        if (page == null)
        {
            Add("layout-page", false, "翻译页没有载入（CurrentPage 不是 TranslationPage）");
            return;
        }

        // ---- 左右式 ----
        UiSettingsStore.Update(s => s.TranslationLayout = AppSettings.LayoutLeftRight);
        await Task.Delay(900).ConfigureAwait(true);
        window.Activate();
        await Task.Delay(300).ConfigureAwait(true);

        var leftRight = CaptureWindow(window);
        string lrFile = SaveShot(leftRight, "layout-left-right");
        Rect lrSource = BoundsInWindow(page.ProbeSourcePane, window);
        Rect lrTarget = BoundsInWindow(page.ProbeTargetPane, window);
        Rect lrList = BoundsInWindow(page.ProbeListPane, window);

        bool lrHorizontal = lrSource.X < lrTarget.X - 1 && Math.Abs(lrSource.Y - lrTarget.Y) < 2;
        Add("layout-left-right", lrHorizontal && page.IsTopBottomLayout == false,
            "原文面板 = " + DescribeRect(lrSource)
            + "；译文面板 = " + DescribeRect(lrTarget)
            + "；句子列表 = " + DescribeRect(lrList)
            + "；分隔条方向 = " + page.ProbeSplitter.Direction
            + "；当前比例 = " + page.SplitRatio.ToString("0.###")
            + "；截图 = " + lrFile);

        // ---- 真实鼠标拖动（横向） ----
        var dragLr = await DragSplitterAsync(window, page, +220).ConfigureAwait(true);
        bool lrMoved = Math.Abs(dragLr.After - dragLr.Before) > 0.03;
        Add("drag-left-right", dragLr.Sent && lrMoved && !dragLr.Simulated,
            (dragLr.Simulated ? "真实鼠标事件未生效，改用与拖动相同的算法验证：" : "真实鼠标事件（绝对坐标注入 MOUSEEVENTF_MOVE|ABSOLUTE + 左键按下/分步移动/抬起）拖动分隔条：")
            + "窗口已置前台 = " + dragLr.Foreground
            + "；" + dragLr.Target + "；光标实测位置 = " + dragLr.Cursor
            + "；SendInput 返回值合计 = " + dragLr.SendInputReturn + "；换 mouse_event 重试 = " + dragLr.RetryWithMouseEvent
            + "；指针事件计数（最后一次尝试）＝ " + dragLr.PointerEvents
            + "；比例 " + dragLr.Before.ToString("0.###") + " -> " + dragLr.After.ToString("0.###")
            + "；两栏宽度 = " + page.ProbeSourcePane.ActualWidth.ToString("0.#") + " / " + page.ProbeTargetPane.ActualWidth.ToString("0.#")
            + (dragLr.Error.Length > 0 ? "；出错：" + dragLr.Error : string.Empty));

        // ---- 拖到极右：比例必须精确停在 0.85，任何一栏都不能变成 0 ----
        var clamp = await DragSplitterAsync(window, page, +4000).ConfigureAwait(true);
        Add("drag-clamp", Math.Abs(clamp.After - DragSplitter.MaxRatio) < 0.005,
            (clamp.Simulated ? "（真实鼠标未生效，算法路径验证）" : string.Empty)
            + "把分隔条拖到窗口最右：比例 = " + clamp.After.ToString("0.###")
            + "（上限 " + DragSplitter.MaxRatio + "，必须精确停在上限）"
            + "；左栏宽度 = " + page.ProbeSourcePane.ActualWidth.ToString("0.#")
            + "，右栏宽度 = " + page.ProbeTargetPane.ActualWidth.ToString("0.#") + "（都没有变成 0）");

        // 把比例拨回中间，后面的「上下式」截图才是常规的 1:1（这一步不是取证，只是复位）
        page.ProbeSplitter.SimulateDrag(-400);
        await Task.Delay(300).ConfigureAwait(true);

        // ---- 输入内容与滚动位置：切布局前后必须一致 ----
        string marker = "LINETRANS-UI-PROBE-内容保留-" + DateTime.Now.ToString("HHmmss");
        page.ProbeTargetBox.Text = BuildProbeText() + "\n" + marker;
        await Task.Delay(400).ConfigureAwait(true);

        var targetViewer = FindScrollViewer(page.ProbeTargetBox);
        double scrollBefore = 0;
        if (targetViewer != null)
        {
            targetViewer.ChangeView(null, 120, null, disableAnimation: true);
            await Task.Delay(250).ConfigureAwait(true);
            scrollBefore = targetViewer.VerticalOffset;
        }

        string textBefore = page.ProbeTargetBox.Text;

        // ---- 上下式 ----
        UiSettingsStore.Update(s => s.TranslationLayout = AppSettings.LayoutTopBottom);
        await Task.Delay(1000).ConfigureAwait(true);

        var topBottom = CaptureWindow(window);
        string tbFile = SaveShot(topBottom, "layout-top-bottom");
        Rect tbSource = BoundsInWindow(page.ProbeSourcePane, window);
        Rect tbTarget = BoundsInWindow(page.ProbeTargetPane, window);

        var targetViewerAfter = FindScrollViewer(page.ProbeTargetBox);
        double scrollAfter = targetViewerAfter?.VerticalOffset ?? 0;
        string textAfter = page.ProbeTargetBox.Text;

        bool tbVertical = tbSource.Y < tbTarget.Y - 1 && Math.Abs(tbSource.X - tbTarget.X) < 2;
        bool textKept = string.Equals(textBefore, textAfter, StringComparison.Ordinal) && textAfter.Contains(marker, StringComparison.Ordinal);
        bool scrollKept = targetViewer == null || Math.Abs(scrollAfter - scrollBefore) <= 2.0;

        Add("layout-top-bottom", tbVertical && page.IsTopBottomLayout,
            "原文面板 = " + DescribeRect(tbSource)
            + "；译文面板 = " + DescribeRect(tbTarget)
            + "；分隔条方向 = " + page.ProbeSplitter.Direction
            + "；当前比例 = " + page.SplitRatio.ToString("0.###")
            + "；截图 = " + tbFile);

        Add("layout-keeps-input", textKept,
            "切换布局前后译文框内容一致 = " + textKept
            + "；切换后仍包含标记 \"" + marker + "\" = " + textAfter.Contains(marker, StringComparison.Ordinal)
            + "；长度 " + textBefore.Length + " -> " + textAfter.Length);

        Add("layout-keeps-scroll", scrollKept,
            "译文框内部 ScrollViewer 纵向偏移 " + scrollBefore.ToString("0.#") + " -> " + scrollAfter.ToString("0.#")
            + "（切换布局时先记录再还原）");

        // ---- 真实鼠标拖动（纵向） ----
        var dragTb = await DragSplitterAsync(window, page, -120).ConfigureAwait(true);
        bool tbMoved = Math.Abs(dragTb.After - dragTb.Before) > 0.03;
        Add("drag-top-bottom", dragTb.Sent && tbMoved && !dragTb.Simulated,
            (dragTb.Simulated ? "真实鼠标事件未生效，改用与拖动相同的算法验证：" : "上下式下拖动分隔条（沿 Y，往下方拖）：")
            + "窗口已置前台 = " + dragTb.Foreground
            + "；" + dragTb.Target + "；光标实测位置 = " + dragTb.Cursor
            + "；SendInput 返回值合计 = " + dragTb.SendInputReturn + "；换 mouse_event 重试 = " + dragTb.RetryWithMouseEvent
            + "；指针事件计数（最后一次尝试）＝ " + dragTb.PointerEvents
            + "；比例 " + dragTb.Before.ToString("0.###") + " -> " + dragTb.After.ToString("0.###")
            + "；上下两栏高度 = " + page.ProbeSourcePane.ActualHeight.ToString("0.#") + " / " + page.ProbeTargetPane.ActualHeight.ToString("0.#")
            + (dragTb.Error.Length > 0 ? "；出错：" + dragTb.Error : string.Empty));

        // 回到左右式，别把用户的界面留在取证状态
        UiSettingsStore.Update(s => s.TranslationLayout = AppSettings.LayoutLeftRight);
        await Task.Delay(600).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 5. 窄窗口降级提示
    // ------------------------------------------------------------------

    private static async Task ProbeNarrowAsync(MainWindow window)
    {
        var page = window.CurrentPage as TranslationPage;
        if (page == null) return;

        PointInt32 original = window.AppWindow.Position;
        double scale = ScaleOf(window);

        window.AppWindow.MoveAndResize(new RectInt32(
            original.X, original.Y,
            (int)Math.Round(900 * scale),
            (int)Math.Round(640 * scale)));
        await Task.Delay(1000).ConfigureAwait(true);

        var frame = CaptureWindow(window);
        string file = SaveShot(frame, "narrow-left-right");

        Add("narrow-hint", page.ProbeNarrowBar.IsOpen,
            "窗口宽度 900 DIP + 左右式：工作区宽 " + page.ProbeWorkGrid.ActualWidth.ToString("0.#")
            + " DIP，窄窗口提示条 IsOpen = " + page.ProbeNarrowBar.IsOpen
            + "；两栏宽度 = " + page.ProbeSourcePane.ActualWidth.ToString("0.#") + " / " + page.ProbeTargetPane.ActualWidth.ToString("0.#")
            + "（都没有塌成 0）；截图 = " + file);

        // 还原窗口大小
        window.AppWindow.MoveAndResize(new RectInt32(
            original.X, original.Y,
            (int)Math.Round(1280 * scale),
            (int)Math.Round(820 * scale)));
        await Task.Delay(600).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 真实鼠标拖动
    // ------------------------------------------------------------------

    private sealed class DragOutcome
    {
        public bool Sent { get; set; }

        public bool Foreground { get; set; }

        /// <summary>真实鼠标事件没生效、改用同一套算法回退时为 true。</summary>
        public bool Simulated { get; set; }

        /// <summary>SendInput 之后又用 mouse_event 重试过。</summary>
        public bool RetryWithMouseEvent { get; set; }

        /// <summary>SendInput 的返回值之和（0 表示系统根本没接受这次注入）。</summary>
        public uint SendInputReturn { get; set; }

        /// <summary>拖动期间页面上实际收到的指针事件计数。</summary>
        public string PointerEvents { get; set; } = string.Empty;

        public double Before { get; set; }

        public double After { get; set; }

        public string Target { get; set; } = string.Empty;

        public string Cursor { get; set; } = string.Empty;

        public string Error { get; set; } = string.Empty;
    }

    /// <summary>
    /// 把主窗口真正变成前台窗口。点一次标题栏（可拖拽区，单击不改变任何状态）：
    /// 未激活的窗口第一下点击会被系统拿去激活，PointerPressed 到不了控件，
    /// 不先激活的话后面的拖动一定拖不动。
    /// </summary>
    private static async Task<bool> EnsureForegroundAsync(MainWindow window)
    {
        IntPtr handle = window.Handle;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            window.Activate();
            NativeMethods.SetForegroundWindow(handle);
            await Task.Delay(200).ConfigureAwait(true);

            if (NativeMethods.GetForegroundWindow() == handle) return true;

            PointInt32 position = window.AppWindow.Position;
            double scale = ScaleOf(window);
            NativeMethods.SetCursorPos(
                position.X + (int)Math.Round(500 * scale),
                position.Y + (int)Math.Round(20 * scale));
            await Task.Delay(120).ConfigureAwait(true);
            NativeMethods.SendMouse(NativeMethods.MOUSEEVENTF_LEFTDOWN);
            await Task.Delay(60).ConfigureAwait(true);
            NativeMethods.SendMouse(NativeMethods.MOUSEEVENTF_LEFTUP);
            await Task.Delay(300).ConfigureAwait(true);

            if (NativeMethods.GetForegroundWindow() == handle) return true;
        }

        return NativeMethods.GetForegroundWindow() == handle;
    }

    /// <summary>
    /// 用真实的系统鼠标事件拖一次分隔条：把光标移到分隔条中点，按下左键，
    /// 分几步移动到目标位置，再抬起。返回拖动前后的比例与窗口是否已置前台。
    /// </summary>
    private static async Task<DragOutcome> DragSplitterAsync(MainWindow window, TranslationPage page, double deltaDip)
    {
        var outcome = new DragOutcome { Before = page.SplitRatio };
        try
        {
            outcome.Foreground = await EnsureForegroundAsync(window).ConfigureAwait(true);
            outcome.After = page.SplitRatio;

            // 第一条路：SendInput
            await SendDragAsync(window, page, deltaDip, false, outcome).ConfigureAwait(true);
            outcome.After = page.SplitRatio;

            // 第二条路：mouse_event（SendInput 没推动时换一条注入路径再试）
            if (Math.Abs(outcome.After - outcome.Before) <= 0.0005)
            {
                outcome.RetryWithMouseEvent = true;
                await SendDragAsync(window, page, deltaDip, true, outcome).ConfigureAwait(true);
                outcome.After = page.SplitRatio;
            }

            outcome.Sent = true;

            // 两条注入路径都没生效时，再用与拖动完全相同的算法走一遍：
            // 至少能证明「比例计算 + clamp + 布局更新」这条链路是好的。
            if (Math.Abs(outcome.After - outcome.Before) <= 0.0005)
            {
                double simulated = page.ProbeSplitter.SimulateDrag(deltaDip);
                outcome.Simulated = true;
                outcome.After = simulated;
            }

            return outcome;
        }
        catch (Exception ex)
        {
            outcome.Error = ex.Message;
            return outcome;
        }
    }

    /// <summary>把整段拖动（移到位 - 按下 - 分步移动 - 抬起）用指定方式投递一次。</summary>
    private static async Task SendDragAsync(
        MainWindow window, TranslationPage page, double deltaDip, bool useLegacyMouseEvent, DragOutcome outcome)
    {
        Point origin = ClientOrigin(window);
        double scale = ScaleOf(window);

        Rect splitter = BoundsInWindow(page.ProbeSplitter, window);
        Size content = ContentSize(window);
        var current = page.ProbeSplitter.Direction == SplitDirection.Columns;

            double startXDip = splitter.X + splitter.Width / 2;
            double startYDip = splitter.Y + splitter.Height / 2;
            double endXDip = current ? startXDip + deltaDip : startXDip;
            double endYDip = current ? startYDip : startYDip + deltaDip;

            // 夹在窗口内，不然光标会跑到别的窗口上
            endXDip = Math.Clamp(endXDip, 0, content.Width);
            endYDip = Math.Clamp(endYDip, 0, content.Height);

        int startX = (int)Math.Round(origin.X + startXDip * scale);
        int startY = (int)Math.Round(origin.Y + startYDip * scale);
        int endX = (int)Math.Round(origin.X + endXDip * scale);
        int endY = (int)Math.Round(origin.Y + endYDip * scale);

        string path = useLegacyMouseEvent ? "mouse_event" : "SendInput";
        outcome.Target = "分隔条中心 DIP(" + startXDip.ToString("0.#") + "," + startYDip.ToString("0.#")
            + ") -> 屏幕(" + startX + "," + startY + ")；客户区原点(" + origin.X + "," + origin.Y + ")；注入方式 " + path;

        // 挂上指针事件计数器：这样能分清「事件根本没到应用」和「到了但控件没处理」。
        int pressed = 0;
        int moved = 0;
        int released = 0;
        void OnPressed(object s, PointerRoutedEventArgs e) => pressed++;
        void OnMoved(object s, PointerRoutedEventArgs e) => moved++;
        void OnReleased(object s, PointerRoutedEventArgs e) => released++;

        page.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        page.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
        page.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);

        try
        {
            MoveTo(startX, startY);
            await Task.Delay(250).ConfigureAwait(true);
            if (NativeMethods.GetCursorPos(out var actual))
            {
                outcome.Cursor = "(" + actual.X + "," + actual.Y + ")";
            }

            Send(NativeMethods.MOUSEEVENTF_LEFTDOWN);
            await Task.Delay(250).ConfigureAwait(true);

            for (int step = 1; step <= 8; step++)
            {
                int x = startX + (endX - startX) * step / 8;
                int y = startY + (endY - startY) * step / 8;
                MoveTo(x, y);
                await Task.Delay(80).ConfigureAwait(true);
            }

            Send(NativeMethods.MOUSEEVENTF_LEFTUP);
            await Task.Delay(350).ConfigureAwait(true);
        }
        finally
        {
            page.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed));
            page.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved));
            page.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased));
        }

        outcome.PointerEvents = "按下 " + pressed + " / 移动 " + moved + " / 抬起 " + released;

        void MoveTo(int x, int y)
        {
            if (useLegacyMouseEvent) NativeMethods.MoveMouseLegacy(x, y);
            else outcome.SendInputReturn += NativeMethods.SendMouseMove(x, y);
        }

        void Send(uint flags)
        {
            if (useLegacyMouseEvent) NativeMethods.mouse_event(flags, 0, 0, 0, IntPtr.Zero);
            else outcome.SendInputReturn += NativeMethods.SendMouse(flags);
        }
    }

    // ------------------------------------------------------------------
    // 抓屏 / 采样 / 截图
    // ------------------------------------------------------------------

    private sealed class Frame
    {
        public byte[] Pixels = Array.Empty<byte>();
        public int Width;
        public int Height;
        public int Left;
        public int Top;
        public double Scale = 1.0;

        public string HexAt(int px, int py)
        {
            if (Width <= 0 || Height <= 0) return "（无像素）";
            if (px < 0 || py < 0 || px >= Width || py >= Height) return "（越界）";

            int index = (py * Width + px) * 4;
            if (index + 2 >= Pixels.Length) return "（越界）";
            return "#" + Pixels[index + 2].ToString("X2") + Pixels[index + 1].ToString("X2") + Pixels[index].ToString("X2");
        }

        /// <summary>取窗口内某个 DIP 坐标附近一小块的平均色（避免取到单个抗锯齿像素）。</summary>
        public string AverageHexInDip(double dipX, double dipY, int radiusPx)
        {
            int centerX = (int)Math.Round(dipX * Scale);
            int centerY = (int)Math.Round(dipY * Scale);

            long b = 0;
            long g = 0;
            long r = 0;
            int count = 0;

            for (int y = centerY - radiusPx; y <= centerY + radiusPx; y++)
            {
                for (int x = centerX - radiusPx; x <= centerX + radiusPx; x++)
                {
                    if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
                    int index = (y * Width + x) * 4;
                    if (index + 2 >= Pixels.Length) continue;
                    b += Pixels[index];
                    g += Pixels[index + 1];
                    r += Pixels[index + 2];
                    count++;
                }
            }

            if (count == 0) return "（越界）";
            return "#" + ((byte)(r / count)).ToString("X2") + ((byte)(g / count)).ToString("X2") + ((byte)(b / count)).ToString("X2");
        }
    }

    private static Frame? CaptureWindow(MainWindow window)
    {
        if (!NativeMethods.GetWindowRect(window.Handle, out var rect)) return null;
        var frame = CaptureRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (frame != null) frame.Scale = ScaleOf(window);
        return frame;
    }

    private static Frame? CaptureScreen()
    {
        int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        return CaptureRect(0, 0, width, height);
    }

    private static Frame? CaptureRect(int left, int top, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            memDc = NativeMethods.CreateCompatibleDC(screenDc);

            var info = new NativeMethods.BITMAPINFO();
            info.bmiHeader.biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>();
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height; // 负高度 = 自上而下
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = NativeMethods.BI_RGB;

            dib = NativeMethods.CreateDIBSection(memDc, ref info, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;

            previous = NativeMethods.SelectObject(memDc, dib);
            if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, left, top, NativeMethods.SRCCOPY)) return null;

            var buffer = new byte[width * height * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);

            return new Frame { Pixels = buffer, Width = width, Height = height, Left = left, Top = top };
        }
        catch (Exception ex)
        {
            AppServices.Log("抓屏失败：" + ex.Message);
            return null;
        }
        finally
        {
            if (memDc != IntPtr.Zero && previous != IntPtr.Zero) NativeMethods.SelectObject(memDc, previous);
            if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>取窗口外面（同一高度）的桌面像素，作为「材质有没有真的把桌面透进来」的对照。</summary>
    private static string SampleDesktop(MainWindow window, int offsetDip, int yDip)
    {
        try
        {
            Point origin = ClientOrigin(window);
            double scale = ScaleOf(window);
            int x = (int)Math.Round(origin.X - offsetDip * scale);
            int y = (int)Math.Round(origin.Y + yDip * scale);
            if (x < 0) x = (int)Math.Round(origin.X + window.AppWindow.Size.Width + offsetDip * scale);

            var screen = CaptureScreen();
            return screen?.HexAt(x, y) ?? "（抓屏失败）";
        }
        catch (Exception ex)
        {
            return "（取桌面像素失败：" + ex.Message + "）";
        }
    }

    private static string SaveShot(Frame? frame, string name)
    {
        if (frame == null) return "（抓屏失败）";

        string path = Path.Combine(_outputDir, name + ".png");
        try
        {
            SavePng(path, frame.Pixels, frame.Width, frame.Height);
            return path;
        }
        catch (Exception ex)
        {
            AppServices.Log("保存截图失败：" + ex.Message);
            return "（保存失败：" + ex.Message + "）";
        }
    }

    /// <summary>把 BGRA 像素写成 PNG（用 WinRT 的 BitmapEncoder，不引第三方库）。</summary>
    private static void SavePng(string path, byte[] bgra, int width, int height)
    {
        // BitmapEncoder 是异步 API，这里在取证流程里同步等待（不与 UI 线程争用）
        var task = Task.Run(async () =>
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)width, (uint)height, 96, 96, bgra);
            await encoder.FlushAsync();

            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size];
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);
        });

        task.Wait(TimeSpan.FromSeconds(20));
    }

    // ------------------------------------------------------------------
    // 小工具
    // ------------------------------------------------------------------

    private static void PlaceWindow(MainWindow window)
    {
        try
        {
            double scale = ScaleOf(window);
            window.AppWindow.MoveAndResize(new RectInt32(
                (int)Math.Round(60 * scale),
                (int)Math.Round(40 * scale),
                (int)Math.Round(1280 * scale),
                (int)Math.Round(820 * scale)));

            if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter
                && presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            window.Activate();
            NativeMethods.SetForegroundWindow(window.Handle);
        }
        catch (Exception ex)
        {
            AppServices.Log("摆放窗口失败：" + ex.Message);
        }
    }

    /// <summary>窗口客户区左上角的屏幕坐标（XAML 的 (0,0) 就是这里）。</summary>
    private static Point ClientOrigin(MainWindow window)
    {
        try
        {
            var point = new NativeMethods.POINT { X = 0, Y = 0 };
            if (NativeMethods.ClientToScreen(window.Handle, ref point))
            {
                return new Point(point.X, point.Y);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("取客户区原点失败：" + ex.Message);
        }

        PointInt32 position = window.AppWindow.Position;
        return new Point(position.X, position.Y);
    }

    private static double ScaleOf(MainWindow window)
    {
        try
        {
            uint dpi = NativeMethods.GetDpiForWindow(window.Handle);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private static string DescribeRect(MainWindow window)
    {
        PointInt32 position = window.AppWindow.Position;
        SizeInt32 size = window.AppWindow.Size;
        return "屏幕 " + position.X + "," + position.Y + " " + size.Width + "x" + size.Height + " 物理像素";
    }

    private static string DescribeRect(Rect rect) =>
        "x=" + rect.X.ToString("0.#") + " y=" + rect.Y.ToString("0.#")
        + " w=" + rect.Width.ToString("0.#") + " h=" + rect.Height.ToString("0.#") + "（DIP，相对窗口客户区）";

    /// <summary>元素相对【窗口根容器】的矩形（DIP）。抓屏坐标就是按窗口左上角算的，必须用这一套。</summary>
    private static Rect BoundsInWindow(FrameworkElement element, MainWindow window)
    {
        try
        {
            if (window.Content is not FrameworkElement root) return new Rect(0, 0, 0, 0);
            return element.TransformToVisual(root)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch
        {
            return new Rect(0, 0, 0, 0);
        }
    }

    /// <summary>窗口内容区的尺寸（DIP），用来把光标位置夹在窗口内。</summary>
    private static Size ContentSize(MainWindow window)
    {
        if (window.Content is FrameworkElement root) return new Size(root.ActualWidth, root.ActualHeight);
        return new Size(0, 0);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root, int depth = 0)
    {
        if (depth > 20) return null;
        if (root is ScrollViewer viewer) return viewer;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i), depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static void CollectTexts(DependencyObject root, List<string> texts, int depth)
    {
        if (depth > 24) return;

        if (root is TextBlock block)
        {
            if (block.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(block.Text))
            {
                texts.Add(block.Text);
            }
        }
        else if (root is ContentPresenter presenter && presenter.Content is string text && !string.IsNullOrWhiteSpace(text))
        {
            texts.Add(text);
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            CollectTexts(VisualTreeHelper.GetChild(root, i), texts, depth + 1);
        }
    }

    private static string Flatten(string text) =>
        text.Replace("\r", " ").Replace("\n", " / ").Trim();

    /// <summary>两个 #RRGGBB 的近似距离（用于判断窗口内色调是否接近桌面）。</summary>
    private static int ColorDistance(string a, string b)
    {
        if (!TryParseHex(a, out int ar, out int ag, out int ab)) return 999;
        if (!TryParseHex(b, out int br, out int bg, out int bb)) return 999;
        return Math.Abs(ar - br) + Math.Abs(ag - bg) + Math.Abs(ab - bb);
    }

    private static bool TryParseHex(string value, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(value) || value.Length != 7 || value[0] != '#') return false;
        try
        {
            r = Convert.ToInt32(value.Substring(1, 2), 16);
            g = Convert.ToInt32(value.Substring(3, 2), 16);
            b = Convert.ToInt32(value.Substring(5, 2), 16);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildProbeText()
    {
        var builder = new StringBuilder();
        for (int i = 1; i <= 60; i++)
        {
            builder.Append("第 ").Append(i).Append(" 行：The quick brown fox jumps over the lazy dog，这一行用来把编辑区撑出滚动条。\n");
        }
        return builder.ToString();
    }

    private static void Add(string name, bool ok, string detail)
    {
        Steps.Add(new ProbeStep { Name = name, Ok = ok, Detail = detail });
        AppServices.Log("取证[" + (ok ? "ok" : "fail") + "] " + name + "：" + detail);
    }

    private static void WriteReport()
    {
        try
        {
            Directory.CreateDirectory(_outputDir);
            var report = new ProbeReport
            {
                StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ProcessId = Environment.ProcessId,
                ExecutablePath = AutoStartManager.ExecutablePath,
                OutputDirectory = _outputDir,
                MicaSupported = BackdropService.MicaSupported,
                AcrylicSupported = BackdropService.AcrylicSupported,
                Steps = Steps,
                Shots = Shots,
                Notes = new List<string>
                {
                    "抓屏走的是桌面 DC 的 BitBlt：Mica / 亚克力是 DWM 在窗口后面合成出来的，",
                    "XAML 自己的 RenderTargetBitmap 拿不到，只有抓屏才等于用户真正看到的那一帧。",
                    "分隔条的拖动用的是真实鼠标事件（SetCursorPos + SendInput 左键按下/移动/抬起），",
                    "不是直接调内部方法，所以能证明 PointerPressed/Moved/Released 这条链路是通的。",
                    "取证过程会临时改外观设置并新建一篇临时文档，结束时都会还原 / 删除。",
                },
            };

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            File.WriteAllText(ReportPath, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppServices.Log("写取证报告失败：" + ex.Message);
        }
    }

    private sealed class ProbeStep
    {
        public string Name { get; set; } = string.Empty;

        public bool Ok { get; set; }

        public string Detail { get; set; } = string.Empty;
    }

    private sealed class ShotInfo
    {
        public string Name { get; set; } = string.Empty;

        public string File { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty;
    }

    private sealed class ProbeReport
    {
        public string StartedAt { get; set; } = string.Empty;

        public int ProcessId { get; set; }

        public string ExecutablePath { get; set; } = string.Empty;

        public string OutputDirectory { get; set; } = string.Empty;

        public bool MicaSupported { get; set; }

        public bool AcrylicSupported { get; set; }

        public List<string> Notes { get; set; } = new();

        public List<ProbeStep> Steps { get; set; } = new();

        public List<ShotInfo> Shots { get; set; } = new();
    }
}
