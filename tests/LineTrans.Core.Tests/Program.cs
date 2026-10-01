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

    private static async Task<int> Main(string[] args)
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
        RunTimecodeRegexRegression();
        RunCsvAndTable();
        RunDetectLanguage();
        RunExport();
        RunCost();
        RunModels();

        // 2026-10 新增：PC 端原生外观设置（翻译页布局 / 背景材质 / 强调色来源）
        RunUiSettings();

        // W3c 新增：文档仓库 / 设置仓库 / AI 调用（含本地假 HTTP 服务，需要 await）
        await RunDocRepositoryAsync();
        await RunSettingsRepositoryAsync();
        await RunAiClientAsync();

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
    // 时间轴正则的行终止符回归（JVM 与 .NET 的 `.` 语义差异）
    //
    // 两边 `.` 的定义都是「除行终止符外的任意字符」，但行终止符集合不同：
    //   JVM ：\n \r U+0085 U+2028 U+2029      .NET：只有 \n
    // 旧实现的 TimecodeRegex 结尾写成 `.*`，于是时间轴行里一旦出现
    // U+0085 / U+2028 / U+2029，Kotlin 判「不是时间轴行」保留整行，
    // C# 却判「是时间轴行」整行丢弃。
    //
    // 语料侧证据：3226 条三方语料（主语料 178 + 补充语料 48 + 随机语料 3000）
    // 里 7 条不一致全部出自这一个根因：
    //   fuzz-01963 / jvmF-06 / fuzz-02365 / jvmF-07（整行丢失）
    //   jvmF-01（U+0085）/ jvmF-02（U+2028）/ jvmF-03（U+2029）
    // 下面每一条的期望值都取自真 Kotlin（JVM 17 + Kotlin 1.9.24）实测输出，
    // 不是手写猜测；末尾两条是「修复后仍应正常丢弃」的反向对照。
    // ------------------------------------------------------------------
    private static void RunTimecodeRegexRegression()
    {
        Console.WriteLine();
        Console.WriteLine("--- 时间轴正则的行终止符（JVM vs .NET）---");

        // 1-3. 行终止符出现在时间轴行中间：Kotlin 保留整行，修复前的 C# 会整行丢弃。
        CheckEq("时间轴行含 U+0085 → 整行保留",
            "00:00:01,000 --> 00:00:02,000\u0085x",
            TextParser.SmartClean("00:00:01,000 --> 00:00:02,000\u0085x"));
        CheckEq("时间轴行含 U+2028 → 整行保留",
            "00:00:01,000 --> 00:00:02,000\u2028x",
            TextParser.SmartClean("00:00:01,000 --> 00:00:02,000\u2028x"));
        CheckEq("时间轴行含 U+2029 → 整行保留",
            "00:00:01,000 --> 00:00:02,000\u2029x",
            TextParser.SmartClean("00:00:01,000 --> 00:00:02,000\u2029x"));

        // 4-5. U+0085 落在行尾：Kotlin 侧 Regex.matches() 要求整串匹配，
        //      Java 的 `$` 允许的「末尾行终止符」补不上，判定同样是「不是时间轴行」。
        //      这两条现有语料没覆盖，是用真 JVM 逐条探针实测出来的（修复前 C# 会丢）。
        CheckEq("时间轴行以 U+0085 结尾 → 整行保留",
            "00:00:01,000 --> 00:00:02,000\u0085",
            TextParser.SmartClean("00:00:01,000 --> 00:00:02,000\u0085"));
        CheckEq("时间轴行以两个 U+0085 结尾 → 整行保留",
            "00:00:01,000 --> 00:00:02,000\u0085\u0085",
            TextParser.SmartClean("00:00:01,000 --> 00:00:02,000\u0085\u0085"));

        // 6. fuzz-01963（随机语料实测差异）：整行原样保留。
        const string fuzz1963 = "00:00:01.000 --> 00:00:02.000\u0085'\u30001...\u2028J.!";
        CheckEq("fuzz-01963 smartClean → 整行保留", fuzz1963, TextParser.SmartClean(fuzz1963));
        CheckUnits("fuzz-01963 LINE（经 smartClean）→ 1 条", new[] { fuzz1963 }, Lines(TextParser.SmartClean(fuzz1963)));

        // 7. fuzz-02365（随机语料实测差异）：时间轴行不再丢失，
        //    逐句结果与 Kotlin 基线逐字一致。
        const string fuzz2365 =
            "00:00:01.000 --> 00:00:02.000日U.S.[Y\uFEFF」」\u20283.14中:、〉語.。?1①K.\n\n######\r";
        CheckEq("fuzz-02365 smartClean → 时间轴行保留、###### 也保留",
            "00:00:01.000 --> 00:00:02.000日U.S.[Y\uFEFF」」\u20283.14中:、〉語.。?1①K.\n######",
            TextParser.SmartClean(fuzz2365));
        CheckUnits("fuzz-02365 SENTENCE → 与 Kotlin 基线一致", new[]
        {
            "00:00:01.000 --> 00:00:02.000日U.S.[Y\uFEFF」」\u20283.14中:、〉語.。?",
            "1①K.######",
        }, Sentences(TextParser.SmartClean(fuzz2365)));

        // 8-9. 反向对照：仍然是时间轴行的两条，必须照旧整行丢弃。
        //      行尾 U+2028 会被 Kotlin 的 trim 去掉（Character.isWhitespace 认它），
        //      剩下的就是一条普通时间轴行；U+FEFF 不是行终止符，`.` 补集里照样能吃下它。
        CheckEq("行尾 U+2028 被 trim 掉后仍是时间轴行 → 整行丢弃",
            "", TextParser.SmartClean("00:00:01,000 --> 00:00:02,000\u2028"));
        CheckEq("时间轴行尾部跟 U+FEFF → 仍是时间轴行，整行丢弃",
            "", TextParser.SmartClean("00:00:01,000 --> 00:00:02,000\uFEFF"));
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
    // 2026-10：PC 端原生外观设置（只新增断言，不动既有断言）
    // ------------------------------------------------------------------
    private static void RunUiSettings()
    {
        Console.WriteLine();
        Console.WriteLine("--- PC 端原生外观设置 ---");

        var defaults = new AppSettings();
        CheckEq("外观：翻译页布局默认值", "left-right", defaults.TranslationLayout);
        CheckEq("外观：背景材质默认值", "mica", defaults.BackdropMaterial);
        CheckEq("外观：强调色来源默认值", "system", defaults.AccentSource);
        Check("外观：默认不是上下式", !defaults.IsTopBottomLayout, "");

        // 既有字段的默认值一个都没变（只读断言，不改任何既有语义）
        CheckEq("外观：既有字段主题默认跟随系统", "SYSTEM", defaults.ThemeMode.ToString());
        CheckEq("外观：既有字段字号缩放默认 1.0", "1", defaults.UiScale.ToString("0.##"));
        CheckEq("外观：既有字段导出格式默认双语 TXT", "TXT_BILINGUAL", defaults.DefaultExportFormat.ToString());
        CheckEq("外观：既有字段查词来源默认 auto", "auto", defaults.DictionarySource);
        CheckEq("外观：既有字段自动保存默认 700ms", "700", defaults.AutoSaveMs.ToString());

        // 布局规范化
        CheckEq("布局规范化：大写", "top-bottom", AppSettings.NormalizeTranslationLayout("TOP-BOTTOM"));
        CheckEq("布局规范化：首尾空格", "top-bottom", AppSettings.NormalizeTranslationLayout("  Top-Bottom  "));
        CheckEq("布局规范化：别名 vertical", "top-bottom", AppSettings.NormalizeTranslationLayout("vertical"));
        CheckEq("布局规范化：认不出的值回落左右式", "left-right", AppSettings.NormalizeTranslationLayout("diagonal"));
        CheckEq("布局规范化：null 回落左右式", "left-right", AppSettings.NormalizeTranslationLayout(null));
        CheckEq("布局规范化：空白串回落左右式", "left-right", AppSettings.NormalizeTranslationLayout("   "));

        // 背景材质规范化
        CheckEq("材质规范化：micaAlt 大小写不敏感", "micaAlt", AppSettings.NormalizeBackdropMaterial("MICAALT"));
        CheckEq("材质规范化：acrylic 去空格", "acrylic", AppSettings.NormalizeBackdropMaterial(" Acrylic "));
        CheckEq("材质规范化：none", "none", AppSettings.NormalizeBackdropMaterial("NONE"));
        CheckEq("材质规范化：认不出的值回落 none", "none", AppSettings.NormalizeBackdropMaterial("glass"));
        CheckEq("材质规范化：null 回落 none", "none", AppSettings.NormalizeBackdropMaterial(null));

        // 强调色来源规范化
        CheckEq("强调色规范化：brand 大写", "brand", AppSettings.NormalizeAccentSource("BRAND"));
        CheckEq("强调色规范化：system", "system", AppSettings.NormalizeAccentSource("System"));
        CheckEq("强调色规范化：认不出的值回落 system", "system", AppSettings.NormalizeAccentSource("purple"));
        CheckEq("强调色规范化：null 回落 system", "system", AppSettings.NormalizeAccentSource(null));

        // 规范化必须幂等（规范值原样返回）
        CheckEq("规范化幂等：布局", "left-right", AppSettings.NormalizeTranslationLayout(AppSettings.LayoutLeftRight));
        CheckEq("规范化幂等：材质", "micaAlt", AppSettings.NormalizeBackdropMaterial(AppSettings.BackdropMicaAlt));
        CheckEq("规范化幂等：强调色", "brand", AppSettings.NormalizeAccentSource(AppSettings.AccentBrand));

        // 既有常量没被改名 / 改值
        CheckEq("外观：四个材质常量", "mica|micaAlt|acrylic|none",
            AppSettings.BackdropMica + "|" + AppSettings.BackdropMicaAlt + "|" + AppSettings.BackdropAcrylic + "|" + AppSettings.BackdropNone);
        CheckEq("外观：两个来源常量", "system|brand", AppSettings.AccentSystem + "|" + AppSettings.AccentBrand);

        // SettingsRepository.Sanitize 不碰这三个新字段（它们由 PC 端界面按规范化函数维护）
        var settings = new AppSettings
        {
            TranslationLayout = "top-bottom",
            BackdropMaterial = "acrylic",
            AccentSource = "brand",
        };
        SettingsRepository.Sanitize(settings);
        CheckEq("Sanitize 保留布局字段", "top-bottom", settings.TranslationLayout);
        CheckEq("Sanitize 保留材质字段", "acrylic", settings.BackdropMaterial);
        CheckEq("Sanitize 保留强调色字段", "brand", settings.AccentSource);
    }

    // ------------------------------------------------------------------
    // W3c：DocRepository 文档仓库
    // ------------------------------------------------------------------
    private static async Task RunDocRepositoryAsync()
    {
        Console.WriteLine();
        Console.WriteLine("--- DocRepository 文档仓库 ---");

        string dir = TempDir("docs");
        try
        {
            using var repo = new DocRepository(dir, debounceMs: 120);

            // ---- 新建 ----
            const string source = "Hello world.\n\nThis is a test.\n\nGoodbye.";
            var doc = repo.Create("测试文档", source, UnitMode.LINE);
            CheckEq("doc-create 单元数（逐行）", "3", doc.Units.Count.ToString());
            CheckEq("doc-create 模式", "LINE", doc.UnitMode.ToString());
            CheckEq("doc-create 源文本", source, doc.SourceText);
            CheckEq("doc-create 文件夹默认", "默认", doc.Folder);
            CheckEq("doc-create 列表里能查到", "测试文档", repo.Get(doc.Id)?.Name ?? "null");
            Check("doc-create 立即落盘", File.Exists(Path.Combine(dir, doc.Id + ".json")), Path.Combine(dir, doc.Id + ".json"));

            // ---- 切分对齐：把已有译文按原句搬过去 ----
            doc.Units[0].Translation = "你好，世界。";
            doc.Units[0].Done = true;
            doc.Units[1].Translation = "这是一个测试。";
            doc.Units[1].Starred = true;
            doc.Units[2].Translation = "再见。";
            repo.Save(doc);

            Check("doc-mode 切到逐句返回 true", repo.ChangeMode(doc.Id, UnitMode.SENTENCE), "");
            CheckEq("doc-mode 模式已更新", "SENTENCE", doc.UnitMode.ToString());
            CheckEq("doc-mode 逐句单元数", "3", doc.Units.Count.ToString());
            CheckEq("doc-mode 第 1 句译文按原句保留", "你好，世界。", doc.Units[0].Translation);
            CheckEq("doc-mode 第 2 句译文按原句保留", "这是一个测试。", doc.Units[1].Translation);
            CheckEq("doc-mode 第 3 句译文按原句保留", "再见。", doc.Units[2].Translation);
            Check("doc-mode done 状态保留", doc.Units[0].Done, "");
            Check("doc-mode 收藏标记保留", doc.Units[1].Starred, "");

            Check("doc-mode 切回逐行返回 true", repo.ChangeMode(doc.Id, UnitMode.LINE), "");
            CheckEq("doc-mode 切回逐行译文仍在", "你好，世界。", doc.Units[0].Translation);
            CheckEq("doc-mode 切回逐行单元数", "3", doc.Units.Count.ToString());
            Check("doc-mode 模式不变时返回 false", !repo.ChangeMode(doc.Id, UnitMode.LINE), "");

            // ---- 切分变化时：命中的保留、没命中的清空，且不崩 ----
            var changed = repo.Create("切分变化", "Hello world. This is a test.\nSecond line here.", UnitMode.LINE);
            CheckEq("doc-realign 切分前单元数", "2", changed.Units.Count.ToString());
            changed.Units[0].Translation = "第一行整行译文";
            changed.Units[1].Translation = "第二行译文";
            changed.Units[1].Starred = true;
            repo.Save(changed);
            repo.ChangeMode(changed.Id, UnitMode.SENTENCE);
            CheckEq("doc-realign 切分后单元数", "3", changed.Units.Count.ToString());
            CheckEq("doc-realign 未命中的单元译文为空", "", changed.Units[0].Translation);
            CheckEq("doc-realign 未命中的单元 done 复位", "False", changed.Units[0].Done.ToString());
            CheckEq("doc-realign 原文相同的单元保留译文", "第二行译文", changed.Units[2].Translation);
            CheckEq("doc-realign 原文相同的单元保留收藏", "True", changed.Units[2].Starred.ToString());

            // ---- 改名 / 移动 / 置顶 / 删除 ----
            Check("doc-edit 改名返回 true", repo.Rename(changed.Id, "新名字"), "");
            CheckEq("doc-edit 改名生效", "新名字", repo.Get(changed.Id)?.Name ?? "null");
            Check("doc-edit 移动到文件夹", repo.Move(changed.Id, "小说"), "");
            // 序数排序：小(U+5C0F) < 默(U+9ED8)，与安卓端 Kotlin 的 sorted() 一致
            CheckEq("doc-edit 文件夹列表", "小说,默认", string.Join(",", repo.Folders()));
            Check("doc-edit 置顶", repo.SetPinned(changed.Id, true), "");
            Check("doc-edit 置顶后排在第一个", ReferenceEquals(repo.Docs[0], repo.Get(changed.Id)), "");
            Check("doc-edit 删除返回 true", repo.Delete(changed.Id), "");
            CheckEq("doc-edit 删除后取不到", "null", repo.Get(changed.Id)?.Name ?? "null");
            Check("doc-edit 删除后磁盘文件也没了", !File.Exists(Path.Combine(dir, changed.Id + ".json")), "");
        }
        finally
        {
            TryDeleteDir(dir);
        }

        // ---- 防抖落盘：等自动写完后重新加载，内容必须一致 ----
        string debounceDir = TempDir("docs-debounce");
        try
        {
            string file;
            string id;
            var repo = new DocRepository(debounceDir, debounceMs: 400);
            var doc = repo.Create("防抖文档", "a\nb\nc", UnitMode.LINE);
            id = doc.Id;
            file = Path.Combine(debounceDir, id + ".json");
            doc.Units[0].Translation = "甲";
            doc.Units[1].Translation = "乙";
            doc.Units[2].Starred = true;
            doc.Pinned = true;
            doc.Folder = "小说";
            doc.LastIndex = 2;
            repo.Save(doc);
            Check("doc-debounce 保存后进入待写队列", repo.PendingCount == 1, "PendingCount=" + repo.PendingCount);

            var startedAt = DateTime.UtcNow;
            bool wrote = await WaitUntilAsync(() => ReadFileWithRetry(file).Contains("甲"), 5000);
            int elapsedMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            Check("doc-debounce 防抖到点自动落盘", wrote && elapsedMs < 3000, "耗时 " + elapsedMs + " ms");
            bool drained = await WaitUntilAsync(() => repo.PendingCount == 0, 3000);
            Check("doc-debounce 写完后待写队列自动清空", drained, "PendingCount=" + repo.PendingCount);
            CheckEq("doc-debounce 没有写盘错误", "0", repo.WriteErrors.Count.ToString());
            repo.Dispose();

            var reloaded = new DocRepository(debounceDir, debounceMs: 400);
            reloaded.Load();
            CheckEq("doc-debounce 重新加载无跳过文件", "0", reloaded.LoadErrors.Count.ToString());
            var loaded = reloaded.Get(id);
            Check("doc-debounce 重新加载取到文档", loaded != null, "");
            if (loaded != null)
            {
                CheckEq("doc-debounce 重新加载译文一致", "甲", loaded.Units[0].Translation);
                CheckEq("doc-debounce 重新加载第二句译文一致", "乙", loaded.Units[1].Translation);
                CheckEq("doc-debounce 重新加载单元数一致", "3", loaded.Units.Count.ToString());
                CheckEq("doc-debounce 重新加载收藏一致", "True", loaded.Units[2].Starred.ToString());
                CheckEq("doc-debounce 重新加载置顶一致", "True", loaded.Pinned.ToString());
                CheckEq("doc-debounce 重新加载文件夹一致", "小说", loaded.Folder);
                CheckEq("doc-debounce 重新加载 lastIndex 一致", "2", loaded.LastIndex.ToString());
                CheckEq("doc-debounce 重新加载源文本一致", "a\nb\nc", loaded.SourceText);
                CheckEq("doc-debounce 重新加载名称一致", "防抖文档", loaded.Name);
            }
            reloaded.Dispose();
        }
        finally
        {
            TryDeleteDir(debounceDir);
        }

        // ---- FlushAsync：防抖期很长时靠它强制落盘 ----
        string flushDir = TempDir("docs-flush");
        try
        {
            using var repo = new DocRepository(flushDir, debounceMs: 60000);
            var doc = repo.Create("强制落盘", "x\ny", UnitMode.LINE);
            string file = Path.Combine(flushDir, doc.Id + ".json");
            doc.Units[0].Translation = "强制译文";
            repo.Save(doc);
            Check("doc-flush 防抖期内还没写新内容", !ReadFileWithRetry(file).Contains("强制译文"), "");
            await repo.FlushAsync();
            Check("doc-flush FlushAsync 后文件存在", File.Exists(file), file);
            Check("doc-flush FlushAsync 后内容已落盘", ReadFileWithRetry(file).Contains("强制译文"), "");
            CheckEq("doc-flush FlushAsync 后待写队列为空", "0", repo.PendingCount.ToString());
        }
        finally
        {
            TryDeleteDir(flushDir);
        }

        // ---- 容错：坏文件跳过、缺字段也能读 ----
        string brokenDir = TempDir("docs-broken");
        try
        {
            string goodId;
            var writer = new DocRepository(brokenDir, debounceMs: 120);
            var good = writer.Create("好文档", "ok", UnitMode.LINE);
            goodId = good.Id;
            writer.Dispose();

            File.WriteAllText(Path.Combine(brokenDir, "broken.json"), "{ 这不是 JSON", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(brokenDir, "no-id.json"), "{\"name\":\"缺少 id\"}", new UTF8Encoding(false));

            var repo = new DocRepository(brokenDir, debounceMs: 120);
            repo.Load();
            CheckEq("doc-broken 只加载好的文档", "1", repo.Docs.Count.ToString());
            CheckEq("doc-broken 记录被跳过的文件数", "2", repo.LoadErrors.Count.ToString());
            Check("doc-broken 好文档仍可读出", repo.Get(goodId) != null, "");
            Check("doc-broken 跳过原因可读", repo.LoadErrors.Any(item => item.Contains("broken.json", StringComparison.Ordinal)), repo.LoadErrors.Count > 0 ? repo.LoadErrors[0] : "");

            // 缺字段的文档：只有 id / name 也要能加载
            File.WriteAllText(Path.Combine(brokenDir, "minimal.json"), "{\"id\":\"minimal-1\",\"name\":\"最小文档\"}", new UTF8Encoding(false));
            var repo2 = new DocRepository(brokenDir, debounceMs: 120);
            repo2.Load();
            var minimal = repo2.Get("minimal-1");
            Check("doc-broken 缺字段文档能加载", minimal != null, "");
            CheckEq("doc-broken 缺字段时文件夹用默认值", "默认", minimal?.Folder ?? "null");
            CheckEq("doc-broken 缺字段时单元数为 0", "0", minimal?.Units.Count.ToString() ?? "null");
            CheckEq("doc-broken 缺字段时模式为 LINE", "LINE", minimal?.UnitMode.ToString() ?? "null");
            CheckEq("doc-broken 缺字段时 updatedAt 自动补齐", "True", (minimal?.UpdatedAt > 0).ToString());
            repo.Dispose();
            repo2.Dispose();
        }
        finally
        {
            TryDeleteDir(brokenDir);
        }

        // ---- 排序：置顶优先 + 最近更新倒序 ----
        string sortDir = TempDir("docs-sort");
        try
        {
            using var repo = new DocRepository(sortDir, debounceMs: 120);
            var a = repo.Create("A", "a", UnitMode.LINE);
            await Task.Delay(15);
            repo.Create("B", "b", UnitMode.LINE);
            await Task.Delay(15);
            repo.Create("C", "c", UnitMode.LINE);
            CheckEq("doc-sort 最近更新在前", "C,B,A", string.Join(",", repo.Docs.Select(d => d.Name)));
            repo.SetPinned(a.Id, true);
            CheckEq("doc-sort 置顶排最前", "A,C,B", string.Join(",", repo.Docs.Select(d => d.Name)));
            repo.SetSort(DocSort.NAME);
            CheckEq("doc-sort 按名称排序（置顶仍在最前）", "A,B,C", string.Join(",", repo.Docs.Select(d => d.Name)));
        }
        finally
        {
            TryDeleteDir(sortDir);
        }

        // ---- 写读争用回归：一边高频写盘、一边持续读盘 ----
        //
        // 修复前这里会挂，原因是 DocRepository 里有两处真缺陷：
        //   1) 防抖写盘和 FlushAsync 是两条独立的写者，会同时去写同一个 <id>.json.tmp 和同一个目标文件；
        //   2) 抢输的那次写盘只记一条 WriteErrors 就完事，谁也不再重试，磁盘会永久停在旧版本。
        // 外加一个环境事实：覆盖改名（File.Move / File.Replace）期间目标文件名会短暂打不开（本机实测 30~230ms），
        // 所以读方要能容忍瞬时共享冲突，但读到的每一份内容都必须是完整 JSON。
        string raceDir = TempDir("docs-race");
        try
        {
            var raceRepo = new DocRepository(raceDir, debounceMs: 200);
            var raceDoc = raceRepo.Create("争用文档",
                string.Join("\n", Enumerable.Range(1, 200).Select(i => "第 " + i + " 行原文")), UnitMode.LINE);
            string raceFile = Path.Combine(raceDir, raceDoc.Id + ".json");

            int goodReads = 0, transientReads = 0, tornReads = 0, wrongDocReads = 0;
            bool stopReader = false;
            var raceReader = Task.Run(() =>
            {
                while (!Volatile.Read(ref stopReader))
                {
                    string text;
                    try
                    {
                        text = File.ReadAllText(raceFile, Encoding.UTF8);
                    }
                    catch (IOException)
                    {
                        Interlocked.Increment(ref transientReads);
                        Thread.Sleep(25);
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref transientReads);
                        Thread.Sleep(25);
                        continue;
                    }

                    // 读到的必须是一份完整文档：要么旧版本、要么新版本，绝不能是半截 JSON。
                    try
                    {
                        using var parsed = JsonDocument.Parse(text);
                        if (parsed.RootElement.TryGetProperty("id", out var idElement) && idElement.GetString() != raceDoc.Id)
                        {
                            Interlocked.Increment(ref wrongDocReads);
                        }
                        else
                        {
                            Interlocked.Increment(ref goodReads);
                        }
                    }
                    catch (JsonException)
                    {
                        Interlocked.Increment(ref tornReads);
                    }
                    Thread.Sleep(25);
                }
            });

            string latest = "";
            for (int round = 1; round <= 12; round++)
            {
                raceDoc.Units[0].Translation = "第 " + round + " 轮 A";
                raceRepo.Save(raceDoc);                 // 进防抖队列（200ms 后自动写这一版）
                await Task.Delay(200);                  // 让防抖写盘真的开始写
                latest = "第 " + round + " 轮 B";
                raceDoc.Units[0].Translation = latest;
                raceRepo.Save(raceDoc);                 // 待写内容变成新的一版
                await raceRepo.FlushAsync();            // 与在途的那次写盘抢同一个文档
            }

            bool raceSettled = await WaitUntilAsync(() => raceRepo.PendingCount == 0, 8000);
            await Task.Delay(300);
            Volatile.Write(ref stopReader, true);
            await raceReader;

            Check("doc-race 持续读盘读到的都是完整 JSON", tornReads == 0 && wrongDocReads == 0,
                "完整 JSON " + goodReads + " 次；瞬时共享冲突（重试即可）" + transientReads + " 次；半截 " + tornReads + " 次；串文档 " + wrongDocReads + " 次");
            Check("doc-race 读方确实读到了内容", goodReads > 0, "goodReads=" + goodReads);
            CheckEq("doc-race 写盘没有记录错误", "0", raceRepo.WriteErrors.Count.ToString());
            Check("doc-race 写盘最终落定（待写队列清空）", raceSettled, "PendingCount=" + raceRepo.PendingCount);

            // 最要紧的一条：磁盘最终内容必须等于内存里的最新内容。
            // 两个写者没串行化时，慢的那次会盖掉新的那次，磁盘就永久停在旧版本。
            string onDisk = ReadFileWithRetry(raceFile);
            string onDiskTranslation;
            using (var parsed = JsonDocument.Parse(onDisk))
            {
                onDiskTranslation = parsed.RootElement.GetProperty("units")[0].GetProperty("translation").GetString() ?? "";
            }
            CheckEq("doc-race 磁盘最终内容等于内存最新内容", latest, onDiskTranslation);
            raceRepo.Dispose();
        }
        finally
        {
            TryDeleteDir(raceDir);
        }
    }

    // ------------------------------------------------------------------
    // W3c：SettingsRepository 设置仓库
    // ------------------------------------------------------------------
    private static async Task RunSettingsRepositoryAsync()
    {
        Console.WriteLine();
        Console.WriteLine("--- SettingsRepository 设置仓库 ---");

        // ---- 非法值回落 ----
        var dirty = new AppSettings
        {
            DefinitionLanguage = "jp",
            DictionarySource = "不存在的来源",
            PromptTemplateId = "不存在的模板",
            ContextUnits = 99,
            AutoSaveMs = 10,
            UiScale = 9f,
            RequestTimeoutSec = 1,
            MaxRetries = 99,
            WebServerPort = 22,
            DailyGoal = -5,
            TargetLang = "   ",
            SourceLang = "",
        };
        SettingsRepository.Sanitize(dirty);
        CheckEq("set-sanitize 非法 definitionLanguage 回落 zh", "zh", dirty.DefinitionLanguage);
        CheckEq("set-sanitize 非法查词来源回落 auto", "auto", dirty.DictionarySource);
        CheckEq("set-sanitize 非法提示词回落 default", "default", dirty.PromptTemplateId);
        CheckEq("set-sanitize 空 targetLang 回落 zh-CN", "zh-CN", dirty.TargetLang);
        CheckEq("set-sanitize 空 sourceLang 回落 auto", "auto", dirty.SourceLang);
        CheckEq("set-sanitize contextUnits 上限 clamp", "10", dirty.ContextUnits.ToString());
        CheckEq("set-sanitize autoSaveMs 下限 clamp", "200", dirty.AutoSaveMs.ToString());
        CheckEq("set-sanitize uiScale 上限 clamp", "1.5", Num(dirty.UiScale));
        CheckEq("set-sanitize 请求超时下限 clamp", "10", dirty.RequestTimeoutSec.ToString());
        CheckEq("set-sanitize 重试次数上限 clamp", "5", dirty.MaxRetries.ToString());
        CheckEq("set-sanitize Web 端口下限 clamp", "1024", dirty.WebServerPort.ToString());
        CheckEq("set-sanitize 每日目标下限 clamp", "0", dirty.DailyGoal.ToString());
        CheckEq("set-sanitize 缺少提供方时自动补一个", "1", dirty.Providers.Count.ToString());
        Check("set-sanitize 补的提供方有默认模型槽位", dirty.ActiveModel != null, "");
        Check("set-sanitize 解析出的当前提供方非空", dirty.ActiveProvider != null, "");

        var negative = new AppSettings { ContextUnits = -3, AutoSaveMs = 999999, UiScale = 0.1f, RequestTimeoutSec = 9999, MaxRetries = -1, WebServerPort = 999999 };
        SettingsRepository.Sanitize(negative);
        CheckEq("set-sanitize contextUnits 下限 clamp", "0", negative.ContextUnits.ToString());
        CheckEq("set-sanitize autoSaveMs 上限 clamp", "5000", negative.AutoSaveMs.ToString());
        CheckEq("set-sanitize uiScale 下限 clamp", "0.8", Num(negative.UiScale));
        CheckEq("set-sanitize 请求超时上限 clamp", "600", negative.RequestTimeoutSec.ToString());
        CheckEq("set-sanitize 重试次数下限 clamp", "0", negative.MaxRetries.ToString());
        CheckEq("set-sanitize Web 端口上限 clamp", "65535", negative.WebServerPort.ToString());

        foreach (string lang in new[] { "zh", "both", "en" })
        {
            var legal = new AppSettings { DefinitionLanguage = lang };
            CheckEq("set-sanitize 合法释义语言保留 " + lang, lang, SettingsRepository.Sanitize(legal).DefinitionLanguage);
        }
        CheckEq("set-sanitize 释义语言 null 回落 zh", "zh", SettingsRepository.SanitizeDefinitionLanguage(null));
        CheckEq("set-sanitize 释义语言大小写正规化", "en", SettingsRepository.SanitizeDefinitionLanguage("EN"));

        var outOfRangeModel = new AppSettings();
        var provider = new ProviderConfig("p", "p");
        var model = new ModelConfig("m", "m", "p") { Temperature = 9.0, MaxTokens = 99_999_999, TopP = 5.0 };
        model.Billing.InputPrice = -1;
        model.Billing.OutputPrice = -2;
        provider.Models.Add(model);
        outOfRangeModel.Providers.Add(provider);
        SettingsRepository.Sanitize(outOfRangeModel);
        CheckEq("set-sanitize 温度越界 clamp", "2", Num(outOfRangeModel.Providers[0].Models[0].Temperature));
        CheckEq("set-sanitize maxTokens 越界 clamp", "1000000", outOfRangeModel.Providers[0].Models[0].MaxTokens.ToString());
        CheckEq("set-sanitize topP 越界 clamp", "1", Num(outOfRangeModel.Providers[0].Models[0].TopP));
        CheckEq("set-sanitize 负单价 clamp 到 0", "0", Num(outOfRangeModel.Providers[0].Models[0].Billing.InputPrice));

        // ---- 缺文件 / 缺字段：都用默认值，不抛异常 ----
        string dir = TempDir("settings");
        try
        {
            string file = Path.Combine(dir, "settings.json");
            using (var repo = new SettingsRepository(file, debounceMs: 100))
            {
                repo.Load();
                CheckEq("set-missing 缺文件时用默认 targetLang", "zh-CN", repo.Settings.TargetLang);
                CheckEq("set-missing 缺文件时用默认 definitionLanguage", "zh", repo.Settings.DefinitionLanguage);
                CheckEq("set-missing 缺文件时用默认 contextUnits", "3", repo.Settings.ContextUnits.ToString());
                CheckEq("set-missing 缺文件时用默认主题", "SYSTEM", repo.Settings.ThemeMode.ToString());
                CheckEq("set-missing 缺文件时用默认防抖间隔", "700", repo.Settings.AutoSaveMs.ToString());
                Check("set-missing 缺文件时也有可用的提供方", repo.ActiveProvider != null, "");
            }

            File.WriteAllText(file, "{\"targetLang\":\"ja\"}", new UTF8Encoding(false));
            using (var repo = new SettingsRepository(file, debounceMs: 100))
            {
                repo.Load();
                CheckEq("set-missing 只给一个字段也能读", "ja", repo.Settings.TargetLang);
                CheckEq("set-missing 其余字段仍是默认值", "3", repo.Settings.ContextUnits.ToString());
                CheckEq("set-missing 其余字段仍是默认值（防抖）", "700", repo.Settings.AutoSaveMs.ToString());
            }

            // ---- 损坏文件：不抛异常 + 备份 ----
            File.WriteAllText(file, "{ 这不是合法 JSON", new UTF8Encoding(false));
            using (var repo = new SettingsRepository(file, debounceMs: 100))
            {
                repo.Load();
                CheckEq("set-broken 损坏文件回落默认设置", "zh-CN", repo.Settings.TargetLang);
                Check("set-broken 记录了告警", !string.IsNullOrEmpty(repo.LoadWarning), repo.LoadWarning ?? "");
                Check("set-broken 损坏文件已备份", Directory.GetFiles(dir, "settings.json.broken-*").Length == 1, "");
            }
        }
        finally
        {
            TryDeleteDir(dir);
        }

        // ---- 保存后重新加载一致 ----
        string dir2 = TempDir("settings-roundtrip");
        try
        {
            string file = Path.Combine(dir2, "settings.json");
            using (var repo = new SettingsRepository(file, debounceMs: 100))
            {
                repo.Load();
                repo.SetProvider("anthropic", "https://api.anthropic.com", "sk-test-key", "claude-3-5-sonnet",
                    temperature: 0.7, maxTokens: 2048, inputPrice: 3.0, outputPrice: 15.0);
                repo.Update(s =>
                {
                    s.TargetLang = "ja";
                    s.SourceLang = "en";
                    s.DetectLanguage = false;
                    s.ContextUnits = 5;
                    s.SystemPrompt = "只输出译文 {targetLang}";
                    s.Glossary = "apple=苹果";
                    s.DefinitionLanguage = "both";
                    s.LookupAiFallback = true;
                    s.WordLookupEnabled = false;
                    // 翻译页三种取词方式的独立开关（2026-10 新增）：故意设成「一开两关」，
                    // 只有真的写进 settings.json 又读回来，才能保证不是「界面上一改就生效、重启就没了」
                    s.LookupHoverEnabled = false;
                    s.LookupClickEnabled = true;
                    s.LookupSelectionEnabled = false;
                    s.ThemeMode = ThemeMode.DARK;
                    s.AutoSaveMs = 1500;
                    s.DefaultExportFormat = ExportFormat.CSV;
                    s.UiScale = 1.2f;
                    s.RequestTimeoutSec = 45;
                });
                Check("set-roundtrip 有内容等待落盘", repo.IsDirty, "");
                await repo.FlushAsync();
                Check("set-roundtrip FlushAsync 后没有待写内容", !repo.IsDirty, "");
                Check("set-roundtrip 文件已生成", File.Exists(file), file);
            }

            using (var repo = new SettingsRepository(file, debounceMs: 100))
            {
                repo.Load();
                var s = repo.Settings;
                Check("set-roundtrip 重新加载没有告警", repo.LoadWarning == null, repo.LoadWarning ?? "");
                CheckEq("set-roundtrip provider 类型一致", "ANTHROPIC", s.ActiveProvider?.Type.ToString() ?? "null");
                CheckEq("set-roundtrip baseUrl 一致", "https://api.anthropic.com", s.ActiveProvider?.BaseUrl ?? "null");
                CheckEq("set-roundtrip apiKey 一致", "sk-test-key", s.ActiveProvider?.ApiKey ?? "null");
                CheckEq("set-roundtrip model 一致", "claude-3-5-sonnet", s.ActiveModel?.Name ?? "null");
                CheckEq("set-roundtrip temperature 一致", "0.7", Num(s.ActiveModel?.Temperature ?? -1));
                CheckEq("set-roundtrip maxTokens 一致", "2048", s.ActiveModel?.MaxTokens.ToString() ?? "null");
                CheckEq("set-roundtrip 输入单价一致", "3", Num(s.ActiveModel?.Billing.InputPrice ?? -1));
                CheckEq("set-roundtrip 输出单价一致", "15", Num(s.ActiveModel?.Billing.OutputPrice ?? -1));
                CheckEq("set-roundtrip 高峰倍率仍为 1", "1", Num(s.ActiveModel?.Billing.PeakMultiplier ?? -1));
                CheckEq("set-roundtrip targetLang 一致", "ja", s.TargetLang);
                CheckEq("set-roundtrip sourceLang 一致", "en", s.SourceLang);
                CheckEq("set-roundtrip detectLanguage 一致", "False", s.DetectLanguage.ToString());
                CheckEq("set-roundtrip contextUnits 一致", "5", s.ContextUnits.ToString());
                CheckEq("set-roundtrip systemPrompt 一致", "只输出译文 {targetLang}", s.SystemPrompt);
                CheckEq("set-roundtrip glossary 一致", "apple=苹果", s.Glossary);
                CheckEq("set-roundtrip definitionLanguage 一致", "both", s.DefinitionLanguage);
                CheckEq("set-roundtrip lookupAiFallback 一致", "True", s.LookupAiFallback.ToString());
                CheckEq("set-roundtrip wordLookupEnabled 一致", "False", s.WordLookupEnabled.ToString());
                CheckEq("set-roundtrip lookupHoverEnabled 一致", "False", s.LookupHoverEnabled.ToString());
                CheckEq("set-roundtrip lookupClickEnabled 一致", "True", s.LookupClickEnabled.ToString());
                CheckEq("set-roundtrip lookupSelectionEnabled 一致", "False", s.LookupSelectionEnabled.ToString());
                CheckEq("set-roundtrip 主题一致", "DARK", s.ThemeMode.ToString());
                CheckEq("set-roundtrip 防抖间隔一致", "1500", s.AutoSaveMs.ToString());
                CheckEq("set-roundtrip 导出格式一致", "CSV", s.DefaultExportFormat.ToString());
                CheckEq("set-roundtrip 界面缩放一致", "1.2", Num(s.UiScale));
                CheckEq("set-roundtrip 请求超时一致", "45", s.RequestTimeoutSec.ToString());

                // 缺字段的老 settings.json 读进来时必须落到「三个都开」的默认值（不能变成全关）
                var fresh = new AppSettings();
                Check("lookup-default 三个取词开关默认都开",
                    fresh.LookupHoverEnabled && fresh.LookupClickEnabled && fresh.LookupSelectionEnabled
                    && fresh.WordLookupEnabled,
                    "默认值：总开关=" + fresh.WordLookupEnabled + " 悬浮=" + fresh.LookupHoverEnabled
                    + " 单击=" + fresh.LookupClickEnabled + " 划词=" + fresh.LookupSelectionEnabled);

                // 释义语言的非法值也要在落盘后再读时被拦住
                string json = File.ReadAllText(file, Encoding.UTF8);
                Check("set-roundtrip 文件里是 provider 扁平结构", json.Contains("\"provider\"") && json.Contains("\"baseUrl\""), "");
            }
        }
        finally
        {
            TryDeleteDir(dir2);
        }

        // ---- 防抖写盘 ----
        string dir3 = TempDir("settings-debounce");
        try
        {
            string file = Path.Combine(dir3, "settings.json");
            using var repo = new SettingsRepository(file, debounceMs: 60000);
            repo.Load();
            repo.Update(s => s.TargetLang = "ko");
            Check("set-debounce 防抖期内还没落盘", !File.Exists(file), file);
            await repo.FlushAsync();
            Check("set-debounce FlushAsync 后已落盘", File.Exists(file), file);
            Check("set-debounce 落盘内容含新值", File.ReadAllText(file, Encoding.UTF8).Contains("ko"), "");
        }
        finally
        {
            TryDeleteDir(dir3);
        }
    }

    // ------------------------------------------------------------------
    // W3c：AiClient AI 调用
    // ------------------------------------------------------------------
    private static async Task RunAiClientAsync()
    {
        Console.WriteLine();
        Console.WriteLine("--- AiClient AI 调用 ---");

        // ---- 提示词构建 ----
        var promptSettings = new AppSettings
        {
            DetectLanguage = false,
            SourceLang = "en",
            TargetLang = "zh-CN",
            Glossary = "apple=苹果\n# 注释行\ncat: 猫",
            ContextUnits = 1,
        };
        string defaultPrompt = PromptBuilder.BuildSystemPrompt(promptSettings, "Hello", "文档A", UnitMode.LINE);
        Check("ai-prompt 默认模板替换 {sourceLang}/{targetLang}",
            defaultPrompt.Contains("从 en 翻译成 zh-CN") && !defaultPrompt.Contains("{"), Show(defaultPrompt));
        Check("ai-prompt 模板无 {glossary} 时术语表追加在末尾",
            defaultPrompt.Contains("术语表（必须严格使用以下译法）：\n- apple → 苹果\n- cat → 猫"), Show(defaultPrompt));
        Check("ai-prompt 逐行模式不追加逐句提示", !defaultPrompt.Contains("当前按句翻译"), Show(defaultPrompt));

        var autoLang = new AppSettings { DetectLanguage = false, SourceLang = "auto", TargetLang = "zh-CN" };
        Check("ai-prompt 源语言 auto 显示为「原语言」",
            PromptBuilder.BuildSystemPrompt(autoLang, "", "d", UnitMode.LINE).Contains("从 原语言 翻译成 zh-CN"), "");

        var detect = new AppSettings { DetectLanguage = true, SourceLang = "en", TargetLang = "zh-CN" };
        Check("ai-prompt 打开探测开关时按原文判语言",
            PromptBuilder.BuildSystemPrompt(detect, "这是一段中文原文。", "d", UnitMode.LINE).Contains("从 zh-CN 翻译成"), "");

        var custom = new AppSettings
        {
            SystemPrompt = "把 {docName} 的 {mode} 内容从 {sourceLang} 翻成 {targetLang}。{glossary}",
            DetectLanguage = false,
            SourceLang = "en",
            TargetLang = "ja",
            Glossary = "a=b",
        };
        string customPrompt = PromptBuilder.BuildSystemPrompt(custom, "x", "我的文档", UnitMode.SENTENCE);
        Check("ai-prompt 自定义模板占位符全部替换",
            customPrompt.Contains("把 我的文档 的 逐句 内容从 en 翻成 ja。") &&
            customPrompt.Contains("- a → b") && !customPrompt.Contains("{"), Show(customPrompt));
        Check("ai-prompt 逐句模式追加提示",
            customPrompt.EndsWith("当前按句翻译，请保证译文是完整通顺的句子。"), Show(customPrompt));

        var units = new List<TranslationUnit>
        {
            new("一", "壹"),
            new("二", ""),
            new("三", "叁"),
            new("四", ""),
        };
        string userPrompt = PromptBuilder.BuildUserPrompt(promptSettings, "文档A", units[3], UnitMode.LINE, 3, 4, units.Take(3).ToList());
        Check("ai-prompt 用户提示带文档名 / 模式 / 进度",
            userPrompt.Contains("文档：文档A  |  当前模式：逐行  |  当前进度：4/4"), Show(userPrompt));
        Check("ai-prompt 前文参考只取最近 N 条已译",
            userPrompt.Contains("前文参考：【原文：三 → 译文：叁】") && !userPrompt.Contains("【原文：一 → 译文：壹】"), Show(userPrompt));
        Check("ai-prompt 用户提示以待翻译原文结尾", userPrompt.EndsWith("  |  待翻译原文：四"), Show(userPrompt));

        var noContext = new AppSettings { ContextUnits = 0 };
        string barePrompt = PromptBuilder.BuildUserPrompt(noContext, "d", new TranslationUnit("X"), UnitMode.LINE, 0, 1,
            new List<TranslationUnit> { new("A", "甲") });
        Check("ai-prompt contextUnits=0 时不带前文参考", !barePrompt.Contains("前文参考"), Show(barePrompt));

        // ---- 未配置：必须抛明确的中文异常 ----
        using (var client = new AiClient(new AppSettings()))
        {
            string message = await CatchMessageAsync(() => client.ChatAsync("s", "u"));
            Check("ai-config 未配置提供商时抛中文异常", message.Contains("API 提供商") && HasCjk(message), message);
        }

        using (var client = new AiClient(MakeSettings("openai", "", "k", "m")))
        {
            string message = await CatchMessageAsync(() => client.ChatAsync("s", "u"));
            Check("ai-config 未配置 Base URL 时抛中文异常", message.Contains("Base URL") && HasCjk(message), message);
        }

        using (var client = new AiClient(MakeSettings("openai", "http://127.0.0.1:1", "k", "   ")))
        {
            string message = await CatchMessageAsync(() => client.ChatAsync("s", "u"));
            Check("ai-config 未配置模型时抛中文异常", message.Contains("模型") && HasCjk(message), message);
        }

        // ---- 地址拼接 ----
        CheckEq("ai-url 去尾斜杠并补 /v1", "https://api.example.com/v1/chat/completions",
            AiClient.ResolveUrl("https://api.example.com/", "/chat/completions"));
        CheckEq("ai-url 已有 /v1 时不重复", "https://api.example.com/v1/messages",
            AiClient.ResolveUrl("https://api.example.com/v1", "/messages"));
        CheckEq("ai-url 没有尾斜杠", "https://api.example.com/v1/chat/completions",
            AiClient.ResolveUrl("https://api.example.com", "/chat/completions"));

        // ---- 本地假服务：OpenAI 兼容 ----
        using var server = new FakeHttpServer();
        const string openAiBody = """{"choices":[{"message":{"role":"assistant","content":"  译文内容  "}}],"usage":{"prompt_tokens":11,"completion_tokens":22,"prompt_tokens_details":{"cached_tokens":5}}}""";
        server.Handler = _ => new FakeResponse(200, openAiBody);

        var openAiSettings = MakeSettings("openai", server.BaseUrl, "sk-abc", "gpt-test", 0.3, 1234, 1.0, 2.0);
        using var openAi = new AiClient(openAiSettings);
        Check("ai-openai 不是 Anthropic 协议", !openAi.IsAnthropic, "");

        var openAiResult = await openAi.ChatAsync("系统提示", "用户提示");
        var openAiReq = server.LastRequest;
        CheckEq("ai-openai 返回文本已 trim", "译文内容", openAiResult.Text);
        CheckEq("ai-openai promptTokens", "11", openAiResult.PromptTokens.ToString());
        CheckEq("ai-openai completionTokens", "22", openAiResult.CompletionTokens.ToString());
        CheckEq("ai-openai cachedTokens", "5", openAiResult.CachedTokens.ToString());
        Check("ai-openai 请求已到达本地假服务", openAiReq != null, "");
        if (openAiReq != null)
        {
            CheckEq("ai-openai 请求方法", "POST", openAiReq.Method);
            CheckEq("ai-openai 请求路径", "/v1/chat/completions", openAiReq.Path);
            CheckEq("ai-openai Authorization 头", "Bearer sk-abc", openAiReq.Header("authorization"));
            Check("ai-openai Content-Type 是 JSON", openAiReq.Header("content-type").Contains("application/json", StringComparison.OrdinalIgnoreCase), openAiReq.Header("content-type"));

            var body = openAiReq.Json();
            CheckEq("ai-openai 请求体 model", "gpt-test", JsonStr(body, "model"));
            CheckEq("ai-openai 请求体 max_tokens", "1234", JsonInt(body, "max_tokens"));
            CheckEq("ai-openai 请求体 temperature", "0.3", Num(JsonDouble(body, "temperature")));
            CheckEq("ai-openai 请求体消息条数", "2", body.GetProperty("messages").GetArrayLength().ToString());
            CheckEq("ai-openai 系统消息角色", "system", JsonStr(body.GetProperty("messages")[0], "role"));
            CheckEq("ai-openai 系统消息内容", "系统提示", JsonStr(body.GetProperty("messages")[0], "content"));
            CheckEq("ai-openai 用户消息角色", "user", JsonStr(body.GetProperty("messages")[1], "role"));
            CheckEq("ai-openai 用户消息内容", "用户提示", JsonStr(body.GetProperty("messages")[1], "content"));
        }
        CheckEq("ai-openai 费用口径与 server.js costOf 一致", "0.000055", Num(openAiResult.Cost));
        CheckEq("ai-openai CostOf 与结果里的费用一致", "0.000055", Num(openAi.CostOf(11, 22)));

        // ---- 本地假服务：Anthropic ----
        const string anthropicBody = """{"id":"msg_1","content":[{"type":"text","text":"Anthropic 译文"},{"type":"tool_use","id":"x"}],"usage":{"input_tokens":9,"output_tokens":4,"cache_read_input_tokens":4}}""";
        server.Handler = _ => new FakeResponse(200, anthropicBody);
        var anthropicSettings = MakeSettings("anthropic", server.BaseUrl, "sk-ant", "claude-test", 0.2, 2048, 3.0, 15.0);
        using var anthropic = new AiClient(anthropicSettings);
        Check("ai-anthropic 按 provider.type 判定协议", anthropic.IsAnthropic, "");

        var anthropicResult = await anthropic.ChatAsync("sys", "usr");
        var anthropicReq = server.LastRequest;
        CheckEq("ai-anthropic 返回文本只拼 text 块", "Anthropic 译文", anthropicResult.Text);
        CheckEq("ai-anthropic input_tokens", "9", anthropicResult.PromptTokens.ToString());
        CheckEq("ai-anthropic output_tokens", "4", anthropicResult.CompletionTokens.ToString());
        CheckEq("ai-anthropic cache_read_input_tokens", "4", anthropicResult.CachedTokens.ToString());
        if (anthropicReq != null)
        {
            CheckEq("ai-anthropic 请求路径", "/v1/messages", anthropicReq.Path);
            CheckEq("ai-anthropic x-api-key 头", "sk-ant", anthropicReq.Header("x-api-key"));
            CheckEq("ai-anthropic anthropic-version 头", "2023-06-01", anthropicReq.Header("anthropic-version"));
            CheckEq("ai-anthropic 不带 Authorization 头", "", anthropicReq.Header("authorization"));

            var body = anthropicReq.Json();
            CheckEq("ai-anthropic 请求体 model", "claude-test", JsonStr(body, "model"));
            CheckEq("ai-anthropic 请求体 system", "sys", JsonStr(body, "system"));
            CheckEq("ai-anthropic 请求体 max_tokens", "2048", JsonInt(body, "max_tokens"));
            CheckEq("ai-anthropic 请求体消息条数", "1", body.GetProperty("messages").GetArrayLength().ToString());
            CheckEq("ai-anthropic 消息角色", "user", JsonStr(body.GetProperty("messages")[0], "role"));
            CheckEq("ai-anthropic 消息内容", "usr", JsonStr(body.GetProperty("messages")[0], "content"));
        }
        CheckEq("ai-anthropic 费用口径与 costOf 一致", "0.000087", Num(anthropicResult.Cost));

        using (var byDomain = new AiClient(MakeSettings("openai", "https://api.anthropic.com", "k", "claude-x")))
        {
            Check("ai-anthropic baseUrl 含 anthropic.com 也走 Anthropic 协议", byDomain.IsAnthropic, "");
        }

        // ---- 错误处理 ----
        server.Handler = _ => new FakeResponse(401, """{"error":{"message":"invalid api key"}}""");
        string apiError = await CatchMessageAsync(() => openAi.ChatAsync("s", "u"));
        Check("ai-error 非 2xx 抛中文异常并带状态码与原始信息",
            apiError.Contains("401") && apiError.Contains("invalid api key") && HasCjk(apiError), apiError);

        server.Handler = _ => new FakeResponse(200, "这不是 JSON");
        string parseError = await CatchMessageAsync(() => openAi.ChatAsync("s", "u"));
        Check("ai-error 响应不是 JSON 时抛中文异常", parseError.Contains("无法解析") && HasCjk(parseError), parseError);

        server.Handler = _ => new FakeResponse(200, """{"usage":{"prompt_tokens":1}}""");
        string emptyError = await CatchMessageAsync(() => openAi.ChatAsync("s", "u"));
        Check("ai-error 响应没有内容时抛中文异常", emptyError.Contains("为空") && HasCjk(emptyError), emptyError);

        // ---- TranslateAsync：提示词带文档名 / 前文参考 / 待翻译原文 ----
        server.Handler = _ => new FakeResponse(200, openAiBody);
        var docSettings = MakeSettings("openai", server.BaseUrl, "k", "gpt-test", 0.2, 1024, 0.0, 0.0);
        docSettings.ContextUnits = 3;
        using (var docClient = new AiClient(docSettings))
        {
            var doc = new TranslationDoc(
                "doc-ai-1",
                "文档名",
                TranslationDoc.DefaultFolder,
                new List<TranslationUnit> { new("Hello.", "你好。", true), new("World.", "", false) },
                UnitMode.SENTENCE,
                "Hello.\nWorld.");

            var translated = await docClient.TranslateAsync(doc, 1);
            var req = server.LastRequest;
            CheckEq("ai-translate 返回译文", "译文内容", translated.Text);
            Check("ai-translate 请求已发出", req != null, "");
            if (req != null)
            {
                var messages = req.Json().GetProperty("messages");
                string system = JsonStr(messages[0], "content");
                string user = JsonStr(messages[1], "content");
                Check("ai-translate 系统提示为逐句模式", system.EndsWith("当前按句翻译，请保证译文是完整通顺的句子。", StringComparison.Ordinal), Show(system));
                Check("ai-translate 用户提示带文档名与进度", user.Contains("文档：文档名  |  当前模式：逐句  |  当前进度：2/2"), Show(user));
                Check("ai-translate 用户提示带前文参考", user.Contains("前文参考：【原文：Hello. → 译文：你好。】"), Show(user));
                Check("ai-translate 用户提示以待翻译原文结尾", user.EndsWith("  |  待翻译原文：World.", StringComparison.Ordinal), Show(user));
            }

            string outOfRange = await CatchMessageAsync(() => docClient.TranslateAsync(doc, 5));
            Check("ai-translate 下标越界抛中文异常", outOfRange.Contains("越界") && HasCjk(outOfRange), outOfRange);
        }

        // ---- CancellationToken：用户要能随时停批量翻译 ----
        server.Handler = _ => new FakeResponse(200, openAiBody, DelayMs: 10000);
        using (var cts = new CancellationTokenSource())
        {
            cts.CancelAfter(300);
            bool cancelled = false;
            string? wrong = null;
            try
            {
                await openAi.ChatAsync("s", "u", cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                wrong = ex.GetType().Name + "：" + ex.Message;
            }

            Check("ai-cancel 取消时抛 OperationCanceledException", cancelled, wrong ?? "没有取消");
        }
    }

    // ------------------------------------------------------------------
    // W3c 用到的断言辅助
    // ------------------------------------------------------------------
    private static string TempDir(string tag) =>
        Path.Combine(Path.GetTempPath(), "linetrans-core-tests", tag + "-" + Guid.NewGuid().ToString("N"));

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响自测结论。
        }
    }

    /// <summary>
    /// 读文档 JSON，容忍瞬时的共享冲突。
    ///
    /// 为什么必须这样读：替换式落盘（先写 &lt;id&gt;.json.tmp，再覆盖改名成 &lt;id&gt;.json）在 Windows 上
    /// 会让目标文件名在改名的一小段时间里打不开——覆盖改名要求目标文件没有别的手柄占着。
    /// 本机实测这个「不可读窗口」30~230ms（杀毒 / 同步盘 / 机械盘会继续放大），
    /// 所以任何轮询读都可能正好撞进去。撞进去是瞬时状态，不是数据问题：重试即可，
    /// 绝不能让 File.ReadAllText 的 IOException 把自测整个打崩（2026-10 那次就是这样崩的）。
    /// </summary>
    private static string ReadFileWithRetry(string path, int attempts = 100, int delayMs = 20)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(25);
            waited += 25;
        }
        return condition();
    }

    private static async Task<string> CatchMessageAsync(Func<Task> action)
    {
        try
        {
            await action();
            return "(没有抛异常)";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static bool HasCjk(string value)
    {
        foreach (char c in value)
        {
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        }
        return false;
    }

    private static string Num(double value) =>
        value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);

    private static string Num(float value) =>
        value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);

    private static string JsonStr(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "(" + name + " 不是字符串)";

    private static string JsonInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32().ToString()
            : "(" + name + " 不是数字)";

    private static double JsonDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : double.NaN;

    private static AppSettings MakeSettings(
        string type,
        string baseUrl,
        string apiKey,
        string model,
        double temperature = 0.2,
        int maxTokens = 4096,
        double inputPrice = 0.0,
        double outputPrice = 0.0)
    {
        var providerType = SettingsRepository.ParseProviderType(type);
        var settings = new AppSettings();
        var provider = new ProviderConfig("p1", ProviderTypeInfo.Label(providerType))
        {
            Type = providerType,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
        };
        var modelConfig = new ModelConfig("m1", model, "p1")
        {
            Temperature = temperature,
            MaxTokens = maxTokens,
        };
        modelConfig.Billing.InputPrice = inputPrice;
        modelConfig.Billing.OutputPrice = outputPrice;
        provider.Models.Add(modelConfig);
        settings.Providers.Add(provider);
        settings.ActiveProviderId = "p1";
        settings.ActiveModelId = "m1";
        return settings;
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
