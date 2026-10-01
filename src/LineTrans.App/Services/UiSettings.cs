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
/// 存放位置（2026-10 起）：
///   · **权威值在 settings.json** —— Core 的 SettingsDto 里已经有 translationLayout /
///     backdropMaterial / accentSource 三个字段（见 <see cref="SettingsRepository.AppearanceFieldsInSettingsFile"/>）；
///   · <c>%APPDATA%\LineTrans\ui.json</c> 只作为【兼容镜像】继续维护：
///     老版本（DTO 里还没有这三个字段的那版）把它当唯一数据源，
///     用户从新版降级回老版时外观设置不会丢。
///
/// 迁移：启动时如果 settings.json 里还没有这三项（老用户第一次跑新版），
/// 就从 ui.json 读一次值写进 settings.json；此后一律以 settings.json 为准。
/// 两个文件都被删掉时这三项回到默认（左右式 / Mica / 跟随系统强调色），不影响翻译设置。
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
/// 外观设置的读写。权威值走 Core 的 <see cref="AppSettings"/> + settings.json
/// （写盘交给 SettingsRepository 的防抖机制），ui.json 只作为兼容镜像同步一份，
/// 写它是同步的 —— 这份文件极小，改设置又是低频动作，不必再排队。
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

    /// <summary>
    /// 读盘；文件不存在 / 字段缺失 / 整份损坏都不会抛异常。
    /// 权威值取 settings.json，settings.json 里没有时才从 ui.json 迁移读一次。
    /// </summary>
    public static void Load()
    {
        LoadWarning = string.Empty;

        // 1) 兼容源：老版本的 ui.json（读不到就用默认值）
        UiSettings legacy = ReadCompatibilityFile();

        // 2) 权威源：settings.json
        bool fromSettings = AppServices.SettingsRepo.AppearanceFieldsInSettingsFile;
        UiSettings effective = fromSettings ? FromSettings(AppServices.Settings) : legacy;
        effective.Sanitize();

        lock (Gate)
        {
            _current = effective;
        }

        // 3) 同步回 AppSettings，并把 ui.json 兼容镜像补齐
        Mirror();
        WriteCompatibilityFile();

        if (!fromSettings)
        {
            // 迁移一次：把 ui.json 的值写进 settings.json，此后它不再参与读取
            try
            {
                AppServices.SettingsRepo.Save();
                AppServices.Log("外观设置：settings.json 里没有这三项，已从 ui.json 迁移（"
                    + effective.TranslationLayout + " / " + effective.BackdropMaterial + " / "
                    + effective.AccentSource + "）");
            }
            catch (Exception ex)
            {
                AppServices.Log("外观设置迁移进 settings.json 失败：" + ex.Message);
            }
        }

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

        // 先镜像到 AppSettings，再落盘：这样 settings.json 的 payload 里带上这三项
        Mirror();
        try
        {
            // 防抖写，退出前 AppServices.Shutdown 会 Flush；不阻塞界面线程
            AppServices.SettingsRepo.Save();
        }
        catch (Exception ex)
        {
            AppServices.Log("外观设置写入 settings.json 失败：" + ex.Message);
        }

        WriteCompatibilityFile();
        Changed?.Invoke();
    }

    /// <summary>从 AppSettings 里取这三项（settings.json 是权威源时用）。</summary>
    private static UiSettings FromSettings(AppSettings settings) => new()
    {
        TranslationLayout = settings.TranslationLayout,
        BackdropMaterial = settings.BackdropMaterial,
        AccentSource = settings.AccentSource,
    };

    /// <summary>读老版本的 ui.json；不存在 / 坏了都退回默认值，只记告警。</summary>
    private static UiSettings ReadCompatibilityFile()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UiSettings();
            string json = File.ReadAllText(FilePath, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<UiSettings>(json, JsonOptions) ?? new UiSettings();
            loaded.Sanitize();
            return loaded;
        }
        catch (Exception ex)
        {
            LoadWarning = "ui.json 解析失败，已回落默认外观设置：" + ex.Message;
            return new UiSettings();
        }
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

    /// <summary>
    /// 维护 ui.json 兼容镜像。它不再参与读取（除非 settings.json 里没有这三项），
    /// 作用只有一个：用户把程序降级回老版本时，外观设置还在。
    /// 失败只记日志，绝不打断界面。
    /// </summary>
    public static void WriteCompatibilityFile()
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
    /// 只动这三个字段；写盘由调用方显式调用 SettingsRepo.Save()（会走防抖）。
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
