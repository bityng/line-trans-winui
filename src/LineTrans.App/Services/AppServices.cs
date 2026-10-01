using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LineTrans.Core;
using LineTrans.Core.Dictionary;

using Microsoft.UI.Xaml;

namespace LineTrans.App.Services;

/// <summary>
/// 全局服务容器：把 Core / Dictionary 两个纯逻辑类库接到界面上。
///
/// 生命周期：<see cref="Initialize"/> 在 App 构造函数里调用一次（早于任何窗口），
/// <see cref="Shutdown"/> 在主窗口关闭时调用（同步落盘，保证不丢数据）。
/// </summary>
public static class AppServices
{
    private static readonly object Gate = new();

    private static bool _initialized;

    /// <summary>AiClient 当前绑定的 AppSettings 实例，用于发现 Load() 换实例。</summary>
    private static AppSettings? _boundSettings;

    /// <summary>文档仓库（%APPDATA%\LineTrans\docs）。</summary>
    public static DocRepository Docs { get; private set; } = null!;

    /// <summary>设置仓库（%APPDATA%\LineTrans\settings.json）。</summary>
    public static SettingsRepository SettingsRepo { get; private set; } = null!;

    /// <summary>AI 客户端。持有 SettingsRepo.Settings 的引用，设置改动即时生效。</summary>
    public static AiClient Ai { get; private set; } = null!;

    /// <summary>我的词库（生词本），落在 %APPDATA%\LineTrans\wordbook.txt。</summary>
    public static Wordbook Words { get; private set; } = null!;

    /// <summary>划词查义服务（离线本地词库 + 可选 AI 兜底）。</summary>
    public static DictionaryService Lookup { get; private set; } = null!;

    /// <summary>当前设置（等价 SettingsRepo.Settings 的快捷方式）。</summary>
    public static AppSettings Settings => SettingsRepo.Settings;

    /// <summary>数据根目录。</summary>
    public static string DataRoot { get; private set; } = string.Empty;

    /// <summary>内置离线词库 core.tsv（随程序输出到 dict\）。</summary>
    public static string CoreDictPath => Path.Combine(AppContext.BaseDirectory, "dict", "core.tsv");

    /// <summary>内置离线词库 lemma.tsv。</summary>
    public static string LemmaDictPath => Path.Combine(AppContext.BaseDirectory, "dict", "lemma.tsv");

    /// <summary>
    /// 应用图标（多尺寸 .ico）。exe 上那份由 csproj 的 ApplicationIcon 嵌进资源段，
    /// 这一份随 Assets 复制到输出目录，给窗口（AppWindow.SetIcon）与托盘（LoadImageW）在运行期读。
    /// </summary>
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "LineTrans.ico");

    /// <summary>生词本文件。</summary>
    public static string WordbookPath => Path.Combine(DataRoot, "wordbook.txt");

    /// <summary>运行日志（启动 / 词库加载 / 未处理异常）。</summary>
    public static string LogPath => Path.Combine(DataRoot, "pc-app.log");

    // ------------------------------------------------------------------
    // 初始化
    // ------------------------------------------------------------------

    /// <summary>构建全部服务。可重复调用，只有第一次生效。</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;

            DataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LineTrans");
            Directory.CreateDirectory(DataRoot);

            SettingsRepo = new SettingsRepository();
            SettingsRepo.Load();

            Docs = new DocRepository(debounceMs: SettingsRepo.Settings.AutoSaveMs);
            Docs.Load();

            // AiClient 必须在 Load 之后建：Load 会替换 Settings 实例。
            Ai = new AiClient(SettingsRepo.Settings);
            _boundSettings = SettingsRepo.Settings;

            Words = new Wordbook(WordbookPath);
            Words.DefinitionLanguage = SettingsRepo.Settings.DefinitionLanguage;
            Words.ApplyToLocalDictionary();

            RebuildLookup();

            SettingsRepo.Changed += OnSettingsChanged;

            _initialized = true;
        }

        Log("启动：数据目录 " + DataRoot);
        if (Docs.LoadErrors.Count > 0) Log("文档加载跳过 " + Docs.LoadErrors.Count + " 个文件");
        if (SettingsRepo.LoadWarning is { Length: > 0 } warning) Log("设置：" + warning);
    }

    /// <summary>设置变化时把影响运行时的字段同步到各服务。</summary>
    private static void OnSettingsChanged()
    {
        try
        {
            // SettingsRepository.Load() 会整体替换 AppSettings 实例，
            // AiClient 绑定的是旧实例，必须重绑，否则改完设置不生效。
            if (!ReferenceEquals(_boundSettings, Settings)) RebindAiClient();

            Docs.DebounceMs = Settings.AutoSaveMs;
            Words.DefinitionLanguage = Settings.DefinitionLanguage;
            RebuildLookup();
        }
        catch (Exception ex)
        {
            Log("设置同步失败：" + ex.Message);
        }
    }

    /// <summary>把 AiClient 重新绑到当前的 AppSettings 实例上（设置被整体替换后调用）。</summary>
    public static void RebindAiClient()
    {
        var previous = Ai;
        Ai = new AiClient(Settings);
        _boundSettings = Settings;
        RebuildLookup();
        try
        {
            previous?.Dispose();
        }
        catch
        {
            // 旧客户端释放失败无所谓
        }
    }

    /// <summary>按当前设置重建划词服务。</summary>
    private static void RebuildLookup()
    {
        var s = Settings;
        var options = new DictionaryOptions
        {
            DictionarySource = s.DictionarySource,
            LocalDictionaryEnabled = s.LocalDictionaryEnabled,
            DictionaryAiExplain = s.LookupAiFallback,
            DefinitionLanguage = s.DefinitionLanguage,
            OxfordAppId = s.OxfordAppId,
            OxfordAppKey = s.OxfordAppKey,
        };
        IAiExplainProvider? ai = s.LookupAiFallback ? new AiExplainProvider(() => Ai) : null;
        Lookup = new DictionaryService(options, ai, null);
    }

    // ------------------------------------------------------------------
    // 词库
    // ------------------------------------------------------------------

    /// <summary>
    /// 加载内置离线词库。必须显式给出路径：Core 的候选目录（exe 目录 / 当前目录 / data 子目录）
    /// 都不包含 csproj 里 Link 出来的 dict\ 子目录，传 null 会一直加载不到。
    /// </summary>
    public static async Task EnsureDictionaryLoadedAsync()
    {
        if (LocalDictionary.IsLoaded) return;

        string? core = File.Exists(CoreDictPath) ? CoreDictPath : null;
        string? lemma = File.Exists(LemmaDictPath) ? LemmaDictPath : null;
        await LocalDictionary.EnsureLoadedAsync(core, lemma).ConfigureAwait(false);
        if (!LocalDictionary.IsLoaded)
        {
            Log("离线词库未加载：" + (LocalDictionary.LastLoadError?.Message ?? "文件缺失"));
        }
    }

    /// <summary>启动时后台预热词库，不阻塞界面。</summary>
    public static void WarmUpDictionary()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureDictionaryLoadedAsync().ConfigureAwait(false);
                Log("离线词库就绪：core=" + LocalDictionary.Size + " lemma=" + LocalDictionary.LemmaSize);
            }
            catch (Exception ex)
            {
                Log("离线词库预热失败：" + ex.Message);
            }
        });
    }

    /// <summary>
    /// 划词查义：关闭 AI 兜底时只查本地词库（离线秒出，且提示语准确）；
    /// 打开 AI 兜底时才走完整的 DictionaryService 来源链。
    /// </summary>
    public static async Task<DictEntry> LookupWordAsync(string word, CancellationToken cancellationToken = default)
    {
        await EnsureDictionaryLoadedAsync().ConfigureAwait(false);
        if (!Settings.LookupAiFallback)
        {
            return DictionaryService.Local(word);
        }
        return await Lookup.LookupAsync(word, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // 主题 / 杂项
    // ------------------------------------------------------------------

    /// <summary>把当前主题与字号缩放应用到窗口根元素。</summary>
    public static void ApplyTheme(FrameworkElement? root)
    {
        if (root == null) return;
        root.RequestedTheme = Settings.ThemeMode switch
        {
            ThemeMode.LIGHT => ElementTheme.Light,
            ThemeMode.DARK => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>字号缩放后的正文尺寸（设计系统基准 14）。</summary>
    public static double BodyFontSize => Math.Round(14.0 * Settings.UiScale, 1);

    /// <summary>用系统默认浏览器打开链接。</summary>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("打开链接失败 " + url + "：" + ex.Message);
        }
    }

    /// <summary>在资源管理器里打开目录。</summary>
    public static void OpenDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("打开目录失败 " + path + "：" + ex.Message);
        }
    }

    /// <summary>日志文件的写锁：托盘线程与 UI 线程现在都会写日志，不加锁会互相截断。</summary>
    private static readonly object LogGate = new();

    /// <summary>写一行日志（失败静默，绝不影响主流程）。</summary>
    public static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // 日志失败无所谓
        }
    }

    // ------------------------------------------------------------------
    // 退出
    // ------------------------------------------------------------------

    /// <summary>
    /// 同步落盘并释放。必须在窗口关闭前调用：DocRepository 的写盘是防抖的，
    /// 不刷就会丢掉最后一次编辑。
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            if (Docs != null)
            {
                Log("退出：待写文档 " + Docs.PendingCount + " 篇");
                Docs.FlushAsync().Wait(5000);
                Docs.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log("文档落盘失败：" + ex.Message);
        }

        try
        {
            if (SettingsRepo != null)
            {
                SettingsRepo.FlushAsync().Wait(3000);
                SettingsRepo.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log("设置落盘失败：" + ex.Message);
        }

        try
        {
            Words?.Flush();
        }
        catch (Exception ex)
        {
            Log("生词本落盘失败：" + ex.Message);
        }

        try
        {
            Ai?.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
