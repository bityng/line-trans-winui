using System;
using System.Runtime.InteropServices;

using LineTrans.App.Interop;
using LineTrans.App.Services;
using LineTrans.App.Views;

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

using Windows.Graphics;

using WinRT.Interop;

namespace LineTrans.App;

/// <summary>
/// 主窗口：自定义标题栏 + NavigationView 导航壳（文档 / 翻译 / 设置 / 关于）。
/// 同时作为全局窗口句柄提供者（文件选择器需要 HWND）与主题应用者。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>默认窗口尺寸（DIP），按当前 DPI 换算成物理像素。</summary>
    private const int DefaultWidthDip = 1280;
    private const int DefaultHeightDip = 800;

    /// <summary>最小窗口尺寸（DIP）。再小的话导航栏 + 内容列会挤成一团。</summary>
    private const int MinWidthDip = 900;
    private const int MinHeightDip = 600;

    /// <summary>防止「钳制尺寸 -> 触发 Changed -> 再钳制」的递归。</summary>
    private bool _clampingSize;

    /// <summary>下一次导航要带给页面的参数（例如文档 id）。</summary>
    private object? _navParameter;

    /// <summary>当前窗口实例（文件选择器 / 页面跳转要用）。</summary>
    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        InitializeComponent();

        Instance = this;
        Title = "逐行翻译";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        double scale = GetScaleFactor();
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(DefaultWidthDip * scale),
            (int)Math.Round(DefaultHeightDip * scale)));

        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnAppWindowClosing;

        ApplyTheme();
        AppServices.SettingsRepo.Changed += ApplyTheme;

        RootNav.SelectedItem = NavHome;
        NavigateTo("home");
    }

    /// <summary>窗口句柄（文件选择器初始化要用）。</summary>
    public IntPtr Handle => WindowNative.GetWindowHandle(this);

    /// <summary>把主题与全局正文字号应用到窗口内容。</summary>
    public void ApplyTheme()
    {
        if (Content is FrameworkElement root)
        {
            AppServices.ApplyTheme(root);
        }

        // FontSize 是继承属性：设在导航壳上可以灌到所有未显式指定字号的文本。
        RootNav.FontSize = AppServices.BodyFontSize;
    }

    /// <summary>打开某篇文档并切到翻译页。</summary>
    public void OpenDocument(string docId)
    {
        _navParameter = docId;

        if (!ReferenceEquals(RootNav.SelectedItem, NavTranslation))
        {
            // 赋值会触发 OnNavSelectionChanged -> NavigateTo，参数在那里被取走
            RootNav.SelectedItem = NavTranslation;
        }
        else
        {
            NavigateTo("translation");
        }
    }

    /// <summary>导航项切换 -> 换页。</summary>
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            NavigateTo(tag);
        }
    }

    /// <summary>按 Tag 导航到对应页面；已经在该页且没有新参数就不重复导航（避免重建页面状态）。</summary>
    private void NavigateTo(string tag)
    {
        Type pageType = tag switch
        {
            "translation" => typeof(TranslationPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(HomePage),
        };

        object? parameter = _navParameter;
        _navParameter = null;

        if (ContentFrame.CurrentSourcePageType != pageType || parameter != null)
        {
            ContentFrame.Navigate(pageType, parameter, new EntranceNavigationTransitionInfo());
        }
    }

    // ------------------------------------------------------------------
    // 托盘
    // ------------------------------------------------------------------

    /// <summary>
    /// 关闭窗口时的行为由托盘设置决定：
    ///   · 托盘开着 + 选「最小化到托盘」→ 取消关闭，藏进托盘继续在后台跑；
    ///   · 其余情况放行，走正常的退出流程。
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.IsExitingNow) return;

        var settings = TraySettingsStore.Current;
        if (!settings.TrayIconEnabled) return;
        if (!string.Equals(settings.CloseAction, TraySettings.CloseToTray, StringComparison.OrdinalIgnoreCase)) return;

        args.Cancel = true;
        HideToTray();
    }

    /// <summary>把主窗口藏进托盘。</summary>
    public void HideToTray()
    {
        try
        {
            AppWindow.Hide();
            AppServices.Log("主窗口已最小化到托盘");

            // 第一次收进托盘时给个气泡提示，否则用户会以为程序已经退出了
            if (!TraySettingsStore.Current.TrayHintShown && App.Tray?.Status.TrayIconAdded == true)
            {
                TraySettingsStore.Update(s => s.TrayHintShown = true);
                App.Tray?.ShowBalloon("逐行翻译仍在后台运行",
                    "窗口已经收进托盘。右键托盘图标可以「显示主窗口 / 全局划词 / 设置 / 退出」。");
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("最小化到托盘失败：" + ex.Message);
        }
    }

    /// <summary>从托盘恢复主窗口并抢到前台。</summary>
    public void ShowFromTray()
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);

            if (AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            AppWindow.Show();
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
            Activate();
            NativeMethods.SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            AppServices.Log("从托盘恢复主窗口失败：" + ex.Message);
        }
    }

    /// <summary>托盘菜单「设置」：切到设置页并把窗口叫出来。</summary>
    public void ShowSettingsPage()
    {
        RootNav.SelectedItem = NavSettings;
        ShowFromTray();
    }

    /// <summary>钳制窗口最小尺寸。AppWindow.Size 是物理像素，所以要乘 DPI 缩放。</summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _clampingSize)
        {
            return;
        }

        double scale = GetScaleFactor();
        SizeInt32 size = sender.Size;
        int minWidth = (int)Math.Round(MinWidthDip * scale);
        int minHeight = (int)Math.Round(MinHeightDip * scale);

        int width = Math.Max(size.Width, minWidth);
        int height = Math.Max(size.Height, minHeight);

        if (width != size.Width || height != size.Height)
        {
            _clampingSize = true;
            try
            {
                sender.Resize(new SizeInt32(width, height));
            }
            finally
            {
                _clampingSize = false;
            }
        }
    }

    /// <summary>当前窗口的 DPI 缩放系数（96 DPI = 1.0）。取不到就按 1.0 处理。</summary>
    private double GetScaleFactor()
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            uint dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
