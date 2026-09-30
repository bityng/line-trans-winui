using System;

using LineTrans.App.Interop;
using LineTrans.App.Services;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

using WinRT.Interop;

namespace LineTrans.App.Views;

/// <summary>
/// 全局划词的弹窗。
///
/// 三个刻意的设计：
///   · 单实例：<see cref="Current"/> 永远只有一个，连续按热键只会刷新内容，不会叠出一堆窗；
///   · 不抢焦点：窗口带 <c>WS_EX_NOACTIVATE</c>，Show 完仍是「用户原来的程序」在前台，
///     选中内容不会被取消，也不会把用户正在打的字吞进弹窗；
///   · 置顶：不激活的窗口如果不置顶，可能被前台窗口盖住，所以用 HWND_TOPMOST 抬到最上层。
///
/// 因为不抢焦点，键盘事件到不了这里，Esc 由托盘线程注册的「Esc 全局热键」兜底转发。
/// </summary>
public sealed partial class LookupPopupWindow : Window
{
    private const int WidthDip = 440;
    private const int MinHeightDip = 160;
    private const int MaxHeightDip = 480;
    private const int FallbackHeightDip = 300;

    private const int HWND_TOPMOST = -1;

    private static LookupPopupWindow? _current;

    private bool _shown;
    private LookupPopupContent _content = new();

    private LookupPopupWindow()
    {
        InitializeComponent();
        Title = "逐行翻译 · 划词";
        RootGrid.KeyDown += OnRootKeyDown;
    }

    /// <summary>
    /// 当前弹窗（没有时为 null）。故意不叫 Current：WinUI 的 Window 自带一个静态 Current，撞名会被编译器警告。
    /// </summary>
    public static LookupPopupWindow? ActivePopup => _current;

    /// <summary>弹窗上正在显示的原文（自检读取用）。</summary>
    public string RenderedCaptured => CapturedText.Text ?? string.Empty;

    /// <summary>弹窗上正在显示的标题（自检读取用）。</summary>
    public string RenderedHeadline => HeadlineText.Text ?? string.Empty;

    /// <summary>弹窗上正在显示的正文（自检读取用）。</summary>
    public string RenderedBody => BodyText.Text ?? string.Empty;

    /// <summary>弹窗上正在显示的来源徽章（自检读取用）。</summary>
    public string RenderedBadge => BadgeText.Text ?? string.Empty;

    /// <summary>弹窗上正在显示的音标（自检读取用）。</summary>
    public string RenderedPhonetic => PhoneticText.Text ?? string.Empty;

    /// <summary>弹窗是否真的可见（自检读取用）。</summary>
    public bool IsPopupVisible
    {
        get
        {
            try
            {
                return NativeMethods.IsWindowVisible(WindowNative.GetWindowHandle(this));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>显示或刷新弹窗。同一时刻只会有一个。</summary>
    public static void ShowOrUpdate(LookupPopupContent content)
    {
        if (content == null) return;

        try
        {
            if (_current == null)
            {
                var window = new LookupPopupWindow();
                window.SetupWindow();
                window.Closed += OnPopupClosed;
                _current = window;
            }

            _current.Apply(content);
            _current.Present();
        }
        catch (Exception ex)
        {
            AppServices.Log("显示划词弹窗失败：" + ex);
            _current = null;
        }
    }

    /// <summary>关闭当前弹窗（没有则什么都不做）。</summary>
    public static void CloseCurrent()
    {
        try
        {
            _current?.Close();
        }
        catch (Exception ex)
        {
            AppServices.Log("关闭划词弹窗失败：" + ex.Message);
            _current = null;
        }
    }

    private static void OnPopupClosed(object sender, WindowEventArgs args)
    {
        _current = null;
        App.Tray?.SetEscapeHotkey(false);
        AppServices.Log("划词弹窗已关闭");
    }

    // ------------------------------------------------------------------
    // 窗口
    // ------------------------------------------------------------------

    private void SetupWindow()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }

            AppWindow.IsShownInSwitchers = false;

            // WS_EX_NOACTIVATE：显示时不抢前台焦点；WS_EX_TOOLWINDOW：不进 Alt+Tab
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            int exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt32();
            NativeMethods.SetWindowLongPtr(
                hwnd,
                NativeMethods.GWL_EXSTYLE,
                new IntPtr(exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
        }
        catch (Exception ex)
        {
            AppServices.Log("初始化划词弹窗样式失败：" + ex.Message);
        }
    }

    private void Apply(LookupPopupContent content)
    {
        _content = content;

        CapturedText.Text = content.Captured.Length > 0 && content.Captured != content.Headline
            ? content.Captured
            : string.Empty;
        CapturedText.Visibility = CapturedText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        HeadlineText.Text = content.Headline;
        PhoneticText.Text = content.Phonetic;
        PhoneticText.Visibility = content.Phonetic.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BadgeText.Text = content.Badge;
        BodyText.Text = content.Body;
        StatusText.Text = content.Status;

        AddWordButton.Visibility = content.CanAddToWordbook ? Visibility.Visible : Visibility.Collapsed;
        CopyResultButton.IsEnabled = !content.IsLoading && content.Body.Length > 0;
        CopySourceButton.IsEnabled = content.Captured.Length > 0;
    }

    private void Present()
    {
        try
        {
            AppServices.ApplyTheme(RootGrid);

            double scale = GetScale();
            double heightDip = MeasureHeightDip();
            int width = (int)Math.Round(WidthDip * scale);
            int height = (int)Math.Round(heightDip * scale);
            RectInt32 rect = PlaceNearCursor(width, height);

            AppWindow.MoveAndResize(rect);

            // Activate() 负责让 WinUI 真正把内容渲染出来；
            // 带 WS_EX_NOACTIVATE 的窗口不会因此抢走前台焦点。
            Activate();

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            NativeMethods.SetWindowPos(
                hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            if (!_shown)
            {
                _shown = true;
                App.Tray?.SetEscapeHotkey(true);
                AppServices.Log("划词弹窗已显示：" + rect.X + "," + rect.Y + " " + rect.Width + "x" + rect.Height);
            }
            else
            {
                App.Tray?.SetEscapeHotkey(true);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("显示划词弹窗失败：" + ex.Message);
        }
    }

    private double MeasureHeightDip()
    {
        try
        {
            RootGrid.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
            double desired = RootGrid.DesiredSize.Height;
            if (double.IsNaN(desired) || double.IsInfinity(desired) || desired <= 0) return FallbackHeightDip;
            return Math.Clamp(desired, MinHeightDip, MaxHeightDip);
        }
        catch
        {
            return FallbackHeightDip;
        }
    }

    private RectInt32 PlaceNearCursor(int width, int height)
    {
        NativeMethods.GetCursorPos(out var cursor);

        int left = cursor.X + 14;
        int top = cursor.Y + 20;

        try
        {
            DisplayArea area = DisplayArea.GetFromPoint(
                new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
            RectInt32 work = area.WorkArea;

            if (left + width > work.X + work.Width) left = cursor.X - width - 14;
            if (top + height > work.Y + work.Height) top = cursor.Y - height - 14;

            left = Math.Clamp(left, work.X, Math.Max(work.X, work.X + work.Width - width));
            top = Math.Clamp(top, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        }
        catch (Exception ex)
        {
            AppServices.Log("计算弹窗位置失败（按光标原样放置）：" + ex.Message);
        }

        return new RectInt32(left, top, width, height);
    }

    private double GetScale()
    {
        try
        {
            uint dpi = NativeMethods.GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    // ------------------------------------------------------------------
    // 交互
    // ------------------------------------------------------------------

    /// <summary>焦点万一落在弹窗上（用户点了它）时的本地 Esc 处理；正常情况下走全局 Esc 热键。</summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCopyResultClick(object sender, RoutedEventArgs e)
        => Copy(_content.Body, "结果");

    private void OnCopySourceClick(object sender, RoutedEventArgs e)
        => Copy(_content.Captured, "原文");

    private void Copy(string text, string label)
    {
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Text = "没有可复制的" + label + "。";
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            StatusText.Text = "已复制" + label + "（" + text.Length + " 字）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnAddWordClick(object sender, RoutedEventArgs e)
    {
        string term = _content.Headline.Length > 0 ? _content.Headline : _content.Captured;
        if (term.Length == 0)
        {
            StatusText.Text = "没有可收藏的词。";
            return;
        }

        if (_content.Body.Length == 0 || _content.IsLoading)
        {
            StatusText.Text = "还没有查到释义，先等查询完成。";
            return;
        }

        try
        {
            AppServices.Words.Upsert(term, _content.Body, _content.Phonetic);
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
