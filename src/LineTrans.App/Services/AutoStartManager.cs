using System;
using System.Diagnostics;
using System.IO;

using Microsoft.Win32;

namespace LineTrans.App.Services;

/// <summary>
/// 开机自启：写 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>。
///
/// 只用 HKCU——不碰 HKLM，不需要管理员权限，也不会影响这台机器上的其他用户。
/// 命令行带 <c>--minimized</c>，开机拉起时直接缩到托盘，不跳主窗口出来打扰人。
/// </summary>
public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "LineTrans";

    /// <summary>当前进程的可执行文件路径。</summary>
    public static string ExecutablePath
    {
        get
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) return path!;

            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>写进注册表的命令行。</summary>
    public static string CommandLine
    {
        get
        {
            string exe = ExecutablePath;
            return exe.Length == 0 ? string.Empty : "\"" + exe + "\" --minimized";
        }
    }

    /// <summary>注册表里当前登记的命令行（没登记过为空）。</summary>
    public static string RegisteredCommand
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) as string ?? string.Empty;
            }
            catch (Exception ex)
            {
                AppServices.Log("读取开机自启注册表失败：" + ex.Message);
                return string.Empty;
            }
        }
    }

    /// <summary>是否已经开启（按注册表实际内容判断，不信内存里的设置）。</summary>
    public static bool IsEnabled => RegisteredCommand.Length > 0;

    /// <summary>写入或删除自启项。失败时 <paramref name="error"/> 是中文原因。</summary>
    public static bool Apply(bool enabled, out string error)
    {
        error = string.Empty;
        string command = CommandLine;
        if (command.Length == 0)
        {
            error = "取不到程序自身路径，无法设置开机自启";
            return false;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("打不开注册表项 " + RunKeyPath);

            if (enabled)
            {
                key.SetValue(ValueName, command, RegistryValueKind.String);
                AppServices.Log("已设置开机自启：" + command);
            }
            else
            {
                if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppServices.Log("已取消开机自启");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "写注册表失败：" + ex.Message;
            AppServices.Log("设置开机自启失败：" + error);
            return false;
        }
    }
}
