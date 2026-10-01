using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using LineTrans.Core;

namespace LineTrans.App.Services;

/// <summary>
/// PC 端外观设置：翻译页布局 / 窗口背景材质 / 强调色来源。
///
/// 为什么既在 Core 的 AppSettings 里有字段，又单独存一份 ui.json：
///   · 这三个字段按任务要求加在 <see cref="AppSettings"/> 上
///     （TranslationLayout / BackdropMaterial / AccentSource 与它们的规范化函数）；
///   · 但 Core 的 SettingsRepository 用的是「显式 DTO 映射」（SettingsDto ↔ AppSettings），
///     它不在本次写入范围内，所以新字段不会被写进 settings.json；
///   · 于是 App 端另存一份 <c>%APPDATA%\LineTrans\ui.json</c>——
///     与既有的 tray.json 完全同一套做法——并在加载后把值镜像回
///     <see cref="AppServices.Settings"/> 的对应字段，保证两边一致。
///
/// 删掉 ui.json 只会让这三项回到默认值（左右式 / Mica / 跟随系统强调色），不影响翻译设置。
/// </summary>
public sealed class UiSettings
{
    /// <summary>翻译页布局：left-right（左原文右译文）/ top-bottom（上原文下译文）。</summary>
    public string TranslationLayout { get; set; } = AppSettings.LayoutLeftRight;

    /// <summary>窗口背景材质：mica / micaAlt / acrylic / none。</summary>
    public string BackdropMaterial { get; set; } = AppSettings.BackdropMica;

    /// <summary>强调色来源：system（跟随系统）/ brand（品牌蓝）。</summary>
    public string AccentSource { get; set; } = AppSettings.AccentSystem;

    public UiSettings Clone() => new()
    {
        TranslationLayout = TranslationLayout,
        BackdropMaterial = BackdropMaterial,
        AccentSource = AccentSource,
    };

    /// <summary>把非法值拉回合法取值（认不出的值走 AppSettings 的规范化函数）。</summary>
    public void Sanitize()
    {
        TranslationLayout = AppSettings.NormalizeTranslationLayout(TranslationLayout);
        BackdropMaterial = AppSettings.NormalizeBackdropMaterial(BackdropMaterial);
        AccentSource = AppSettings.NormalizeAccentSource(AccentSource);
    }
}

/// <summary>
/// ui.json 的读写。写盘是同步的：这份文件极小，改设置又是低频动作，
/// 没必要像 settings.json 那样做防抖（也避免退出时还要再刷一次盘）。
/// </summary>
public static class UiSettingsStore
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
    private static UiSettings _current = new();

    /// <summary>当前设置（永远非空）。</summary>
    public static UiSettings Current
    {
        get { lock (Gate) return _current; }
    }

    /// <summary>设置变化通知（主窗口、翻译页、设置页都订阅）。</summary>
    public static event Action? Changed;

    /// <summary>加载时的中文告警（文件损坏 / 字段非法），没有问题时为空。</summary>
    public static string LoadWarning { get; private set; } = string.Empty;

    /// <summary>数据目录（与 settings.json / tray.json 同目录）。</summary>
    public static string DataDirectory => TraySettingsStore.DataDirectory;

    /// <summary>外观设置文件：<c>%APPDATA%\LineTrans\ui.json</c>。</summary>
    public static string FilePath => Path.Combine(DataDirectory, "ui.json");

    /// <summary>读盘；文件不存在 / 字段缺失 / 整份损坏都不会抛异常。</summary>
    public static void Load()
    {
        LoadWarning = string.Empty;
        UiSettings loaded;

        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                loaded = JsonSerializer.Deserialize<UiSettings>(json, JsonOptions) ?? new UiSettings();
            }
            else
            {
                loaded = new UiSettings();
            }
        }
        catch (Exception ex)
        {
            LoadWarning = "ui.json 解析失败，已回落默认外观设置：" + ex.Message;
            loaded = new UiSettings();
        }

        loaded.Sanitize();
        lock (Gate)
        {
            _current = loaded;
        }

        Mirror();
        if (LoadWarning.Length > 0) AppServices.Log("外观设置：" + LoadWarning);
        Changed?.Invoke();
    }

    /// <summary>用一份新设置替换当前设置、写盘并立即生效。</summary>
    public static void Apply(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Sanitize();
        lock (Gate)
        {
            _current = settings;
        }
        Save();
        Mirror();
        Changed?.Invoke();
    }

    /// <summary>改动当前设置（界面最常用的入口）。</summary>
    public static void Update(Action<UiSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        UiSettings next;
        lock (Gate) next = _current.Clone();
        mutate(next);
        Apply(next);
    }

    /// <summary>写盘。失败只记日志，绝不打断界面。</summary>
    public static void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath) ?? DataDirectory;
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            UiSettings snapshot;
            lock (Gate) snapshot = _current.Clone();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, JsonOptions), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppServices.Log("外观设置写盘失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把当前外观设置镜像进 Core 的 AppSettings 字段。
    /// 只动这三个新字段，不触发 settings.json 的写盘（Core 的 DTO 里没有它们，
    /// 写盘也不会带上，反而多一次无意义的磁盘写入）。
    /// </summary>
    private static void Mirror()
    {
        try
        {
            var settings = AppServices.Settings;
            UiSettings snapshot;
            lock (Gate) snapshot = _current;

            settings.TranslationLayout = AppSettings.NormalizeTranslationLayout(snapshot.TranslationLayout);
            settings.BackdropMaterial = AppSettings.NormalizeBackdropMaterial(snapshot.BackdropMaterial);
            settings.AccentSource = AppSettings.NormalizeAccentSource(snapshot.AccentSource);
        }
        catch (Exception ex)
        {
            AppServices.Log("外观设置镜像到 AppSettings 失败：" + ex.Message);
        }
    }
}
