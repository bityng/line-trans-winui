using System;
using System.Runtime.InteropServices;

using LineTrans.App.Interop;

namespace LineTrans.App.Services;

/// <summary>
/// 模拟键盘输入（SendInput）。
///
/// 全局划词靠它往当前前台程序发一次 Ctrl+C；自检脚本也用它往记事本里打字。
/// 一律走 <c>SendInput</c> 而不是已废弃的 <c>keybd_event</c>。
/// </summary>
public static class InputSender
{
    /// <summary>发送 Ctrl+C（复制）。返回是否把 4 个键盘事件全都投递出去了。</summary>
    public static bool SendCtrlC() => SendCombo(NativeMethods.VK_CONTROL, NativeMethods.VK_C);

    /// <summary>发送 Ctrl+A（全选），自检里选记事本正文用。</summary>
    public static bool SendCtrlA() => SendCombo(NativeMethods.VK_CONTROL, NativeMethods.VK_A);

    /// <summary>发送一个组合键：修饰键按下 → 主键按下 → 主键抬起 → 修饰键抬起。</summary>
    public static bool SendCombo(ushort modifier, ushort key)
    {
        var inputs = new NativeMethods.INPUT[4];
        inputs[0] = KeyInput(modifier, false);
        inputs[1] = KeyInput(key, false);
        inputs[2] = KeyInput(key, true);
        inputs[3] = KeyInput(modifier, true);
        return Send(inputs);
    }

    /// <summary>按 Unicode 逐字发送一段文本（不受键盘布局影响）。</summary>
    public static bool SendUnicodeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        var inputs = new NativeMethods.INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = UnicodeInput(text[i], false);
            inputs[i * 2 + 1] = UnicodeInput(text[i], true);
        }
        return Send(inputs);
    }

    /// <summary>单独按一个键（按下 + 抬起）。</summary>
    public static bool SendKey(ushort key)
    {
        var inputs = new NativeMethods.INPUT[2];
        inputs[0] = KeyInput(key, false);
        inputs[1] = KeyInput(key, true);
        return Send(inputs);
    }

    private static bool Send(NativeMethods.INPUT[] inputs)
    {
        if (inputs.Length == 0) return true;
        uint sent = NativeMethods.SendInput(
            (uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        return sent == inputs.Length;
    }

    private static NativeMethods.INPUT KeyInput(ushort virtualKey, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = 0,
                dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static NativeMethods.INPUT UnicodeInput(char ch, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };
}
