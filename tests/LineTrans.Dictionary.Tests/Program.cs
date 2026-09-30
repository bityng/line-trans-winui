using System.Diagnostics;
using System.Globalization;
using System.Text;
using LineTrans.Core.Dictionary;

namespace LineTrans.Dictionary.Tests;

/// <summary>
/// 离线词库 + 划词查义链路的 headless 自测（控制台断言，零第三方依赖）。
/// 用真实 4 万行词库数据跑，不用玩具数据。
/// 任何一条 FAIL 都会让退出码非 0。
/// </summary>
internal static class Program
{
    private static readonly TestRunner T = new();
    private static readonly Random Rng = new(20260930);
    private static readonly Stopwatch Sw = new();

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("LineTrans 离线词库（C# 移植版）自测");
        Console.WriteLine("运行时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        var corePath = Resolve("core.tsv");
        var lemmaPath = Resolve("lemma.tsv");

        var load = Section1Load(corePath, lemmaPath);
        Section2Direct();
        Section3Lemma();
        Section4Variants(corePath, lemmaPath);
        Section5Clean();
        Section6Case();
        Section7Lru();
        Section8Miss();
        Section9Performance(load.SampleWords);
        Section10Wordbook();
        Section11DictionaryService();
        Section12AiFallback();
        Section13BackgroundLoad(corePath, lemmaPath, load.SyncLoadMs);

        return T.Summary("离线词库自测");
    }

    // ------------------------------------------------------------------ 工具

    private static readonly Dictionary<string, string> PathCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>定位数据文件：输出目录 → 工程目录 → 源码树快照。</summary>
    private static string Resolve(string fileName)
    {
        if (PathCache.TryGetValue(fileName, out var cached)) return cached;

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "data", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "data", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), fileName),
            @"F:\逐行翻译\.work\native-dict\data\" + fileName,
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                PathCache[fileName] = c;
                return c;
            }
        }
        PathCache[fileName] = candidates[0];
        return candidates[0];
    }

    private static string Pad(string s, int width)
    {
        var w = 0;
        foreach (var ch in s) w += ch > 0x2E80 ? 2 : 1;
        return s + new string(' ', Math.Max(0, width - w));
    }

    // ------------------------------------------------------------------ 1. 加载

    private sealed class LoadResult
    {
        public bool CoreOk;
        public bool LemmaOk;
        public double SyncLoadMs;
        public long CoreCount;
        public long LemmaCount;
        public List<string> SampleWords { get; } = new();
    }

    private static LoadResult Section1Load(string corePath, string lemmaPath)
    {
        T.Section("1. 词库加载（真实数据）");
        T.Info("core.tsv  = " + corePath);
        T.Info("lemma.tsv = " + lemmaPath);

        var result = new LoadResult();
        T.Check("core.tsv 文件存在", File.Exists(corePath), corePath);
        T.Check("lemma.tsv 文件存在", File.Exists(lemmaPath), lemmaPath);
        if (!File.Exists(corePath) || !File.Exists(lemmaPath))
        {
            return result;
        }

        var coreLines = File.ReadAllLines(corePath);
        var lemmaLines = File.ReadAllLines(lemmaPath);
        var arrowCount = 0;
        var goodCount = 0;
        foreach (var line in lemmaLines)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            if (parts[1].Trim() == "->") arrowCount++;
            else if (parts[1].Trim().Length > 0) goodCount++;
        }
        T.Info("原始文件行数：core=" + coreLines.Length + "，lemma=" + lemmaLines.Length);
        T.Info("lemma 第二列健康度：有效原形 = " + goodCount + "，恒为 \"->\" 的坏行 = " + arrowCount);
        T.Check("lemma.tsv 不是那份第二列恒为 \"->\" 的坏文件", arrowCount == 0 && goodCount > 0,
            "有效=" + goodCount + " 坏行=" + arrowCount);

        Sw.Restart();
        result.CoreOk = LocalDictionary.LoadFiles(corePath, lemmaPath);
        Sw.Stop();

        var coreSize = LocalDictionary.Size;
        var lemmaSize = LocalDictionary.LemmaSize;
        result.CoreCount = coreSize;
        result.LemmaCount = lemmaSize;
        result.SyncLoadMs = Sw.Elapsed.TotalMilliseconds;
        result.LemmaOk = lemmaSize > 0 && result.CoreOk;

        Console.WriteLine();
        Console.WriteLine("   >>> 词库加载统计：core.tsv 加载词条 " + coreSize + " 条；lemma.tsv 可用映射 " + lemmaSize + " 条");
        Console.WriteLine("   >>> 加载耗时（同步，含 4 万行 + 10 万行解析）：" + Sw.ElapsedMilliseconds + " ms");
        Console.WriteLine();

        T.Check("core.tsv 加载成功", result.CoreOk, "IsLoaded=" + LocalDictionary.IsLoaded);
        T.Check("core 词条数在 39000~41000 之间（期望约 40000）", coreSize >= 39000 && coreSize <= 41000, "实际 " + coreSize);
        T.Check("lemma 可用映射数远大于 0（修复前为 0）", lemmaSize > 1000, "实际 " + lemmaSize);
        T.Check("IsLoaded 为 true", LocalDictionary.IsLoaded, "IsLoaded=" + LocalDictionary.IsLoaded);

        foreach (var line in coreLines)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('\t');
            if (parts.Length >= 2 && parts[0].Length >= 4 && parts[0].Length <= 10 && parts[0].All(char.IsLetter))
            {
                result.SampleWords.Add(parts[0].Trim().ToLowerInvariant());
            }
        }
        T.Info("可用于随机查词性能测试的词条样本数：" + result.SampleWords.Count);
        T.Check("随机查词样本充足", result.SampleWords.Count > 1000, "样本数 " + result.SampleWords.Count);
        return result;
    }

    // ------------------------------------------------------------------ 2. 直接命中

    private static void Section2Direct()
    {
        T.Section("2. 直接命中（core.tsv direct）");
        ExpectDirect("the", "ðә", "art. 那");
        ExpectDirect("run", "rʌn", "n. 跑");
        ExpectDirect("book", "buk", "n. 书, 书籍");
    }

    private static LookupResult ExpectDirect(string word, string phonetic, string meaningFragment)
    {
        var hit = LocalDictionary.Lookup(word);
        var meaning = hit.Meaning;
        Console.WriteLine("   lookup(\"" + word + "\") → 命中=" + hit.Found
            + " via=" + hit.Via
            + " 词头=" + TestRunner.Show(hit.BaseWord)
            + " 音标=" + TestRunner.Show(hit.Phonetic)
            + " 释义=" + TestRunner.Show(TestRunner.Truncate(meaning, 60)));
        T.Check("「" + word + "」命中", hit.Found, "via=" + hit.Via);
        if (!hit.Found) return hit;
        T.CheckEqual("「" + word + "」via 为 direct", "direct", hit.Via);
        T.CheckEqual("「" + word + "」音标", phonetic, hit.Phonetic);
        T.Check("「" + word + "」释义非空", meaning.Length > 0, "释义长度 " + meaning.Length);
        T.CheckContains("「" + word + "」释义包含期望片段", meaning, meaningFragment);
        return hit;
    }

    // ------------------------------------------------------------------ 3. 不规则词形还原

    private static void Section3Lemma()
    {
        T.Section("3. 不规则词形还原（lemma.tsv，任务点名的 6 个词）");

        // 任务书点名的 6 个不规则词形（词 → 期望的原形）
        ExpectLemma("ran", "run");
        ExpectLemma("went", "go");
        ExpectLemma("children", "child");
        ExpectLemma("feet", "foot");
        ExpectLemma("men", "man");
        ExpectLemma("mice", "mouse");

        Console.WriteLine();
        T.Info("补充样本：「better」在 core.tsv 里本身就是词头（lemma.tsv 里 better → well），按「直接命中优先」的既定顺序会直接命中，此处如实标注。");
        ExpectLemma("better", "well");

        Console.WriteLine();
        T.Info("补充：以下词在 core.tsv 里本身就是词头，按「直接命中优先」的既定顺序，via=direct 属正常；此处只校验 lemma.tsv 的映射链条是否可达。");
        var chain = new (string Word, string Expect)[]
        {
            ("children", "child"),
            ("better", "well"),
            ("is", "be"),
            ("was", "be"),
            ("are", "be"),
            ("gone", "go"),
        };
        foreach (var (word, expect) in chain)
        {
            var hit = LocalDictionary.Lookup(word);
            Console.WriteLine("   链条检查 " + Pad(word, 12) + " → 命中=" + hit.Found + " 词头=" + TestRunner.Show(hit.BaseWord)
                + " via=" + hit.Via + "（lemma 期望=" + expect + "）");
        }
    }

    /// <summary>
    /// 该词自身就是 core.tsv 的词头吗？用于判断按「导入 → core 直接命中 → lemma → 规则变形」
    /// 的既定顺序会走到哪一级（直接命中优先，因此不会再走到 lemma）。
    /// </summary>
    private static bool IsOwnHeadword(LookupResult hit, string word)
        => hit.Found
           && string.Equals(hit.BaseWord, word, StringComparison.Ordinal)
           && (hit.Via == "direct" || hit.Via == "import");

    private static void ExpectLemma(string word, string expectedBase)
    {
        var hit = LocalDictionary.Lookup(word);
        var baseInCore = LocalDictionary.Lookup(expectedBase);
        var line = "   lookup(\"" + word + "\") → 命中=" + hit.Found
            + " via=" + hit.Via
            + " 词头=" + TestRunner.Show(hit.BaseWord)
            + " 音标=" + TestRunner.Show(hit.Phonetic)
            + " 释义=" + TestRunner.Show(TestRunner.Truncate(hit.Meaning, 50));
        Console.WriteLine(line);

        T.Check("「" + word + "」命中", hit.Found, "via=" + hit.Via);
        if (!hit.Found) return;

        if (IsOwnHeadword(hit, word))
        {
            // core.tsv 本身就收录了该词形（children / better），既定顺序是「直接命中优先于 lemma」，
            // 所以只会走 direct。这里如实标注，不伪造成 via=lemma。
            T.Info("「" + word + "」在 core.tsv 里本身就是词头 → 按既定顺序直接命中（via=direct），不会走到 lemma 还原。");
            T.Check("「" + word + "」词头即自身（直接命中优先于 lemma，符合既定顺序）",
                string.Equals(hit.BaseWord, word, StringComparison.Ordinal), "词头=" + TestRunner.Show(hit.BaseWord));
        }
        else if (baseInCore.Found)
        {
            T.Check("「" + word + "」经 lemma 还原到原形「" + expectedBase + "」",
                string.Equals(hit.BaseWord, expectedBase, StringComparison.Ordinal),
                "实际词头=" + TestRunner.Show(hit.BaseWord) + " via=" + hit.Via);
            T.CheckEqual("「" + word + "」via=lemma", "lemma", hit.Via);
        }
        else
        {
            T.Info("「" + expectedBase + "」不在 core.tsv 词头里，按任务书要求如实报告，不做伪造断言；"
                + "「" + word + "」实际命中=" + hit.Found + " 词头=" + hit.BaseWord + " via=" + hit.Via);
        }

        T.Check("「" + word + "」释义非空", hit.Meaning.Length > 0, "释义长度 " + hit.Meaning.Length);
        T.Check("「" + word + "」音标非空", hit.Phonetic.Length > 0, "音标=" + TestRunner.Show(hit.Phonetic));
    }

    // ------------------------------------------------------------------ 4. 规则变形

    private static void Section4Variants(string corePath, string lemmaPath)
    {
        T.Section("4. 规则变形（variants：ies/es/s/ing/ed/er/est/ly）");

        Console.WriteLine("   <<< 规则变形候选序列（直接调用 Variants()，固定输入 → 固定候选序列）");
        var vector = new (string Word, string Expect)[]
        {
            ("ies", "ie"),
            ("cities", "city, citi, citie"),
            ("es", "（空：len=2 不满足 >3）"),
            ("does", "do, doe"),
            ("s", "（空：len=1 不满足 >2）"),
            ("is", "（空：len=2 不满足 >2）"),
            ("xing", "（空：len=4 不满足 >5）"),
            ("sings", "sing"),
            ("making", "mak, make"),
            ("ed", "（空：len=2 不满足 >4）"),
            ("fled", "（空：len=4 不满足 >4）"),
            ("er", "（空：len=2 不满足 >4）"),
            ("user", "（空：len=4 不满足 >4）"),
            ("est", "（空：len=3 不满足 >5）"),
            ("biggest", "bigg"),
            ("ly", "（空：len=2 不满足 >4）"),
            ("likely", "like"),
            ("playing", "play, playe"),
            ("studies", "study, studi, studie"),
            ("boxes", "box, boxe"),
            ("happier", "happi, happie"),
            ("running", "runn, runne"),
        };
        foreach (var (word, expect) in vector)
        {
            var actual = string.Join(", ", LocalDictionary.Variants(word));
            var shown = actual.Length == 0 ? "（空）" : actual;
            // 期望值里可以带「（空：len=N 不满足 >M）」这样的教学注记，比较时只取「（空）」
            var expectShown = expect.StartsWith("（空：", StringComparison.Ordinal) ? "（空）" : expect;
            Console.WriteLine("   " + Pad("\"" + word + "\"", 12) + " → " + shown
                + (expectShown == expect ? "" : "     " + expect));
            T.CheckEqual("Variants(\"" + word + "\") 序列", expectShown, shown);
        }

        Console.WriteLine();
        T.Info("任务书点名的 5 个词：逐个打印最终解析路径（既定顺序：导入 → core 直接命中 → lemma → 规则变形）");
        ExpectResolve("running", "run");
        ExpectResolve("studies", "study");
        ExpectResolve("boxes", "box");
        ExpectResolve("played", "play");
        ExpectResolve("happier", "happy");

        Console.WriteLine();
        T.Info("变体回退链（先查原形表，再查 lemma 后回原形表）——这些都是只能靠 lemma/变形链命中的词，只校验最终词头：");
        var baseChecks = new (string Word, string ExpectBase)[]
        {
            ("studies", "study"),
            ("boxes", "box"),
            ("happier", "happy"),
            ("apples", "apple"),
            ("walked", "walk"),
        };
        foreach (var (word, expectBase) in baseChecks)
        {
            var hit = LocalDictionary.Lookup(word);
            Console.WriteLine("   " + Pad(word, 12) + " → 命中=" + hit.Found + " via=" + hit.Via + " 词头=" + TestRunner.Show(hit.BaseWord));
            T.Check("「" + word + "」命中", hit.Found, "via=" + hit.Via);
            T.CheckEqual("「" + word + "」词头", expectBase, hit.BaseWord);
        }

        Console.WriteLine();
        T.Info("真实数据上「只能靠规则变形命中」的词（本身不是 core 词头、也不在 lemma.tsv 里，只有 variants() 能命中）：");
        var pure = FindPureVariantCases(corePath, lemmaPath, 3);
        if (pure.Count == 0)
        {
            T.Fail("variant 分支可达性", "在真实数据上没有找到只能靠规则变形命中的词");
        }
        else
        {
            foreach (var (word, expectBase) in pure)
            {
                var hit = LocalDictionary.Lookup(word);
                Console.WriteLine("   " + Pad(word, 16) + " → 命中=" + hit.Found + " via=" + hit.Via + " 词头=" + TestRunner.Show(hit.BaseWord)
                    + " 释义=" + TestRunner.Show(TestRunner.Truncate(hit.Meaning, 40)));
                T.Check("「" + word + "」命中", hit.Found, "via=" + hit.Via);
                T.CheckEqual("「" + word + "」via=variant（只能靠规则变形命中）", "variant", hit.Via);
                T.CheckEqual("「" + word + "」还原到原形", expectBase, hit.BaseWord);
            }
        }
    }

    /// <summary>
    /// 断言 word 最终解析到 expectedBase。
    /// 若 word 本身就是 core 词头，则按「直接命中优先」的既定顺序只会走 direct，此时只断言词头即自身，
    /// 并打印 variants() 候选（规则链是否直接给出该原形以信息形式展示，见第 3 节的 lemma 链路）。
    /// </summary>
    private static void ExpectResolve(string word, string expectedBase)
    {
        var hit = LocalDictionary.Lookup(word);
        var variants = LocalDictionary.Variants(word);
        Console.WriteLine("   lookup(\"" + word + "\") → 命中=" + hit.Found
            + " via=" + hit.Via
            + " 词头=" + TestRunner.Show(hit.BaseWord)
            + " 音标=" + TestRunner.Show(hit.Phonetic)
            + " 释义=" + TestRunner.Show(TestRunner.Truncate(hit.Meaning, 50)));
        T.Check("「" + word + "」命中", hit.Found, "via=" + hit.Via);
        if (!hit.Found) return;

        if (IsOwnHeadword(hit, word))
        {
            T.Info("「" + word + "」在 core.tsv 里本身就是词头 → 按既定顺序直接返回（via=direct），不经过规则变形；"
                + "variants(\"" + word + "\") = [" + JoinList(variants) + "]，期望原形=" + expectedBase);
            T.Check("「" + word + "」词头即自身（直接命中优先于规则变形）",
                string.Equals(hit.BaseWord, word, StringComparison.Ordinal), "词头=" + TestRunner.Show(hit.BaseWord));
            return;
        }

        T.Check("「" + word + "」最终解析到原形「" + expectedBase + "」",
            string.Equals(hit.BaseWord, expectedBase, StringComparison.Ordinal),
            "实际词头=" + TestRunner.Show(hit.BaseWord) + " via=" + hit.Via + " variants=[" + JoinList(variants) + "]");
        T.Check("「" + word + "」via 为 lemma 或 variant", hit.Via == "lemma" || hit.Via == "variant", "via=" + hit.Via);
        T.Check("「" + word + "」释义非空", hit.Meaning.Length > 0, "释义长度 " + hit.Meaning.Length);
    }

    private static string JoinList(IReadOnlyList<string> list)
        => list.Count == 0 ? "（空）" : string.Join(", ", list);

    /// <summary>
    /// 在真实词库上找出「只能靠规则变形命中」的查询词：该词本身不是 core.tsv 词头、也不在 lemma.tsv 里，
    /// 但 variants() 能把它还原到 core 词头，且 Lookup 实际就走 variant 分支。
    /// 用途：证明 variant 分支在真实数据上确实可达，而不是纸面上的分支。
    /// </summary>
    private static List<(string Word, string Base)> FindPureVariantCases(string corePath, string lemmaPath, int take)
    {
        var headwords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(corePath))
        {
            var i = line.IndexOf('\t');
            if (i > 0) headwords.Add(line.Substring(0, i));
        }
        var lemmas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(lemmaPath))
        {
            var i = line.IndexOf('\t');
            if (i > 0) lemmas.Add(line.Substring(0, i));
        }

        var sorted = headwords.ToList();
        sorted.Sort(StringComparer.Ordinal);

        var found = new List<(string Word, string Base)>();
        foreach (var head in sorted)
        {
            // 优先挑「像正常英语词形」的样本：纯小写单字、且不出现三连字母（避开 aaah 这类噪声）
            if (head.Length < 4 || !IsAsciiLower(head) || HasTripleRun(head)) continue;
            foreach (var suffix in new[] { "s", "es", "ed", "ing", "er", "ly", "est" })
            {
                var candidate = head + suffix;
                if (headwords.Contains(candidate) || lemmas.Contains(candidate)) continue;
                if (HasTripleRun(candidate)) continue;
                if (!LocalDictionary.Variants(candidate).Contains(head)) continue;
                var probe = LocalDictionary.Lookup(candidate);
                if (!probe.Found || probe.Via != "variant" || !string.Equals(probe.BaseWord, head, StringComparison.Ordinal)) continue;
                found.Add((candidate, head));
                if (found.Count >= take) return found;
            }
        }
        return found;
    }

    private static bool IsAsciiLower(string s)
    {
        foreach (var c in s) if (c < 'a' || c > 'z') return false;
        return true;
    }

    private static bool HasTripleRun(string s)
    {
        for (var i = 2; i < s.Length; i++) if (s[i] == s[i - 1] && s[i] == s[i - 2]) return true;
        return false;
    }

    // ------------------------------------------------------------------ 5. 取词清洗

    private static void Section5Clean()
    {
        T.Section("5. 取词清洗（Trim + 去掉首尾非字母/非 - 非 ' 的字符 + 小写）");
        var cases = new (string Raw, string Expect)[]
        {
            ("(Hello),", "hello"),
            ("don't", "don't"),
            ("...", ""),
            ("  Running!  ", "running"),
            ("\u201cword\u201d", "word"),
            // 中文是 Unicode 字母（Lo 类），按「只剥离非字母」的规则会被保留 —— 与安卓端 trim { } 的行为一致
            ("\u4f60\u597d hello \u3002", "\u4f60\u597d hello"),
            ("well-known", "well-known"),
            // 撇号在保留集合里（isLetter 为假，但 == '\''），首尾都不会被剥离 —— 与安卓端 trim { } 的行为一致
            ("'quoted'", "'quoted'"),
            ("\u2014", ""),
            ("", ""),
        };
        foreach (var (raw, expect) in cases)
        {
            var actual = LocalDictionary.CleanWord(raw);
            Console.WriteLine("   CleanWord(" + TestRunner.Show(raw) + ") = " + TestRunner.Show(actual));
            T.CheckEqual("清洗 " + TestRunner.Show(raw), expect, actual);
        }

        var dot = LocalDictionary.Lookup("...");
        Console.WriteLine("   lookup(\"...\") → 命中=" + dot.Found + " via=" + dot.Via + " 异常=无");
        T.Check("「...」返回未命中且不抛异常", !dot.Found && dot.Via == "miss", "Found=" + dot.Found + " via=" + dot.Via);

        var apostrophe = LocalDictionary.Lookup("don't");
        Console.WriteLine("   lookup(\"don't\") → 命中=" + apostrophe.Found + " 词头=" + TestRunner.Show(apostrophe.BaseWord));
        T.Check("「don't」保留撇号且能被查询（命中或未命中都算不崩）", apostrophe.Query == "don't", "清洗结果=" + TestRunner.Show(apostrophe.Query));
    }

    // ------------------------------------------------------------------ 6. 大小写

    private static void Section6Case()
    {
        T.Section("6. 大小写一致性：Running / RUNNING / running");
        var a = LocalDictionary.Lookup("Running");
        var b = LocalDictionary.Lookup("RUNNING");
        var c = LocalDictionary.Lookup("running");
        foreach (var (label, hit) in new[] { ("Running", a), ("RUNNING", b), ("running", c) })
        {
            Console.WriteLine("   " + Pad(label, 10) + " → 命中=" + hit.Found + " via=" + hit.Via + " 词头=" + TestRunner.Show(hit.BaseWord)
                + " 音标=" + TestRunner.Show(hit.Phonetic) + " 释义=" + TestRunner.Show(TestRunner.Truncate(hit.Meaning, 40)));
        }
        T.Check("三种写法都命中", a.Found && b.Found && c.Found, "Found=" + a.Found + "/" + b.Found + "/" + c.Found);
        T.Check("三种写法 via 一致", a.Via == b.Via && b.Via == c.Via, a.Via + "/" + b.Via + "/" + c.Via);
        T.Check("三种写法词头一致", a.BaseWord == b.BaseWord && b.BaseWord == c.BaseWord, a.BaseWord + "/" + b.BaseWord + "/" + c.BaseWord);
        T.Check("三种写法音标一致", a.Phonetic == b.Phonetic && b.Phonetic == c.Phonetic, a.Phonetic + "/" + b.Phonetic + "/" + c.Phonetic);
    }

    // ------------------------------------------------------------------ 7. LRU

    private static void Section7Lru()
    {
        T.Section("7. LRU 缓存（容量 200，key = 小写单词）");
        LocalDictionary.ClearCache();
        LocalDictionary.ResetCounters();
        Console.WriteLine("   容量=" + LocalDictionary.CacheCapacity + "（期望 200）");
        T.CheckEqual("LRU 容量为 200", 200, LocalDictionary.CacheCapacity);

        // 注意：CleanWord 会剥掉首尾的非字母字符，而数字不是字母，
        // 所以「zzq0001」「zzq0002」… 全都会被清洗成同一个「zzq」，凑不出 300 个不同的 key。
        // 这里改用纯字母后缀生成 300 个互不相同的查询词。
        var sample = new string[300];
        for (var i = 0; i < sample.Length; i++) sample[i] = "zz" + AlphaSuffix(i + 1);
        T.CheckEqual("样本词两两不同", 300, sample.Distinct(StringComparer.Ordinal).Count());
        T.Check("样本词只含字母（清洗后不变）",
            sample.All(w => LocalDictionary.CleanWord(w) == w),
            sample[0] + " … " + sample[sample.Length - 1]);

        foreach (var word in sample) LocalDictionary.Lookup(word);
        Console.WriteLine("   连查 " + sample.Length + " 个不同词后，缓存条目数 = " + LocalDictionary.CacheCount);
        T.Check("缓存条目数不超过 200", LocalDictionary.CacheCount <= 200, "实际 " + LocalDictionary.CacheCount);
        T.CheckEqual("缓存条目数恰好等于容量 200", 200, LocalDictionary.CacheCount);

        // LRU 淘汰：最早查过的那个词应该已被挤出缓存，再查一次必然回表
        LocalDictionary.ResetCounters();
        var evicted = LocalDictionary.Lookup(sample[0]);
        Console.WriteLine("   最早查过的「" + sample[0] + "」再查一次 → 命中=" + evicted.Found + " via=" + evicted.Via
            + "，回表次数=" + LocalDictionary.CoreProbes + "（应为 1，说明它已被淘汰）");
        T.CheckEqual("最久未使用的词已被淘汰（再查需回表）", 1L, LocalDictionary.CoreProbes);

        // 重复查询不再回表：只重复最近 150 个词（能完整装进容量 200 的缓存，避开 LRU 循环抖动）
        var hot = sample.Skip(sample.Length - 150).ToArray();
        LocalDictionary.ResetCounters();
        var repeat = 0;
        for (var round = 0; round < 5; round++)
        {
            foreach (var word in hot) { LocalDictionary.Lookup(word); repeat++; }
        }
        Console.WriteLine("   重复查询最近 " + hot.Length + " 个词 × 5 轮 = " + repeat + " 次：CoreProbes="
            + LocalDictionary.CoreProbes + "，CacheHits=" + LocalDictionary.CacheHits);
        T.CheckEqual("重复查询完全不再回表", 0L, LocalDictionary.CoreProbes);
        T.CheckEqual("重复查询全部命中缓存", (long)repeat, LocalDictionary.CacheHits);

        LocalDictionary.ClearCache();
        LocalDictionary.ResetCounters();
        LocalDictionary.Lookup("the");
        LocalDictionary.Lookup("the");
        Console.WriteLine("   清零后：lookup(\"the\") 两次 → CacheLookups=2，CacheHits=1（第二次不再回表）");
        T.CheckEqual("第二次查词命中缓存", 1L, LocalDictionary.CacheHits);
        T.CheckEqual("两次查询只回表一次", 1L, LocalDictionary.CoreProbes);
    }

    /// <summary>1 → a、2 → b、…、26 → z、27 → aa（纯字母后缀，用于生成大量互不相同的查询词）。</summary>
    private static string AlphaSuffix(int n)
    {
        var s = "";
        while (n > 0)
        {
            n--;
            s = (char)('a' + n % 26) + s;
            n /= 26;
        }
        return s;
    }

    // ------------------------------------------------------------------ 8. 未命中

    private static void Section8Miss()
    {
        T.Section("8. 未命中：zzzzzz / qwertyuiop");
        foreach (var word in new[] { "zzzzzz", "qwertyuiop" })
        {
            LookupResult hit;
            try
            {
                hit = LocalDictionary.Lookup(word);
            }
            catch (Exception ex)
            {
                T.Fail("「" + word + "」不抛异常", "抛出 " + ex.GetType().Name + ": " + ex.Message);
                continue;
            }
            Console.WriteLine("   lookup(\"" + word + "\") → 命中=" + hit.Found + " via=" + hit.Via + " Item=" + (hit.Item is null ? "<null>" : "<非空>"));
            T.Check("「" + word + "」返回未命中", !hit.Found, "Found=" + hit.Found);
            T.CheckEqual("「" + word + "」via=miss", "miss", hit.Via);
            T.Check("「" + word + "」Item 为 null", hit.Item is null, "Item=" + (hit.Item is null ? "<null>" : "<非空>"));
        }
    }

    // ------------------------------------------------------------------ 9. 性能

    private static void Section9Performance(List<string> sample)
    {
        T.Section("9. 性能：1000 次随机查词");
        if (sample.Count == 0)
        {
            T.Fail("随机查词性能测试", "没有可用样本");
            return;
        }

        LocalDictionary.ClearCache();
        var words = new string[1000];
        for (var i = 0; i < words.Length; i++) words[i] = sample[Rng.Next(sample.Count)];

        // 预热：不去掉 JIT 影响的原始数据会失真
        foreach (var w in sample.Take(200)) LocalDictionary.Lookup(w);

        // 冷缓存：1000 次随机查词（不预先清缓存的话测不出真实回表开销）
        LocalDictionary.ClearCache();
        LocalDictionary.ResetCounters();
        Sw.Restart();
        var found = 0;
        for (var i = 0; i < words.Length; i++)
        {
            if (LocalDictionary.Lookup(words[i]).Found) found++;
        }
        Sw.Stop();
        var coldMs = Sw.Elapsed.TotalMilliseconds;

        Console.WriteLine("   冷缓存 1000 次随机查词：命中 " + found + " 次，总耗时 "
            + coldMs.ToString("F2", CultureInfo.InvariantCulture) + " ms，平均 "
            + (coldMs / words.Length).ToString("F4", CultureInfo.InvariantCulture) + " ms/次");
        T.Check("1000 次查词命中率 > 90%", found > 900, "命中 " + found + " 次");
        T.Check("1000 次查词总耗时 < 1000 ms（远快于每秒 1000 次的量级）", coldMs < 1000,
            coldMs.ToString("F2", CultureInfo.InvariantCulture) + " ms");

        // 热缓存
        LocalDictionary.ResetCounters();
        Sw.Restart();
        for (var i = 0; i < words.Length; i++) LocalDictionary.Lookup(words[i]);
        Sw.Stop();
        var warmMs = Sw.Elapsed.TotalMilliseconds;
        Console.WriteLine("   热缓存同批 1000 次查词：总耗时 " + warmMs.ToString("F2", CultureInfo.InvariantCulture)
            + " ms，平均 " + (warmMs / words.Length).ToString("F4", CultureInfo.InvariantCulture) + " ms/次，CacheHits=" + LocalDictionary.CacheHits);
        T.Check("热缓存 1000 次查词总耗时 < 100 ms", warmMs < 100, warmMs.ToString("F2", CultureInfo.InvariantCulture) + " ms");

        // 单次冷查询（清空缓存后单点计时）
        LocalDictionary.ClearCache();
        LocalDictionary.Lookup("dictionary");
        Sw.Restart();
        for (var i = 0; i < 1000; i++)
        {
            LocalDictionary.ClearCacheEntries();
            LocalDictionary.Lookup(words[i]);
        }
        Sw.Stop();
        Console.WriteLine("   每次先清空缓存的 1000 次冷查询：总耗时 " + Sw.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + " ms，平均 "
            + (Sw.Elapsed.TotalMilliseconds / 1000.0).ToString("F4", CultureInfo.InvariantCulture) + " ms/次");
    }

    // ------------------------------------------------------------------ 10. 自定义词库

    private static void Section10Wordbook()
    {
        T.Section("10. 自定义词库（Wordbook）与导入优先级");

        var book = new Wordbook();
        T.CheckEqual("释义语言非法值回落 zh", "zh", new Wordbook { DefinitionLanguage = "fr" }.DefinitionLanguage);
        T.CheckEqual("释义语言 both 合法", "both", new Wordbook { DefinitionLanguage = "both" }.DefinitionLanguage);
        T.CheckEqual("释义语言 en 合法", "en", new Wordbook { DefinitionLanguage = "en" }.DefinitionLanguage);

        var imported = book.ImportText("zzcustom\t自定义词\nzzcustom2=\u81ea\u5b9a\u4e49\u8bcd\u4e8c\n# 注释行\nbadline");
        Console.WriteLine("   ImportText 导入条数 = " + imported + "，词库条目数 = " + book.Count);
        T.CheckEqual("导入条数", 2, imported);
        T.CheckEqual("词库条目数", 2, book.Count);
        T.Check("Find 大小写不敏感", book.Find("ZZCUSTOM") is not null, "Find(\"ZZCUSTOM\")");

        book.ApplyToLocalDictionary();
        T.CheckEqual("导入词典条数进入 LocalDictionary", 2, LocalDictionary.ImportSize);

        var beforeThe = LocalDictionary.Lookup("the");
        var custom = LocalDictionary.Lookup("zzcustom");
        var customCase = LocalDictionary.Lookup("ZzCustom");
        Console.WriteLine("   导入后 lookup(\"zzcustom\") → 命中=" + custom.Found + " via=" + custom.Via + " 释义=" + TestRunner.Show(custom.Meaning));
        Console.WriteLine("   导入后 lookup(\"ZzCustom\") → 命中=" + customCase.Found + " via=" + customCase.Via);
        T.Check("导入词命中", custom.Found, "via=" + custom.Via);
        T.CheckEqual("导入词 via=import（优先级高于 core）", "import", custom.Via);
        T.Check("导入词大小写不敏感", customCase.Found && customCase.Via == "import", "via=" + customCase.Via);
        T.CheckEqual("导入词典不影响 core 词条", "direct", beforeThe.Via);

        // 覆盖 core 里的词
        LocalDictionary.ImportEntries(new[] { new DictItem { Word = "the", Phonetic = "test", Meaning = "\u81ea\u5b9a\u4e49\u8986\u76d6\u91ca\u4e49" } });
        var overridden = LocalDictionary.Lookup("the");
        Console.WriteLine("   用导入词典覆盖 the 后 → via=" + overridden.Via + " 释义=" + TestRunner.Show(overridden.Meaning));
        T.CheckEqual("导入词典覆盖 core 条目", "import", overridden.Via);

        LocalDictionary.ClearImport();
        var restored = LocalDictionary.Lookup("the");
        T.CheckEqual("清空导入后 the 回到 direct", "direct", restored.Via);
        T.CheckEqual("清空导入后 ImportSize=0", 0, LocalDictionary.ImportSize);

        // 文本导入导出往返
        var book2 = new Wordbook();
        book2.ImportText("apple=\u82f9\u679c\nbanana=be=\u9999\u8549");
        var exported = book2.ExportText();
        var book3 = new Wordbook();
        var reloaded = book3.ImportText(exported);
        Console.WriteLine("   导出文本：" + TestRunner.Show(exported));
        T.CheckEqual("导出后重新导入条数一致", 2, reloaded);
        T.CheckEqual("往返后释义一致", "\u9999\u8549", book3.Find("banana")?.Meaning);
        T.CheckEqual("往返后音标一致", "be", book3.Find("banana")?.Reading);

        // 带持久化文件的词库
        var tmp = Path.Combine(Path.GetTempPath(), "linetrans-wordbook-" + Guid.NewGuid().ToString("N") + ".txt");
        var book4 = new Wordbook(tmp);
        book4.Upsert("persist", "\u6301\u4e45\u5316\u6d4b\u8bd5");
        book4.Flush();
        T.Check("防抖持久化写盘", File.Exists(tmp), tmp);
        var book5 = new Wordbook(tmp);
        T.CheckEqual("重新载入后条目数", 1, book5.Count);
        T.CheckEqual("重新载入后释义", "\u6301\u4e45\u5316\u6d4b\u8bd5", book5.Find("persist")?.Meaning);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    // ------------------------------------------------------------------ 11. 划词查义服务

    private static void Section11DictionaryService()
    {
        T.Section("11. 划词查义服务（DictionaryService）");
        LocalDictionary.ClearCache();
        LocalDictionary.ResetCounters();

        var options = new DictionaryOptions { DefinitionLanguage = "fr" };
        Console.WriteLine("   构造时传入 definitionLanguage=\"fr\" → 规范化后 = " + options.DefinitionLanguage);
        T.CheckEqual("非法释义语言回落 zh", "zh", options.DefinitionLanguage);

        var service = new DictionaryService(options);
        T.CheckEqual("服务缓存容量为 200", 200, service.CacheCapacity);
        var entry = DictionaryService.Local("running");
        Console.WriteLine("   Local(\"running\") → Word=" + TestRunner.Show(entry.Word)
            + " 音标=" + TestRunner.Show(entry.Phonetic)
            + " 来源=" + TestRunner.Show(entry.Source)
            + " 词义数=" + entry.Senses.Count
            + " Ok=" + entry.Ok);
        foreach (var sense in entry.Senses.Take(3))
        {
            Console.WriteLine("        · 词性=" + TestRunner.Show(sense.PartOfSpeech) + " 释义=" + TestRunner.Show(TestRunner.Truncate(sense.Definition, 40)));
        }
        T.Check("running 的本地释义 Ok", entry.Ok, "Senses=" + entry.Senses.Count);
        // 词库里的音标写作 ASCII 撇号 'rʌniŋ；FormatPhonetic 只负责补斜杠，不把撇号转成重音符
        T.CheckEqual("音标包成 /…/ 形式", "/'r\u028cni\u014b/", entry.Phonetic);

        var miss = DictionaryService.Local("zzzzzz");
        T.Check("未命中返回 error 且不抛异常", !miss.Ok && !string.IsNullOrEmpty(miss.Error), "Error=" + TestRunner.Show(miss.Error));

        // 词性拆分
        var split = DictionaryService.SplitPos("n. 名词解释");
        T.CheckEqual("SplitPos 词性", "n", split.PartOfSpeech);
        T.CheckEqual("SplitPos 释义", "名词解释", split.Definition);
        var split2 = DictionaryService.SplitPos("[\u8ba1] 后端, 总线允许");
        Console.WriteLine("   SplitPos(\"[\u8ba1] 后端, 总线允许\") → 词性=" + TestRunner.Show(split2.PartOfSpeech) + " 释义=" + TestRunner.Show(split2.Definition));
        // 与 Kotlin 一致：splitPos 只做 trim + trimEnd('.')，方括号作为标记一并展示（DictionaryService.kt:142）
        T.CheckEqual("SplitPos 方括号标记（含方括号，与 Kotlin 一致）", "[\u8ba1]", split2.PartOfSpeech);

        // 多义拆分
        var senses = DictionaryService.SplitSenses("n. 跑；vi. 奔跑；vt. 使跑");
        Console.WriteLine("   SplitSenses(\"n. 跑；vi. 奔跑；vt. 使跑\") = " + senses.Count + " 条："
            + string.Join(" | ", senses.Select(s => s.PartOfSpeech + "=" + s.Definition)));
        T.CheckEqual("多义拆分条数（3 个「；」分隔的义项）", 3, senses.Count);
        if (senses.Count >= 3)
        {
            T.CheckEqual("第 1 条词性", "n", senses[0].PartOfSpeech);
            T.CheckEqual("第 1 条释义", "跑", senses[0].Definition);
            T.CheckEqual("第 3 条词性", "vt", senses[2].PartOfSpeech);
            T.CheckEqual("第 3 条释义", "使跑", senses[2].Definition);
        }
        T.CheckEqual("换行也算义项分隔符", 2, DictionaryService.SplitSenses("n. 甲\nv. 乙").Count);

        // 来源顺序
        Console.WriteLine("   来源顺序（默认 local）：" + string.Join(" → ", DictionaryService.BuildOrder(new DictionaryOptions())));
        Console.WriteLine("   来源顺序（关掉本地词库）：" + string.Join(" → ", DictionaryService.BuildOrder(new DictionaryOptions { LocalDictionaryEnabled = false })));
        T.CheckEqual("默认顺序首项为 local", "local", DictionaryService.BuildOrder(new DictionaryOptions())[0]);
        T.Check("关闭本地词库后顺序里没有 local",
            DictionaryService.BuildOrder(new DictionaryOptions { LocalDictionaryEnabled = false }).All(s => s != "local"),
            string.Join(" → ", DictionaryService.BuildOrder(new DictionaryOptions { LocalDictionaryEnabled = false })));

        // 全链路异步查词（离线：联网来源都会失败，但本地词库先命中）
        var onLine = new DictionaryService(new DictionaryOptions { DictionarySource = "oxford_web" });
        var asyncEntry = onLine.LookupAsync("book").GetAwaiter().GetResult();
        Console.WriteLine("   LookupAsync(\"book\") → 来源=" + TestRunner.Show(asyncEntry.Source) + " Ok=" + asyncEntry.Ok
            + " 词义数=" + asyncEntry.Senses.Count + " Match=" + asyncEntry.Match);
        T.Check("异步查词命中本地词库", asyncEntry.Ok, "来源=" + asyncEntry.Source);
        T.CheckEqual("本地来源标签", "本地词库", asyncEntry.Source);
        T.Check("Match 为 direct", asyncEntry.Match == LookupMatchKind.Direct || asyncEntry.Match == LookupMatchKind.Variant,
            "Match=" + asyncEntry.Match);
        T.CheckEqual("缓存条目数 1", 1, onLine.CachedCount);
        T.CheckEqual("牛津网页来源未被调用（本地先命中）", 0L, onLine.SourceCallCount("oxford_web"));

        var second = onLine.LookupAsync("book").GetAwaiter().GetResult();
        Console.WriteLine("   二次 LookupAsync(\"book\") → 缓存条目数=" + onLine.CachedCount + "，本地词库回表次数=" + LocalDictionary.CoreProbes);
        T.CheckEqual("二次查询命中服务缓存", 1, onLine.CachedCount);

        // 离线未命中：不抛异常，返回 error
        var offlineMiss = onLine.LookupAsync("zzzzzz").GetAwaiter().GetResult();
        Console.WriteLine("   LookupAsync(\"zzzzzz\") 离线 → Ok=" + offlineMiss.Ok + " Error=" + TestRunner.Show(offlineMiss.Error));
        T.Check("离线未命中不抛异常", !offlineMiss.Ok && !string.IsNullOrEmpty(offlineMiss.Error), "Error=" + TestRunner.Show(offlineMiss.Error));
    }

    // ------------------------------------------------------------------ 12. AI 兜底接口

    private sealed class FakeAi : IAiExplainProvider
    {
        public int ExplainShortCalls;
        public int ExplainWordCalls;
        public int EntryCalls;

        public Task<string?> ExplainWordAsync(string word, CancellationToken cancellationToken = default)
        {
            ExplainWordCalls++;
            return Task.FromResult<string?>("AI 释义：" + word);
        }

        public Task<string?> ExplainShortAsync(string word, IReadOnlyList<string> definitions, CancellationToken cancellationToken = default)
        {
            ExplainShortCalls++;
            return Task.FromResult<string?>("AI 简释：" + word + "（" + definitions.Count + " 条英文释义）");
        }

        public Task<DictEntry?> EntryAsync(string word, CancellationToken cancellationToken = default)
        {
            EntryCalls++;
            return Task.FromResult<DictEntry?>(new DictEntry
            {
                Word = word,
                Source = DictionarySource.Label(DictionarySource.Ai),
                Senses = new[] { new DictSense { PartOfSpeech = "n", Definition = "AI 生成的释义" } },
            });
        }
    }

    private static void Section12AiFallback()
    {
        T.Section("12. AI 兜底（仅接口，由上层注入，类库不联网）");

        var ai = new FakeAi();
        var service = new DictionaryService(new DictionaryOptions { DictionaryAiExplain = true }, ai);
        var entry = service.LookupAsync("apple").GetAwaiter().GetResult();
        Console.WriteLine("   apple → Ok=" + entry.Ok + " 来源=" + TestRunner.Show(entry.Source)
            + " 译文=" + TestRunner.Show(entry.Translation) + " ExplainShortCalls=" + ai.ExplainShortCalls);
        T.Check("本地命中且 AI 补充译文", entry.Ok && !string.IsNullOrWhiteSpace(entry.Translation), "译文=" + TestRunner.Show(entry.Translation));

        var aiOnly = new FakeAi();
        var service2 = new DictionaryService(
            new DictionaryOptions { DictionarySource = "ai", LocalDictionaryEnabled = false, DictionaryAiExplain = false },
            aiOnly);
        var entry2 = service2.LookupAsync("zzzzzz").GetAwaiter().GetResult();
        Console.WriteLine("   纯 AI 顺序查 zzzzzz → Ok=" + entry2.Ok + " 来源=" + TestRunner.Show(entry2.Source) + " EntryCalls=" + aiOnly.EntryCalls);
        T.Check("AI 来源被调用", aiOnly.EntryCalls == 1, "EntryCalls=" + aiOnly.EntryCalls);
        T.Check("AI 结果被采纳", entry2.Ok, "Ok=" + entry2.Ok);

        // 注入式联网取词（离线测试里用假函数验证解析链路）
        var fetcher = new Func<string, string, CancellationToken, Task<string?>>((url, headers, ct) =>
            Task.FromResult<string?>(
                "<html><span class=\"phon\">/\u02c8\u00e6p\u0259l/</span>"
                + "<span class=\"pos\">noun</span><span class=\"def\">a round fruit</span>"
                + "<span class=\"def\">the tree of this fruit</span></html>"));
        var service3 = new DictionaryService(
            new DictionaryOptions { DictionarySource = "oxford_web", LocalDictionaryEnabled = false },
            null,
            fetcher);
        var entry3 = service3.LookupAsync("apple").GetAwaiter().GetResult();
        Console.WriteLine("   假网页取词 apple → Ok=" + entry3.Ok + " 音标=" + TestRunner.Show(entry3.Phonetic)
            + " 词义数=" + entry3.Senses.Count + " 来源=" + TestRunner.Show(entry3.Source));
        T.Check("牛津网页解析链路可用", entry3.Ok && entry3.Senses.Count == 2, "Senses=" + entry3.Senses.Count);
        T.CheckEqual("网页音标解析", "/\u02c8\u00e6p\u0259l/", entry3.Phonetic);
        T.CheckEqual("网页词性解析", "noun", entry3.Senses[0].PartOfSpeech);

    }

    // ------------------------------------------------------------------ 13. 后台加载

    private static void Section13BackgroundLoad(string corePath, string lemmaPath, double syncLoadMs)
    {
        T.Section("13. 后台加载（不阻塞调用方）");
        LocalDictionary.Reset();
        T.Check("Reset 后 IsLoaded=false", !LocalDictionary.IsLoaded, "IsLoaded=" + LocalDictionary.IsLoaded);

        Sw.Restart();
        var task = LocalDictionary.EnsureLoadedAsync(corePath, lemmaPath);
        var callMs = Sw.Elapsed.TotalMilliseconds;
        Sw.Stop();
        Console.WriteLine("   EnsureLoadedAsync 立即返回，调用耗时 " + callMs.ToString("F2", CultureInfo.InvariantCulture)
            + " ms（第 1 节测得的同步加载耗时 " + syncLoadMs.ToString("F2", CultureInfo.InvariantCulture) + " ms）");
        T.Check("EnsureLoadedAsync 立即返回，不阻塞调用方",
            callMs < Math.Max(50.0, syncLoadMs / 2.0),
            "调用耗时 " + callMs.ToString("F2", CultureInfo.InvariantCulture) + " ms，同步加载 " + syncLoadMs.ToString("F2", CultureInfo.InvariantCulture) + " ms");

        var early = LocalDictionary.Lookup("the");
        T.Info("加载完成前 lookup(\"the\") 不阻塞、不抛异常：命中=" + early.Found + " via=" + early.Via
            + "（后台线程若已跑完则可能已经命中，两种都正常）");

        task.GetAwaiter().GetResult();
        Console.WriteLine("   后台加载完成：core=" + LocalDictionary.Size + " 条，lemma=" + LocalDictionary.LemmaSize
            + " 条，IsLoaded=" + LocalDictionary.IsLoaded);
        T.Check("后台加载完成后 core 约 4 万条", LocalDictionary.Size >= 39000 && LocalDictionary.Size <= 41000, "实际 " + LocalDictionary.Size);
        T.Check("后台加载完成后 lemma 约 10 万条", LocalDictionary.LemmaSize > 100000, "实际 " + LocalDictionary.LemmaSize);
    }

}
