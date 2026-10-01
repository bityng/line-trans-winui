using System;
using System.Threading.Tasks;

using LineTrans.App.Services;

using Microsoft.UI.Xaml;

namespace LineTrans.App;

/// <summary>
/// 应用入口：拉起主窗口之前先把服务容器建好（设置 / 文档仓库 / AI 客户端 / 离线词库），
/// 主窗口之后再把托盘宿主与全局热键线程拉起来。
///
/// 拿到 <c>--selftest</c> 时会跑一遍自检（托盘 / 热键 / 剪贴板保护 / 划词链路），
/// 把原始结果写进 %APPDATA%\LineTrans\selftest-report.json 然后退出。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>托盘 / 全局热键宿主（未启动时为 null）。</summary>
    public static TrayIconHost? Tray { get; private set; }

    /// <summary>是否正在走「真退出」流程（区别于「关闭窗口 = 最小化到托盘」）。</summary>
    public static bool IsExitingNow { get; private set; }

    public App()
    {
        InitializeComponent();

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        AppServices.Initialize();
        AppServices.WarmUpDictionary();
        TraySettingsStore.Load();
        // PC 端外观设置（翻译页布局 / 背景材质 / 强调色来源）：权威值在 settings.json，
        // ui.json 只是老版本的兼容镜像（settings.json 里没有这三项时才会被迁移读一次）
        UiSettingsStore.Load();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Closed += OnWindowClosed;
        _window.Activate();

        StartTrayHost();

        AppServices.Log("主窗口已激活");

        if (SelfTest.IsRequested)
        {
            _ = RunSelfTestAsync();
        }
        else if (UiProbe.IsRequested)
        {
            _ = RunUiProbeAsync();
        }
        else if (BugProbe.IsRequested)
        {
            _ = RunBugProbeAsync();
        }
        else if (HasArgument("--minimized"))
        {
            // 开机自启拉起来的那一次：直接缩到托盘，别跳出来
            MainWindow.Instance?.HideToTray();
        }
    }

    // ------------------------------------------------------------------
    // 托盘
    // ------------------------------------------------------------------

    private void StartTrayHost()
    {
        try
        {
            var host = new TrayIconHost();
            Tray = host;

            host.CaptureRequested += () => RunOnUiThread(() => _ = GlobalCaptureService.TriggerAsync("全局热键"));
            host.EscapePressed += () => RunOnUiThread(GlobalCaptureService.ClosePopup);
            host.ShowWindowRequested += () => RunOnUiThread(() => MainWindow.Instance?.ShowFromTray());
            host.SettingsRequested += () => RunOnUiThread(() => MainWindow.Instance?.ShowSettingsPage());
            host.ExitRequested += () => RunOnUiThread(ExitApplication);

            host.Start(TraySettingsStore.Current);

            TraySettingsStore.Changed += () => host.Apply(TraySettingsStore.Current);

            var status = host.Status;
            AppServices.Log("托盘宿主已启动：图标=" + status.TrayIconAdded + "，热键=" + status.HotkeyText
                + "（" + (status.HotkeyRegistered ? "已注册" : "未注册") + "）");
        }
        catch (Exception ex)
        {
            AppServices.Log("托盘宿主启动失败：" + ex);
        }
    }

    /// <summary>把动作切到 UI 线程执行（托盘线程 / 热键线程回调进来时用）。</summary>
    public static void RunOnUiThread(Action action)
    {
        var queue = MainWindow.Instance?.DispatcherQueue;
        if (queue == null)
        {
            return;
        }

        if (queue.HasThreadAccess) action();
        else queue.TryEnqueue(() => action());
    }

    /// <summary>
    /// 真正退出程序（托盘菜单「退出」与自检结束时调用）。
    /// 先把托盘拆掉（图标和热键一并摘除），再关主窗口，最后 Exit。
    /// </summary>
    private void ExitApplication()
    {
        if (IsExitingNow) return;
        IsExitingNow = true;

        AppServices.Log("退出程序");

        try
        {
            Tray?.Dispose();
        }
        catch (Exception ex)
        {
            AppServices.Log("关闭托盘失败：" + ex.Message);
        }
        Tray = null;

        try
        {
            _window?.Close();
        }
        catch (Exception ex)
        {
            AppServices.Log("关闭窗口失败：" + ex.Message);
        }

        Current.Exit();
    }

    private static bool HasArgument(string value)
    {
        try
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch
        {
            // 拿不到命令行就当作没有这个参数
        }
        return false;
    }

    private async Task RunSelfTestAsync()
    {
        try
        {
            await SelfTest.RunAsync();
        }
        finally
        {
            ExitApplication();
        }
    }

    /// <summary>
    /// <c>--uiprobe</c>：原生外观 / 毛玻璃 / 翻译页布局的截图与像素取证，
    /// 跑完写报告到 %APPDATA%\LineTrans\uiprobe\ 并退出。
    /// </summary>
    private async Task RunUiProbeAsync()
    {
        try
        {
            await UiProbe.RunAsync();
        }
        catch (Exception ex)
        {
            AppServices.Log("取证失败：" + ex);
        }
        finally
        {
            ExitApplication();
        }
    }

    /// <summary>
    /// <c>--bugprobe</c>：把文档 / 翻译 / 导出 / 设置 / 图标 / 窗口这几条用户路径走一遍，
    /// 结果写成纯文本报告到 %APPDATA%\LineTrans\bugprobe\ 并退出。
    /// </summary>
    private async Task RunBugProbeAsync()
    {
        try
        {
            await BugProbe.RunAsync();
        }
        catch (Exception ex)
        {
            AppServices.Log("走查失败：" + ex);
        }
        finally
        {
            ExitApplication();
        }
    }

    /// <summary>
    /// 窗口关闭：同步落盘（DocRepository 平时是防抖写盘，不刷会丢最后一次编辑）。
    /// 注意「关闭窗口」在设置里选了「最小化到托盘」时会被 MainWindow 拦下，这里不会被调到。
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // 先立旗：关闭过程中还会有异步回调进来（主题变化、设置变化），
        // 它们据此判断「正在退出」并且不再碰窗口 API。
        IsExitingNow = true;

        AppServices.Log("窗口关闭，开始落盘");

        try
        {
            Tray?.Dispose();
        }
        catch (Exception ex)
        {
            AppServices.Log("关闭托盘失败：" + ex.Message);
        }
        Tray = null;

        AppServices.Shutdown();
        IsExitingNow = true;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppServices.Log("未处理异常：" + e.Message + Environment.NewLine + e.Exception);
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        AppServices.Log("AppDomain 未处理异常：" + (e.ExceptionObject as Exception));
    }
}
