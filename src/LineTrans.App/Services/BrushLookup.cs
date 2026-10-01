using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using Windows.UI;

namespace LineTrans.App.Services;

/// <summary>
/// 按资源键从应用资源里取 Fluent 刷子 / 颜色。
///
/// 为什么要这个工具：WinUI 自带的主题资源（LayerFillColorDefaultBrush、SystemAccentColor …）
/// 需要按「当前主题」解析，XAML 里用 ThemeResource 很自然，但代码里要拿到同一个对象
/// 就只能查资源字典。这里把查询收口，查不到返回 null，调用方自己决定回落，绝不抛异常。
/// </summary>
internal static class BrushLookup
{
    /// <summary>取刷子；查不到返回 null。</summary>
    public static SolidColorBrush? Brush(string key) => Lookup(key) as SolidColorBrush;

    /// <summary>取颜色（SystemAccentColor 这类键本身就是 Color）；查不到返回 null。</summary>
    public static Color? ColorValue(string key) => Lookup(key) is Color color ? color : null;

    private static object? Lookup(string key)
    {
        var app = Application.Current;
        if (app == null) return null;
        return Lookup(app.Resources, key, 0);
    }

    private static object? Lookup(ResourceDictionary dictionary, string key, int depth)
    {
        if (depth > 4) return null;

        // 索引器会一并处理主题字典（ThemeDictionaries）与合并字典；键不存在时可能抛异常，
        // 也可能返回 null，两种都按「没查到」处理。
        try
        {
            object? value = dictionary[key];
            if (value != null) return value;
        }
        catch
        {
            // 键不存在：继续往下找
        }

        // 索引器不搜合并字典时，这里兜住。
        try
        {
            foreach (var merged in dictionary.MergedDictionaries)
            {
                object? value = Lookup(merged, key, depth + 1);
                if (value != null) return value;
            }
        }
        catch
        {
            // 合并字典不可枚举时忽略
        }

        return null;
    }
}
