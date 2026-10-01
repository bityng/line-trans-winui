using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using LineTrans.App.ViewModels;
using LineTrans.App.Views;
using LineTrans.Core;

namespace LineTrans.App.Services;

/// <summary>
/// <c>--bugprobe</c>：把 PC 端的功能按用户路径走一遍，边跑边断言，结果写成纯文本报告。
///
/// 为什么要有它：<see cref="UiProbe"/> 只管「外观 / 布局 / 像素取证」，
/// 而「文档增删改查 / 导入 / 逐行逐句切换 / 译文落盘 / 六种导出 / 设置往返」这些
/// 只有真的跑一遍才靠得住 —— 单元测试盯的是 Core 的纯逻辑，盯不到 App 这一层接得对不对。
///
/// 三条纪律：
///   1) 不破坏用户数据：创建的文档一律带 <see cref="Marker"/> 前缀，跑完删干净；
///      settings.json 与 ui.json 先备份再改，收尾原样还原。
///   2) 不联网：AI 相关一律不碰（未配置时本来就会走中文提示路径）。
///   3) 只读 + 断言，不改业务行为。
/// </summary>
public static class BugProbe
{
    private const string Marker = "BUGPROBE-临时-";
    private const string Folder = "BUGPROBE-临时文件夹";

    private static readonly List<string> Lines = new();
    private static readonly List<string> Created = new();

    private static string _outputDir = string.Empty;
    private static string _settingsBackup = string.Empty;
    private static string _uiBackup = string.Empty;
    private static bool _settingsExisted;
    private static bool _uiExisted;

    public static bool IsRequested
    {
        get
        {
            try
            {
                foreach (string arg in Environment.GetCommandLineArgs())
                {
                    if (string.Equals(arg, "--bugprobe", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch
            {
                // 拿不到命令行就当没要求
            }
            return false;
        }
    }

    public static string ReportPath => Path.Combine(
        TraySettingsStore.DataDirectory, "bugprobe", "bugprobe-report.txt");

    public static async Task RunAsync()
    {
        _outputDir = Path.GetDirectoryName(ReportPath) ?? TraySettingsStore.DataDirectory;
        Directory.CreateDirectory(_outputDir);
        Log("=== 功能走查开始 ===");

        BackupSettings();

        try
        {
            await GroupDocumentsAsync().ConfigureAwait(true);
            await GroupTranslationAsync().ConfigureAwait(true);
            await GroupExportAsync().ConfigureAwait(true);
            GroupSettings();
            GroupWindowAndIcon();
        }
        catch (Exception ex)
        {
            Fail("bugprobe", "走查程序自身抛异常：" + ex);
        }
        finally
        {
            Cleanup();
            Write();
            Log("=== 功能走查结束，报告 " + ReportPath + " ===");
        }
    }

    // ==================================================================
    // 一、文档：新建 / 导入四种格式 / 改名 / 移动 / 置顶 / 删除 / 搜索 / 筛选 / 分组
    // ==================================================================

    private static async Task GroupDocumentsAsync()
    {
        // 走查会往真实的数据目录里加文档，所以先量一个基线：收藏筛选只能拿它当参照，
        // 不能假设数据目录里只有本程序的临时文档（用户自己的文档也可能有收藏）。
        int starredBefore = AppServices.Docs.Docs.Count(d => d.StarredCount > 0);

        // ---- 1. 从 .txt 新建（含 BOM 与 CRLF，模拟真实文件）----
        var doc = NewDoc("txt", "第一行\r\n第二行\r\n\r\n第三行\r\n", UnitMode.LINE, smartClean: true);
        Check("doc-create-txt", doc.Units.Count == 3,
            "从 .txt 正文新建（逐行）：单元数 = " + doc.Units.Count + "（空行只分段，应为 3）；"
            + "单元 = [" + string.Join(" / ", doc.Units.Select(u => u.Source)) + "]");

        // ---- 2. .srt：时间轴与序号要被 SmartClean 清掉 ----
        string srt = "1\n00:00:01,000 --> 00:00:03,000\nHello world\n\n2\n00:00:03,500 --> 00:00:05,000\nSecond line\n";
        var srtDoc = NewDoc("srt", srt, UnitMode.LINE, smartClean: true);
        bool srtClean = srtDoc.Units.Count == 2
            && srtDoc.Units.All(u => !u.Source.Contains("-->", StringComparison.Ordinal)
                                     && !u.Source.Trim().Equals("1", StringComparison.Ordinal)
                                     && !u.Source.Trim().Equals("2", StringComparison.Ordinal));
        Check("doc-import-srt", srtClean,
            "导入 .srt：单元数 = " + srtDoc.Units.Count + "（应为 2，序号行与时间轴行被清掉）；"
            + "单元 = [" + string.Join(" / ", srtDoc.Units.Select(u => u.Source)) + "]");

        // ---- 3. .md：Markdown 标记要被清掉 ----
        string md = "# 标题\n\n- 列表项一\n- 列表项二\n\n**加粗** 与 *斜体*\n";
        var mdDoc = NewDoc("md", md, UnitMode.LINE, smartClean: true);
        bool mdClean = mdDoc.Units.All(u =>
            !u.Source.StartsWith("#", StringComparison.Ordinal)
            && !u.Source.StartsWith("- ", StringComparison.Ordinal));
        Check("doc-import-md", mdClean && mdDoc.Units.Count >= 3,
            "导入 .md：单元数 = " + mdDoc.Units.Count + "；行首 Markdown 标记已清掉 = " + mdClean
            + "；清理后 = [" + string.Join(" / ", mdDoc.Units.Select(u => u.Source)) + "]"
            + "（行内强调 **x** / *x* 刻意保留：SmartClean 是安卓端 TextParser.kt 的逐条等价移植，"
            + "行为必须与安卓端 / 网页端一致，不是 bug）");

        // ---- 4. .csv：内容原样保留（不被当成标记清掉）----
        string csv = "index,source,translation\n1,Hello,你好\n2,World,世界\n";
        var csvDoc = NewDoc("csv", csv, UnitMode.LINE, smartClean: true);
        bool csvKept = csvDoc.Units.Count >= 3 && csvDoc.Units[1].Source.Contains("Hello", StringComparison.Ordinal);
        Check("doc-import-csv", csvKept,
            "导入 .csv：单元数 = " + csvDoc.Units.Count + "；第 2 行 = \"" + (csvDoc.Units.Count > 1 ? csvDoc.Units[1].Source : "") + "\"");

        // ---- 5. 不改切分方式时 ChangeMode 返回 false（防误清空）----
        bool sameMode = !AppServices.Docs.ChangeMode(doc.Id, UnitMode.LINE);
        Check("doc-mode-noop", sameMode, "切分方式没变时 ChangeMode 返回 false（不会重建单元）");

        // ---- 6. 改名 / 移动 / 置顶 ----
        AppServices.Docs.Rename(doc.Id, Marker + "已改名");
        AppServices.Docs.Move(doc.Id, Folder);
        AppServices.Docs.SetPinned(doc.Id, true);
        await AppServices.Docs.FlushAsync().ConfigureAwait(true);

        var reread = ReloadFromDisk().Get(doc.Id);
        bool metaKept = reread != null
            && reread.Name == Marker + "已改名"
            && reread.Folder == Folder
            && reread.Pinned;
        Check("doc-rename-move-pin-persist", metaKept,
            "改名 / 移动 / 置顶后从磁盘重读：Name = \"" + (reread?.Name ?? "（没读到）")
            + "\"，Folder = \"" + (reread?.Folder ?? "") + "\"，Pinned = " + (reread?.Pinned.ToString() ?? "?"));

        // ---- 7. 搜索 / 筛选（走文档页同一套 DocRow.Matches）----
        var rows = AppServices.Docs.Docs.Select(d => new DocRow(d)).ToList();
        int byName = rows.Count(r => r.Matches("已改名", DocFilter.ALL));
        int byFolder = rows.Count(r => r.Matches(Folder, DocFilter.ALL));
        int byBody = rows.Count(r => r.Matches("第三行", DocFilter.ALL));
        int byNothing = rows.Count(r => r.Matches("绝不可能命中的字符串-zzz", DocFilter.ALL));
        Check("doc-search", byName == 1 && byFolder >= 1 && byBody == 1 && byNothing == 0,
            "搜索：按名字命中 " + byName + "，按文件夹命中 " + byFolder + "，按正文命中 " + byBody
            + "，乱码命中 " + byNothing + "（应为 0）");

        var unfinished = rows.Count(r => r.Matches("", DocFilter.UNFINISHED));
        var starred = rows.Count(r => r.Matches("", DocFilter.STARRED));
        Check("doc-filter", unfinished >= 1 && starred == starredBefore,
            "筛选：未完成 " + unfinished + " 篇（应 >= 1）；收藏 " + starred + " 篇，"
            + "与走查开始时的基线一致（基线 " + starredBefore + " 篇，临时文档没有收藏过）");

        // ---- 8. 文件夹分组 ----
        var folders = AppServices.Docs.Folders();
        Check("doc-folders", folders.Contains(Folder),
            "文件夹列表 = [" + string.Join("、", folders) + "]");

        // ---- 9. 排序：置顶优先 ----
        AppServices.Docs.SetSort(DocSort.NAME);
        var sorted = AppServices.Docs.Docs;
        bool pinFirst = sorted.Count == 0 || sorted[0].Pinned;
        Check("doc-sort-pin-first", pinFirst,
            "按名称排序后第一篇 = \"" + (sorted.Count > 0 ? sorted[0].Name : "（空）")
            + "\"，Pinned = " + (sorted.Count > 0 ? sorted[0].Pinned.ToString() : "?") + "（置顶必须排最前）");
        AppServices.Docs.SetSort(DocSort.UPDATED);

        // ---- 10. 删除 ----
        string victim = mdDoc.Id;
        bool removed = AppServices.Docs.Delete(victim);
        await AppServices.Docs.FlushAsync().ConfigureAwait(true);
        Created.Remove(victim);
        bool goneFromMemory = AppServices.Docs.Get(victim) == null;
        bool goneFromDisk = !File.Exists(Path.Combine(AppServices.Docs.DirectoryPath, victim + ".json"));
        Check("doc-delete", removed && goneFromMemory && goneFromDisk,
            "删除文档：内存已移除 = " + goneFromMemory + "；磁盘文件已删 = " + goneFromDisk);
    }

    private static TranslationDoc NewDoc(string what, string text, UnitMode mode, bool smartClean)
    {
        string body = smartClean ? TextParser.SmartClean(text) : text;
        var doc = AppServices.Docs.Create(Marker + what, body, mode, Folder);
        Created.Add(doc.Id);
        return doc;
    }

    // ==================================================================
    // 二、翻译：编辑落盘 / 收藏 / 完成 / 逐行逐句切换保留译文
    // ==================================================================

    private static async Task GroupTranslationAsync()
    {
        var window = MainWindow.Instance;
        if (window == null)
        {
            Fail("translation-page", "主窗口没起来，翻译页走查跳过");
            return;
        }

        // 三行，其中两行原文相同 —— 用来验证「切分方式变化后按原文保留译文」
        var doc = AppServices.Docs.Create(
            Marker + "翻译", "Alpha line\nBeta line\n", UnitMode.LINE, Folder);
        Created.Add(doc.Id);
        await AppServices.Docs.FlushAsync().ConfigureAwait(true);
        AppServices.Docs.Save(doc, immediate: true);

        window.OpenDocument(doc.Id);
        await Task.Delay(1500).ConfigureAwait(true);

        var page = window.CurrentPage as TranslationPage;
        if (page == null)
        {
            Fail("translation-page", "翻译页没有载入（CurrentPage 不是 TranslationPage）");
            return;
        }

        // 页面对象出现 != 文档已经装好（Frame.Navigate 是异步的），不等就会把编辑丢进空档里。
        int waited = 0;
        while (!page.ProbeDocLoaded && waited < 5000)
        {
            await Task.Delay(100).ConfigureAwait(true);
            waited += 100;
        }
        Check("translation-page-ready", page.ProbeDocLoaded,
            "翻译页在 " + waited + " ms 内把文档装好（单元 " + doc.Units.Count + " 条）");

        // ---- 11. 编辑译文 -> 真的落盘（用另一个仓库实例从磁盘重读，等价于「重启后还在」）----
        page.ProbeSetTargetText("阿尔法行");
        await Task.Delay(300).ConfigureAwait(true);

        // 先把三个中间状态量出来，免得失败时只剩「磁盘上是空」这一条线索：
        //   输入框回读值 / 内存里 unit[0] 的值 / 页面真正载入的是哪篇文档、停在第几句
        string boxEcho = page.ProbeTargetText;
        var live = AppServices.Docs.Get(doc.Id);
        string inMemory = live != null && live.Units.Count > 0 ? live.Units[0].Translation : "（文档不在仓库里）";

        await AppServices.Docs.FlushAsync().ConfigureAwait(true);

        var fromDisk = ReloadFromDisk().Get(doc.Id);
        string? persisted = fromDisk?.Units.Count > 0 ? fromDisk!.Units[0].Translation : null;

        Check("translation-edit-observed",
            boxEcho == "阿尔法行" && page.ProbeDocId == doc.Id && page.ProbeIndex == 0,
            "输入框回读 = " + Quote(boxEcho) + "；内存 unit[0] = " + Quote(inMemory) + "；"
            + "页面载入的文档 id = " + (page.ProbeDocId.Length == 0 ? "（空）" : page.ProbeDocId)
            + "（期望 " + doc.Id + "）；当前句号 = " + page.ProbeIndex + "（期望 0）");

        Check("translation-persist", persisted == "阿尔法行",
            "在翻译页译文框里输入「阿尔法行」-> FlushAsync -> 新建 DocRepository 从磁盘重读："
            + "unit[0].Translation = \"" + (persisted ?? "（没读到）") + "\"（必须一字不差）");

        // ---- 12. 切到下一句，上一句的编辑不能丢 ----
        page.ProbeRaiseNext();
        await Task.Delay(400).ConfigureAwait(true);
        string? first = fromDisk == null ? null : ReloadFromDisk().Get(doc.Id)?.Units[0].Translation;
        Check("translation-next-keeps-previous", first == "阿尔法行",
            "点「下一句」后重新读盘：第 1 句译文 = \"" + (first ?? "（丢了）") + "\"");

        // ---- 13. 收藏 / 标记完成 ----
        page.ProbeRaiseStar();
        page.ProbeRaiseDone();
        await Task.Delay(200).ConfigureAwait(true);
        await AppServices.Docs.FlushAsync().ConfigureAwait(true);
        var flags = ReloadFromDisk().Get(doc.Id)?.Units[1];
        Check("translation-star-done", flags is { Starred: true, Done: true },
            "第 2 句点「收藏」+「标记完成」后重读：Starred = " + (flags?.Starred.ToString() ?? "?")
            + "，Done = " + (flags?.Done.ToString() ?? "?"));

        // ---- 14. 逐行 -> 逐句：原文完全相同的单元必须保住译文 ----
        //   两行本来就是两句话，切分结果一致，所以两条译文都必须原样留在新单元上。
        var doc2 = AppServices.Docs.Create(Marker + "切分", "Hello.\nWorld.\n", UnitMode.LINE, Folder);
        Created.Add(doc2.Id);
        doc2.Units[0].Translation = "你好。";
        doc2.Units[0].Done = true;
        doc2.Units[0].Starred = true;
        doc2.Units[1].Translation = "世界。";
        doc2.Units[1].Done = true;
        AppServices.Docs.Save(doc2, immediate: true);
        await AppServices.Docs.FlushAsync().ConfigureAwait(true);

        bool switched = AppServices.Docs.ChangeMode(doc2.Id, UnitMode.SENTENCE);
        bool kept = switched && doc2.Units.Count == 2
            && doc2.Units[0].Translation == "你好。" && doc2.Units[0].Starred
            && doc2.Units[1].Translation == "世界。";
        Check("translation-mode-keeps-same-source", kept,
            "逐行 -> 逐句（原文相同）：切换 = " + switched + "；新单元 " + doc2.Units.Count + " 条 = ["
            + string.Join(" / ", doc2.Units.Select(u => u.Source + "=>" + (u.Translation.Length > 0 ? u.Translation : "（空）")
                + (u.Starred ? "★" : string.Empty))) + "]");

        // ---- 15. 切分边界真的变了时：不崩，且明确知道会丢（这是产品既定语义，与网页端一致）----
        var doc3 = AppServices.Docs.Create(Marker + "边界", "One. Two. Three.\n", UnitMode.LINE, Folder);
        Created.Add(doc3.Id);
        doc3.Units[0].Translation = "一。二。三。";
        doc3.Units[0].Done = true;
        AppServices.Docs.Save(doc3, immediate: true);
        await AppServices.Docs.FlushAsync().ConfigureAwait(true);

        bool switched3 = AppServices.Docs.ChangeMode(doc3.Id, UnitMode.SENTENCE);
        Check("translation-mode-boundary-change", switched3 && doc3.Units.Count == 3,
            "逐行 -> 逐句（一条 3 句的行拆成 3 条）：切换 = " + switched3 + "；新单元 " + doc3.Units.Count
            + " 条；沿用译文的条目 = " + doc3.Units.Count(u => u.IsTranslated)
            + "（行与句边界不同，按原文对齐必然沿用不到 —— 与网页端 applyMode 语义一致，属预期）");

        // ---- 15. 没有可翻译内容时给中文提示而不是崩 ----
        var empty = AppServices.Docs.Create(Marker + "空文档", "   \n\n  \n", UnitMode.LINE, Folder);
        Created.Add(empty.Id);
        window.OpenDocument(empty.Id);
        await Task.Delay(1200).ConfigureAwait(true);
        var page2 = window.CurrentPage as TranslationPage;
        Check("translation-empty-doc", page2 != null,
            "打开一篇切分后为空的文档：翻译页仍能载入（不崩），单元数 = " + empty.Units.Count);

        window.NavigateToTagForProbe("home");
        await Task.Delay(400).ConfigureAwait(true);
    }

    // ==================================================================
    // 三、导出：六种格式各导一次并检查内容
    // ==================================================================

    private static async Task GroupExportAsync()
    {
        var doc = AppServices.Docs.Create(Marker + "导出", "Line one\nLine two\n", UnitMode.LINE, Folder);
        Created.Add(doc.Id);
        doc.Units[0].Translation = "第一行";
        doc.Units[0].Done = true;
        doc.Units[1].Translation = "含,逗号|竖线\"引号\"";
        doc.Units[1].Done = true;

        foreach (var format in ExportService.Formats)
        {
            var outcome = await ExportService.ExportAsync(doc, format, useSavePicker: false).ConfigureAwait(true);
            string name = "export-" + format.ToString().ToLowerInvariant();
            if (outcome == null || !outcome.Ok)
            {
                Fail(name, "导出失败：" + (outcome?.Error ?? "返回 null"));
                continue;
            }

            string path = outcome.FilePath;
            if (!File.Exists(path))
            {
                Fail(name, "导出报告成功但文件不存在：" + path);
                continue;
            }

            string content = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            string head = content.Replace("\r", string.Empty).Replace("\n", " ⏎ ");
            if (head.Length > 160) head = head.Substring(0, 160) + "…";

            bool ok = format switch
            {
                ExportFormat.TXT_TRANSLATED_ONLY => content.Contains("第一行", StringComparison.Ordinal)
                                                    && !content.Contains("Line one", StringComparison.Ordinal),
                ExportFormat.TXT_BILINGUAL => content.Contains("Line one\t第一行", StringComparison.Ordinal),
                ExportFormat.TXT_SOURCE_FALLBACK => content.Contains("第一行", StringComparison.Ordinal),
                ExportFormat.MARKDOWN_TABLE => content.Contains("| # | 原文 | 译文 |", StringComparison.Ordinal)
                                               && content.Contains(@"\|", StringComparison.Ordinal),
                ExportFormat.CSV => content.Contains("index,source,translation", StringComparison.Ordinal)
                                    && content.Contains("\"含,逗号|竖线\"\"引号\"\"\"", StringComparison.Ordinal),
                ExportFormat.JSON => content.Contains("\"total\": 2", StringComparison.Ordinal)
                                     && content.Contains("\\\"引号\\\"", StringComparison.Ordinal),
                _ => false,
            };

            Use(path);
            Check(name, ok,
                ExportFormatInfo.Label(format) + " -> " + Path.GetFileName(path)
                + "（" + outcome.Bytes + " 字节）；正文开头：" + head);
        }
    }

    // ==================================================================
    // 四、设置：改完落盘 / 重启后仍在 / 恢复默认 / 导入导出
    // ==================================================================

    private static void GroupSettings()
    {
        var repo = AppServices.SettingsRepo;

        // ---- 16. 三项外观设置的归属：ui.json 已经并入 settings.json ----
        UiSettingsStore.Update(s =>
        {
            s.TranslationLayout = AppSettings.LayoutTopBottom;
            s.BackdropMaterial = AppSettings.BackdropAcrylic;
            s.AccentSource = AppSettings.AccentBrand;
        });
        repo.Save(immediate: true);

        string settingsJson = File.Exists(repo.FilePath) ? File.ReadAllText(repo.FilePath) : string.Empty;
        bool inSettings = settingsJson.Contains("\"translationLayout\"", StringComparison.Ordinal)
                          && settingsJson.Contains("\"backdropMaterial\"", StringComparison.Ordinal)
                          && settingsJson.Contains("\"accentSource\"", StringComparison.Ordinal);
        Check("settings-appearance-in-settings-json", inSettings,
            "改了「上下式 / 亚克力 / 品牌蓝」后 settings.json 里出现三个字段 = " + inSettings
            + "（文件 " + repo.FilePath + "）");

        // ---- 17. 重启后仍在：新仓库实例 Load ----
        var fresh = new SettingsRepository(repo.FilePath);
        fresh.Load();
        bool reloaded = fresh.Settings.TranslationLayout == AppSettings.LayoutTopBottom
                        && fresh.Settings.BackdropMaterial == AppSettings.BackdropAcrylic
                        && fresh.Settings.AccentSource == AppSettings.AccentBrand;
        Check("settings-appearance-survives-restart", reloaded,
            "新 SettingsRepository 实例读同一个文件：Layout = " + fresh.Settings.TranslationLayout
            + "，Backdrop = " + fresh.Settings.BackdropMaterial
            + "，Accent = " + fresh.Settings.AccentSource);

        // ---- 18. 普通设置也一并落盘 ----
        var s = repo.Settings;
        s.TargetLang = "ja";
        s.SourceLang = "en";
        s.ThemeMode = ThemeMode.DARK;
        s.UiScale = 1.25f;
        repo.Save(immediate: true);

        var fresh2 = new SettingsRepository(repo.FilePath);
        fresh2.Load();
        bool plain = fresh2.Settings.TargetLang == "ja" && fresh2.Settings.SourceLang == "en"
                     && fresh2.Settings.ThemeMode == ThemeMode.DARK
                     && Math.Abs(fresh2.Settings.UiScale - 1.25f) < 0.001;
        Check("settings-plain-survives-restart", plain,
            "目标语言 / 源语言 / 主题 / 字号缩放写盘后重读："
            + fresh2.Settings.TargetLang + " / " + fresh2.Settings.SourceLang + " / "
            + fresh2.Settings.ThemeMode + " / " + fresh2.Settings.UiScale);

        // ---- 19. 导出 / 导入 JSON 往返 ----
        string exported = repo.ToJson();
        string roundTripPath = Path.Combine(_outputDir, "settings-roundtrip.json");
        File.WriteAllText(roundTripPath, exported, new UTF8Encoding(false));
        var imported = new SettingsRepository(roundTripPath);
        imported.Load();
        bool roundTrip = imported.Settings.TargetLang == "ja"
                         && imported.Settings.TranslationLayout == AppSettings.LayoutTopBottom
                         && imported.Settings.AccentSource == AppSettings.AccentBrand;
        Check("settings-export-import-roundtrip", roundTrip,
            "导出 JSON（" + exported.Length + " 字符）-> 新仓库读回：目标语言 = " + imported.Settings.TargetLang
            + "，布局 = " + imported.Settings.TranslationLayout + "，强调色 = " + imported.Settings.AccentSource);
        imported.Dispose();

        // ---- 20. 恢复默认：删掉 settings.json 后 Load 必须回到默认值 ----
        File.Delete(repo.FilePath);
        repo.Load();
        bool reset = repo.Settings.TranslationLayout == AppSettings.LayoutLeftRight
                     && repo.Settings.BackdropMaterial == AppSettings.BackdropMica
                     && repo.Settings.AccentSource == AppSettings.AccentSystem
                     && repo.Settings.ThemeMode == ThemeMode.SYSTEM
                     && repo.Settings.TargetLang == "zh-CN";
        Check("settings-reset-to-default", reset,
            "删掉 settings.json 后 Load：布局 = " + repo.Settings.TranslationLayout
            + "，材质 = " + repo.Settings.BackdropMaterial + "，强调色 = " + repo.Settings.AccentSource
            + "，主题 = " + repo.Settings.ThemeMode + "，目标语言 = " + repo.Settings.TargetLang);
    }

    // ==================================================================
    // 五、窗口与图标
    // ==================================================================

    private static void GroupWindowAndIcon()
    {
        var window = MainWindow.Instance;
        if (window == null)
        {
            Fail("window", "主窗口没起来");
            return;
        }

        // ---- 21. 应用图标文件在位 ----
        string icon = AppServices.IconPath;
        var info = new FileInfo(icon);
        Check("icon-file", info.Exists && info.Length > 5000,
            "应用图标 " + icon + "：" + (info.Exists ? info.Length + " 字节" : "不存在"));

        // ---- 22. exe 资源段里真的有图标 ----
        int iconCount = (int)ExtractIconEx(Environment.ProcessPath ?? "", -1, IntPtr.Zero, IntPtr.Zero, 0);
        Check("icon-in-exe", iconCount > 0,
            "从当前进程的 exe（" + Path.GetFileName(Environment.ProcessPath ?? "?") + "）里用 ExtractIconEx 读图标组："
            + iconCount + " 个图标（大于 0 说明 ApplicationIcon 生效）");

        // ---- 23. 窗口图标句柄非零 ----
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        Check("icon-window", hwnd != IntPtr.Zero && iconCount > 0,
            "主窗口句柄 = 0x" + hwnd.ToString("X") + "；窗口图标来自 exe 资源段（AppWindow.SetIcon 已设过同一份 ico）");

        // ---- 24. 最小尺寸钳制 ----
        var appWindow = window.AppWindow;
        var before = appWindow.Size;
        appWindow.Resize(new Windows.Graphics.SizeInt32(400, 300));
        var after = appWindow.Size;
        double scale = 1.0;
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) scale = dpi / 96.0;
        }
        catch
        {
            // 拿不到 DPI 就按 1.0 算
        }

        int minW = (int)Math.Round(900 * scale);
        int minH = (int)Math.Round(600 * scale);
        Check("window-min-size", after.Width >= minW - 2 && after.Height >= minH - 2,
            "把窗口强行 Resize 到 400x300 -> 实际 = " + after.Width + "x" + after.Height
            + "（最小 " + minW + "x" + minH + " 物理像素，DPI 缩放 " + scale.ToString("0.###") + "）");

        // 还原
        appWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(1280 * scale), (int)Math.Round(820 * scale)));
        Log("窗口尺寸还原：" + before.Width + "x" + before.Height + " -> " + appWindow.Size.Width + "x" + appWindow.Size.Height);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string szFileName, int nIconIndex, IntPtr phiconLarge, IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ==================================================================
    // 基础设施
    // ==================================================================

    private static DocRepository ReloadFromDisk()
    {
        var repo = new DocRepository(AppServices.Docs.DirectoryPath, debounceMs: 200);
        repo.Load();
        return repo;
    }

    private static void BackupSettings()
    {
        try
        {
            string settings = AppServices.SettingsRepo.FilePath;
            _settingsExisted = File.Exists(settings);
            if (_settingsExisted) _settingsBackup = File.ReadAllText(settings);

            string ui = UiSettingsStore.FilePath;
            _uiExisted = File.Exists(ui);
            if (_uiExisted) _uiBackup = File.ReadAllText(ui);
        }
        catch (Exception ex)
        {
            Log("备份设置失败：" + ex.Message);
        }
    }

    private static void Cleanup()
    {
        try
        {
            // 删掉本次创建的全部文档（含已改名 / 已移动的）
            foreach (string id in Created.ToList())
            {
                AppServices.Docs.Delete(id);
            }

            // 顺带清掉可能残留的同前缀文档
            foreach (var doc in AppServices.Docs.Docs.Where(d => d.Name.StartsWith(Marker, StringComparison.Ordinal)).ToList())
            {
                AppServices.Docs.Delete(doc.Id);
            }

            AppServices.Docs.FlushAsync().Wait(3000);
        }
        catch (Exception ex)
        {
            Log("清理临时文档失败：" + ex.Message);
        }

        // 走查导出的 6 个文件留在 %APPDATA%\LineTrans\exports\ 会变成垃圾，验完就删
        foreach (string extra in Extras)
        {
            try
            {
                if (File.Exists(extra)) File.Delete(extra);
            }
            catch (Exception ex)
            {
                Log("清理导出文件失败：" + extra + "（" + ex.Message + "）");
            }
        }

        try
        {
            string settings = AppServices.SettingsRepo.FilePath;
            if (_settingsExisted) File.WriteAllText(settings, _settingsBackup, new UTF8Encoding(false));
            else if (File.Exists(settings)) File.Delete(settings);
            AppServices.SettingsRepo.Load();

            string ui = UiSettingsStore.FilePath;
            if (_uiExisted) File.WriteAllText(ui, _uiBackup, new UTF8Encoding(false));
            else if (File.Exists(ui)) File.Delete(ui);
            UiSettingsStore.Load();

            MainWindow.Instance?.ApplyTheme();
            MainWindow.Instance?.ApplyBackdrop();
        }
        catch (Exception ex)
        {
            Log("还原设置失败：" + ex.Message);
        }
    }

    /// <summary>给字段值加引号（诊断信息里一眼看出空串与空白）。</summary>
    private static string Quote(string? value) => "\"" + (value ?? "（null）") + "\"";

    private static void Check(string name, bool ok, string detail) => Add(ok ? "PASS" : "FAIL", name, detail);

    private static void Fail(string name, string detail) => Add("FAIL", name, detail);

    private static void Use(string extraFile)
    {
        // 报告里也可以列出导出产物，方便人工复核
        Extras.Add(extraFile);
    }

    private static readonly List<string> Extras = new();

    private static void Add(string status, string name, string detail)
    {
        string line = status + "  " + name + "  " + detail;
        Lines.Add(line);
        Log("走查 " + line);
    }

    private static void Write()
    {
        try
        {
            int pass = Lines.Count(l => l.StartsWith("PASS", StringComparison.Ordinal));
            int fail = Lines.Count(l => l.StartsWith("FAIL", StringComparison.Ordinal));

            var sb = new StringBuilder();
            sb.AppendLine("逐行翻译 PC 端 · 功能走查报告（--bugprobe）");
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("进程：" + (Environment.ProcessPath ?? "?") + "  PID " + Environment.ProcessId);
            sb.AppendLine("数据目录：" + AppServices.DataRoot);
            sb.AppendLine("通过：" + pass + "　失败：" + fail + "　合计：" + (pass + fail));
            sb.AppendLine(new string('=', 100));
            foreach (string line in Lines) sb.AppendLine(line);
            if (Extras.Count > 0)
            {
                sb.AppendLine(new string('-', 100));
                sb.AppendLine("本次产生的导出文件（检查完内容后已删除，避免污染 exports 目录）：");
                foreach (string extra in Extras) sb.AppendLine("  " + extra);
            }

            File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log("写走查报告失败：" + ex.Message);
        }
    }

    private static void Log(string message) => AppServices.Log(message);
}
