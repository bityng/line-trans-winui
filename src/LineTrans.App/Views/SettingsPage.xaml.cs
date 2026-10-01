using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using LineTrans.App.Interop;
using LineTrans.App.Services;
using LineTrans.Core;
using LineTrans.Core.Dictionary;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

using Windows.Storage;
using Windows.Storage.Pickers;

namespace LineTrans.App.Views;

/// <summary>
/// 设置页：AI 服务 / 语言与提示词 / 划词查义 / 界面 / 数据。
/// 保存走 <c>SettingsRepository.Save(immediate: true)</c>，并触发 Changed 事件让全应用即时生效。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private bool _loading;

    private static readonly (string Code, string Label)[] Languages =
    {
        ("auto", "自动检测"),
        ("zh-CN", "中文（简体）"),
        ("zh-TW", "中文（繁体）"),
        ("en", "英语"),
        ("ja", "日语"),
        ("ko", "韩语"),
        ("fr", "法语"),
        ("de", "德语"),
        ("es", "西班牙语"),
        ("ru", "俄语"),
        ("pt", "葡萄牙语"),
        ("it", "意大利语"),
        ("ar", "阿拉伯语"),
    };

    public SettingsPage()
    {
        InitializeComponent();

        FontSize = AppServices.BodyFontSize;

        FillLanguages(SourceLangBox);
        FillLanguages(TargetLangBox);
        FillTemplates();
        FillHotkeyPresets();

        LoadFromSettings();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadFromSettings();
        _ = RefreshDictionaryStatusAsync();
        SubscribeTrayStatus(true);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        SubscribeTrayStatus(false);
    }

    /// <summary>
    /// 托盘状态变化是从托盘线程发出来的，这里统一切回 UI 线程再刷文本。
    /// 进页面订阅、离开退订，避免页面被 NavigationView 反复重建时挂一堆回调。
    /// </summary>
    private void SubscribeTrayStatus(bool subscribe)
    {
        var host = App.Tray;
        if (host == null) return;

        if (subscribe) host.StatusChanged += OnTrayStatusChanged;
        else host.StatusChanged -= OnTrayStatusChanged;
    }

    private void OnTrayStatusChanged() => App.RunOnUiThread(RefreshTrayStatus);

    // ------------------------------------------------------------------
    // 选项填充
    // ------------------------------------------------------------------

    private static void FillLanguages(ComboBox box)
    {
        box.Items.Clear();
        foreach (var item in Languages)
        {
            box.Items.Add(new ComboBoxItem { Content = item.Label, Tag = item.Code });
        }
    }

    private void FillTemplates()
    {
        TemplateBox.Items.Clear();
        foreach (var template in PromptTemplates.All)
        {
            TemplateBox.Items.Add(new ComboBoxItem { Content = template.Name + "（" + template.Id + "）", Tag = template.Id });
        }
    }

    private static string SelectedTag(ComboBox box, string fallback)
    {
        if (box.SelectedItem is ComboBoxItem { Tag: string tag } && tag.Length > 0) return tag;
        return fallback;
    }

    // ------------------------------------------------------------------
    // 读 / 写
    // ------------------------------------------------------------------

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var s = AppServices.Settings;

            ProviderTypeBox.SelectedIndex = s.ActiveProvider?.Type == ProviderType.ANTHROPIC ? 1 : 0;

            var provider = s.ActiveProvider;
            BaseUrlBox.Text = provider?.BaseUrl ?? string.Empty;
            ApiKeyBox.Password = provider?.ApiKey ?? string.Empty;

            var model = s.ActiveModel;
            ModelBox.Text = model?.Name ?? string.Empty;
            TemperatureBox.Value = model?.Temperature ?? 0.2;
            MaxTokensBox.Value = model?.MaxTokens ?? 4096;
            InputPriceBox.Value = model?.Billing.InputPrice ?? 0.0;
            OutputPriceBox.Value = model?.Billing.OutputPrice ?? 0.0;

            SelectByCode(SourceLangBox, s.SourceLang);
            SelectByCode(TargetLangBox, s.TargetLang);
            DetectLanguageCheck.IsChecked = s.DetectLanguage;
            ContextUnitsBox.Value = s.ContextUnits;
            SelectTag(TemplateBox, s.PromptTemplateId);
            SystemPromptBox.Text = s.SystemPrompt;
            GlossaryBox.Text = s.Glossary;

            WordLookupSwitch.IsOn = s.WordLookupEnabled;
            LocalDictSwitch.IsOn = s.LocalDictionaryEnabled;
            AiFallbackSwitch.IsOn = s.LookupAiFallback;
            DefinitionLangBox.SelectedIndex = s.DefinitionLanguage switch
            {
                DefinitionLanguage.Both => 1,
                DefinitionLanguage.En => 2,
                _ => 0,
            };

            ThemeBox.SelectedIndex = s.ThemeMode switch
            {
                ThemeMode.LIGHT => 1,
                ThemeMode.DARK => 2,
                _ => 0,
            };
            ScaleSlider.Value = Math.Clamp(s.UiScale, 0.8f, 1.5f);
            UpdateScaleLabel();

            // 外观三项存在 ui.json（AppSettings 里也有同名新字段，加载后由 UiSettingsStore 镜像过去）
            var ui = UiSettingsStore.Current;
            BackdropBox.SelectedIndex = AppSettings.NormalizeBackdropMaterial(ui.BackdropMaterial) switch
            {
                AppSettings.BackdropMicaAlt => 1,
                AppSettings.BackdropAcrylic => 2,
                AppSettings.BackdropNone => 3,
                _ => 0,
            };
            AccentBox.SelectedIndex = AppSettings.NormalizeAccentSource(ui.AccentSource) == AppSettings.AccentBrand ? 1 : 0;
            LayoutBox.SelectedIndex = AppSettings.NormalizeTranslationLayout(ui.TranslationLayout) == AppSettings.LayoutTopBottom ? 1 : 0;

            var tray = TraySettingsStore.Current;
            TrayIconSwitch.IsOn = tray.TrayIconEnabled;
            CloseActionBox.SelectedIndex = string.Equals(
                tray.CloseAction, TraySettings.CloseToExit, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            GlobalHotkeySwitch.IsOn = tray.HotkeyEnabled;
            HotkeyBox.Text = tray.Hotkey;
            HotkeyPresetBox.SelectedIndex = IndexOfTag(HotkeyPresetBox, tray.Hotkey);
            AutoStartSwitch.IsOn = AutoStartManager.IsEnabled;

            DataDirText.Text = AppServices.DataRoot;
            SettingsFileText.Text = AppServices.SettingsRepo.FilePath;
            TrayStatusText.Text = "托盘状态：…";
            AutoStartStatusText.Text = BuildAutoStartStatus();
            SaveStatusText.Text = string.Empty;
        }
        finally
        {
            _loading = false;
        }

        RefreshDictStatus();
        RefreshAppearanceStatus();
    }

    private static void SelectByCode(ComboBox box, string code)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem { Tag: string tag }
                && string.Equals(tag, code, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem { Tag: string value }
                && string.Equals(value, tag, StringComparison.Ordinal))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static double NumberOr(double value, double fallback)
        => double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var repo = AppServices.SettingsRepo;

            // AI 走扁平入口：一次性重建 provider 与 model，字段口径与网页端 config.json 一致。
            repo.SetProvider(
                ProviderTypeBox.SelectedIndex == 1 ? "anthropic" : "openai",
                (BaseUrlBox.Text ?? string.Empty).Trim(),
                ApiKeyBox.Password ?? string.Empty,
                (ModelBox.Text ?? string.Empty).Trim(),
                NumberOr(TemperatureBox.Value, 0.2),
                (int)Math.Round(NumberOr(MaxTokensBox.Value, 4096)),
                NumberOr(InputPriceBox.Value, 0.0),
                NumberOr(OutputPriceBox.Value, 0.0),
                save: false);

            var s = repo.Settings;

            s.SourceLang = SelectedTag(SourceLangBox, "auto");
            s.TargetLang = SelectedTag(TargetLangBox, "zh-CN");
            s.DetectLanguage = DetectLanguageCheck.IsChecked == true;
            s.ContextUnits = (int)Math.Round(NumberOr(ContextUnitsBox.Value, 3));
            s.PromptTemplateId = SelectedTag(TemplateBox, AppSettings.DefaultPromptId);
            s.SystemPrompt = SystemPromptBox.Text ?? string.Empty;
            s.Glossary = GlossaryBox.Text ?? string.Empty;

            s.WordLookupEnabled = WordLookupSwitch.IsOn;
            s.LocalDictionaryEnabled = LocalDictSwitch.IsOn;
            s.LookupAiFallback = AiFallbackSwitch.IsOn;
            s.DictionaryAiExplain = AiFallbackSwitch.IsOn;
            s.DefinitionLanguage = DefinitionLanguageBox();

            s.ThemeMode = ThemeBox.SelectedIndex switch
            {
                1 => ThemeMode.LIGHT,
                2 => ThemeMode.DARK,
                _ => ThemeMode.SYSTEM,
            };
            s.UiScale = (float)Math.Round(ScaleSlider.Value, 2);

            repo.Save(immediate: true);

            ApplyThemeEverywhere();
            RefreshDictStatus();
            AppServices.WarmUpDictionary();

            string trayFailure = ApplyTraySettings();
            RefreshTrayStatus();

            SaveStatusText.Text = "已保存并立即生效（" + DateTime.Now.ToString("HH:mm:ss") + "）"
                + (trayFailure.Length > 0 ? "　" + trayFailure : string.Empty);
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = "保存失败：" + ex.Message;
            AppServices.Log("保存设置失败：" + ex.Message);
        }
    }

    private string DefinitionLanguageBox() => DefinitionLangBox.SelectedIndex switch
    {
        1 => DefinitionLanguage.Both,
        2 => DefinitionLanguage.En,
        _ => DefinitionLanguage.Zh,
    };

    private void ApplyThemeEverywhere()
    {
        RequestedTheme = AppServices.Settings.ThemeMode switch
        {
            ThemeMode.LIGHT => ElementTheme.Light,
            ThemeMode.DARK => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        MainWindow.Instance?.ApplyTheme();
        MainWindow.Instance?.ApplyBackdrop();
        RefreshAppearanceStatus();
    }

    // ------------------------------------------------------------------
    // 界面控件回调
    // ------------------------------------------------------------------

    /// <summary>背景材质：改完立刻生效（走 ui.json + UiSettingsStore.Changed）。</summary>
    private void OnBackdropChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        string value = BackdropBox.SelectedIndex switch
        {
            1 => AppSettings.BackdropMicaAlt,
            2 => AppSettings.BackdropAcrylic,
            3 => AppSettings.BackdropNone,
            _ => AppSettings.BackdropMica,
        };

        UiSettingsStore.Update(s => s.BackdropMaterial = value);
        RefreshAppearanceStatus();
    }

    /// <summary>强调色来源：跟随系统 / 品牌蓝。</summary>
    private void OnAccentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        string value = AccentBox.SelectedIndex == 1 ? AppSettings.AccentBrand : AppSettings.AccentSystem;
        UiSettingsStore.Update(s => s.AccentSource = value);
        RefreshAppearanceStatus();
    }

    /// <summary>翻译页布局：左右式 / 上下式（翻译页正在打开时会即时跟着换）。</summary>
    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        string value = LayoutBox.SelectedIndex == 1 ? AppSettings.LayoutTopBottom : AppSettings.LayoutLeftRight;
        UiSettingsStore.Update(s => s.TranslationLayout = value);
    }

    /// <summary>把「本机支持什么、当前实际生效什么」直接写出来，省得用户以为设置没生效。</summary>
    private void RefreshAppearanceStatus()
    {
        string actual = AppSettings.NormalizeBackdropMaterial(BackdropService.Effective) switch
        {
            AppSettings.BackdropMica => "云母（Mica）",
            AppSettings.BackdropMicaAlt => "云母 Alt（Mica Alt）",
            AppSettings.BackdropAcrylic => "亚克力（Acrylic）",
            _ => "无",
        };

        BackdropStatusText.Text = "本机支持——云母：" + YesNo(BackdropService.MicaSupported)
            + "　亚克力：" + YesNo(BackdropService.AcrylicSupported)
            + "　当前实际生效：" + actual
            + (BackdropService.LastNote.Length > 0 ? "（" + BackdropService.LastNote + "）" : "。");

        AccentStatusText.Text = "当前强调色：" + AccentService.Describe() + "。";
    }

    private static string YesNo(bool value) => value ? "是" : "否";

    private void UpdateScaleLabel()
    {
        int percent = (int)Math.Round(ScaleSlider.Value * 100);
        ScaleLabel.Text = "字号缩放：" + percent + "%（作用于正文与翻译编辑区）";
    }

    private void OnScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        UpdateScaleLabel();
    }

    private void OnOpenDataDirClick(object sender, RoutedEventArgs e)
        => AppServices.OpenDirectory(AppServices.DataRoot);

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "linetrans-settings" };
            picker.FileTypeChoices.Add("JSON 设置", new List<string> { ".json" });

            var window = MainWindow.Instance;
            if (window != null) WinRT.Interop.InitializeWithWindow.Initialize(picker, window.Handle);

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            await FileIO.WriteTextAsync(file, AppServices.SettingsRepo.ToJson());
            SaveStatusText.Text = "设置已导出到 " + file.Path;
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");

            var window = MainWindow.Instance;
            if (window != null) WinRT.Interop.InitializeWithWindow.Initialize(picker, window.Handle);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string json = await FileIO.ReadTextAsync(file);
            ValidateSettingsJson(json);

            var repo = AppServices.SettingsRepo;
            string path = repo.FilePath;
            string backup = Path.Combine(
                Path.GetDirectoryName(path) ?? AppServices.DataRoot,
                "settings.before-import.json");
            if (File.Exists(path)) File.Copy(path, backup, overwrite: true);

            File.WriteAllText(path, json, new UTF8Encoding(false));
            repo.Load();
            AppServices.RebindAiClient();

            LoadFromSettings();
            ApplyThemeEverywhere();

            SaveStatusText.Text = string.IsNullOrEmpty(repo.LoadWarning)
                ? "已从 " + file.Name + " 导入设置（原设置已备份）。"
                : "导入完成，但：" + repo.LoadWarning;
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = "导入失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 导入前先做最小校验。SettingsRepository 没有公开的「从 JSON 载入」入口，
    /// 这里的做法是校验通过后覆盖 settings.json 再调用公开的 Load()。
    /// </summary>
    private static void ValidateSettingsJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("这个文件不是 JSON 对象，无法作为设置导入。");
        }

        string[] knownKeys =
        {
            "provider", "targetLang", "sourceLang", "theme", "uiScale",
            "glossary", "wordLookupEnabled", "dictionarySource", "promptTemplateId",
        };
        foreach (string key in knownKeys)
        {
            if (document.RootElement.TryGetProperty(key, out _)) return;
        }

        throw new InvalidOperationException("这个 JSON 里没有任何可识别的设置字段，可能不是逐行翻译的导出文件。");
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        var repo = AppServices.SettingsRepo;
        await repo.FlushAsync();
        repo.Load();
        AppServices.RebindAiClient();
        LoadFromSettings();
        ApplyThemeEverywhere();
        SaveStatusText.Text = string.IsNullOrEmpty(repo.LoadWarning)
            ? "已重新载入 settings.json。"
            : "已重新载入，但：" + repo.LoadWarning;
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "恢复默认设置",
            Content = new TextBlock
            {
                Text = "会把当前 settings.json 备份为 settings.backup.json，然后重置为默认值。\n"
                    + "界面外观（主题 / 背景材质 / 强调色来源 / 翻译页布局）也会一并回到默认。\n\n文档与译文不受影响。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "恢复默认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var repo = AppServices.SettingsRepo;
            string path = repo.FilePath;
            string backup = Path.Combine(
                Path.GetDirectoryName(path) ?? AppServices.DataRoot,
                "settings.backup.json");
            if (File.Exists(path)) File.Copy(path, backup, overwrite: true);
            if (File.Exists(path)) File.Delete(path);

            repo.Load();
            AppServices.RebindAiClient();

            // 外观三项单独存 ui.json，恢复默认时一起重置
            UiSettingsStore.Apply(new UiSettings());

            LoadFromSettings();
            ApplyThemeEverywhere();
            SaveStatusText.Text = "已恢复默认设置（原设置备份在 " + Path.GetFileName(backup) + "）。";
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = "恢复默认失败：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------
    // 托盘与全局划词
    // ------------------------------------------------------------------

    private void FillHotkeyPresets()
    {
        HotkeyPresetBox.Items.Clear();
        foreach (string preset in HotkeySpec.Presets)
        {
            HotkeyPresetBox.Items.Add(new ComboBoxItem { Content = preset, Tag = preset });
        }
    }

    private static int IndexOfTag(ComboBox box, string tag)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem { Tag: string value }
                && string.Equals(value, tag, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private void OnHotkeyPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (HotkeyPresetBox.SelectedItem is ComboBoxItem { Tag: string tag } && tag.Length > 0)
        {
            HotkeyBox.Text = tag;
        }
    }

    /// <summary>
    /// 「测试」按钮：留 3 秒给用户切到别的程序里选中文字，然后走一次和热键完全相同的划词链路。
    /// </summary>
    private async void OnTestCaptureClick(object sender, RoutedEventArgs e)
    {
        if (GlobalCaptureService.IsRunning)
        {
            TrayStatusText.Text = "上一次划词还没结束，请稍等一下再试。";
            return;
        }

        for (int seconds = 3; seconds > 0; seconds--)
        {
            TrayStatusText.Text = seconds + " 秒后自动划词：请切到别的程序里选中要查的文字…";
            await Task.Delay(1000);
        }

        TrayStatusText.Text = "正在划词…";
        await GlobalCaptureService.TriggerAsync("设置页测试");
        RefreshTrayStatus();
    }

    /// <summary>把界面上的托盘设置写进 tray.json 并即时生效；返回需要提示给用户的中文说明。</summary>
    private string ApplyTraySettings()
    {
        var current = TraySettingsStore.Current;
        var next = current.Clone();

        next.TrayIconEnabled = TrayIconSwitch.IsOn;
        next.CloseAction = CloseActionBox.SelectedIndex == 1 ? TraySettings.CloseToExit : TraySettings.CloseToTray;
        next.HotkeyEnabled = GlobalHotkeySwitch.IsOn;

        string note = string.Empty;
        string typed = (HotkeyBox.Text ?? string.Empty).Trim();
        if (HotkeySpec.TryParse(typed, out var spec, out string error))
        {
            next.Hotkey = spec.Text;
        }
        else
        {
            next.Hotkey = current.Hotkey;
            note = "热键「" + typed + "」无法识别（" + error + "），仍用 " + current.Hotkey + "。";
        }

        TraySettingsStore.Apply(next);
        HotkeyBox.Text = next.Hotkey;

        string autoStartError = string.Empty;
        if (AutoStartSwitch.IsOn != AutoStartManager.IsEnabled
            && !AutoStartManager.Apply(AutoStartSwitch.IsOn, out autoStartError))
        {
            note = note.Length == 0 ? autoStartError : note + " " + autoStartError;
        }

        AutoStartSwitch.IsOn = AutoStartManager.IsEnabled;
        AutoStartStatusText.Text = BuildAutoStartStatus();

        return note;
    }

    private void RefreshTrayStatus()
    {
        var host = App.Tray;
        if (host == null)
        {
            TrayStatusText.Text = "托盘状态：未启动（托盘宿主没有起来，托盘图标与全局热键都不可用）";
            return;
        }

        var status = host.Status;
        string icon = status.TrayIconAdded
            ? "托盘图标已注册（Shell_NotifyIcon 返回 "
              + (status.ShellNotifyIconResult.HasValue ? status.ShellNotifyIconResult.Value.ToString() : "未调用") + "）"
            : "托盘图标未注册（Shell_NotifyIcon 返回 "
              + (status.ShellNotifyIconResult.HasValue ? status.ShellNotifyIconResult.Value.ToString() : "未调用")
              + "，错误码 " + status.ShellNotifyIconError + "：" + NativeMethods.DescribeError(status.ShellNotifyIconError) + "）";

        string hotkey;
        if (status.HotkeyFellBack)
        {
            hotkey = "设置里填的 " + status.ConfiguredHotkeyText + " 被别的程序占用了，当前实际生效的是 "
                + status.HotkeyText;
        }
        else if (status.HotkeyRegistered)
        {
            hotkey = "全局热键 " + status.HotkeyText + " 已注册";
        }
        else
        {
            hotkey = "全局热键 " + status.HotkeyText + " 未注册（RegisterHotKey 错误码 " + status.HotkeyError
                + "：" + NativeMethods.DescribeError(status.HotkeyError) + "）";
        }

        TrayStatusText.Text = "托盘状态：" + icon + "；" + hotkey + "。" + status.Message;
    }

    private static string BuildAutoStartStatus()
    {
        string command = AutoStartManager.RegisteredCommand;
        return command.Length == 0
            ? @"开机自启：未开启（HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下没有 LineTrans 项）"
            : "开机自启：已开启 → " + command;
    }

    // ------------------------------------------------------------------
    // 离线词库状态
    // ------------------------------------------------------------------

    private async Task RefreshDictionaryStatusAsync()
    {
        try
        {
            await AppServices.EnsureDictionaryLoadedAsync();
        }
        catch (Exception ex)
        {
            AppServices.Log("设置页载入词库失败：" + ex.Message);
        }
        RefreshDictStatus();
    }

    private void RefreshDictStatus()
    {
        if (!LocalDictionary.IsLoaded)
        {
            DictStatusText.Text = "离线词库状态：尚未载入（首次查词时会自动载入）";
            return;
        }

        DictStatusText.Text = "离线词库状态：已载入词条 " + LocalDictionary.Size.ToString("N0")
            + " 条，词形还原 " + LocalDictionary.LemmaSize.ToString("N0")
            + " 条，我的词库 " + LocalDictionary.ImportSize.ToString("N0")
            + " 条。本地词库只提供中文释义，" + DefinitionLanguageLabel() + "。";
    }

    private string DefinitionLanguageLabel() => DefinitionLangBox.SelectedIndex switch
    {
        1 => "中英对照只对 AI 兜底结果生效",
        2 => "仅英文只对 AI 兜底结果生效",
        _ => "释义语言已设为仅中文",
    };
}
