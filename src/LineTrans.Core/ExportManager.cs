using System.Text;

namespace LineTrans.Core;

/// <summary>
/// 导出：6 种格式的纯文本生成 + 文件名生成。
///
/// 移植自安卓端 <c>data/ExportManager.kt</c>；其中 <c>export(context, ...)</c> 依赖 SAF 与
/// SettingsRepository，属于平台相关部分，这里只保留纯逻辑（buildText / buildFileName / shareText）。
/// </summary>
public static class ExportManager
{
    /// <summary>按格式生成导出文本。</summary>
    public static string BuildText(TranslationDoc doc, ExportFormat format)
    {
        switch (format)
        {
            case ExportFormat.TXT_TRANSLATED_ONLY:
                return string.Join("\n", doc.Units
                    .Select(u => u.Translation)
                    .Where(TranslationUnit.IsNotBlank));

            case ExportFormat.TXT_BILINGUAL:
                return string.Join("\n", doc.Units.Select(u =>
                    TranslationUnit.IsBlank(u.Translation) ? u.Source : u.Source + "\t" + u.Translation));

            case ExportFormat.TXT_SOURCE_FALLBACK:
                return string.Join("\n", doc.Units.Select(u => u.IsTranslated ? u.Translation : u.Source));

            case ExportFormat.MARKDOWN_TABLE:
                {
                    var sb = new StringBuilder();
                    sb.Append("| # | 原文 | 译文 |\n");
                    sb.Append("| --- | --- | --- |\n");
                    for (int i = 0; i < doc.Units.Count; i++)
                    {
                        var unit = doc.Units[i];
                        sb.Append("| ");
                        sb.Append(i + 1);
                        sb.Append(" | ");
                        sb.Append(MdEscape(unit.Source));
                        sb.Append(" | ");
                        sb.Append(MdEscape(unit.Translation));
                        sb.Append(" |\n");
                    }
                    return sb.ToString();
                }

            case ExportFormat.CSV:
                {
                    var sb = new StringBuilder();
                    sb.Append("index,source,translation\n");
                    for (int i = 0; i < doc.Units.Count; i++)
                    {
                        var unit = doc.Units[i];
                        sb.Append(i + 1);
                        sb.Append(',');
                        sb.Append(TextParser.CsvEscape(unit.Source));
                        sb.Append(',');
                        sb.Append(TextParser.CsvEscape(unit.Translation));
                        sb.Append('\n');
                    }
                    return sb.ToString();
                }

            case ExportFormat.JSON:
                {
                    var sb = new StringBuilder();
                    sb.Append("{\n");
                    sb.Append("  \"name\": \"").Append(JsonEscape(doc.Name)).Append("\",\n");
                    sb.Append("  \"mode\": \"").Append(doc.UnitMode.ToString()).Append("\",\n");
                    sb.Append("  \"total\": ").Append(doc.TotalCount).Append(",\n");
                    sb.Append("  \"translated\": ").Append(doc.TranslatedCount).Append(",\n");
                    sb.Append("  \"units\": [\n");
                    for (int i = 0; i < doc.Units.Count; i++)
                    {
                        var unit = doc.Units[i];
                        sb.Append("    { \"index\": ").Append(i + 1);
                        sb.Append(", \"source\": \"").Append(JsonEscape(unit.Source)).Append("\"");
                        sb.Append(", \"translation\": \"").Append(JsonEscape(unit.Translation)).Append("\" }");
                        if (i != doc.Units.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append("  ]\n}");
                    return sb.ToString();
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "未知的导出格式");
        }
    }

    /// <summary>生成文件名（不含目录）。</summary>
    public static string BuildFileName(TranslationDoc doc, ExportFormat format)
    {
        string baseName = doc.Name.EndsWith(".txt", StringComparison.Ordinal)
            ? doc.Name.Substring(0, doc.Name.Length - 4)
            : doc.Name;
        if (baseName.Length == 0) baseName = "未命名文档";

        string suffix = format switch
        {
            ExportFormat.TXT_TRANSLATED_ONLY => "译文",
            ExportFormat.TXT_BILINGUAL => "对照",
            ExportFormat.TXT_SOURCE_FALLBACK => "替换原文",
            ExportFormat.MARKDOWN_TABLE => "对照表",
            ExportFormat.CSV => "对照表",
            ExportFormat.JSON => "数据",
            _ => "导出",
        };
        return baseName + "_" + suffix + "." + ExportFormatInfo.Extension(format);
    }

    /// <summary>分享用的纯文本（优先对照，未翻译的显示原文）。</summary>
    public static string ShareText(TranslationDoc doc) => BuildText(doc, ExportFormat.TXT_BILINGUAL);

    private static string MdEscape(string text) => text.Replace("|", "\\|").Replace("\n", " ");

    private static string JsonEscape(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20) sb.Append(' ');
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
