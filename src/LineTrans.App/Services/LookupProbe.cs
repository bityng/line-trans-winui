using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using LineTrans.App.Controls;
using LineTrans.App.Interop;
using LineTrans.App.Views;
using LineTrans.Core;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;
using Windows.Graphics;

namespace LineTrans.App.Services;

/// <summary>
/// <c>LineTrans.App.exe --lookupprobe</c>：翻译页划词查义（悬浮 / 单击 / 划词）的取证程序。
///
/// 它做的是真事，不是「调内部方法看返回 true」：
///   1. 把窗口摆到固定位置，造一篇临时文档并切到翻译页；
///   2. 先用 GetRangeFromPoint 反查出一个「确实落在某个词上」的坐标（点不准就什么都证明不了）；
///   3. 用 mouse_event 注入【真实鼠标】：移进去停住 / 单击 / 按住拖出一段选区；
///   4. 每一步之后读回真实状态：浮层是否打开、浮层的标题 / 音标 / 徽章 / 正文原文；
///   5. 原文区与译文区各跑一遍；
///   6. 三个开关逐个关、逐个验（关掉的失效，没关的照常）。
///
/// 报告写成纯文本到 %APPDATA%\LineTrans\lookupprobe\lookupprobe-report.txt，跑完退出。
/// </summary>
public static class LookupProbe
{
    private static readonly List<string> Lines = new();

    private static string _dir = string.Empty;
    private static string _docId = string.Empty;
    private static int _failures;

    /// <summary>命令行是否带 --lookupprobe。</summary>
    public static bool IsRequested
    {
        get
        {
            try
            {
                return Environment.GetCommandLineArgs()
                    .Any(a => string.Equals(a, "--lookupprobe", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>报告目录。</summary>
    public static string OutputDirectory => Path.Combine(TraySettingsStore.DataDirectory, "lookupprobe");

    /// <summary>报告路径。</summary>
    public static string ReportPath => Path.Combine(OutputDirectory, "lookupprobe-report.txt");

    public static async Task RunAsync()
    {
        _dir = OutputDirectory;
        Directory.CreateDirectory(_dir);
        AppServices.Log("=== 划词查义取证开始 ===");

        try
        {
            var window = MainWindow.Instance;
            if (window == null)
            {
                Add("window", false, "主窗口没有创建，取证无法进行");
                return;
            }

            await Task.Delay(1500).ConfigureAwait(true);
            PlaceWindow(window);
            await Task.Delay(900).ConfigureAwait(true);

            ProbeEnvironment();
            await ProbeDictionaryAsync().ConfigureAwait(true);

            await OpenProbeDocumentAsync(window).ConfigureAwait(true);
            var page = window.CurrentPage as TranslationPage;
            if (page == null)
            {
                Add("page", false, "翻译页没有载入（CurrentPage 不是 TranslationPage）");
                return;
            }

            // ---- 原文区：悬浮 / 单击 / 划词 ----
            await ProbeEditorAsync(window, page, page.ProbeSourceBox, "source", "原文区", "hello", "charlie").ConfigureAwait(true);

            // ---- 译文区：把一段英文塞进去，同样跑三种 ----
            page.ProbeSetTargetText("apple banana cherry date elderberry fig grape honey");
            await Task.Delay(500).ConfigureAwait(true);
            await ProbeEditorAsync(window, page, page.ProbeTargetBox, "target", "译文区", "apple", "cherry").ConfigureAwait(true);

            // ---- 三个开关 ----
            await ProbeSwitchesAsync(window, page).ConfigureAwait(true);
            ProbeSettingsJson();
        }
        catch (Exception ex)
        {
            Add("lookupprobe", false, "取证程序自身抛异常：" + ex);
        }
        finally
        {
            try
            {
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

            Write();
            AppServices.Log("=== 划词查义取证结束，报告 " + ReportPath + " ===");
        }
    }

    // ------------------------------------------------------------------
    // 一、环境与词库
    // ------------------------------------------------------------------

    private static void ProbeEnvironment()
    {
        var s = AppServices.Settings;
        Add("env", true,
            "总开关 WordLookupEnabled=" + s.WordLookupEnabled
            + "；悬浮 LookupHoverEnabled=" + s.LookupHoverEnabled
            + "；单击 LookupClickEnabled=" + s.LookupClickEnabled
            + "；划词 LookupSelectionEnabled=" + s.LookupSelectionEnabled
            + "；LocalDictionaryEnabled=" + s.LocalDictionaryEnabled
            + "；LookupAiFallback=" + s.LookupAiFallback
            + "；core.tsv 存在=" + File.Exists(AppServices.CoreDictPath)
            + "；lemma.tsv 存在=" + File.Exists(AppServices.LemmaDictPath));
    }

    private static async Task ProbeDictionaryAsync()
    {
        try
        {
            var entry = await AppServices.LookupWordAsync("hello").ConfigureAwait(true);
            Add("dict-direct", entry.Ok,
                "绕开界面直接查 hello：Ok=" + entry.Ok
                + "；Word=\"" + entry.Word + "\""
                + "；Phonetic=\"" + (entry.Phonetic ?? "") + "\""
                + "；Source=\"" + entry.Source + "\""
                + "；释义=" + string.Join(" | ", entry.Senses.Select(x => x.PartOfSpeech + " " + x.Definition)));
        }
        catch (Exception ex)
        {
            Add("dict-direct", false, "直接查词抛异常：" + ex);
        }
    }

    private static async Task OpenProbeDocumentAsync(MainWindow window)
    {
        string text = string.Join("\n", Enumerable.Range(1, 30)
            .Select(i => "hello alpha bravo charlie delta echo foxtrot golf hotel india juliet 第 " + i + " 行"));

        var doc = AppServices.Docs.Create("LOOKUPPROBE-临时文档", text, UnitMode.LINE);
        _docId = doc.Id;
        await AppServices.Docs.FlushAsync().ConfigureAwait(true);

        window.OpenDocument(doc.Id);
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(150).ConfigureAwait(true);
            if (window.CurrentPage is TranslationPage p && p.ProbeDocLoaded) break;
        }

        var page = window.CurrentPage as TranslationPage;
        Add("doc", page?.ProbeDocLoaded == true,
            "临时文档 id=" + doc.Id + "；单元数=" + doc.Units.Count
            + "；第 1 单元原文=\"" + doc.Units[0].Source + "\""
            + "；翻译页已载入=" + (page?.ProbeDocLoaded == true)
            + "；原文框实际文本=\"" + (page?.ProbeSourceText ?? "") + "\"");
    }

    // ------------------------------------------------------------------
    // 二、一个编辑区跑三种方式
    // ------------------------------------------------------------------

    private static async Task ProbeEditorAsync(
        MainWindow window, TranslationPage page, RichEditBox box,
        string tag, string label, string firstWord, string thirdWord)
    {
        await EnsureForegroundAsync(window).ConfigureAwait(true);

        Rect rect = BoundsInWindow(box, window);
        double scale = ScaleOf(window);
        Point origin = ClientOrigin(window);

        string trace() => tag == "source" ? page.ProbeSourceLookupTrace : page.ProbeTargetLookupTrace;

        Add("area-" + tag, rect.Width > 20 && rect.Height > 20,
            label + "：矩形（DIP，窗口客户区）= x=" + rect.X.ToString("0.#") + " y=" + rect.Y.ToString("0.#")
            + " w=" + rect.Width.ToString("0.#") + " h=" + rect.Height.ToString("0.#")
            + "；框内文本=\"" + LookupText.Shorten(EditorWordLookup.DocumentText(box), 70) + "\"");

        bool foundFirst = TryFindWordPoint(window, box, firstWord, out Point firstDip, out string firstSeen);
        bool foundThird = TryFindWordPoint(window, box, thirdWord, out Point thirdDip, out string thirdSeen);

        Add("hit-test-" + tag, foundFirst,
            "用 ITextDocument.GetRangeFromPoint 反查坐标：找 \"" + firstWord + "\" 的点 = ("
            + firstDip.X.ToString("0.#") + "," + firstDip.Y.ToString("0.#") + ")（结果 " + foundFirst + "，"
            + "该点上取到的是 \"" + firstSeen + "\"）；找 \"" + thirdWord + "\" 的点 = ("
            + thirdDip.X.ToString("0.#") + "," + thirdDip.Y.ToString("0.#") + ")（结果 " + foundThird + "，"
            + "该点上取到的是 \"" + thirdSeen + "\"）");

        int firstX = (int)Math.Round(origin.X + firstDip.X * scale);
        int firstY = (int)Math.Round(origin.Y + firstDip.Y * scale);
        int thirdX = (int)Math.Round(origin.X + (thirdDip.X + 40) * scale);
        int thirdY = (int)Math.Round(origin.Y + thirdDip.Y * scale);

        // ---- 1) 悬浮 ----
        await ResetLookupAsync(page, window, label).ConfigureAwait(true);
        MoveTo(firstX, firstY);
        await Task.Delay(1100).ConfigureAwait(true);
        Add("hover-" + tag, page.ProbeLookupOpen,
            label + "真实鼠标移上去停住（超过 400ms 延时）：鼠标停在 \"" + firstWord + "\" 上，屏幕(" + firstX + "," + firstY + ")"
            + "；" + Quote(page.ProbeLastTriggerReport)
            + "；浮层已打开=" + page.ProbeLookupOpen + DescribePanel(page, trace()));

        // ---- 2) 单击 ----
        await ResetLookupAsync(page, window, label).ConfigureAwait(true);
        ClickAt(firstX, firstY);
        await Task.Delay(1100).ConfigureAwait(true);
        Add("click-" + tag, page.ProbeLookupOpen,
            label + "真实鼠标单击单词：点在 \"" + firstWord + "\" 上，屏幕(" + firstX + "," + firstY + ")"
            + "；" + Quote(page.ProbeLastTriggerReport)
            + "；浮层已打开=" + page.ProbeLookupOpen + DescribePanel(page, trace()));

        // ---- 3) 划词（按住左键拖出一段选区） ----
        await ResetLookupAsync(page, window, label).ConfigureAwait(true);
        await DragSelectAsync(firstX, firstY, thirdX, thirdY).ConfigureAwait(true);
        await Task.Delay(1400).ConfigureAwait(true);
        Add("selection-" + tag, page.ProbeLookupOpen,
            label + "真实鼠标按住左键从 \"" + firstWord + "\" 拖到 \"" + thirdWord + "\"：屏幕(" + firstX + "," + firstY + ")"
            + " → (" + thirdX + "," + thirdY + ")"
            + "；" + Quote(page.ProbeLastTriggerReport)
            + "；浮层已打开=" + page.ProbeLookupOpen + DescribePanel(page, trace()));
    }

    // ------------------------------------------------------------------
    // 三、三个开关逐个关、逐个验
    // ------------------------------------------------------------------

    private static async Task ProbeSwitchesAsync(MainWindow window, TranslationPage page)
    {
        var s = AppServices.Settings;
        bool hover0 = s.LookupHoverEnabled;
        bool click0 = s.LookupClickEnabled;
        bool selection0 = s.LookupSelectionEnabled;
        bool master0 = s.WordLookupEnabled;

        var box = page.ProbeSourceBox;
        Rect rect = BoundsInWindow(box, window);
        double scale = ScaleOf(window);
        Point origin = ClientOrigin(window);

        TryFindWordPoint(window, box, "hello", out Point firstDip, out _);
        TryFindWordPoint(window, box, "charlie", out Point thirdDip, out _);
        int firstX = (int)Math.Round(origin.X + firstDip.X * scale);
        int firstY = (int)Math.Round(origin.Y + firstDip.Y * scale);
        int thirdX = (int)Math.Round(origin.X + (thirdDip.X + 40) * scale);
        int thirdY = (int)Math.Round(origin.Y + thirdDip.Y * scale);

        try
        {
            // 说明：每个开关都【单独】验一次 —— 只动被测的那一个，另外两个明确置为关闭，
            // 这样「浮层没弹」就一定是因为被测开关关了，而不是被别的开关顺带顶掉。

            // (1) 悬浮开 + 单击关 + 划词关：停在词上要弹，单击不许弹
            s.LookupHoverEnabled = true;
            s.LookupClickEnabled = false;
            s.LookupSelectionEnabled = false;

            await ResetLookupAsync(page, window, "开关(1)").ConfigureAwait(true);
            MoveTo(firstX, firstY);
            await Task.Delay(1100).ConfigureAwait(true);
            Add("switch-hover-on", page.ProbeLookupOpen,
                "只开「悬浮」（单击与划词都关）：鼠标停在 \"" + "hello" + "\" 上 1.1 秒，浮层已打开=" + page.ProbeLookupOpen
                + "（期望 True）" + DescribePanel(page, page.ProbeSourceLookupTrace));

            await ResetLookupAsync(page, window, "开关(1)").ConfigureAwait(true);
            ClickAt(firstX, firstY);
            await Task.Delay(1100).ConfigureAwait(true);
            Add("switch-click-off", !page.ProbeLookupOpen,
                "关掉「单击」开关（悬浮与划词也关）后单击 \"" + "hello" + "\"：浮层已打开=" + page.ProbeLookupOpen
                + "（期望 False）；" + Quote(page.ProbeLastTriggerReport));

            // (2) 单击开 + 悬浮关 + 划词关：单击要弹
            s.LookupHoverEnabled = false;
            s.LookupClickEnabled = true;
            s.LookupSelectionEnabled = false;

            await ResetLookupAsync(page, window, "开关(2)").ConfigureAwait(true);
            ClickAt(firstX, firstY);
            await Task.Delay(1100).ConfigureAwait(true);
            Add("switch-click-on", page.ProbeLookupOpen,
                "只开「单击」（关掉悬浮之后，用单击还能查到）：单击 \"" + "hello" + "\" 后浮层已打开=" + page.ProbeLookupOpen
                + "（期望 True）" + DescribePanel(page, page.ProbeSourceLookupTrace));

            await ResetLookupAsync(page, window, "开关(2)").ConfigureAwait(true);
            MoveTo(firstX, firstY);
            await Task.Delay(1100).ConfigureAwait(true);
            Add("switch-hover-off", !page.ProbeLookupOpen,
                "关掉「悬浮」开关后把鼠标停在 \"" + "hello" + "\" 上 1.1 秒：浮层已打开=" + page.ProbeLookupOpen
                + "（期望 False）；" + Quote(page.ProbeLastTriggerReport));

            // (3) 划词开 + 悬浮关 + 单击关：拖选要弹
            s.LookupHoverEnabled = false;
            s.LookupClickEnabled = false;
            s.LookupSelectionEnabled = true;

            await ResetLookupAsync(page, window, "开关(3)").ConfigureAwait(true);
            await DragSelectAsync(firstX, firstY, thirdX, thirdY).ConfigureAwait(true);
            await Task.Delay(1400).ConfigureAwait(true);
            Add("switch-selection-on", page.ProbeLookupOpen,
                "只开「划词」（悬浮与单击都关）：按住左键从 \"" + "hello" + "\" 拖到 \""
                + "charlie" + "\"，浮层已打开=" + page.ProbeLookupOpen
                + "（期望 True）；" + Quote(page.ProbeLastTriggerReport)
                + DescribePanel(page, page.ProbeSourceLookupTrace));

            // (4) 划词关（三个都关）：拖选不许弹
            s.LookupHoverEnabled = false;
            s.LookupClickEnabled = false;
            s.LookupSelectionEnabled = false;

            await ResetLookupAsync(page, window, "开关(4)").ConfigureAwait(true);
            await DragSelectAsync(firstX, firstY, thirdX, thirdY).ConfigureAwait(true);
            await Task.Delay(1400).ConfigureAwait(true);
            Add("switch-selection-off", !page.ProbeLookupOpen,
                "关掉「划词」开关（三个都关）后拖选同一段：浮层已打开=" + page.ProbeLookupOpen
                + "（期望 False）；" + Quote(page.ProbeLastTriggerReport));

            // (5) 总开关：三个子开关全开，但总开关关掉 —— 三种都不该触发
            s.LookupHoverEnabled = true;
            s.LookupClickEnabled = true;
            s.LookupSelectionEnabled = true;
            s.WordLookupEnabled = false;

            await ResetLookupAsync(page, window, "总开关").ConfigureAwait(true);
            ClickAt(firstX, firstY);
            await Task.Delay(900).ConfigureAwait(true);
            bool clickBlocked = !page.ProbeLookupOpen;

            await ResetLookupAsync(page, window, "总开关").ConfigureAwait(true);
            await DragSelectAsync(firstX, firstY, thirdX, thirdY).ConfigureAwait(true);
            await Task.Delay(1200).ConfigureAwait(true);
            bool selectionBlocked = !page.ProbeLookupOpen;

            await ResetLookupAsync(page, window, "总开关").ConfigureAwait(true);
            MoveTo(firstX, firstY);
            await Task.Delay(1100).ConfigureAwait(true);
            bool hoverBlocked = !page.ProbeLookupOpen;

            Add("switch-master-off", clickBlocked && selectionBlocked && hoverBlocked,
                "关掉总开关 WordLookupEnabled 后（三个子开关都还是开的）："
                + "单击后浮层已打开=" + !clickBlocked + "（期望 False）；"
                + "拖选后浮层已打开=" + !selectionBlocked + "（期望 False）；"
                + "悬浮后浮层已打开=" + !hoverBlocked + "（期望 False）");
        }
        finally
        {
            s.LookupHoverEnabled = hover0;
            s.LookupClickEnabled = click0;
            s.LookupSelectionEnabled = selection0;
            s.WordLookupEnabled = master0;
            page.ProbeHideLookup();
        }
    }

    /// <summary>三个开关 + 总开关都写给 settings.json 的序列化结果（不写盘，只检查映射）。</summary>
    private static void ProbeSettingsJson()
    {
        var s = AppServices.Settings;
        bool hover0 = s.LookupHoverEnabled;
        bool click0 = s.LookupClickEnabled;
        bool selection0 = s.LookupSelectionEnabled;

        try
        {
            s.LookupHoverEnabled = false;
            s.LookupClickEnabled = true;
            s.LookupSelectionEnabled = false;

            string json = AppServices.SettingsRepo.ToJson();
            bool hasHover = json.Contains("\"lookupHoverEnabled\": false", StringComparison.Ordinal);
            bool hasClick = json.Contains("\"lookupClickEnabled\": true", StringComparison.Ordinal);
            bool hasSelection = json.Contains("\"lookupSelectionEnabled\": false", StringComparison.Ordinal);

            int at = json.IndexOf("\"lookupHoverEnabled\"", StringComparison.Ordinal);
            string excerpt = at < 0 ? "（没找到）" : json.Substring(Math.Max(0, at - 40), Math.Min(220, json.Length - Math.Max(0, at - 40)));

            Add("settings-json", hasHover && hasClick && hasSelection,
                "把三个开关设成 false/true/false 之后 SettingsRepo.ToJson() 里的原文片段：" + Quote(LookupText.Shorten(excerpt, 200))
                + "；三个字段都在=" + (hasHover && hasClick && hasSelection));
        }
        catch (Exception ex)
        {
            Add("settings-json", false, "检查设置序列化时抛异常：" + ex.Message);
        }
        finally
        {
            s.LookupHoverEnabled = hover0;
            s.LookupClickEnabled = click0;
            s.LookupSelectionEnabled = selection0;
        }
    }

    // ------------------------------------------------------------------
    // 鼠标与坐标
    // ------------------------------------------------------------------

    /// <summary>收起浮层、把鼠标挪到窗口标题栏（触发 PointerExited），再等布局稳下来。</summary>
    private static async Task ResetLookupAsync(TranslationPage page, MainWindow window, string label)
    {
        page.ProbeHideLookup();

        PointInt32 position = window.AppWindow.Position;
        double scale = ScaleOf(window);
        int x = position.X + (int)Math.Round(640 * scale);
        int y = position.Y + (int)Math.Round(12 * scale);

        MoveTo(x, y);
        await Task.Delay(260).ConfigureAwait(true);

        if (page.ProbeLookupOpen)
        {
            AppServices.Log("划词取证：" + label + " 复位时浮层仍然开着，再收一次");
            page.ProbeHideLookup();
            await Task.Delay(200).ConfigureAwait(true);
        }
    }

    /// <summary>在控件里反查一个「确实落在目标词上」的坐标（DIP，相对窗口客户区）。</summary>
    private static bool TryFindWordPoint(MainWindow window, RichEditBox box, string word, out Point dipPoint, out string seen)
    {
        dipPoint = new Point(0, 0);
        seen = string.Empty;

        Rect rect = BoundsInWindow(box, window);
        string text = EditorWordLookup.DocumentText(box);
        double maxX = Math.Min(rect.Width - 2, 420);
        double maxY = Math.Min(rect.Height - 2, 80);

        for (double y = 4; y < maxY; y += 4)
        {
            for (double x = 2; x < maxX; x += 3)
            {
                string found = EditorWordLookup.WordAtPoint(box, new Point(x, y), text, out _, out _);
                if (found.Length == 0) continue;
                if (seen.Length == 0) seen = found + "@" + x.ToString("0") + "," + y.ToString("0");
                if (string.Equals(found, word, StringComparison.OrdinalIgnoreCase))
                {
                    dipPoint = new Point(rect.X + x, rect.Y + y);
                    return true;
                }
            }
        }

        return false;
    }

    private static void MoveTo(int x, int y) => NativeMethods.MoveMouseLegacy(x, y);

    private static void Send(uint flags) => NativeMethods.mouse_event(flags, 0, 0, 0, IntPtr.Zero);

    private static void ClickAt(int x, int y)
    {
        MoveTo(x, y);
        System.Threading.Thread.Sleep(150);
        Send(NativeMethods.MOUSEEVENTF_LEFTDOWN);
        System.Threading.Thread.Sleep(50);
        Send(NativeMethods.MOUSEEVENTF_LEFTUP);
    }

    private static async Task DragSelectAsync(int startX, int startY, int endX, int endY)
    {
        MoveTo(startX, startY);
        await Task.Delay(160).ConfigureAwait(true);

        Send(NativeMethods.MOUSEEVENTF_LEFTDOWN);
        await Task.Delay(140).ConfigureAwait(true);

        for (int step = 1; step <= 6; step++)
        {
            MoveTo(startX + (endX - startX) * step / 6, startY + (endY - startY) * step / 6);
            await Task.Delay(70).ConfigureAwait(true);
        }

        await Task.Delay(90).ConfigureAwait(true);
        Send(NativeMethods.MOUSEEVENTF_LEFTUP);
        await Task.Delay(220).ConfigureAwait(true);
    }

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
            Send(NativeMethods.MOUSEEVENTF_LEFTDOWN);
            await Task.Delay(60).ConfigureAwait(true);
            Send(NativeMethods.MOUSEEVENTF_LEFTUP);
            await Task.Delay(300).ConfigureAwait(true);

            if (NativeMethods.GetForegroundWindow() == handle) return true;
        }

        return NativeMethods.GetForegroundWindow() == handle;
    }

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

    private static Point ClientOrigin(MainWindow window)
    {
        try
        {
            var point = new NativeMethods.POINT { X = 0, Y = 0 };
            if (NativeMethods.ClientToScreen(window.Handle, ref point)) return new Point(point.X, point.Y);
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

    private static string DescribePanel(TranslationPage page, string trace)
    {
        string head = "；浮层被显示次数=" + page.ProbeLookupShowCount
            + "；浮层位置=" + page.ProbeLookupPlacement;
        var panel = page.ProbeLookupPanel;
        if (panel == null) return head + "；浮层面板还没创建；" + trace;
        return head + "；浮层标题=" + Quote(panel.ProbeWord)
            + "；音标=" + Quote(panel.ProbePhonetic)
            + "；徽章=" + Quote(panel.ProbeSourceBadge)
            + "；匹配=" + Quote(panel.ProbeMatchLabel)
            + "；正文=" + Quote(panel.ProbeMeaning)
            + "；" + trace;
    }

    private static string Quote(string? value) => "\"" + (value ?? string.Empty) + "\"";

    private static void Add(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Lines.Add("[" + (ok ? "ok" : "fail") + "] " + name + "：" + detail);
        AppServices.Log("划词取证[" + (ok ? "ok" : "fail") + "] " + name + "：" + detail);
    }

    private static void Write()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var builder = new StringBuilder();
            builder.AppendLine("逐行翻译 PC 端 · 划词查义取证报告");
            builder.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine("进程：" + Environment.ProcessId + "　可执行文件：" + AutoStartManager.ExecutablePath);
            builder.AppendLine("失败条目：" + _failures + " / " + Lines.Count);
            builder.AppendLine(new string('=', 78));
            foreach (string line in Lines) builder.AppendLine(line);
            builder.AppendLine(new string('=', 78));
            builder.AppendLine("说明：");
            builder.AppendLine("· 鼠标事件用 mouse_event 注入（本机实测 UI 线程发 SendInput 到不了 WinUI 的指针栈）。");
            builder.AppendLine("· 坐标不是估的：先用 ITextDocument.GetRangeFromPoint 扫出「确实落在这个词上」的点再注入鼠标。");
            builder.AppendLine("· AI 未配置时划词走不到真译文，浮层会显示 AiClient 抛出的中文提示 —— 这本身证明划词链路走到了模型调用这一步。");

            File.WriteAllText(ReportPath, builder.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppServices.Log("写取证报告失败：" + ex.Message);
        }
    }
}
