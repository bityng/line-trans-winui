using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LineTrans.Core;

namespace LineTrans.Core.Tests;

/// <summary>
/// 手写断言的控制台自测程序（不依赖任何第三方测试框架）。
///
/// 用法：
///   dotnet run --project tests\LineTrans.Core.Tests              # 跑全部断言
///   dotnet run --project tests\LineTrans.Core.Tests -- --corpus tests\corpus.json --out tests\cs-output.json
///                                                               # 用同一份语料跑 C# 版，产出 cs-output.json 供差分比对
/// 退出码：0 = 全部通过，1 = 有失败。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能不支持，忽略 */ }

        if (args.Length > 0 && args[0] == "--corpus")
        {
            return RunCorpusMode(args);
        }

        Console.WriteLine("===== LineTrans.Core 自测 =====");
        RunSection53();
        RunEdgeCases();
        RunKotlinPrimary();
        RunLineMode();
        RunSmartClean();
        RunCsvAndTable();
        RunDetectLanguage();
        RunExport();
        RunCost();
        RunModels();

        Console.WriteLine();
        Console.WriteLine("===== 汇总 =====");
        Console.WriteLine("通过: " + _passed);
        Console.WriteLine("失败: " + _failed);
        Console.WriteLine(_failed == 0 ? "结果: 全部通过" : "结果: 有失败项");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // §5.3 用例表
    // ------------------------------------------------------------------
    private static void RunSection53()
    {
        Console.WriteLine();
        Console.WriteLine("--- §5.3 用例表 ---");

        CheckUnits("sec53-01 Mr./3.5/p.m.", new[]
        {
            "Mr. Smith went to Washington.",
            "He arrived at 3.5 p.m. yesterday.",
        }, Sentences("Mr. Smith went to Washington. He arrived at 3.5 p.m. yesterday."));

        CheckUnits("sec53-02 U.S.", new[]
        {
            "The U.S. economy grew.",
            "It was unexpected.",
        }, Sentences("The U.S. economy grew. It was unexpected."));

        CheckUnits("sec53-03 Dr./J. K.", new[]
        {
            "Dr. J. K. Rowling wrote it.",
            "Really.",
        }, Sentences("Dr. J. K. Rowling wrote it. Really."));

        CheckUnits("sec53-04 Wait... what? Yes!", new[]
        {
            "Wait…",
            "what?",
            "Yes!",
        }, Sentences("Wait... what? Yes!"));

        CheckUnits("sec53-05 网址", new[]
        {
            "See http://example.com/path.",
            "It works.",
        }, Sentences("See http://example.com/path. It works."));

        CheckUnits("sec53-06 三行都以 . 结尾", new[]
        {
            "dog.",
            "This is a new line.",
            "And a third line.",
        }, Sentences("dog.\nThis is a new line.\nAnd a third line."));

        CheckUnits("sec53-07 中文软换行合成 1 句", new[]
        {
            "这是一个中文段落，中间只是换行而已。",
        }, Sentences("这是一个中文段落，\n中间只是换行而已。"));

        CheckUnits("sec53-08 中文省略号", new[]
        {
            "等等…",
            "你说什么？",
            "好的！",
        }, Sentences("等等……你说什么？好的！"));
    }

    // ------------------------------------------------------------------
    // 边界 / 异常输入
    // ------------------------------------------------------------------
    private static void RunEdgeCases()
    {
        Console.WriteLine();
        Console.WriteLine("--- 边界与异常输入 ---");

        CheckUnits("空字符串 → 0 句", Array.Empty<string>(), Sentences(""));
        CheckUnits("纯空白 → 0 句", Array.Empty<string>(), Sentences("   \t \n  "));
        CheckUnits("纯换行 → 0 句", Array.Empty<string>(), Sentences("\n\n\n"));
        CheckUnits("单字符", new[] { "a" }, Sentences("a"));
        CheckUnits("单个句点（标点也是一句）", new[] { "." }, Sentences("."));
        CheckUnits("三个点 → 一个省略号", new[] { "…" }, Sentences("..."));
        CheckUnits("只有标点（碎片并回前句）", new[] { "！！！" }, Sentences("！！！"));

        // 注意：单字母片段会被「碎片合并」并回上一句，所以 a. 与 b. 合成 a.b.
        CheckUnits("CRLF", new[] { "a.b." }, Sentences("a.\r\nb."));
        CheckUnits("单独 CR", new[] { "a.b." }, Sentences("a.\rb."));
        CheckUnits("CRLF 空行分段", new[] { "a.b." }, Sentences("a.\r\n\r\nb."));
        CheckUnits("空行里夹空格制表符", new[] { "a.b." }, Sentences("a.\n \t \nb."));
        CheckUnits("两个多字母句子按空行分开", new[] { "Hello there.", "General Kenobi." },
            Sentences("Hello there.\r\n\r\nGeneral Kenobi."));

        CheckUnits("制表符夹在句中", new[] { "a\tb\tc" }, Sentences("a\tb\tc"));
        CheckUnits("emoji 不参与碎片判定", new[] { "Hi 😀 there." }, Sentences("Hi 😀 there."));
        CheckUnits("首尾空白被 trim", new[] { "前后有空格。" }, Sentences("  前后有空格。  "));
        CheckUnits("超长无标点串仍是一句", new[] { new string('x', 2000) + "." }, Sentences(new string('x', 2000) + "."));

        // 软换行：英文补空格、中文不补
        CheckUnits("英文软换行补空格", new[] { "This is a soft wrapped line." }, Sentences("This is a soft\nwrapped line."));
        CheckUnits("上一行以句号结尾", new[] { "dog.", "This is a new line." }, Sentences("dog.\nThis is a new line."));
        CheckUnits("引号结尾跨行补空格", new[] { "quote\" next line" }, Sentences("quote\"\nnext line"));
        CheckUnits("括号结尾跨行补空格", new[] { "(bracket) next" }, Sentences("(bracket)\nnext"));

        // 省略号的各种形态
        CheckUnits("四点省略号", new[] { "Hmm…", "Really?" }, Sentences("Hmm.... Really?"));
        CheckUnits("单独 … 也断句", new[] { "Well…", "I think so." }, Sentences("Well… I think so."));
        CheckUnits("省略号后跟叹号", new[] { "No…!", "Never!" }, Sentences("No…! Never!"));
        CheckUnits("混合省略号（单字碎片并回）", new[] { "a..b…c…d" }, Sentences("a..b...c….d"));

        CheckUnits("中文省略号", new[] { "等等…", "你说什么？", "好的！" }, Sentences("等等……你说什么？好的！"));
        CheckUnits("中文分号断句", new[] { "他说；", "然后又走了。" }, Sentences("他说；然后又走了。"));
        CheckUnits("英文分号不断句", new[] { "a; b.c" }, Sentences("a; b. c"));
        CheckUnits("中文逗号不断句", new[] { "甲，乙，丙。" }, Sentences("甲，乙，丙。"));
    }

    // ------------------------------------------------------------------
    // Kotlin 主实现与网页端 server.js 的既有差异：
    // 这些用例上两端本来就不同，C# 一律跟随 Kotlin（主实现），
    // 并在 tools/known-divergences.json 里登记，差分脚本会把它们单独归类。
    // ------------------------------------------------------------------
    private static void RunKotlinPrimary()
    {
        Console.WriteLine();
        Console.WriteLine("--- Kotlin 主实现与网页端的既有差异（C# 跟随 Kotlin）---");

        // Kotlin 用 CharSequence.lines()，能识别单独的 \r；server.js 用的是 split(/\r?\n/)。
        CheckEq("单独 CR 的 smartClean（Kotlin 拆行，JS 不拆）",
            "Text here.",
            TextParser.SmartClean("1\r00:00:01,000 --> 00:00:02,000\rText here."));

        // Kotlin 的 trim 用 Character.isWhitespace||isSpaceChar，不把 U+FEFF 当空白；JS 的 trim 会去掉它。
        CheckEq("BOM + WEBVTT（Kotlin 保留 BOM，于是该行不被丢弃）",
            "\uFEFFWEBVTT",
            TextParser.SmartClean("\uFEFFWEBVTT"));

        // Java 正则的 \s 只认 ASCII 空白；JS 的 \s 还认 U+00A0。
        CheckEq("Markdown 标记里的 NBSP（Java \\s 不匹配，保留整行）",
            "#\u00A0标题",
            TextParser.SmartClean("#\u00A0标题"));

        Check("NBSP 在 Kotlin 里算空白（KotlinTrim）", TextParser.IsKotlinWhitespace('\u00A0'), "");
        Check("U+FEFF 在 Kotlin 里不算空白", !TextParser.IsKotlinWhitespace('\uFEFF'), "");
        Check("U+0085 在 Kotlin 里不算空白", !TextParser.IsKotlinWhitespace('\u0085'), "");
    }

    // ------------------------------------------------------------------
    // 逐行模式
    // ------------------------------------------------------------------
    private static void RunLineMode()
    {
        Console.WriteLine();
        Console.WriteLine("--- 逐行（LINE）---");

        CheckUnits("空行不产生单元", new[] { "a", "b" }, Lines("a\n\nb"));
        CheckUnits("行首尾空白被去掉", new[] { "x" }, Lines("   x   "));
        CheckUnits("CRLF", new[] { "a", "b", "c" }, Lines("a\r\nb\r\nc"));
        CheckUnits("单独 CR", new[] { "a", "b", "c" }, Lines("a\rb\rc"));
        CheckUnits("空输入", Array.Empty<string>(), Lines(""));
        Check("Parse(LINE) 与 ParseLines 一致",
            TranslationWriter.Equals(
                new[] { "a", "b" },
                TextParser.Parse("a\nb", UnitMode.LINE).Select(u => u.Source).ToList()),
            "");

        var unit = TextParser.BuildLineUnit("原文", "译文");
        Check("BuildLineUnit 带上译文", unit.Source == "原文" && unit.Translation == "译文", unit.Source + "/" + unit.Translation);
    }

    // ------------------------------------------------------------------
    // 智能清理
    // ------------------------------------------------------------------
    private static void RunSmartClean()
    {
        Console.WriteLine();
        Console.WriteLine("--- 智能清理 smartClean ---");

        CheckEq("去字幕时间轴与序号行",
            "Hello there.\nGeneral Kenobi.",
            TextParser.SmartClean("1\n00:00:01,000 --> 00:00:02,500\nHello there.\n\n2\n00:00:03,000 --> 00:00:04,000\nGeneral Kenobi."));

        CheckEq("去 VTT 头", "Text.", TextParser.SmartClean("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nText."));
        CheckEq("去 Markdown 标题", "标题\n正文", TextParser.SmartClean("# 标题\n正文"));
        CheckEq("去列表标记", "项目一\n项目二", TextParser.SmartClean("- 项目一\n* 项目二"));
        CheckEq("去引用标记", "引用", TextParser.SmartClean("> 引用"));
        CheckEq("去有序列表标记", "有序", TextParser.SmartClean("1. 有序"));
        CheckEq("序号行（顿号/括号）", "正文", TextParser.SmartClean("1、\n2)\n正文"));
        CheckEq("缩进标题也清理", "缩进", TextParser.SmartClean("   # 缩进  "));
        CheckEq("空输入仍是空", "", TextParser.SmartClean(""));
        CheckEq("压缩空行", "a\nb", TextParser.SmartClean("a\n\n\n\nb"));
    }

    // ------------------------------------------------------------------
    // CSV / 对照表
    // ------------------------------------------------------------------
    private static void RunCsvAndTable()
    {
        Console.WriteLine();
        Console.WriteLine("--- CSV / 对照表 ---");

        CheckUnits("splitCsvLine 普通", new[] { "a", "b", "c" }, TextParser.SplitCsvLine("a,b,c"));
        CheckUnits("splitCsvLine 引号包裹", new[] { "a,b", "c" }, TextParser.SplitCsvLine("\"a,b\",c"));
        CheckUnits("splitCsvLine 转义引号", new[] { "a\"b", "c" }, TextParser.SplitCsvLine("\"a\"\"b\",c"));
        CheckUnits("splitCsvLine 空串", new[] { "" }, TextParser.SplitCsvLine(""));

        CheckEq("csvEscape 普通", "plain", TextParser.CsvEscape("plain"));
        CheckEq("csvEscape 含逗号", "\"a,b\"", TextParser.CsvEscape("a,b"));
        CheckEq("csvEscape 含引号", "\"a\"\"b\"", TextParser.CsvEscape("a\"b"));
        CheckEq("csvEscape 含换行", "\"a\nb\"", TextParser.CsvEscape("a\nb"));

        CheckEq("splitSourceTranslation 制表符", "Hello|你好", Repr(TextParser.SplitSourceTranslation("Hello\t你好")));
        CheckEq("splitSourceTranslation 箭头", "a|b", Repr(TextParser.SplitSourceTranslation("a => b")));
        CheckEq("splitSourceTranslation 竖线", "左|右", Repr(TextParser.SplitSourceTranslation("左|右")));
        CheckEq("splitSourceTranslation 英文逗号", "Hello|你好", Repr(TextParser.SplitSourceTranslation("Hello,你好")));
        CheckEq("中文逗号不当 CSV", "null", Repr(TextParser.SplitSourceTranslation("他说，你好")));
        CheckEq("普通文本不是对照", "null", Repr(TextParser.SplitSourceTranslation("plain text")));
        CheckEq("空行返回 null", "null", Repr(TextParser.SplitSourceTranslation("   ")));

        Check("looksLikeTable 制表符表", TextParser.LooksLikeTable("a\tb\nc\td\n"), "");
        Check("looksLikeTable 普通文本", !TextParser.LooksLikeTable("第一行\n第二行\n第三行"), "");
        Check("looksLikeTable 单行不算表", !TextParser.LooksLikeTable("a\tb"), "");
    }

    // ------------------------------------------------------------------
    // 语言探测
    // ------------------------------------------------------------------
    private static void RunDetectLanguage()
    {
        Console.WriteLine();
        Console.WriteLine("--- 语言探测 ---");

        CheckEq("英文", "en", TextParser.DetectLanguage("Hello world"));
        CheckEq("中文", "zh-CN", TextParser.DetectLanguage("你好世界"));
        CheckEq("日文", "ja", TextParser.DetectLanguage("こんにちは"));
        CheckEq("韩文", "ko", TextParser.DetectLanguage("안녕하세요"));
        CheckEq("空文本", "auto", TextParser.DetectLanguage(""));
        CheckEq("纯数字", "auto", TextParser.DetectLanguage("12345"));
        CheckEq("中文多时判 zh-CN", "zh-CN", TextParser.DetectLanguage("abc 中文测试"));
    }

    // ------------------------------------------------------------------
    // 导出
    // ------------------------------------------------------------------
    private static void RunExport()
    {
        Console.WriteLine();
        Console.WriteLine("--- 导出 ---");

        var doc = MakeDoc("测试文档", UnitMode.LINE, new[]
        {
            new TranslationUnit("Hello", "你好"),
            new TranslationUnit("World", ""),
        });

        CheckEq("仅译文", "你好", ExportManager.BuildText(doc, ExportFormat.TXT_TRANSLATED_ONLY));
        CheckEq("原文+译文", "Hello\t你好\nWorld", ExportManager.BuildText(doc, ExportFormat.TXT_BILINGUAL));
        CheckEq("已译替换源文", "你好\nWorld", ExportManager.BuildText(doc, ExportFormat.TXT_SOURCE_FALLBACK));

        CheckEq("Markdown 表格",
            "| # | 原文 | 译文 |\n| --- | --- | --- |\n| 1 | Hello | 你好 |\n| 2 | World |  |\n",
            ExportManager.BuildText(doc, ExportFormat.MARKDOWN_TABLE));

        CheckEq("CSV", "index,source,translation\n1,Hello,你好\n2,World,\n",
            ExportManager.BuildText(doc, ExportFormat.CSV));

        CheckEq("JSON",
            "{\n  \"name\": \"测试文档\",\n  \"mode\": \"LINE\",\n  \"total\": 2,\n  \"translated\": 1,\n  \"units\": [\n" +
            "    { \"index\": 1, \"source\": \"Hello\", \"translation\": \"你好\" },\n" +
            "    { \"index\": 2, \"source\": \"World\", \"translation\": \"\" }\n  ]\n}",
            ExportManager.BuildText(doc, ExportFormat.JSON));

        CheckEq("Markdown 转义 |", "| 1 | a\\|b | c |\n",
            ExportManager.BuildText(MakeDoc("d", UnitMode.LINE, new[] { new TranslationUnit("a|b", "c") }), ExportFormat.MARKDOWN_TABLE)
                .Split('\n')[2] + "\n");

        CheckEq("CSV 含逗号字段", "index,source,translation\n1,\"a,b\",c\n",
            ExportManager.BuildText(MakeDoc("d", UnitMode.LINE, new[] { new TranslationUnit("a,b", "c") }), ExportFormat.CSV));

        CheckEq("文件名", "测试文档_对照.txt", ExportManager.BuildFileName(doc, ExportFormat.TXT_BILINGUAL));
        CheckEq("文件名去 .txt", "a_数据.json", ExportManager.BuildFileName(MakeDoc("a.txt", UnitMode.LINE, Array.Empty<TranslationUnit>()), ExportFormat.JSON));
        CheckEq("文件名兜底", "未命名文档_译文.txt", ExportManager.BuildFileName(MakeDoc("", UnitMode.LINE, Array.Empty<TranslationUnit>()), ExportFormat.TXT_TRANSLATED_ONLY));
        CheckEq("分享文本", "Hello\t你好\nWorld", ExportManager.ShareText(doc));
        CheckEq("格式标签", "Markdown 表格", ExportFormatInfo.Label(ExportFormat.MARKDOWN_TABLE));
        CheckEq("格式扩展名", "json", ExportFormatInfo.Extension(ExportFormat.JSON));
    }

    // ------------------------------------------------------------------
    // 费用估算
    // ------------------------------------------------------------------
    private static void RunCost()
    {
        Console.WriteLine();
        Console.WriteLine("--- 费用估算 ---");

        var model = new ModelConfig("m1", "模型一", "p1");
        model.Billing.InputPrice = 1.0;
        model.Billing.OutputPrice = 2.0;

        CheckEq("无高峰倍率", "2", CostCalculator.CostFor(model, 1_000_000, 500_000).ToString("0.####"));

        model.Billing.PeakMultiplier = 2.0;
        model.Billing.PeakStartHour = 8;
        model.Billing.PeakEndHour = 22;
        model.Billing.HourProvider = () => 10;
        CheckEq("高峰时段翻倍", "4", CostCalculator.CostFor(model, 1_000_000, 500_000).ToString("0.####"));

        model.Billing.HourProvider = () => 23;
        CheckEq("非高峰恢复原价", "2", CostCalculator.CostFor(model, 1_000_000, 500_000).ToString("0.####"));

        model.Billing.PeakStartHour = 22;
        model.Billing.PeakEndHour = 6;
        model.Billing.HourProvider = () => 23;
        CheckEq("跨夜高峰段（23 点）", "4", CostCalculator.CostFor(model, 1_000_000, 500_000).ToString("0.####"));

        model.Billing.HourProvider = () => 3;
        CheckEq("跨夜高峰段（3 点）", "4", CostCalculator.CostFor(model, 1_000_000, 500_000).ToString("0.####"));

        model.Billing.HourProvider = () => 12;
        CheckEq("跨夜非高峰段（12 点）", "2", CostCalculator.CostFor(model, 1_000_000, 500_000).ToString("0.####"));

        var noPrice = new ModelConfig("m2", "免费", "p1");
        Check("未配置价格时 hasPrice=false", !noPrice.Billing.HasPrice, "");
    }

    // ------------------------------------------------------------------
    // 数据模型
    // ------------------------------------------------------------------
    private static void RunModels()
    {
        Console.WriteLine();
        Console.WriteLine("--- 数据模型 ---");

        var doc = MakeDoc("d", UnitMode.LINE, new[]
        {
            new TranslationUnit("A", "译A"),
            new TranslationUnit("B", ""),
            new TranslationUnit("C", ""),
        });

        CheckEq("总数", "3", doc.TotalCount.ToString());
        CheckEq("已处理数", "1", doc.TranslatedCount.ToString());
        CheckEq("剩余数", "2", doc.RemainingCount.ToString());
        CheckEq("进度", "0.3333", doc.Progress.ToString("0.####"));
        Check("未完成", !doc.IsFinished, "");

        CheckEq("下一个未完成（从 0 开始）", "1", doc.NextUndoneIndex(0)?.ToString() ?? "null");
        // 对齐 Kotlin Models.kt:60-67：nextUndoneIndex(from) 是「从 from 本身开始」找，
        // 不是从 from+1 开始。doc = [A 已译, B 空, C 空]，from=2 时索引 2 本就未完成，故为 2。
        CheckEq("下一个未完成（从 2 开始，命中自身）", "2", doc.NextUndoneIndex(2)?.ToString() ?? "null");
        // 这才是真正的环形回绕：0 与 2 已完成，从 2 出发要绕一圈才回到 1。
        var wrapDoc = MakeDoc("d-wrap", UnitMode.LINE, new[]
        {
            new TranslationUnit("A", "译A", true),
            new TranslationUnit("B", ""),
            new TranslationUnit("C", "译C", true),
        });
        CheckEq("下一个未完成（从 2 出发环形绕回 1）", "1", wrapDoc.NextUndoneIndex(2)?.ToString() ?? "null");
        // 负数下标：与 Kotlin 的 ((from + offset) % n + n) % n 一致。
        CheckEq("下一个未完成（from 为负也要回绕）", "1", wrapDoc.NextUndoneIndex(-1)?.ToString() ?? "null");
        CheckEq("翻译记忆命中", "译A", doc.MemoryTranslation("A") ?? "null");
        CheckEq("翻译记忆排除自身", "null", doc.MemoryTranslation("A", 0) ?? "null");
        CheckEq("翻译记忆未命中", "null", doc.MemoryTranslation("Z") ?? "null");

        var doneDoc = MakeDoc("d2", UnitMode.LINE, new[] { new TranslationUnit("A", "译", true) });
        CheckEq("全部完成时返回 null", "null", doneDoc.NextUndoneIndex(0)?.ToString() ?? "null");
        var emptyDoc = MakeDoc("d3", UnitMode.LINE, Array.Empty<TranslationUnit>());
        CheckEq("空文档返回 null", "null", emptyDoc.NextUndoneIndex(0)?.ToString() ?? "null");
        Check("空文档进度为 0", emptyDoc.Progress == 0f, "");

        var settings = new AppSettings();
        settings.Glossary = "apple=苹果\n# 注释行\ncat: 猫\n没有分隔符\n=缺键";
        var entries = settings.GlossaryEntries;
        CheckEq("术语表条数", "2", entries.Count.ToString());
        CheckEq("术语表第一条", "apple=苹果", entries.Count > 0
            ? entries[0].Key + "=" + entries[0].Value
            : "");
        CheckEq("术语表第二条", "cat=猫", entries.Count > 1
            ? entries[1].Key + "=" + entries[1].Value
            : "");

        CheckEq("默认提示词兜底", PromptTemplates.Default, settings.EffectivePrompt);
        CheckEq("模板 byId", "literal", PromptTemplates.ById("literal").Id);
        CheckEq("模板 byId 兜底", "default", PromptTemplates.ById("不存在").Id);
        CheckEq("模板数量", "5", PromptTemplates.All.Count.ToString());

        var provider = new ProviderConfig("p1", "提供方");
        provider.Models.Add(new ModelConfig("m1", "模型一", "p1"));
        settings.Providers.Add(provider);
        settings.ActiveProviderId = "p1";
        settings.ActiveModelId = "m1";
        Check("activeModel 解析", settings.ActiveModel?.Name == "模型一", "");
        CheckEq("allModels 计数", "1", settings.AllModels.Count().ToString());
    }

    // ------------------------------------------------------------------
    // 语料模式：用 C# 版跑同一份 corpus，产出 cs-output.json
    // ------------------------------------------------------------------
    private static int RunCorpusMode(string[] args)
    {
        string? inPath = null;
        string? outPath = null;
        // 注意："--corpus" 就在 args[0]，循环必须从 0 开始（曾经写成 i=1，
        // 结果 inPath 永远是 null，语料模式直接打用法并返回 2）。
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--corpus") inPath = args[i + 1];
            if (args[i] == "--out") outPath = args[i + 1];
        }
        if (inPath == null || outPath == null)
        {
            Console.Error.WriteLine("用法: --corpus <corpus.json> --out <cs-output.json>");
            return 2;
        }

        var json = File.ReadAllText(inPath, Encoding.UTF8);
        var items = JsonSerializer.Deserialize<List<CorpusItem>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new List<CorpusItem>();

        var entries = new List<CsEntry>();
        foreach (var item in items)
        {
            string mode = string.IsNullOrEmpty(item.Mode) ? "SENTENCE" : item.Mode!;
            string text = item.Clean ? TextParser.SmartClean(item.Input) : item.Input;
            var units = TextParser.Parse(text, mode == "LINE" ? UnitMode.LINE : UnitMode.SENTENCE);
            entries.Add(new CsEntry(
                item.Id ?? "",
                mode,
                item.Clean,
                item.Note ?? "",
                item.Input,
                units.Select(u => u.Source).ToList()));
        }

        var payload = new CsOutput("LineTrans.Core TextParser.Parse()/SmartClean()", entries);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload, options) + "\n", new UTF8Encoding(false));
        Console.WriteLine("[cs-split] 语料 " + entries.Count + " 条 → " + outPath);
        return 0;
    }

    private sealed class CorpusItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }

        [JsonPropertyName("input")] public string Input { get; set; } = "";

        [JsonPropertyName("mode")] public string? Mode { get; set; }

        [JsonPropertyName("clean")] public bool Clean { get; set; }

        [JsonPropertyName("note")] public string? Note { get; set; }
    }

    private sealed record CsEntry(string id, string mode, bool clean, string note, string input, List<string> output);

    private sealed record CsOutput(string source, List<CsEntry> entries);

    // ------------------------------------------------------------------
    // 断言基础设施
    // ------------------------------------------------------------------
    private static List<string> Sentences(string text) =>
        TextParser.Parse(text, UnitMode.SENTENCE).Select(u => u.Source).ToList();

    private static List<string> Lines(string text) =>
        TextParser.Parse(text, UnitMode.LINE).Select(u => u.Source).ToList();

    private static TranslationDoc MakeDoc(string name, UnitMode mode, IEnumerable<TranslationUnit> units)
    {
        var doc = new TranslationDoc("doc-1", name, TranslationDoc.DefaultFolder, null, mode);
        doc.Units.AddRange(units);
        return doc;
    }

    private static string Repr((string Source, string Translation)? value) =>
        value == null ? "null" : value.Value.Source + "|" + value.Value.Translation;

    private static void CheckEq(string name, string expected, string actual) =>
        Check(name, expected == actual, "期望 [" + Show(expected) + "] 实际 [" + Show(actual) + "]");

    private static void CheckUnits(string name, IReadOnlyList<string> expected, IReadOnlyList<string> actual) =>
        Check(name, TranslationWriter.Equals(expected, actual),
            "期望 " + expected.Count + " 条 [" + Show(string.Join(" ⏎ ", expected)) + "] 实际 " + actual.Count + " 条 [" +
            Show(string.Join(" ⏎ ", actual)) + "]");

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("PASS  " + name);
        }
        else
        {
            _failed++;
            Console.WriteLine("FAIL  " + name + "  " + detail);
        }
    }

    private static string Show(string value) =>
        value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}

/// <summary>序列比较工具。</summary>
internal static class TranslationWriter
{
    public static bool Equals(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
