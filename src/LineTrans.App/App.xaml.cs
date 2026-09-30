using Microsoft.UI.Xaml;

namespace LineTrans.App;

/// <summary>
/// 应用入口。本批次只负责拉起主窗口，保持最小实现。
///
/// 后续挂载点（按计划加入顺序，落地时把对应注释替换成真实代码）：
///   ① 依赖注入 / 服务容器：LineTrans.Core 落地后在此构建 ServiceProvider。
///   ② 设置仓储：读取用户设置（主题跟随系统 / 语言 / API 配置）。
///   ③ 全局异常兜底：UnhandledException + AppDomain.UnhandledException + 崩溃日志。
///   ④ 单实例：多开时激活已有窗口。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // 挂载点①：初始化依赖注入容器（本批次无）
        // 挂载点③：注册全局异常处理（本批次无）
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // 挂载点②：窗口创建后应用用户设置与主题（本批次无）
        // 挂载点④：单实例激活已有窗口（本批次无）
    }
}
