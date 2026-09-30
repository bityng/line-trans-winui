using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;

using LineTrans.App.Services;
using LineTrans.Core.Dictionary;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LineTrans.App.Views;

/// <summary>
/// 关于页。AGPL-3.0 第 13 条的合规落点：
/// 应用名 / 版本号 / 许可 / 三个源码仓库入口，缺一不可。
/// </summary>
public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();

        FontSize = AppServices.BodyFontSize;

        VersionText.Text = ReadVersion();
        CopyrightText.Text = "Copyright (C) 2026 LineTrans contributors　·　版本 " + VersionText.Text;
        RepoAndroidButton.Tag = RepoAndroidUrls.Android;
        RepoAndroidButton.Content = RepoAndroidUrls.Android;
        RepoWebButton.Tag = RepoAndroidUrls.Web;
        RepoWebButton.Content = RepoAndroidUrls.Web;
        RepoWinUiButton.Tag = RepoAndroidUrls.WinUi;
        RepoWinUiButton.Content = RepoAndroidUrls.WinUi;
    }

    /// <summary>三个仓库地址常量，方便自测与替换。</summary>
    public static class RepoAndroidUrls
    {
        public const string Android = "https://github.com/bityng/line-trans-android";
        public const string Web = "https://github.com/bityng/line-trans-web";
        public const string WinUi = "https://github.com/bityng/line-trans-winui";
    }

    /// <summary>版本号取自 csproj 的 &lt;Version&gt;（经 AssemblyInformationalVersion 暴露）。</summary>
    private static string ReadVersion()
    {
        try
        {
            var assembly = typeof(AboutPage).Assembly;
            string? info = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                return plus > 0 ? info.Substring(0, plus) : info!;
            }
            return assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        }
        catch
        {
            return "0.1.0";
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RenderDictionaryStat();
        _ = RefreshDictionaryStatAsync();
    }

    private async Task RefreshDictionaryStatAsync()
    {
        try
        {
            await AppServices.EnsureDictionaryLoadedAsync();
        }
        catch (Exception ex)
        {
            AppServices.Log("关于页载入词库失败：" + ex.Message);
        }
        RenderDictionaryStat();
    }

    private void RenderDictionaryStat()
    {
        if (!LocalDictionary.IsLoaded)
        {
            DictionaryStatText.Text = "离线词库状态：尚未载入（首次查询时会自动载入）";
            return;
        }

        DictionaryStatText.Text = "离线词库状态：已载入词条 " + LocalDictionary.Size.ToString("N0")
            + " 条，词形还原 " + LocalDictionary.LemmaSize.ToString("N0")
            + " 条，我的词库 " + LocalDictionary.ImportSize.ToString("N0") + " 条";
    }

    private void OnRepoClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && url.Length > 0)
        {
            AppServices.OpenUrl(url);
        }
    }

    private void OnOpenDataDirClick(object sender, RoutedEventArgs e)
        => AppServices.OpenDirectory(AppServices.DataRoot);

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppServices.Log("用户在关于页打开了运行日志");
            Process.Start(new ProcessStartInfo(AppServices.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppServices.Log("打开日志失败：" + ex.Message);
        }
    }
}
