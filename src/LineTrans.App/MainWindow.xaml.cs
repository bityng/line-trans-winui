using System;
using System.Runtime.InteropServices;

using LineTrans.App.Interop;
using LineTrans.App.Services;
using LineTrans.App.Views;

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Graphics;

using WinRT.Interop;

namespace LineTrans.App;

/// <summary>
/// 主窗口：原生标题栏 + NavigationView 导航壳（文档 / 翻译 / 关于 / 设置）。
/// 同时作为全局窗口句柄提供者（文件选择器需要 HWND）、主题应用者与背景材质持有者。
///
/// 原生化改造要点：
///   · ExtendsContentIntoTitleBar + SetTitleBar：自绘标题栏，但右侧按 AppWindow.TitleBar.RightInset
///     留出系统按钮区的空白，避免内容被最小化 / 最大化 / 关闭盖住；
///   · 背景材质（Mica / Mica Alt / 亚克力 / 无）见 <see cref="BackdropService"/>；
///   · 强调色（跟随系统 / 品牌蓝）见 <see cref="AccentService"/>；
///   · 页面切换用 Frame 的原生导航过渡，不自造动画。
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

    /// <summary>窗口内容根元素（缓存下来，避免窗口关掉之后再读 <c>Window.Content</c> 抛异常）。</summary>
    private FrameworkElement? _themeRoot;

    /// <summary>窗口是否已经关闭。关闭之后再碰 WinUI 的窗口 API 会抛 COMException。</summary>
    private bool _windowClosed;

    /// <summary>下一次导航要带给页面的参数（例如文档 id）。</summary>
    private object? _navParameter;

    /// <summary>当前窗口实例（文件选择器 / 页面跳转要用）。</summary>
    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        InitializeComponent();

        Instance = this;
        Title = "逐行翻译";

        ApplyAppIcon();
        SetupTitleBar();

        double scale = GetScaleFactor();
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(DefaultWidthDip * scale),
            (int)Math.Round(DefaultHeightDip * scale)));

        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnAppWindowClosing;

        ApplyTheme();
        ApplyBackdrop();

        // 系统浅色/深色切换、以及用户改背景材质 / 强调色 / 布局，都从这里统一刷新。
        //
        // 这里有个必须防住的坑（实测崩过）：ActualThemeChanged 是异步派发的，
        // 窗口关掉之后它仍可能再触发一次。那时 ApplyTheme 里若再去读 Window.Content，
        // WinUI 会抛 COMException 0x800710DD「The WinUI Desktop Window object has already been closed.」，
        // 而事件回调里的异常会被 XAML 升级成 fail-fast —— 进程以 0xC0000409 退出，
        // 事件查看器里留一条 APPCRASH（Microsoft.UI.Xaml.dll / 0xc000027b）。
        // 所以：缓存根元素 + 记 Closed 状态 + 整段包 try。
        if (Content is FrameworkElement root)
        {
            _themeRoot = root;
            root.ActualThemeChanged += (_, _) => ApplyTheme();
        }
        Closed += (_, _) => _windowClosed = true;

        AppServices.SettingsRepo.Changed += ApplyTheme;
        UiSettingsStore.Changed += OnUiSettingsChanged;

        RootNav.SelectedItem = NavHome;
        NavigateTo("home");
    }

    /// <summary>窗口句柄（文件选择器初始化要用）。</summary>
    public IntPtr Handle => WindowNative.GetWindowHandle(this);

    /// <summary>当前内容页类型（自检 / 截图取证用）。</summary>
    public Type? CurrentPageType => ContentFrame.CurrentSourcePageType;

    /// <summary>当前承载的页面实例（自检 / 截图取证用）。</summary>
    public object? CurrentPage => ContentFrame.Content;

    // ------------------------------------------------------------------
    // 图标
    // ------------------------------------------------------------------

    /// <summary>
    /// 给窗口装上应用图标。exe 资源段里那份（csproj ApplicationIcon）已经管住任务栏与 Alt+Tab，
    /// 这里显式再设一次，覆盖「固定到任务栏」与部分非打包场景下退回系统默认图标的情况。
    /// </summary>
    private void ApplyAppIcon()
    {
        try
        {
            string path = AppServices.IconPath;
            if (File.Exists(path))
            {
                AppWindow.SetIcon(path);
                return;
            }

            AppServices.Log("没找到应用图标文件（" + path + "），窗口沿用 exe 内嵌图标。");
        }
        catch (Exception ex)
        {
            AppServices.Log("设置窗口图标失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // 标题栏
    // ------------------------------------------------------------------

    /// <summary>自绘标题栏：整条可拖拽，右侧留出系统按钮区的宽度。</summary>
    private void SetupTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        UpdateTitleBarInset();
    }

    /// <summary>
    /// 把标题栏右侧留白（DIP）对齐到系统的窗口按钮区宽度。
    /// AppWindow.TitleBar.RightInset 是物理像素，XAML 用的是 DIP，所以要按 DPI 折算；
    /// 最大化 / 还原、换显示器（DPI 变化）后都要重新算。
    /// </summary>
    private void UpdateTitleBarInset()
    {
        try
        {
            double scale = GetScaleFactor();
            double inset = AppWindow.TitleBar.RightInset / scale;
            TitleBarInsetColumn.Width = new GridLength(Math.Max(0, inset));
        }
        catch (Exception ex)
        {
            AppServices.Log("计算标题栏右侧留白失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // 主题 / 材质 / 强调色
    // ------------------------------------------------------------------

    /// <summary>
    /// 把主题、字号缩放与强调色应用到窗口内容。
    /// 绝不抛异常：退出流程里 <c>SettingsRepo.Changed</c> / <c>ActualThemeChanged</c>
    /// 仍可能回调进来，抛出去就是一次 fail-fast（见构造函数里的坑记录）。
    /// </summary>
    public void ApplyTheme()
    {
        if (_windowClosed) return;

        try
        {
            var root = _themeRoot ?? Content as FrameworkElement;
            if (root != null)
            {
                AppServices.ApplyTheme(root);
            }

            // FontSize 是继承属性：设在导航壳上可以灌到所有未显式指定字号的文本。
            RootNav.FontSize = AppServices.BodyFontSize;

            // 强调色要跟着浅色/深色走（浅色主题的悬停色更深、深色主题更亮）
            AccentService.Apply(root);
        }
        catch (Exception ex)
        {
            AppServices.Log("应用主题失败（窗口可能正在关闭）：" + ex.Message);
        }
    }

    /// <summary>按设置应用窗口背景材质（含能力检测与回落）。同样不抛异常。</summary>
    public void ApplyBackdrop()
    {
        if (_windowClosed) return;

        try
        {
            BackdropService.Apply(this, OpaqueFallback, RootNav);
        }
        catch (Exception ex)
        {
            AppServices.Log("应用背景材质失败（窗口可能正在关闭）：" + ex.Message);
        }
    }

    private void OnUiSettingsChanged()
    {
        ApplyBackdrop();
        ApplyTheme();
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

    /// <summary>
    /// 按 Tag 导航到对应页面；已经在该页且没有新参数就不重复导航（避免重建页面状态）。
    /// 过渡动画交给 Frame 的默认实现（NavigationView 页面切换用的就是它），不自造。
    /// </summary>
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
            ContentFrame.Navigate(pageType, parameter);
        }
    }

    /// <summary>供自检 / 截图取证用：按 Tag 切页。</summary>
    public void NavigateToTagForProbe(string tag) => NavigateTo(tag);

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
        if (_windowClosed) return;

        if (args.DidPresenterChange || args.DidPositionChange)
        {
            // 最大化 / 还原会让系统按钮区宽度变化，标题栏留白要跟着重算
            UpdateTitleBarInset();
        }

        if (!args.DidSizeChange || _clampingSize)
        {
            return;
        }

        UpdateTitleBarInset();

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
