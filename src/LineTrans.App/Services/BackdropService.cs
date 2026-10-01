using System;

using LineTrans.Core;

using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LineTrans.App.Services;

/// <summary>
/// 窗口背景材质：Mica / Mica Alt / 亚克力 / 无。
///
/// 三个关键点（很容易做错）：
///   1) 能力检测：用 MicaController.IsSupported() / DesktopAcrylicController.IsSupported() 先问系统。
///      Win10 或用户关掉「透明效果」时不支持——这时自动回落到「无」，
///      不崩、也不留一块黑底（内容层同时切到不透明的系统窗口底色）。
///   2) 材质只在【内容不铺满不透明背景】时才看得见。所以有材质时内容层必须是半透明的
///      （页面根用 LayerFillColorDefaultBrush），否则 SystemBackdrop 设了也白设。
///      对应地，NavigationView 的窗格背景也要跟着换（它默认是不透明的）
///      —— 有材质时换成 Fluent 的半透明层，没材质时换成不透明的系统底色。
///   3) 「无」的时候要把不透明底兜回来，不然 Mica 撤掉后窗口后面就是桌面透出来的黑。
/// </summary>
public static class BackdropService
{
    /// <summary>设置里要求使用的材质（规范化后的原始取值）。</summary>
    public static string Requested { get; private set; } = AppSettings.BackdropMica;

    /// <summary>实际生效的材质（不支持时是 none）。</summary>
    public static string Effective { get; private set; } = AppSettings.BackdropNone;

    /// <summary>最近一次应用材质时的中文说明（回落原因等），没有问题时为空。</summary>
    public static string LastNote { get; private set; } = string.Empty;

    /// <summary>本机 / 本 Windows 版本是否支持云母。</summary>
    public static bool MicaSupported => MicaController.IsSupported();

    /// <summary>本机 / 本 Windows 版本是否支持亚克力。</summary>
    public static bool AcrylicSupported => DesktopAcrylicController.IsSupported();

    /// <summary>
    /// 按当前设置应用背景材质。
    /// </summary>
    /// <param name="window">主窗口。</param>
    /// <param name="opaqueFallback">「无材质」时显示的不透明兜底层（盖住窗口后面的桌面）。</param>
    /// <param name="nav">导航壳；窗格背景要跟着材质一起换，否则云母只会在内容区露出一条。</param>
    public static void Apply(Window? window, FrameworkElement? opaqueFallback, NavigationView? nav)
    {
        if (window == null) return;

        string requested = AppSettings.NormalizeBackdropMaterial(UiSettingsStore.Current.BackdropMaterial);
        string effective = requested;
        string note = string.Empty;

        switch (requested)
        {
            case AppSettings.BackdropMica:
            case AppSettings.BackdropMicaAlt:
                if (!MicaSupported)
                {
                    effective = AppSettings.BackdropNone;
                    note = "本机不支持云母（MicaController.IsSupported() = False，Win10 或系统里关掉了透明效果），已回落到「无」。";
                }
                break;

            case AppSettings.BackdropAcrylic:
                if (!AcrylicSupported)
                {
                    effective = AppSettings.BackdropNone;
                    note = "本机不支持亚克力（DesktopAcrylicController.IsSupported() = False），已回落到「无」。";
                }
                break;

            default:
                effective = AppSettings.BackdropNone;
                break;
        }

        try
        {
            window.SystemBackdrop = effective switch
            {
                AppSettings.BackdropMica => new MicaBackdrop { Kind = MicaKind.Base },
                AppSettings.BackdropMicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
                AppSettings.BackdropAcrylic => new DesktopAcrylicBackdrop(),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            effective = AppSettings.BackdropNone;
            note = "设置背景材质失败（" + ex.Message + "），已回落到「无」。";
            try { window.SystemBackdrop = null; } catch { /* 回落失败就保持原样 */ }
        }

        if (opaqueFallback != null)
        {
            opaqueFallback.Visibility = effective == AppSettings.BackdropNone
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        ApplyPaneBackground(nav, effective != AppSettings.BackdropNone);

        Requested = requested;
        Effective = effective;
        LastNote = note;

        AppServices.Log("背景材质：请求 " + requested + "，实际 " + effective
            + "（Mica 支持 " + MicaSupported + "，亚克力支持 " + AcrylicSupported + "）"
            + (note.Length > 0 ? "；" + note : string.Empty));
    }

    /// <summary>
    /// 换掉 NavigationView 窗格背景。
    /// 默认值是不透明的，会把云母整个盖住；有材质时换成 Fluent 的半透明层（LayerFillColorDefaultBrush），
    /// 没材质时换成不透明的系统窗口底色（SolidBackgroundFillColorBaseBrush）。
    /// 两个刷子都借 WinUI 自带的，不自己调色。
    /// </summary>
    private static void ApplyPaneBackground(NavigationView? nav, bool translucent)
    {
        if (nav == null) return;

        try
        {
            Brush? brush = BrushLookup.Brush(translucent
                ? "LayerFillColorDefaultBrush"
                : "SolidBackgroundFillColorBaseBrush");

            if (brush == null)
            {
                // 查不到就退到透明：透出的是页面根容器的底色，仍然是对的观感。
                var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                nav.Resources["NavigationViewDefaultPaneBackground"] = transparent;
                nav.Resources["NavigationViewExpandedPaneBackground"] = transparent;
                return;
            }

            nav.Resources["NavigationViewDefaultPaneBackground"] = brush;
            nav.Resources["NavigationViewExpandedPaneBackground"] = brush;
        }
        catch (Exception ex)
        {
            AppServices.Log("设置导航窗格背景失败：" + ex.Message);
        }
    }
}
