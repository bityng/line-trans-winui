using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

using LineTrans.App.Interop;
using LineTrans.App.Views;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

using WinRT.Interop;

namespace LineTrans.App.Services;

/// <summary>
/// <c>LineTrans.App.exe --selftest</c>：启动后自动跑一遍托盘 / 全局热键 / 剪贴板保护 / 划词链路，
/// 把每一步的原始结果写成 <c>%APPDATA%\LineTrans\selftest-report.json</c>，然后退出。
///
/// 真东西：真的 Shell_NotifyIcon、真的 RegisterHotKey、真的往托盘窗口投递 WM_HOTKEY、
/// 真的 SendInput Ctrl+C、真的系统剪贴板、真的离线词库查询、真的弹窗。
/// 唯一模拟的环节是「谁是那个前台程序」：自检会开一个自己的窗口当事前台，
/// 它收到 Ctrl+C 时用 Win32 剪贴板 API 把「选中的文字」写进去——
/// 这样不必去动用户正在用的记事本，也不会把测试文字打进别人的文档里。
/// </summary>
public static class SelfTest
{
    private const string MarkerA = "LINETRANS-SELFTEST-CLIPBOARD-A";
    private const string MarkerB = "LINETRANS-SELFTEST-CLIPBOARD-B";
    private const string SampleWord = "hello";

    private static readonly List<StepReport> Steps = new();

    /// <summary>命令行是否带 --selftest。</summary>
    public static bool IsRequested
    {
        get
        {
            try
            {
                return Environment.GetCommandLineArgs()
                    .Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>报告路径。</summary>
    public static string ReportPath => Path.Combine(TraySettingsStore.DataDirectory, "selftest-report.json");

    /// <summary>跑完整套自检并落盘报告。</summary>
    public static async Task RunAsync()
    {
        AppServices.Log("=== 自检开始 ===");

        // 自检会反复读写剪贴板，先把用户原本的剪贴板存起来，跑完原样还回去
        ClipboardSnapshot userClipboard = await ClipboardGuard.CaptureAsync().ConfigureAwait(true);

        try
        {
            await Task.Delay(1500).ConfigureAwait(true); // 等词库预热 + 托盘线程就绪

            Add(CheckTray());
            Add(CheckHotkey());
            Add(CheckAutoStart());
            Add(await CheckClipboardTextRoundTripAsync().ConfigureAwait(true));
            Add(await CheckClipboardFileRoundTripAsync().ConfigureAwait(true));
            await CheckCaptureChainAsync().ConfigureAwait(true);
            Add(await CheckSentencePathAsync().ConfigureAwait(true));
            Add(await CheckCloseToTrayAsync().ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            Add(new StepReport("selftest", false, "自检自身抛异常：" + ex, "failed"));
        }

        bool userClipboardRestored = await ClipboardGuard.RestoreAsync(userClipboard).ConfigureAwait(true);
        AppServices.Log("自检：用户原剪贴板（" + userClipboard.Describe() + "）还原 = " + userClipboardRestored);

        WriteReport();
        AppServices.Log("=== 自检结束，报告 " + ReportPath + " ===");
    }

    // ------------------------------------------------------------------
    // 1. 托盘图标
    // ------------------------------------------------------------------

    private static StepReport CheckTray()
    {
        var status = App.Tray?.Status;
        if (status == null)
        {
            return new StepReport("tray-icon", false, "托盘宿主没有启动（App.Tray 为空）", "failed");
        }

        string detail = "Shell_NotifyIcon 返回 = "
            + (status.ShellNotifyIconResult.HasValue ? status.ShellNotifyIconResult.Value.ToString() : "未调用")
            + "；GetLastError = " + status.ShellNotifyIconError
            + "；Win32 说明 = " + NativeMethods.DescribeError(status.ShellNotifyIconError)
            + "；托盘窗口句柄 = 0x" + (App.Tray?.Handle ?? IntPtr.Zero).ToString("X")
            + "；状态：" + status.Message;

        return new StepReport("tray-icon", status.TrayIconAdded, detail, status.TrayIconAdded ? "passed" : "failed");
    }

    // ------------------------------------------------------------------
    // 2. 全局热键
    // ------------------------------------------------------------------

    private static StepReport CheckHotkey()
    {
        var status = App.Tray?.Status;
        if (status == null)
        {
            return new StepReport("global-hotkey", false, "托盘宿主没有启动，热键未注册", "failed");
        }

        string detail = "设置里配置的热键 = " + status.ConfiguredHotkeyText
            + "；实际注册成功的热键 = " + status.HotkeyText
            + "；RegisterHotKey 返回值 = " + (status.HotkeyRegistered ? "TRUE" : "FALSE")
            + "；GetLastError = " + status.HotkeyError
            + "；Win32 说明 = " + NativeMethods.DescribeError(status.HotkeyError)
            + "；是否发生备用切换 = " + status.HotkeyFellBack
            + "；启用开关 = " + (TraySettingsStore.Current.HotkeyEnabled ? "开" : "关")
            + "；物理按键未模拟（见 notes）";

        return new StepReport("global-hotkey", status.HotkeyRegistered, detail,
            status.HotkeyRegistered ? "passed" : "failed");
    }

    // ------------------------------------------------------------------
    // 2b. 开机自启（HKCU 的 Run 项）—— 写完读回来，然后清理掉
    // ------------------------------------------------------------------

    private static StepReport CheckAutoStart()
    {
        try
        {
            string before = AutoStartManager.RegisteredCommand;
            if (before.Length > 0)
            {
                // 用户本来就开了自启，绝不能替他关掉
                return new StepReport("auto-start", false,
                    "注册表里本来就有自启项，为避免动用户设置，本步未执行：" + before, "skipped");
            }

            string onError = string.Empty;
            bool onOk = AutoStartManager.Apply(true, out onError) && AutoStartManager.IsEnabled;
            string written = AutoStartManager.RegisteredCommand;

            string offError = string.Empty;
            bool offOk = AutoStartManager.Apply(false, out offError) && !AutoStartManager.IsEnabled;

            string after = AutoStartManager.RegisteredCommand;
            bool ok = onOk && offOk && after.Length == 0;

            return new StepReport("auto-start", ok,
                "写入前 = \"" + before + "\""
                + "；Apply(true) 成功 = " + onOk + (onError.Length > 0 ? "（" + onError + "）" : string.Empty)
                + "；写进注册表的命令行 = \"" + written + "\""
                + "；Apply(false) 成功 = " + offOk + (offError.Length > 0 ? "（" + offError + "）" : string.Empty)
                + "；清理后 = \"" + after + "\"（期望空）"
                + "；注册表路径 = HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
                ok ? "passed" : "failed");
        }
        catch (Exception ex)
        {
            return new StepReport("auto-start", false, "异常：" + ex, "failed");
        }
    }

    // ------------------------------------------------------------------
    // 3. 剪贴板保护：文本
    // ------------------------------------------------------------------

    private static async Task<StepReport> CheckClipboardTextRoundTripAsync()
    {
        try
        {
            await ClipboardGuard.SetTextAsync(MarkerA).ConfigureAwait(true);
            await Task.Delay(120).ConfigureAwait(true);
            ClipboardSnapshot snapshot = await ClipboardGuard.CaptureAsync().ConfigureAwait(true);

            // 模拟「模拟 Ctrl+C 之后剪贴板被改写」的状态
            await ClipboardGuard.SetTextAsync(MarkerB).ConfigureAwait(true);
            await Task.Delay(120).ConfigureAwait(true);
            string overwritten = await ClipboardGuard.ReadTextAsync().ConfigureAwait(true);

            bool restored = await ClipboardGuard.RestoreAsync(snapshot).ConfigureAwait(true);
            await Task.Delay(120).ConfigureAwait(true);
            string after = await ClipboardGuard.ReadTextAsync().ConfigureAwait(true);

            bool ok = restored && string.Equals(after, MarkerA, StringComparison.Ordinal);
            string detail = "快照类型 = " + snapshot.Kind + "（" + snapshot.Describe() + "）"
                + "；被覆盖后 = \"" + overwritten + "\"（期望 " + MarkerB + "）"
                + "；RestoreAsync 返回 = " + restored
                + "；还原后 = \"" + after + "\""
                + "；期望 = \"" + MarkerA + "\"";

            return new StepReport("clipboard-text-roundtrip", ok, detail, ok ? "passed" : "failed");
        }
        catch (Exception ex)
        {
            return new StepReport("clipboard-text-roundtrip", false, "异常：" + ex, "failed");
        }
    }

    // ------------------------------------------------------------------
    // 4. 剪贴板保护：非文本（文件列表）
    // ------------------------------------------------------------------

    private static async Task<StepReport> CheckClipboardFileRoundTripAsync()
    {
        string temp = Path.Combine(Path.GetTempPath(), "linetrans-selftest-file.txt");
        try
        {
            await File.WriteAllTextAsync(temp, "linetrans selftest").ConfigureAwait(true);
            StorageFile file = await StorageFile.GetFileFromPathAsync(temp);
            await Task.Delay(120).ConfigureAwait(true);

            var package = new DataPackage();
            package.SetStorageItems(new[] { file });
            Clipboard.SetContent(package);
            Clipboard.Flush();
            await Task.Delay(200).ConfigureAwait(true);

            ClipboardSnapshot snapshot = await ClipboardGuard.CaptureAsync().ConfigureAwait(true);
            if (snapshot.Kind != ClipboardKind.Files)
            {
                return new StepReport("clipboard-file-roundtrip", false,
                    "剪贴板里的文件列表没能被识别成 Files，实际是 " + snapshot.Kind, "failed");
            }

            await ClipboardGuard.SetTextAsync("LINETRANS-SELFTEST-OVERWRITE").ConfigureAwait(true);
            await Task.Delay(120).ConfigureAwait(true);
            bool restored = await ClipboardGuard.RestoreAsync(snapshot).ConfigureAwait(true);
            await Task.Delay(200).ConfigureAwait(true);

            bool hasFiles = Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
            bool ok = restored && hasFiles;
            string detail = "快照类型 = " + snapshot.Kind + "（" + snapshot.Describe() + "）"
                + "；RestoreAsync 返回 = " + restored
                + "；还原后剪贴板仍含文件列表 = " + hasFiles;

            return new StepReport("clipboard-file-roundtrip", ok, detail, ok ? "passed" : "failed");
        }
        catch (Exception ex)
        {
            return new StepReport("clipboard-file-roundtrip", false, "异常：" + ex, "failed");
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // 临时文件删不掉无所谓
            }
        }
    }

    // ------------------------------------------------------------------
    // 5 + 6 + 7. 热键触发 → 读剪贴板 → 查词 → 弹窗
    // ------------------------------------------------------------------

    private static async Task CheckCaptureChainAsync()
    {
        var tray = App.Tray;
        if (tray == null)
        {
            Add(new StepReport("e2e-no-selection", false, "托盘宿主没有启动，投不了 WM_HOTKEY", "skipped"));
            Add(new StepReport("e2e-capture-lookup", false, "同上，未执行", "skipped"));
            Add(new StepReport("popup-single-instance", false, "同上，未执行", "skipped"));
            return;
        }

        IntPtr mainHandle = MainWindow.Instance != null
            ? WindowNative.GetWindowHandle(MainWindow.Instance)
            : IntPtr.Zero;

        try
        {
            LookupPopupWindow.CloseCurrent();
            await Task.Delay(400).ConfigureAwait(true);

            // ---- 场景一：前台窗口在，但「没有选中任何文字」 ----
            tray.ShowCopyTarget(string.Empty);
            await Task.Delay(400).ConfigureAwait(true);
            bool focusedEmpty = await FocusCopyTargetAsync().ConfigureAwait(true);

            await ClipboardGuard.SetTextAsync(MarkerA).ConfigureAwait(true);
            await Task.Delay(250).ConfigureAwait(true);

            string foreground1 = HandleText(NativeMethods.GetForegroundWindow());
            string target = HandleText(tray.CopyTargetHandle);
            tray.SimulateCaptureHotkey();
            await Task.Delay(2200).ConfigureAwait(true);

            string clipboardAfterEmpty = await ClipboardGuard.ReadTextAsync().ConfigureAwait(true);
            LookupPopupWindow? popupAfterEmpty = LookupPopupWindow.ActivePopup;
            bool noSelectionShown = popupAfterEmpty != null
                && popupAfterEmpty.RenderedHeadline.Contains("未检测到选中文字", StringComparison.Ordinal);
            bool clipboardKept = string.Equals(clipboardAfterEmpty, MarkerA, StringComparison.Ordinal);

            Add(new StepReport(
                "e2e-no-selection",
                noSelectionShown && clipboardKept,
                "复制目标置前台 = " + focusedEmpty
                + "；触发瞬间前台窗口 = " + foreground1 + "（复制目标 = " + target + "）"
                + "；弹窗标题 = \"" + (popupAfterEmpty?.RenderedHeadline ?? "（没有弹窗）") + "\""
                + "；弹窗可见 = " + (popupAfterEmpty?.IsPopupVisible ?? false)
                + "；剪贴板 = \"" + clipboardAfterEmpty + "\"（期望仍是 " + MarkerA + "）",
                noSelectionShown && clipboardKept ? "passed" : "failed"));

            // ---- 场景二：前台的「选中内容」是 hello，走完整条链 ----
            tray.ShowCopyTarget(SampleWord);
            await Task.Delay(400).ConfigureAwait(true);
            bool focusedHello = await FocusCopyTargetAsync().ConfigureAwait(true);

            await ClipboardGuard.SetTextAsync(MarkerA).ConfigureAwait(true);
            await Task.Delay(250).ConfigureAwait(true);

            string foreground2 = HandleText(NativeMethods.GetForegroundWindow());
            tray.SimulateCaptureHotkey();
            await Task.Delay(3200).ConfigureAwait(true);

            LookupPopupWindow? popup = LookupPopupWindow.ActivePopup;
            string capturedText = GlobalCaptureService.LastCapturedText;
            string clipboardRestored = await ClipboardGuard.ReadTextAsync().ConfigureAwait(true);

            bool capturedOk = string.Equals(capturedText, SampleWord, StringComparison.Ordinal);
            bool popupOk = popup != null && popup.IsPopupVisible && popup.RenderedBody.Length > 0;
            bool restoredOk = string.Equals(clipboardRestored, MarkerA, StringComparison.Ordinal);

            Add(new StepReport(
                "e2e-capture-lookup",
                capturedOk && popupOk && restoredOk,
                "复制目标置前台 = " + focusedHello
                + "；触发瞬间前台窗口 = " + foreground2 + "（复制目标 = " + target + "）"
                + "；捕获文字 = \"" + capturedText + "\"（期望 " + SampleWord + "）"
                + "；LastClipboardChanged = " + GlobalCaptureService.LastClipboardChanged
                + "；弹窗可见 = " + (popup?.IsPopupVisible ?? false)
                + "；弹窗标题 = \"" + (popup?.RenderedHeadline ?? "（没有弹窗）") + "\""
                + "；弹窗徽章 = \"" + (popup?.RenderedBadge ?? string.Empty) + "\""
                + "；弹窗音标 = \"" + (popup?.RenderedPhonetic ?? string.Empty) + "\""
                + "；弹窗正文 = \"" + Flatten(popup?.RenderedBody ?? string.Empty) + "\""
                + "；剪贴板还原后 = \"" + clipboardRestored + "\"（期望 " + MarkerA + "）",
                capturedOk && popupOk && restoredOk ? "passed" : "failed"));

            // ---- 场景三：连续快速按两次热键，只应有一个弹窗 ----
            tray.ShowCopyTarget(SampleWord);
            await Task.Delay(400).ConfigureAwait(true);
            await FocusCopyTargetAsync().ConfigureAwait(true);

            tray.SimulateCaptureHotkey();
            await Task.Delay(60).ConfigureAwait(true);
            tray.SimulateCaptureHotkey();
            await Task.Delay(2800).ConfigureAwait(true);

            int popupWindows = CountVisibleWindowsExcluding(mainHandle, tray.CopyTargetHandle);
            bool single = popupWindows == 1 && LookupPopupWindow.ActivePopup != null;

            Add(new StepReport(
                "popup-single-instance",
                single,
                "连按两次热键后，本进程可见的顶层窗口数（不含主窗口与复制目标） = " + popupWindows
                + "；LookupPopupWindow.ActivePopup 非空 = " + (LookupPopupWindow.ActivePopup != null)
                + "；弹窗标题 = \"" + (LookupPopupWindow.ActivePopup?.RenderedHeadline ?? string.Empty) + "\""
                + "；全局划词是否仍在跑 = " + GlobalCaptureService.IsRunning,
                single ? "passed" : "failed"));
        }
        catch (Exception ex)
        {
            Add(new StepReport("e2e-chain", false, "异常：" + ex, "failed"));
        }
        finally
        {
            tray.HideCopyTarget();
        }
    }

    // ------------------------------------------------------------------
    // 8. 整句 → AI 翻译（未配置 Key 时要有明确中文提示）
    // ------------------------------------------------------------------

    private static async Task<StepReport> CheckSentencePathAsync()
    {
        var tray = App.Tray;
        if (tray == null)
        {
            return new StepReport("e2e-sentence-ai", false, "托盘宿主没有启动，未执行", "skipped");
        }

        var provider = AppServices.Settings.ActiveProvider;
        string model = AppServices.Settings.ActiveModel?.Name ?? string.Empty;
        bool configured = provider != null
            && !string.IsNullOrWhiteSpace(provider.BaseUrl)
            && model.Length > 0;

        if (configured)
        {
            return new StepReport("e2e-sentence-ai", false,
                "这台机器已经配置了 AI（baseUrl=" + provider!.BaseUrl + "，model=" + model
                + "），自检不发起真实联网调用，本步未执行", "skipped");
        }

        try
        {
            const string sentence = "Hello world, this is a sentence.";
            tray.ShowCopyTarget(sentence);
            await Task.Delay(400).ConfigureAwait(true);
            await FocusCopyTargetAsync().ConfigureAwait(true);

            tray.SimulateCaptureHotkey();
            await Task.Delay(2600).ConfigureAwait(true);

            LookupPopupWindow? popup = LookupPopupWindow.ActivePopup;
            string badge = popup?.RenderedBadge ?? string.Empty;
            string body = popup?.RenderedBody ?? string.Empty;
            bool ok = popup != null
                && badge.Contains("未配置", StringComparison.Ordinal)
                && body.Contains("尚未配置", StringComparison.Ordinal);

            return new StepReport("e2e-sentence-ai", ok,
                "捕获文字 = \"" + GlobalCaptureService.LastCapturedText + "\""
                + "；弹窗标题 = \"" + (popup?.RenderedHeadline ?? "（没有弹窗）") + "\""
                + "；弹窗徽章 = \"" + badge + "\""
                + "；弹窗正文 = \"" + Flatten(body) + "\"（期望含「尚未配置」的中文提示）",
                ok ? "passed" : "failed");
        }
        catch (Exception ex)
        {
            return new StepReport("e2e-sentence-ai", false, "异常：" + ex, "failed");
        }
    }

    // ------------------------------------------------------------------
    // 9. 关闭窗口 → 最小化到托盘（而不是退出进程）
    // ------------------------------------------------------------------

    private static async Task<StepReport> CheckCloseToTrayAsync()
    {
        var window = MainWindow.Instance;
        if (window == null)
        {
            return new StepReport("close-to-tray", false, "主窗口不存在，未执行", "skipped");
        }

        var settings = TraySettingsStore.Current;
        if (!settings.TrayIconEnabled
            || !string.Equals(settings.CloseAction, TraySettings.CloseToTray, StringComparison.OrdinalIgnoreCase))
        {
            return new StepReport("close-to-tray", false,
                "当前设置是「关闭即退出」或托盘已关（TrayIconEnabled=" + settings.TrayIconEnabled
                + "，CloseAction=" + settings.CloseAction + "），本步未执行", "skipped");
        }

        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(window);
            bool visibleBefore = NativeMethods.IsWindowVisible(hwnd);

            NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(1000).ConfigureAwait(true);

            bool visibleAfter = NativeMethods.IsWindowVisible(hwnd);
            bool trayStillThere = App.Tray?.Status.TrayIconAdded == true;
            bool processAlive = App.Tray?.Status.HotkeyRegistered == true;

            bool ok = visibleBefore && !visibleAfter && trayStillThere && processAlive;
            return new StepReport("close-to-tray", ok,
                "发 WM_CLOSE 前主窗口可见 = " + visibleBefore
                + "；之后主窗口可见 = " + visibleAfter + "（期望 False，即被拦下改成隐藏）"
                + "；托盘图标仍在 = " + trayStillThere
                + "；全局热键仍注册 = " + processAlive + "（说明进程没有退出）",
                ok ? "passed" : "failed");
        }
        catch (Exception ex)
        {
            return new StepReport("close-to-tray", false, "异常：" + ex, "failed");
        }
    }

    /// <summary>把复制目标推成前台窗口，直到 GetForegroundWindow 真的是它。</summary>
    private static async Task<bool> FocusCopyTargetAsync()
    {
        var tray = App.Tray;
        if (tray == null) return false;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            tray.FocusCopyTarget();
            await Task.Delay(150).ConfigureAwait(true);
            if (NativeMethods.GetForegroundWindow() == tray.CopyTargetHandle) return true;
        }

        return NativeMethods.GetForegroundWindow() == tray.CopyTargetHandle;
    }

    private static int CountVisibleWindowsExcluding(IntPtr excludeA, IntPtr excludeB)
    {
        uint pid = (uint)Environment.ProcessId;
        int count = 0;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == excludeA || hwnd == excludeB) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != pid) return true;
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            count++;
            return true;
        }, IntPtr.Zero);

        return count;
    }

    // ------------------------------------------------------------------
    // 报告
    // ------------------------------------------------------------------

    private static void Add(StepReport step)
    {
        Steps.Add(step);
        AppServices.Log("自检[" + step.Status + "] " + step.Name + "：" + step.Detail);
    }

    private static string Flatten(string text)
        => text.Replace("\r", " ").Replace("\n", " / ").Trim();

    private static string HandleText(IntPtr hwnd) => "0x" + hwnd.ToString("X");

    private static void WriteReport()
    {
        try
        {
            var report = new ReportDocument
            {
                StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ProcessId = Environment.ProcessId,
                ExecutablePath = AutoStartManager.ExecutablePath,
                TraySettingsFile = TraySettingsStore.FilePath,
                LogFile = AppServices.LogPath,
                Steps = Steps,
                Notes = new List<string>
                {
                    "「按下物理 Ctrl+Alt+C」这一环没有模拟：自检是往托盘窗口投递真正的 WM_HOTKEY，",
                    "走的是和物理按键完全相同的消息路径（RegisterHotKey 的返回值单独在 global-hotkey 步骤里核过）。",
                    "「谁是那个前台程序」也是模拟的：自检开了一个自己的窗口当前台，它收到 Ctrl+C 时用 Win32",
                    "剪贴板 API 把选中内容写进去。这样做是为了不碰用户正在使用的记事本等内容——",
                    "不模拟的部分是：真实的输入队列投递、真实的系统剪贴板改写、真实的离线词库查询、真实的弹窗渲染。",
                    "全屏独占程序、以管理员权限运行的程序里拿不到选中文字，属已知的系统限制。",
                    "「整句 AI 翻译」依赖用户自己的 API Key，未配置时弹窗给出中文提示，自检不做联网调用。",
                    "自检开始时会保存用户原本的剪贴板内容，结束时原样还原。",
                },
            };

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            string dir = Path.GetDirectoryName(ReportPath) ?? TraySettingsStore.DataDirectory;
            Directory.CreateDirectory(dir);
            File.WriteAllText(ReportPath, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppServices.Log("写自检报告失败：" + ex.Message);
        }
    }

    private sealed class StepReport
    {
        public StepReport(string name, bool ok, string detail, string status)
        {
            Name = name;
            Ok = ok;
            Detail = detail;
            Status = status;
        }

        public string Name { get; }

        public bool Ok { get; }

        public string Detail { get; }

        /// <summary>passed / failed / skipped。</summary>
        public string Status { get; }
    }

    private sealed class ReportDocument
    {
        public string StartedAt { get; set; } = string.Empty;

        public int ProcessId { get; set; }

        public string ExecutablePath { get; set; } = string.Empty;

        public string TraySettingsFile { get; set; } = string.Empty;

        public string LogFile { get; set; } = string.Empty;

        public List<string> Notes { get; set; } = new();

        public List<StepReport> Steps { get; set; } = new();
    }
}
