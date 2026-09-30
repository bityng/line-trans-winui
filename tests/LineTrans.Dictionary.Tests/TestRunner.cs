using System.Globalization;
using System.Text;
using LineTrans.Core.Dictionary;

namespace LineTrans.Dictionary.Tests;

/// <summary>极简断言器：逐条打印 PASS/FAIL，统计失败数用于设置退出码。</summary>
internal sealed class TestRunner
{
    private readonly List<string> _failures = new();
    private int _passed;
    private int _failed;
    private string _currentSection = "";

    public int Passed => _passed;

    public int Failed => _failed;

    public IReadOnlyList<string> Failures => _failures;

    public void Section(string title)
    {
        _currentSection = title;
        Console.WriteLine();
        Console.WriteLine("===================================================================");
        Console.WriteLine("== " + title);
        Console.WriteLine("===================================================================");
    }

    public void Info(string message) => Console.WriteLine("   [信息] " + message);

    public void Pass(string name, string detail = "")
        => Report(true, name, detail);

    public void Fail(string name, string detail)
        => Report(false, name, detail);

    private void Report(bool ok, string name, string detail)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("   [PASS] " + name + (detail.Length > 0 ? "  | " + detail : ""));
        }
        else
        {
            _failed++;
            var line = _currentSection + " :: " + name + "  | " + detail;
            _failures.Add(line);
            Console.WriteLine("   [FAIL] " + name + "  | " + detail);
        }
    }

    public void Check(string name, bool condition, string detail)
        => Report(condition, name, detail);

    public void CheckEqual(string name, object? expected, object? actual)
        => Report(
            Equals(expected, actual),
            name,
            "期望=" + Show(expected) + " 实际=" + Show(actual));

    public void CheckContains(string name, string haystack, string needle)
        => Report(
            haystack.Contains(needle, StringComparison.Ordinal),
            name,
            "应在「" + Show(haystack) + "」中包含「" + needle + "」");

    public static string Show(object? value) => value switch
    {
        null => "<null>",
        string s => "\"" + Truncate(s.Replace("\n", "\\n"), 70) + "\"",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null>",
    };

    public static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";

    public int Summary(string title)
    {
        Console.WriteLine();
        Console.WriteLine("===================================================================");
        Console.WriteLine("== " + title + " 汇总");
        Console.WriteLine("===================================================================");
        Console.WriteLine("PASS = " + _passed + "    FAIL = " + _failed);
        if (_failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败明细：");
            foreach (var f in _failures) Console.WriteLine("  - " + f);
        }
        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "全部通过" : "存在失败项");
        return _failed == 0 ? 0 : 1;
    }
}
