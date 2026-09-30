using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LineTrans.App.Services;

/// <summary>
/// 托盘 / 全局热键 / 开机自启的设置。
///
/// 为什么不放进 LineTrans.Core 的 AppSettings：<c>AppSettings</c> 在 Core 工程里，
/// 而本次任务的写入范围只有 <c>src\LineTrans.App\</c>，Core 只读。
/// 因此这一小组设置单独存一份 <c>%APPDATA%\LineTrans\tray.json</c>，
/// 与 <c>settings.json</c> 井水不犯河水：删除它只会让托盘设置回到默认值，不影响翻译设置。
/// </summary>
public sealed class TraySettings
{
    /// <summary>关闭主窗口时：最小化到托盘。</summary>
    public const string CloseToTray = "tray";

    /// <summary>关闭主窗口时：直接退出程序。</summary>
    public const string CloseToExit = "exit";

    /// <summary>是否显示托盘图标。关掉之后就没有托盘菜单，也没有「最小化到托盘」。</summary>
    public bool TrayIconEnabled { get; set; } = true;

    /// <summary>关闭主窗口的行为：<see cref="CloseToTray"/> 或 <see cref="CloseToExit"/>。</summary>
    public string CloseAction { get; set; } = CloseToTray;

    /// <summary>是否启用全局热键。</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>全局热键文本，例如 "Ctrl+Alt+C"。</summary>
    public string Hotkey { get; set; } = HotkeySpec.DefaultText;

    /// <summary>是否开机自动启动（HKCU 的 Run 项）。</summary>
    public bool AutoStartEnabled { get; set; }

    /// <summary>是否已经提示过「已最小化到托盘」，只提示一次。</summary>
    public bool TrayHintShown { get; set; }

    public TraySettings Clone() => new()
    {
        TrayIconEnabled = TrayIconEnabled,
        CloseAction = CloseAction,
        HotkeyEnabled = HotkeyEnabled,
        Hotkey = Hotkey,
        AutoStartEnabled = AutoStartEnabled,
        TrayHintShown = TrayHintShown,
    };

    /// <summary>把非法值拉回合法范围，返回是否有过修正。</summary>
    public bool Sanitize(out string warning)
    {
        warning = string.Empty;

        if (!string.Equals(CloseAction, CloseToExit, StringComparison.OrdinalIgnoreCase))
        {
            CloseAction = CloseToTray;
        }
        else
        {
            CloseAction = CloseToExit;
        }

        if (!HotkeySpec.TryParse(Hotkey, out var spec, out string error))
        {
            Hotkey = HotkeySpec.DefaultText;
            warning = "热键 \"" + Hotkey + "\" 无法识别（" + error + "），已回落默认值 " + HotkeySpec.DefaultText;
            return true;
        }

        Hotkey = spec.Text;
        return false;
    }
}

/// <summary>
/// tray.json 的读写。写盘是同步的——这份文件极小，而且改设置是低频动作，
/// 没必要像 settings.json 那样做防抖（也避免退出时还得再刷一次盘）。
/// </summary>
public static class TraySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly object Gate = new();
    private static TraySettings _current = new();

    /// <summary>当前设置（永远非空）。</summary>
    public static TraySettings Current
    {
        get { lock (Gate) return _current; }
    }

    /// <summary>设置变化通知（界面与托盘线程都会订阅）。</summary>
    public static event Action? Changed;

    /// <summary>加载时的中文告警（文件损坏 / 字段非法），没有问题时为空。</summary>
    public static string LoadWarning { get; private set; } = string.Empty;

    /// <summary>数据目录（与 settings.json 同目录）。</summary>
    public static string DataDirectory => string.IsNullOrEmpty(AppServices.DataRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LineTrans")
        : AppServices.DataRoot;

    /// <summary>托盘设置文件：<c>%APPDATA%\LineTrans\tray.json</c>。</summary>
    public static string FilePath => Path.Combine(DataDirectory, "tray.json");

    /// <summary>读盘。文件不存在 / 字段缺失 / 整份损坏都不会抛异常。</summary>
    public static void Load()
    {
        LoadWarning = string.Empty;
        TraySettings loaded;

        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                loaded = JsonSerializer.Deserialize<TraySettings>(json, JsonOptions) ?? new TraySettings();
            }
            else
            {
                loaded = new TraySettings();
            }
        }
        catch (Exception ex)
        {
            LoadWarning = "tray.json 解析失败，已回落默认托盘设置：" + ex.Message;
            loaded = new TraySettings();
        }

        string extra = string.Empty;
        if (loaded.Sanitize(out string warning)) extra = warning;

        lock (Gate)
        {
            _current = loaded;
        }

        LoadWarning = string.IsNullOrEmpty(LoadWarning) ? extra : LoadWarning + "；" + extra;
        if (LoadWarning.Length > 0) AppServices.Log("托盘设置：" + LoadWarning);
        Changed?.Invoke();
    }

    /// <summary>用一份新设置替换当前设置并写盘。</summary>
    public static void Apply(TraySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Sanitize(out _);
        lock (Gate)
        {
            _current = settings;
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>改动当前设置并写盘。</summary>
    public static void Update(Action<TraySettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        TraySettings next;
        lock (Gate)
        {
            next = _current.Clone();
        }
        mutate(next);
        Apply(next);
    }

    /// <summary>写盘。失败只记日志，绝不打断界面。</summary>
    public static void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath) ?? DataDirectory;
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            TraySettings snapshot;
            lock (Gate) snapshot = _current.Clone();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, JsonOptions), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppServices.Log("托盘设置写盘失败：" + ex.Message);
        }
    }
}
