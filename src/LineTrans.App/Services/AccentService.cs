using System;

using LineTrans.Core;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using Windows.UI;

namespace LineTrans.App.Services;

/// <summary>
/// 强调色（Accent）。
///
/// 默认是「跟随系统强调色」：不写死任何色值，直接读系统的 SystemAccentColor，
/// 并按浅色 / 深色主题各取一档变体（浅色主题悬停变深、深色主题悬停变亮），
/// 与 WinUI 自带强调按钮的观感一致。
///
/// 设置里选「品牌蓝」时，把同一批刷子改成品牌蓝（网页端 style.css 与安卓端用的同一支主色）。
///
/// 实现方式：Colors.xaml 里的 Accent*Brush 都是【可变刷子】——这里是直接改它们的 Color。
/// 刷子是共享对象，改颜色会立刻反映到所有引用它的控件上，既不用重建资源字典，
/// 也不用重新导航页面（ThemeResource 的重新解析往往要等主题切换，改颜色没有这个延迟）。
/// </summary>
public static class AccentService
{
    // 品牌蓝（浅色 / 深色各一档，与网页端 html[data-theme] 两套变量对齐）
    private static readonly Color BrandFill = Color.FromArgb(0xFF, 0x4D, 0x6B, 0xFE);
    private static readonly Color BrandHover = Color.FromArgb(0xFF, 0x3F, 0x5C, 0xF0);
    private static readonly Color BrandPressed = Color.FromArgb(0xFF, 0x33, 0x4C, 0xD6);
    private static readonly Color BrandFillDark = Color.FromArgb(0xFF, 0x56, 0x86, 0xFE);
    private static readonly Color BrandHoverDark = Color.FromArgb(0xFF, 0x6B, 0x96, 0xFF);
    private static readonly Color BrandPressedDark = Color.FromArgb(0xFF, 0x8F, 0xB0, 0xFF);

    // ------------------------------------------------------------------
    // 要改颜色的刷子清单（定义见 Themes\Colors.xaml）
    //
    // 为什么要把 WinUI 的那一串主题资源键也列进来：
    // 系统控件的模板（AccentButtonStyle / CheckBox / ToggleSwitch / Slider / HyperlinkButton）
    // 取色走的是「资源键 -> 刷子对象」，只有把这些键本身换成我们的刷子，
    // 系统模板才会跟着「品牌蓝 / 跟随系统」变 —— 详见 Colors.xaml 里的坑记录。
    // ------------------------------------------------------------------

    /// <summary>强调色填充（主按钮底色、开关打开态、复选框勾选态、滑块、进度条）。</summary>
    private static readonly string[] FillBrushKeys =
    {
        "AccentFillBrush",
        "AccentButtonBackground",
        "ToggleSwitchFillOn",
        "ToggleSwitchStrokeOn",
        "CheckBoxCheckBackgroundFillChecked",
        "CheckBoxCheckBackgroundStrokeChecked",
        "CheckBoxCheckBackgroundFillIndeterminate",
        "CheckBoxCheckBackgroundStrokeIndeterminate",
        "RadioButtonOuterEllipseCheckedFill",
        "RadioButtonOuterEllipseCheckedStroke",
        "SliderThumbBackground",
        "SliderTrackValueFill",
        "AccentFillColorDefaultBrush",
    };

    /// <summary>悬停态。</summary>
    private static readonly string[] HoverBrushKeys =
    {
        "AccentHoverBrush",
        "AccentButtonBackgroundPointerOver",
        "AccentFillColorSecondaryBrush",
    };

    /// <summary>按下态。</summary>
    private static readonly string[] PressedBrushKeys =
    {
        "AccentPressedBrush",
        "AccentButtonBackgroundPressed",
        "AccentFillColorTertiaryBrush",
    };

    /// <summary>强调色文字（超链接、徽章文字）。</summary>
    private static readonly string[] TextBrushKeys =
    {
        "AccentTextBrush",
        "AccentSubtleBrush",
        "HyperlinkButtonForeground",
        "AccentTextFillColorPrimaryBrush",
    };

    /// <summary>当前实际生效的强调色来源（system / brand）。</summary>
    public static string Effective { get; private set; } = AppSettings.AccentSystem;

    /// <summary>把当前强调色来源应用到全局刷子。themeSource 用来判断浅色 / 深色。</summary>
    public static string Apply(FrameworkElement? themeSource)
    {
        string source = AppSettings.NormalizeAccentSource(UiSettingsStore.Current.AccentSource);
        bool dark = IsDark(themeSource);

        Color fill;
        Color hover;
        Color pressed;
        Color text;

        if (source == AppSettings.AccentBrand)
        {
            fill = dark ? BrandFillDark : BrandFill;
            hover = dark ? BrandHoverDark : BrandHover;
            pressed = dark ? BrandPressedDark : BrandPressed;
            text = dark ? BrandHoverDark : BrandFill;
        }
        else
        {
            // 跟随系统：XAML 里的默认值本来就是 SystemAccentColor 系列，
            // 这里只是按主题挑一档变体（读不到系统色就什么都不改，保持 XAML 的默认值）。
            Color? baseColor = BrushLookup.ColorValue("SystemAccentColor");
            Color? light1 = BrushLookup.ColorValue("SystemAccentColorLight1");
            Color? light2 = BrushLookup.ColorValue("SystemAccentColorLight2");
            Color? light3 = BrushLookup.ColorValue("SystemAccentColorLight3");
            Color? dark1 = BrushLookup.ColorValue("SystemAccentColorDark1");
            Color? dark2 = BrushLookup.ColorValue("SystemAccentColorDark2");

            fill = baseColor ?? dark1 ?? BrandFill;
            hover = dark ? (light1 ?? fill) : (dark1 ?? fill);
            pressed = dark ? (light3 ?? light2 ?? hover) : (dark2 ?? hover);
            text = dark ? (light2 ?? light1 ?? fill) : (dark1 ?? fill);
        }

        // 应用自己的语义刷子
        foreach (string key in FillBrushKeys) SetBrushColor(key, fill);
        foreach (string key in HoverBrushKeys) SetBrushColor(key, hover);
        foreach (string key in PressedBrushKeys) SetBrushColor(key, pressed);
        foreach (string key in TextBrushKeys) SetBrushColor(key, text);

        Effective = source;
        return source;
    }

    /// <summary>当前是深色主题吗（拿不到元素时按应用级主题判断）。</summary>
    private static bool IsDark(FrameworkElement? themeSource)
    {
        if (themeSource != null) return themeSource.ActualTheme == ElementTheme.Dark;
        return Application.Current?.RequestedTheme == ApplicationTheme.Dark;
    }

    private static void SetBrushColor(string key, Color color)
    {
        try
        {
            var brush = BrushLookup.Brush(key);
            if (brush != null) brush.Color = color;
        }
        catch (Exception ex)
        {
            AppServices.Log("设置强调色刷子 " + key + " 失败：" + ex.Message);
        }
    }

    /// <summary>系统强调色的十六进制（取证报告里做对照用，读不到返回「未知」）。</summary>
    public static string SystemAccentHex()
    {
        var color = BrushLookup.ColorValue("SystemAccentColor");
        return color == null
            ? "未知"
            : "#" + color.Value.R.ToString("X2") + color.Value.G.ToString("X2") + color.Value.B.ToString("X2");
    }

    /// <summary>设置页用：当前强调色的中文说明（含实际取到的颜色，便于排查）。</summary>
    public static string Describe()
    {
        var brush = BrushLookup.Brush("AccentFillBrush");
        string color = brush == null ? "未知" : "#" + brush.Color.R.ToString("X2") + brush.Color.G.ToString("X2") + brush.Color.B.ToString("X2");
        return (Effective == AppSettings.AccentBrand ? "品牌蓝" : "跟随系统强调色") + "（当前主色 " + color + "）";
    }
}
