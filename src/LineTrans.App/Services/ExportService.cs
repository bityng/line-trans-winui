using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using LineTrans.Core;

using Microsoft.UI.Xaml.Controls;

using Windows.Storage;
using Windows.Storage.Pickers;

namespace LineTrans.App.Services;

/// <summary>
/// 导出结果。用户在选择对话框里按「取消」时不会产生这个对象（返回 null）。
/// </summary>
public sealed class ExportOutcome
{
    /// <summary>最终落盘路径。</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>导出格式的中文名（用于提示文案）。</summary>
    public string FormatLabel { get; init; } = string.Empty;

    /// <summary>文件字节数。</summary>
    public long Bytes { get; init; }

    /// <summary>导出的条目数。</summary>
    public int Units { get; init; }

    /// <summary>是否走了「导出到数据目录」的兜底路径。</summary>
    public bool UsedFallback { get; init; }

    /// <summary>成功但有话要说（例如所选位置写不进去，已改存数据目录）。</summary>
    public string? Warning { get; init; }

    /// <summary>失败原因，成功时为 null。</summary>
    public string? Error { get; init; }

    /// <summary>是否成功。</summary>
    public bool Ok => Error == null;
}

/// <summary>
/// 导出落盘：把 <see cref="ExportManager"/> 生成的文本写到磁盘。
///
/// 两条路径：
///   1) 正常路径：<see cref="FileSavePicker"/> 让用户选位置。unpackaged 应用必须先通过
///      <c>WinRT.Interop.InitializeWithWindow.Initialize</c> 把窗口句柄交给选择器，
///      否则调用会直接抛异常（这是 WinUI 3 桌面端的固定要求，和安卓端的 SAF 同理）；
///   2) 兜底路径：选择器不可用，或用户主动选了「导出到数据目录」时，直接写到
///      <c>%APPDATA%\LineTrans\exports\</c>，再在资源管理器里定位 —— 结果绝不丢失。
///
/// 编码统一 UTF-8；直写路径带 BOM，这样 Excel 打开导出的 CSV 不会把中文认成乱码。
/// </summary>
public static class ExportService
{
    /// <summary>菜单里的格式顺序。</summary>
    public static readonly ExportFormat[] Formats =
    {
        ExportFormat.TXT_BILINGUAL,
        ExportFormat.TXT_TRANSLATED_ONLY,
        ExportFormat.TXT_SOURCE_FALLBACK,
        ExportFormat.MARKDOWN_TABLE,
        ExportFormat.CSV,
        ExportFormat.JSON,
    };

    /// <summary>不弹保存对话框时的落盘目录。</summary>
    public static string ExportDirectory => Path.Combine(AppServices.DataRoot, "exports");

    private static readonly UTF8Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    // ------------------------------------------------------------------
    // 菜单
    // ------------------------------------------------------------------

    /// <summary>
    /// 造一个「导出」菜单：六种格式各一项，末尾追加「导出到数据目录」。
    /// 每次点击都重新造，这样菜单始终对着最新的文档内容。
    /// </summary>
    /// <param name="doc">要导出的文档。</param>
    /// <param name="report">拿到结果后的回调；用户取消时传 null。</param>
    public static MenuFlyout BuildMenu(TranslationDoc doc, Func<ExportOutcome?, Task> report)
    {
        var flyout = new MenuFlyout();

        foreach (var format in Formats)
        {
            var captured = format;
            var item = new MenuFlyoutItem
            {
                Text = ExportFormatInfo.Label(format),
                Tag = "export:" + format,
            };
            item.Click += async (s, e) => await report(await ExportAsync(doc, captured));
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var quick = new MenuFlyoutItem
        {
            Text = "导出到数据目录（不弹保存对话框）",
            Tag = "export:quick",
        };
        quick.Click += async (s, e) =>
            await report(await ExportAsync(doc, AppServices.Settings.DefaultExportFormat, useSavePicker: false));
        flyout.Items.Add(quick);

        return flyout;
    }

    // ------------------------------------------------------------------
    // 导出
    // ------------------------------------------------------------------

    /// <summary>
    /// 导出一次。不抛异常：失败会放进 <see cref="ExportOutcome.Error"/>。
    /// </summary>
    /// <returns>用户取消时返回 null。</returns>
    public static async Task<ExportOutcome?> ExportAsync(
        TranslationDoc doc,
        ExportFormat format,
        bool useSavePicker = true)
    {
        if (doc == null) return Failure(format, "没有可导出的文档。");

        string label = ExportFormatInfo.Label(format);
        string text;
        try
        {
            text = ExportManager.BuildText(doc, format);
        }
        catch (Exception ex)
        {
            return Failure(format, "生成导出内容失败：" + ex.Message);
        }

        string fileName = ExportManager.BuildFileName(doc, format);

        if (!useSavePicker)
        {
            return Fallback(doc, format, text, fileName, label, null);
        }

        var pick = await PickAsync(format, fileName);
        if (pick.Canceled) return null;

        if (pick.File != null)
        {
            try
            {
                await WriteAsync(pick.File, text);
                AppServices.Log("导出：" + pick.File.Path + "（" + label + "）");
                return Describe(pick.File.Path, text, doc, label, false, null);
            }
            catch (Exception ex)
            {
                // 位置是选好了，但写不进去（权限 / 被占用 / 只读盘）：退到数据目录，别把结果弄丢。
                return Fallback(doc, format, text, fileName, label,
                    "所选位置写不进去（" + ex.Message + "），已改存到数据目录。");
            }
        }

        return Fallback(doc, format, text, fileName, label, pick.Error);
    }

    /// <summary>弹保存对话框。三条出路：选好文件 / 用户取消 / 不可用（返回原因）。</summary>
    private static async Task<PickResult> PickAsync(ExportFormat format, string suggestedName)
    {
        try
        {
            string extension = "." + ExportFormatInfo.Extension(format);
            string stem = Path.GetFileNameWithoutExtension(suggestedName);
            if (stem.Length == 0) stem = "导出";

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = stem,
                DefaultFileExtension = extension,
            };
            picker.FileTypeChoices.Add(ExportFormatInfo.Label(format), new List<string> { extension });

            var window = MainWindow.Instance;
            if (window == null)
            {
                return new PickResult { Error = "主窗口尚未就绪，保存对话框打不开。" };
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, window.Handle);

            var file = await picker.PickSaveFileAsync();
            if (file == null) return new PickResult { Canceled = true };

            return new PickResult { File = file };
        }
        catch (Exception ex)
        {
            return new PickResult { Error = "保存对话框不可用：" + ex.Message };
        }
    }

    /// <summary>写文件：优先按真实路径直写（能带 UTF-8 BOM），失败再退回 WinRT 文件 API。</summary>
    private static async Task WriteAsync(StorageFile file, string text)
    {
        string path = file.Path ?? string.Empty;
        if (path.Length > 0)
        {
            try
            {
                await File.WriteAllTextAsync(path, text, Utf8WithBom);
                return;
            }
            catch
            {
                // 落到下面的 WinRT 写盘
            }
        }

        await FileIO.WriteTextAsync(file, text, Windows.Storage.Streams.UnicodeEncoding.Utf8);
    }

    /// <summary>兜底：写到数据目录，重名自动加序号，绝不覆盖已有导出。</summary>
    private static ExportOutcome? Fallback(
        TranslationDoc doc,
        ExportFormat format,
        string text,
        string fileName,
        string label,
        string? warning)
    {
        try
        {
            Directory.CreateDirectory(ExportDirectory);
            string path = UniquePath(Path.Combine(ExportDirectory, fileName));
            File.WriteAllText(path, text, Utf8WithBom);
            AppServices.Log("导出（数据目录）：" + path + "（" + label + "）");
            return Describe(path, text, doc, label, true, warning);
        }
        catch (Exception ex)
        {
            return Failure(format, "导出失败：" + ex.Message);
        }
    }

    private static ExportOutcome Describe(
        string path,
        string text,
        TranslationDoc doc,
        string label,
        bool usedFallback,
        string? warning)
    {
        long bytes = Utf8WithBom.GetByteCount(text);
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) bytes = info.Length;
        }
        catch
        {
            // 拿不到 FileInfo 就用算出来的字节数
        }

        return new ExportOutcome
        {
            FilePath = path,
            FormatLabel = label,
            Bytes = bytes,
            Units = doc.Units.Count,
            UsedFallback = usedFallback,
            Warning = warning,
        };
    }

    private static ExportOutcome Failure(ExportFormat format, string message) => new ExportOutcome
    {
        FormatLabel = ExportFormatInfo.Label(format),
        Error = message,
    };

    /// <summary>重名时依次尝试 <c>名字 (2).txt</c>、<c>名字 (3).txt</c>…</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, stem + " (" + i + ")" + extension);
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(dir, stem + " (" + Guid.NewGuid().ToString("N") + ")" + extension);
    }

    /// <summary>保存对话框的三态结果。</summary>
    private sealed class PickResult
    {
        public StorageFile? File { get; init; }

        public bool Canceled { get; init; }

        public string? Error { get; init; }
    }
}
