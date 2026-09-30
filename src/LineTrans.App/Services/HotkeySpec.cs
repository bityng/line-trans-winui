using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using LineTrans.App.Interop;

namespace LineTrans.App.Services;

/// <summary>
/// 一条全局热键：修饰符 + 虚拟键码 + 可读文本（"Ctrl+Alt+C"）。
///
/// 设置里存的是文本，运行期用 <see cref="TryParse"/> 解析；解析失败一律回落默认值并给出中文原因，
/// 绝不抛异常——设置文件是用户可以直接编辑的。
/// </summary>
public sealed class HotkeySpec
{
    /// <summary>默认热键：Ctrl+Alt+C。</summary>
    public const string DefaultText = "Ctrl+Alt+C";

    public HotkeySpec(uint modifiers, uint virtualKey, string text)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        Text = text;
    }

    /// <summary>MOD_* 位组合。</summary>
    public uint Modifiers { get; }

    /// <summary>虚拟键码。</summary>
    public uint VirtualKey { get; }

    /// <summary>规范化后的可读文本，例如 "Ctrl+Alt+C"。</summary>
    public string Text { get; }

    public static HotkeySpec Default { get; } = new(
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
        NativeMethods.VK_C,
        DefaultText);

    /// <summary>解析 "Ctrl+Alt+C" 这类文本。失败时 <paramref name="error"/> 是中文原因。</summary>
    public static bool TryParse(string? raw, out HotkeySpec spec, out string error)
    {
        spec = Default;
        error = string.Empty;

        string text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "热键不能为空";
            return false;
        }

        uint modifiers = 0;
        uint key = 0;
        bool hasKey = false;

        string[] parts = text.Split(new[] { '+', '\uFF0B' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawPart in parts)
        {
            string part = rawPart.Trim();
            if (part.Length == 0) continue;

            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                case "控制":
                case "控制键":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    continue;
                case "alt":
                case "option":
                    modifiers |= NativeMethods.MOD_ALT;
                    continue;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    continue;
                case "win":
                case "windows":
                case "meta":
                case "super":
                    modifiers |= NativeMethods.MOD_WIN;
                    continue;
            }

            if (hasKey)
            {
                error = "热键里只能有一个主键：\"" + text + "\"";
                return false;
            }

            if (!TryParseKey(part, out key))
            {
                error = "不认识主键 \"" + part + "\"：只支持 A-Z、0-9、F1-F24";
                return false;
            }
            hasKey = true;
        }

        if (!hasKey)
        {
            error = "热键缺少主键：例如 Ctrl+Alt+C";
            return false;
        }

        if (modifiers == 0)
        {
            error = "热键至少要带一个修饰键（Ctrl / Alt / Shift / Win）";
            return false;
        }

        spec = new HotkeySpec(modifiers, key, Format(modifiers, key));
        return true;
    }

    /// <summary>按 Ctrl / Alt / Shift / Win 的固定顺序拼出可读文本。</summary>
    public static string Format(uint modifiers, uint virtualKey)
    {
        var builder = new StringBuilder();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) builder.Append("Ctrl+");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) builder.Append("Alt+");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) builder.Append("Shift+");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) builder.Append("Win+");
        builder.Append(KeyName(virtualKey));
        return builder.ToString();
    }

    public override string ToString() => Text;

    private static bool TryParseKey(string part, out uint key)
    {
        key = 0;
        string value = part.Trim().ToUpperInvariant();
        if (value.Length == 0) return false;

        // F1 - F24
        if (value.Length >= 2 && value[0] == 'F'
            && int.TryParse(value.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fn)
            && fn >= 1 && fn <= 24)
        {
            key = (uint)(0x70 + fn - 1);
            return true;
        }

        if (value.Length == 1)
        {
            char c = value[0];
            if (c >= 'A' && c <= 'Z') { key = c; return true; }
            if (c >= '0' && c <= '9') { key = c; return true; }
        }

        return false;
    }

    private static string KeyName(uint virtualKey)
    {
        if (virtualKey >= 0x70 && virtualKey <= 0x87) return "F" + (virtualKey - 0x70 + 1);
        return ((char)virtualKey).ToString();
    }

    /// <summary>
    /// 备用组合：设置里的热键被别的程序占用（ERROR_HOTKEY_ALREADY_REGISTERED=1409）时按顺序往下试，
    /// 免得用户装完发现「按了没反应」却不知道为什么。实际生效的组合会写在托盘状态与设置页上。
    /// </summary>
    public static IReadOnlyList<string> Fallbacks { get; } = new[]
    {
        "Ctrl+Alt+D",
        "Ctrl+Alt+Q",
        "Ctrl+Alt+H",
        "Ctrl+Shift+C",
        "Alt+Shift+D",
    };

    /// <summary>给设置页用的一份候选列表（下拉选择 + 允许手输）。</summary>
    public static IReadOnlyList<string> Presets { get; } = new[]
    {
        "Ctrl+Alt+C",
        "Ctrl+Alt+D",
        "Ctrl+Alt+Q",
        "Ctrl+Shift+C",
        "Ctrl+Shift+D",
        "Alt+Shift+D",
        "Ctrl+Alt+F1",
    };
}
