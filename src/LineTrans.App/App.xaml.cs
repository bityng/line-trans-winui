using System;

using LineTrans.App.Services;

using Microsoft.UI.Xaml;

namespace LineTrans.App;

/// <summary>
/// 应用入口：拉起主窗口之前先把服务容器建好（设置 / 文档仓库 / AI 客户端 / 离线词库）。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        AppServices.Initialize();
        AppServices.WarmUpDictionary();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Closed += OnWindowClosed;
        _window.Activate();

        AppServices.Log("主窗口已激活");
    }

    /// <summary>窗口关闭：同步落盘（DocRepository 平时是防抖写盘，不刷会丢最后一次编辑）。</summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        AppServices.Log("窗口关闭，开始落盘");
        AppServices.Shutdown();
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
