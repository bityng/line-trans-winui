using System;
using System.Collections.Generic;
using System.Threading;

using LineTrans.App.Interop;

namespace LineTrans.App.Services;

/// <summary>托盘 / 热键的实时状态快照（跨线程只读，整体替换，避免逐字段加锁）。</summary>
public sealed record TrayStatus(
    bool TrayIconAdded,
    bool? ShellNotifyIconResult,
    int ShellNotifyIconError,
    bool HotkeyRegistered,
    int HotkeyError,
    string HotkeyText,
    string ConfiguredHotkeyText,
    bool EscapeHotkeyRegistered,
    string Message)
{
    /// <summary>实际生效的热键是否与设置里配置的不是同一个（说明配置的那个被别的程序占了）。</summary>
    public bool HotkeyFellBack => HotkeyRegistered
        && ConfiguredHotkeyText.Length > 0
        && !string.Equals(HotkeyText, ConfiguredHotkeyText, StringComparison.Ordinal);

    public static TrayStatus Empty { get; } = new(
        false, null, 0, false, 0, HotkeySpec.DefaultText, HotkeySpec.DefaultText, false, "托盘尚未启动");
}

/// <summary>
/// 系统托盘 + 全局热键宿主。
///
/// 结构：一个独占线程，创建一个不显示的顶层窗口，跑标准 GetMessage 循环。
///   · <c>Shell_NotifyIcon</c> 需要一个能收回调消息的 hwnd → 就是这个窗口；
///   · <c>RegisterHotKey</c> 的 WM_HOTKEY 会被投递到「创建窗口的那个线程」的消息队列 → 也是这个线程；
///   · explorer 崩溃重启后 shell 会广播 TaskbarCreated，在同一个窗口过程里把图标加回去。
/// 三件事共用一个窗口过程，所以选纯 P/Invoke 而不引第三方托盘库是划算的。
///
/// 线程约定：本类的公开成员都可以从任意线程调用；事件在托盘线程上触发，
/// 订阅方（App）负责用 DispatcherQueue 切回 UI 线程。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    /// <summary>全局划词热键的 id。</summary>
    public const int HotkeyIdCapture = 0xA001;

    /// <summary>弹窗期间的 Esc 热键 id（弹窗不抢焦点，Esc 靠它兜底）。</summary>
    public const int HotkeyIdEscape = 0xA002;

    private const uint TrayIconId = 1;
    private const int CommandShow = 1001;
    private const int CommandCapture = 1002;
    private const int CommandSettings = 1003;
    private const int CommandExit = 1004;
    private const int WM_QUIT_HOST = NativeMethods.WM_APP + 4;
    private const int WM_ESCAPE_HOTKEY = NativeMethods.WM_APP + 5;
    private const int WM_COPYTARGET_SHOW = NativeMethods.WM_APP + 6;
    private const int WM_COPYTARGET_HIDE = NativeMethods.WM_APP + 7;
    private const int WM_COPYTARGET_FOCUS = NativeMethods.WM_APP + 8;
    private const string WindowClassName = "LineTransTrayHostWnd";
    private const string CopyTargetClassName = "LineTransCopyTargetWnd";

    /// <summary>窗口过程必须是静态的，且委托不能被 GC 回收。</summary>
    private static NativeMethods.WndProcDelegate? _wndProcKeepAlive;

    private static NativeMethods.WndProcDelegate? _copyTargetProcKeepAlive;

    private static TrayIconHost? _instance;

    /// <summary>自检用的「复制目标」窗口：模拟一个「前台程序里有选中文字、按 Ctrl+C 会复制」的场景。</summary>
    private readonly object _copyTargetGate = new();
    private IntPtr _copyTargetHwnd;
    private string _copyTargetText = string.Empty;

    private readonly object _stateGate = new();
    private TrayStatus _status = TrayStatus.Empty;

    private Thread? _thread;
    private ManualResetEventSlim? _ready;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private uint _taskbarCreatedMessage;
    private bool _iconAdded;
    private bool _captureHotkeyRegistered;
    private bool _escapeHotkeyRegistered;
    private TraySettings _settings = new();

    /// <summary>按下全局热键（要划词）。</summary>
    public event Action? CaptureRequested;

    /// <summary>弹出查词窗期间按下 Esc。</summary>
    public event Action? EscapePressed;

    /// <summary>托盘菜单「显示主窗口」。</summary>
    public event Action? ShowWindowRequested;

    /// <summary>托盘菜单「设置」。</summary>
    public event Action? SettingsRequested;

    /// <summary>托盘菜单「退出」。</summary>
    public event Action? ExitRequested;

    /// <summary>托盘图标 / 热键状态发生变化。</summary>
    public event Action? StatusChanged;

    /// <summary>最近一次状态快照。</summary>
    public TrayStatus Status => Volatile.Read(ref _status);

    /// <summary>隐藏窗口句柄（还没启动时为 <c>IntPtr.Zero</c>）。</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>启动托盘线程并等待第一轮初始化完成（最多 3 秒）。</summary>
    public void Start(TraySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_thread != null) return;

        _settings = settings.Clone();
        _instance = this;
        _ready = new ManualResetEventSlim(false);

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "LineTrans.TrayHost",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        _ready.Wait(3000);
    }

    /// <summary>重新应用设置（改热键 / 关托盘 / 开关热键）。</summary>
    public void Apply(TraySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Clone();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_APPLY_SETTINGS, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>弹窗显示 / 隐藏时开关「Esc 全局热键」（弹窗不抢焦点，Esc 到不了它）。</summary>
    public void SetEscapeHotkey(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.PostMessageW(_hwnd, WM_ESCAPE_HOTKEY, enabled ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
    }

    // ------------------------------------------------------------------
    // 自检用的「复制目标」窗口
    // ------------------------------------------------------------------

    /// <summary>复制目标窗口的句柄（没显示时是 IntPtr.Zero）。</summary>
    public IntPtr CopyTargetHandle => _copyTargetHwnd;

    /// <summary>
    /// 显示一个「前台程序」：它被置为前台后会响应 Ctrl+C。
    /// <paramref name="text"/> 非空时把这段文字当作「选中的内容」写进剪贴板；
    /// 传空串则什么都不做——正好用来模拟「当前没有选中任何文字」。
    /// </summary>
    public void ShowCopyTarget(string text)
    {
        lock (_copyTargetGate) _copyTargetText = text ?? string.Empty;
        if (_hwnd != IntPtr.Zero) NativeMethods.PostMessageW(_hwnd, WM_COPYTARGET_SHOW, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>销毁复制目标窗口。</summary>
    public void HideCopyTarget()
    {
        if (_hwnd != IntPtr.Zero) NativeMethods.PostMessageW(_hwnd, WM_COPYTARGET_HIDE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>把复制目标窗口推成前台窗口（在托盘线程上执行，然后等结果）。</summary>
    public void FocusCopyTarget()
    {
        if (_hwnd != IntPtr.Zero) NativeMethods.PostMessageW(_hwnd, WM_COPYTARGET_FOCUS, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>冒一个气泡提示（托盘图标没加成功时静默失败）。</summary>
    public void ShowBalloon(string title, string text)
    {
        if (_hwnd == IntPtr.Zero || !_iconAdded) return;
        try
        {
            var data = NewIconData();
            data.uFlags = NativeMethods.NIF_INFO;
            data.szInfoTitle = Trim(title, 63);
            data.szInfo = Trim(text, 255);
            data.dwInfoFlags = NativeMethods.NIIF_INFO;
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
        }
        catch (Exception ex)
        {
            AppServices.Log("托盘气泡提示失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 自检用：向托盘窗口投递一条真正的 <c>WM_HOTKEY</c>。
    /// 走的是和物理按键完全相同的那条消息路径，只是省掉了键盘硬件这一环。
    /// </summary>
    public bool SimulateCaptureHotkey()
    {
        if (_hwnd == IntPtr.Zero) return false;
        return NativeMethods.PostMessageW(
            _hwnd, NativeMethods.WM_HOTKEY, new IntPtr(HotkeyIdCapture), IntPtr.Zero);
    }

    public void Dispose()
    {
        Thread? thread = _thread;
        if (thread == null) return;
        _thread = null;

        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.PostMessageW(_hwnd, WM_QUIT_HOST, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("结束托盘线程失败：" + ex.Message);
        }

        try
        {
            if (!thread.Join(3000)) AppServices.Log("托盘线程未在 3 秒内退出");
        }
        catch (Exception ex)
        {
            AppServices.Log("等待托盘线程退出失败：" + ex.Message);
        }

        _ready?.Dispose();
        _ready = null;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    // ------------------------------------------------------------------
    // 托盘线程
    // ------------------------------------------------------------------

    private void ThreadMain()
    {
        try
        {
            _wndProcKeepAlive = WndProc;
            IntPtr hInstance = NativeMethods.GetModuleHandleW(null);

            var wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                hInstance = hInstance,
                lpszClassName = WindowClassName,
            };
            NativeMethods.RegisterClassExW(ref wc);

            // 造在屏幕外，正常情况不显示；只有弹托盘菜单时会临时 SW_SHOWNOACTIVATE 一下。
            _hwnd = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_TOOLWINDOW,
                WindowClassName,
                "LineTrans.TrayHost",
                0,
                -32000, -32000, 1, 1,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Publish(new TrayStatus(false, null, 0, false, 0,
                    HotkeySpec.DefaultText, HotkeySpec.DefaultText, false,
                    "托盘宿主窗口创建失败，托盘与全局热键都不可用"));
                AppServices.Log("托盘宿主窗口创建失败");
                return;
            }

            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

            ApplySettingsCore();
            _ready?.Set();

            while (true)
            {
                int result = NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
                if (result <= 0) break;
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("托盘线程异常退出：" + ex);
        }
        finally
        {
            _ready?.Set();
            DestroyCopyTargetCore();
            RemoveIcon();
            if (_icon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
            {
                // explorer 重启：图标没了，重新加一次
                _iconAdded = false;
                if (_settings.TrayIconEnabled)
                {
                    AddPublishDetail("资源管理器重启，正在重新注册托盘图标");
                    EnsureIconAdded();
                    Publish(BuildStatus());
                }
                return IntPtr.Zero;
            }

            switch ((int)msg)
            {
                case NativeMethods.WM_TRAYICON:
                    OnTrayMessage((int)lParam);
                    return IntPtr.Zero;

                case NativeMethods.WM_HOTKEY:
                    OnHotkey((int)wParam);
                    return IntPtr.Zero;

                case NativeMethods.WM_APPLY_SETTINGS:
                    ApplySettingsCore();
                    return IntPtr.Zero;

                case WM_ESCAPE_HOTKEY:
                    SetEscapeHotkeyCore(wParam != IntPtr.Zero);
                    return IntPtr.Zero;

                case WM_COPYTARGET_SHOW:
                    ShowCopyTargetCore();
                    return IntPtr.Zero;

                case WM_COPYTARGET_FOCUS:
                    FocusCopyTargetCore();
                    return IntPtr.Zero;

                case WM_COPYTARGET_HIDE:
                    DestroyCopyTargetCore();
                    return IntPtr.Zero;

                case NativeMethods.WM_KEYDOWN:
                case NativeMethods.WM_SYSKEYDOWN:
                    if (TryHandleCopyKey(wParam)) return IntPtr.Zero;
                    break;

                case WM_QUIT_HOST:
                    NativeMethods.DestroyWindow(hWnd);
                    return IntPtr.Zero;

                case NativeMethods.WM_DESTROY:
                    RemoveIcon();
                    NativeMethods.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("托盘消息处理失败（msg=0x" + msg.ToString("X") + "）：" + ex.Message);
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnTrayMessage(int mouseMessage)
    {
        switch (mouseMessage)
        {
            case NativeMethods.WM_LBUTTONUP:
            case NativeMethods.WM_LBUTTONDBLCLK:
                ShowWindowRequested?.Invoke();
                break;

            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_CONTEXTMENU:
                ShowContextMenu();
                break;
        }
    }

    private void OnHotkey(int id)
    {
        if (id == HotkeyIdCapture) CaptureRequested?.Invoke();
        else if (id == HotkeyIdEscape) EscapePressed?.Invoke();
    }

    private void ShowContextMenu()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(CommandShow), "显示主窗口");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(CommandCapture), "全局划词");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(CommandSettings), "设置");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(CommandExit), "退出");
            NativeMethods.SetMenuDefaultItem(menu, (uint)CommandShow, 0);

            if (!NativeMethods.GetCursorPos(out var pt))
            {
                pt = new NativeMethods.POINT { X = 0, Y = 0 };
            }

            // 隐藏窗口平时不显示，这里临时显示（屏幕外）一次，
            // 否则 SetForegroundWindow 不生效，菜单点外面不会自动收起。
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, -32000, -32000, 1, 1,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.SetForegroundWindow(_hwnd);

            int command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);

            NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, -32000, -32000, 1, 1,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);

            switch (command)
            {
                case CommandShow:
                    ShowWindowRequested?.Invoke();
                    break;
                case CommandCapture:
                    CaptureRequested?.Invoke();
                    break;
                case CommandSettings:
                    SettingsRequested?.Invoke();
                    break;
                case CommandExit:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("弹出托盘菜单失败：" + ex.Message);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    // ------------------------------------------------------------------
    // 图标 / 热键
    // ------------------------------------------------------------------

    private void ApplySettingsCore()
    {
        TraySettings settings = _settings;

        if (settings.TrayIconEnabled) EnsureIconAdded();
        else RemoveIcon();

        RegisterCaptureHotkeyCore(settings);
        Publish(BuildStatus());
    }

    private void EnsureIconAdded()
    {
        if (_hwnd == IntPtr.Zero) return;

        if (_icon == IntPtr.Zero) _icon = TrayIconFactory.Create();

        try
        {
            var data = NewIconData();
            data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
            uint message = (uint)(_iconAdded ? NativeMethods.NIM_MODIFY : NativeMethods.NIM_ADD);
            bool ok = NativeMethods.Shell_NotifyIconW(message, ref data);

            _iconAdded = ok;
            _lastShellNotifyResult = ok;
            _lastShellNotifyError = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            AddPublishDetail(ok
                ? "托盘图标已注册"
                : "托盘图标注册失败：" + NativeMethods.DescribeError(_lastShellNotifyError));
        }
        catch (Exception ex)
        {
            _iconAdded = false;
            _lastShellNotifyResult = false;
            _lastShellNotifyError = -1;
            AddPublishDetail("托盘图标注册异常：" + ex.Message);
        }
    }

    private void RemoveIcon()
    {
        if (_hwnd == IntPtr.Zero || !_iconAdded) return;
        try
        {
            var data = NewIconData();
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
        }
        catch
        {
            // 退出路径上删图标失败无所谓
        }
        _iconAdded = false;
    }

    private void RegisterCaptureHotkeyCore(TraySettings settings)
    {
        if (_captureHotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyIdCapture);
            _captureHotkeyRegistered = false;
        }

        _hotkeyRegistered = false;
        _hotkeyError = 0;
        _hotkeyText = settings.Hotkey;
        _configuredHotkeyText = settings.Hotkey;

        if (!settings.HotkeyEnabled)
        {
            AddPublishDetail("全局热键已停用");
            return;
        }

        if (!HotkeySpec.TryParse(settings.Hotkey, out var primary, out string parseError))
        {
            _hotkeyError = -1;
            AddPublishDetail("热键 \"" + settings.Hotkey + "\" 无法识别：" + parseError
                + "，请在「设置 → 托盘与全局划词」里改一个");
            return;
        }

        _configuredHotkeyText = primary.Text;
        _hotkeyText = primary.Text;

        var candidates = new List<string> { primary.Text };
        foreach (string fallback in HotkeySpec.Fallbacks)
        {
            bool duplicate = false;
            foreach (string existing in candidates)
            {
                if (string.Equals(existing, fallback, StringComparison.OrdinalIgnoreCase)) { duplicate = true; break; }
            }
            if (!duplicate) candidates.Add(fallback);
        }

        int primaryError = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!HotkeySpec.TryParse(candidates[i], out var spec, out _)) continue;

            bool ok = NativeMethods.RegisterHotKey(
                _hwnd, HotkeyIdCapture, spec.Modifiers | NativeMethods.MOD_NOREPEAT, spec.VirtualKey);

            if (ok)
            {
                _captureHotkeyRegistered = true;
                _hotkeyRegistered = true;
                _hotkeyError = 0;
                _hotkeyText = spec.Text;

                if (i == 0)
                {
                    AddPublishDetail("全局热键 " + spec.Text + " 已注册");
                }
                else
                {
                    AddPublishDetail("全局热键 " + primary.Text + " 注册失败："
                        + NativeMethods.DescribeError(primaryError) + "；已自动改用备用组合 " + spec.Text
                        + "（可在「设置 → 托盘与全局划词」里改回来）");
                    if (_iconAdded)
                    {
                        ShowBalloon("逐行翻译", "热键 " + primary.Text + " 被别的程序占用了，已自动改用 "
                            + spec.Text + "。可以到「设置 → 托盘与全局划词」里换成别的组合。");
                    }
                }
                return;
            }

            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (i == 0) primaryError = error;
        }

        _hotkeyError = primaryError == 0 ? -1 : primaryError;
        AddPublishDetail("全局热键 " + primary.Text + " 注册失败：" + NativeMethods.DescribeError(_hotkeyError)
            + "；备用组合也都被占用了，请在「设置 → 托盘与全局划词」里换一个组合键");
        if (_iconAdded)
        {
            ShowBalloon("逐行翻译", "全局热键 " + primary.Text + " 注册失败："
                + NativeMethods.DescribeError(_hotkeyError) + "，请在设置里换一个组合键。");
        }
    }

    private void ShowCopyTargetCore()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_copyTargetHwnd != IntPtr.Zero) return;

        try
        {
            IntPtr hInstance = NativeMethods.GetModuleHandleW(null);

            if (_copyTargetProcKeepAlive == null)
            {
                _copyTargetProcKeepAlive = CopyTargetProc;
                var wc = new NativeMethods.WNDCLASSEX
                {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = System.Runtime.InteropServices.Marshal
                        .GetFunctionPointerForDelegate(_copyTargetProcKeepAlive),
                    hInstance = hInstance,
                    lpszClassName = CopyTargetClassName,
                };
                NativeMethods.RegisterClassExW(ref wc);
            }

            // 和托盘宿主一样摆在屏幕外：它是「前台窗口」，但不需要真的出现在用户眼前
            _copyTargetHwnd = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_TOOLWINDOW,
                CopyTargetClassName,
                "LineTrans.SelftestCopyTarget",
                0,
                -32000, -32000, 320, 200,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_copyTargetHwnd == IntPtr.Zero)
            {
                AppServices.Log("自检：复制目标窗口创建失败");
                return;
            }

            NativeMethods.ShowWindow(_copyTargetHwnd, NativeMethods.SW_SHOWNOACTIVATE);
            AppServices.Log("自检：复制目标窗口已创建 0x" + _copyTargetHwnd.ToString("X"));
        }
        catch (Exception ex)
        {
            AppServices.Log("自检：创建复制目标窗口异常：" + ex.Message);
        }
    }

    private void FocusCopyTargetCore()
    {
        if (_copyTargetHwnd == IntPtr.Zero) return;
        NativeMethods.ShowWindow(_copyTargetHwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetForegroundWindow(_copyTargetHwnd);
        NativeMethods.SetFocus(_copyTargetHwnd);
    }

    private void DestroyCopyTargetCore()
    {
        if (_copyTargetHwnd == IntPtr.Zero) return;
        NativeMethods.ShowWindow(_copyTargetHwnd, NativeMethods.SW_HIDE);
        NativeMethods.DestroyWindow(_copyTargetHwnd);
        _copyTargetHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// 复制目标窗口 / 托盘窗口收到 Ctrl+C 时，把「选中的文字」用 Win32 剪贴板 API 写进去。
    /// 这是在模拟「一个前台程序响应 Ctrl+C」——是自检里唯一一处「假的」环节，
    /// 但它走的仍然是真实的系统输入队列与真实剪贴板。
    /// </summary>
    private bool TryHandleCopyKey(IntPtr wParam)
    {
        if ((int)wParam != 'C') return false;
        if ((NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) == 0) return false;

        string text;
        lock (_copyTargetGate) text = _copyTargetText;

        if (text.Length == 0)
        {
            // 模拟「没有选中任何文字」：什么都不做，剪贴板保持原样
            AppServices.Log("自检：复制目标收到 Ctrl+C，但没有选中内容，剪贴板保持原样");
            return true;
        }

        bool ok = WriteClipboardText(text);
        AppServices.Log("自检：复制目标收到 Ctrl+C，已把 " + text.Length + " 字写入剪贴板（"
            + (ok ? "成功" : "失败") + "）");
        return true;
    }

    private static bool WriteClipboardText(string text)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    NativeMethods.EmptyClipboard();
                    IntPtr memory = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(text);
                    if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, memory) != IntPtr.Zero)
                    {
                        // 数据交给系统了，不能再自己释放
                        return true;
                    }
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }
            Thread.Sleep(30);
        }
        return false;
    }

    /// <summary>复制目标窗口的窗口过程：只关心 Ctrl+C。</summary>
    private IntPtr CopyTargetProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
            {
                if (TryHandleCopyKey(wParam)) return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("复制目标窗口消息处理失败：" + ex.Message);
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void SetEscapeHotkeyCore(bool enabled)
    {
        if (enabled)
        {
            if (_escapeHotkeyRegistered) return;
            _escapeHotkeyRegistered = NativeMethods.RegisterHotKey(
                _hwnd, HotkeyIdEscape, 0, NativeMethods.VK_ESCAPE);
        }
        else
        {
            if (!_escapeHotkeyRegistered) return;
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyIdEscape);
            _escapeHotkeyRegistered = false;
        }
        Publish(BuildStatus());
    }

    // ------------------------------------------------------------------
    // 状态
    // ------------------------------------------------------------------

    private bool? _lastShellNotifyResult;
    private int _lastShellNotifyError;
    private bool _hotkeyRegistered;
    private int _hotkeyError;
    private string _hotkeyText = HotkeySpec.DefaultText;
    private string _configuredHotkeyText = HotkeySpec.DefaultText;
    private string _detail = string.Empty;

    private void AddPublishDetail(string line)
    {
        _detail = _detail.Length == 0 ? line : _detail + "；" + line;
    }

    private TrayStatus BuildStatus()
    {
        string detail = _detail;
        _detail = string.Empty;
        return new TrayStatus(
            _iconAdded,
            _lastShellNotifyResult,
            _lastShellNotifyError,
            _hotkeyRegistered,
            _hotkeyError,
            _hotkeyText,
            _configuredHotkeyText,
            _escapeHotkeyRegistered,
            detail.Length == 0 ? "托盘已就绪" : detail);
    }

    private void Publish(TrayStatus status)
    {
        TraceStatus(status);
        lock (_stateGate)
        {
            Volatile.Write(ref _status, status);
        }
        StatusChanged?.Invoke();
    }

    private static void TraceStatus(TrayStatus status)
    {
        AppServices.Log("托盘状态：图标=" + (status.TrayIconAdded ? "已注册" : "未注册")
            + "（Shell_NotifyIcon 返回值 " + (status.ShellNotifyIconResult?.ToString() ?? "未调用") + "，"
            + "错误码 " + status.ShellNotifyIconError + "）"
            + "，热键=" + (status.HotkeyRegistered ? status.HotkeyText + " 已注册" : "未注册")
            + "（配置值 " + status.ConfiguredHotkeyText
            + "，RegisterHotKey 错误码 " + status.HotkeyError + "）"
            + "，Esc 热键=" + (status.EscapeHotkeyRegistered ? "开" : "关")
            + "；" + status.Message);
    }

    private NativeMethods.NOTIFYICONDATA NewIconData() => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        uCallbackMessage = (uint)NativeMethods.WM_TRAYICON,
        hIcon = _icon,
        szTip = "逐行翻译",
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Trim(string? text, int max)
    {
        string value = text ?? string.Empty;
        return value.Length <= max ? value : value.Substring(0, max);
    }
}
