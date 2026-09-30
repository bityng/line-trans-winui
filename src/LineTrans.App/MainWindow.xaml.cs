using System;
using System.Runtime.InteropServices;
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

    public MainWindow()
    {
        InitializeComponent();

        Title = "逐行翻译";

        // 自定义标题栏：内容延伸到标题栏区域，交互区交给 AppTitleBar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 标题栏按钮透明，融进窗口背景（深浅色都成立）
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        // 默认 1280x800
        double scale = GetScaleFactor();
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(DefaultWidthDip * scale),
            (int)Math.Round(DefaultHeightDip * scale)));

        AppWindow.Changed += OnAppWindowChanged;

        // 启动即落在「文档」，保持导航项高亮与页面一致
        RootNav.SelectedItem = NavHome;
        NavigateTo("home");
    }

    /// <summary>导航项切换 -> 换页。</summary>
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            NavigateTo(tag);
        }
    }

    /// <summary>按 Tag 导航到对应页面；已经在该页就不重复导航（避免重建页面状态）。</summary>
    private void NavigateTo(string tag)
    {
        Type pageType = tag switch
        {
            "translation" => typeof(TranslationPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(HomePage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
        }
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
