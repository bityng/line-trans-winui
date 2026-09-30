using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LineTrans.App.Services;

/// <summary>剪贴板里原本是什么。</summary>
public enum ClipboardKind
{
    /// <summary>空。</summary>
    Empty,

    /// <summary>文本（最常见）。</summary>
    Text,

    /// <summary>文件 / 文件夹列表（资源管理器里复制文件）。</summary>
    Files,

    /// <summary>位图（截图工具、画图）。</summary>
    Bitmap,

    /// <summary>能识别但不是上面几种（HTML 片段、自定义格式……）。</summary>
    Unknown,
}

/// <summary>一份剪贴板快照。</summary>
public sealed class ClipboardSnapshot
{
    public ClipboardKind Kind { get; init; }

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<IStorageItem> Items { get; init; } = Array.Empty<IStorageItem>();

    public RandomAccessStreamReference? Bitmap { get; init; }

    public IReadOnlyList<string> Formats { get; init; } = Array.Empty<string>();

    /// <summary>给人看的一句话描述，写日志和自检报告用。</summary>
    public string Describe() => Kind switch
    {
        ClipboardKind.Empty => "空",
        ClipboardKind.Text => "文本（" + Text.Length + " 字：" + Preview(Text) + "）",
        ClipboardKind.Files => "文件列表（" + Items.Count + " 项）",
        ClipboardKind.Bitmap => "位图",
        _ => "其他格式（" + string.Join(", ", Formats.Take(4)) + "）",
    };

    private static string Preview(string text)
    {
        string flat = text.Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= 24 ? flat : flat.Substring(0, 24) + "…";
    }
}

/// <summary>
/// 剪贴板保护。
///
/// 全局划词的原理是「模拟 Ctrl+C 把选中文字搬进剪贴板」，这会覆盖用户原本的剪贴板内容，
/// 所以流程必须是：先快照 → 再 Ctrl+C → 读走文字 → 立刻把快照写回去。
///
/// 另外全程不清空剪贴板：判断「到底有没有复制到东西」用的是
/// <c>GetClipboardSequenceNumber</c>（剪贴板每次被改写都会自增），
/// 而不是「先清空再看有没有内容」这种会破坏原内容的做法。
///
/// 线程：WinRT 的 Clipboard API 必须在 UI 线程调用，本类的调用点全部在 UI 线程。
/// </summary>
public static class ClipboardGuard
{
    private const int RetryCount = 4;

    /// <summary>读取当前剪贴板内容并做成快照。任何异常都吞掉并返回「未知格式」，绝不抛出。</summary>
    public static async Task<ClipboardSnapshot> CaptureAsync()
    {
        try
        {
            DataPackageView view = await RetryAsync(() => Task.FromResult(Clipboard.GetContent()));
            string[] formats = view.AvailableFormats?.ToArray() ?? Array.Empty<string>();

            if (view.Contains(StandardDataFormats.Text))
            {
                string text = await RetryAsync(async () => await view.GetTextAsync()) ?? string.Empty;
                return new ClipboardSnapshot { Kind = ClipboardKind.Text, Text = text, Formats = formats };
            }

            if (view.Contains(StandardDataFormats.StorageItems))
            {
                var items = await RetryAsync(async () => await view.GetStorageItemsAsync())
                    ?? (IReadOnlyList<IStorageItem>)Array.Empty<IStorageItem>();
                return new ClipboardSnapshot { Kind = ClipboardKind.Files, Items = items, Formats = formats };
            }

            if (view.Contains(StandardDataFormats.Bitmap))
            {
                RandomAccessStreamReference? bitmap = await RetryAsync(async () => await view.GetBitmapAsync());
                return new ClipboardSnapshot { Kind = ClipboardKind.Bitmap, Bitmap = bitmap, Formats = formats };
            }

            return formats.Length == 0
                ? new ClipboardSnapshot { Kind = ClipboardKind.Empty, Formats = formats }
                : new ClipboardSnapshot { Kind = ClipboardKind.Unknown, Formats = formats };
        }
        catch (Exception ex)
        {
            AppServices.Log("剪贴板快照失败：" + ex.Message);
            return new ClipboardSnapshot { Kind = ClipboardKind.Unknown };
        }
    }

    /// <summary>把快照写回剪贴板。返回是否成功；失败会记日志，不抛异常。</summary>
    public static async Task<bool> RestoreAsync(ClipboardSnapshot? snapshot)
    {
        if (snapshot == null) return false;

        try
        {
            switch (snapshot.Kind)
            {
                case ClipboardKind.Empty:
                    await RetryAsync(() =>
                    {
                        Clipboard.Clear();
                        return Task.FromResult(true);
                    });
                    return true;

                case ClipboardKind.Text:
                    return await SetAsync(BuildText(snapshot.Text), "文本");

                case ClipboardKind.Files:
                    return await SetAsync(BuildFiles(snapshot.Items), "文件列表");

                case ClipboardKind.Bitmap:
                    if (snapshot.Bitmap == null) return false;
                    return await SetAsync(BuildBitmap(snapshot.Bitmap), "位图");

                default:
                    AppServices.Log("剪贴板保护：原内容是「" + snapshot.Describe() + "」，无法完整复刻，已跳过还原");
                    return false;
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("剪贴板还原失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>只读取剪贴板里的文本（不改动剪贴板）。没有文本时返回空串。</summary>
    public static async Task<string> ReadTextAsync()
    {
        try
        {
            DataPackageView view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return string.Empty;
            return (await RetryAsync(async () => await view.GetTextAsync())) ?? string.Empty;
        }
        catch (Exception ex)
        {
            AppServices.Log("读取剪贴板文本失败：" + ex.Message);
            return string.Empty;
        }
    }

    /// <summary>写一段文本进剪贴板（自检用）。</summary>
    public static async Task<bool> SetTextAsync(string text)
        => await SetAsync(BuildText(text), "文本");

    private static DataPackage BuildText(string text)
    {
        var package = new DataPackage();
        package.SetText(text ?? string.Empty);
        return package;
    }

    private static DataPackage BuildFiles(IReadOnlyList<IStorageItem> items)
    {
        var package = new DataPackage();
        package.SetStorageItems(items);
        return package;
    }

    private static DataPackage BuildBitmap(RandomAccessStreamReference bitmap)
    {
        var package = new DataPackage();
        package.SetBitmap(bitmap);
        return package;
    }

    private static async Task<bool> SetAsync(DataPackage package, string label)
    {
        try
        {
            await RetryAsync(() =>
            {
                Clipboard.SetContent(package);
                // OLE 剪贴板是延迟渲染的：写完不 Flush，紧接着 GetContent 可能读到旧内容甚至空内容。
                // Flush 会把数据立刻提交出去，同时也让程序退出后剪贴板内容仍然可用。
                Clipboard.Flush();
                return Task.FromResult(true);
            });
            return true;
        }
        catch (Exception ex)
        {
            AppServices.Log("写回剪贴板（" + label + "）失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 剪贴板是跨进程的独占资源：别的程序正开着剪贴板时 CLIPBRD_E_CANT_OPEN（0x800401D0）很常见，
    /// 退一步重试几次基本上就过去了。
    /// </summary>
    private static async Task<T> RetryAsync<T>(Func<Task<T>> action)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < RetryCount; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < RetryCount - 1) await Task.Delay(40).ConfigureAwait(true);
            }
        }
        throw last ?? new InvalidOperationException("剪贴板操作失败");
    }
}
