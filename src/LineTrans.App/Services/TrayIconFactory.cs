using System;
using System.Runtime.InteropServices;

using LineTrans.App.Interop;

namespace LineTrans.App.Services;

/// <summary>
/// 自绘托盘图标（HICON）。
///
/// 仓库里没有任何 .ico（<c>Assets</c> 下只有词库与一个 .gitkeep），也不引入图像库，
/// 所以这里用 GDI 直接画：一块品牌色圆角方块 + 白色「译」字，尺寸取系统的托盘图标尺寸
/// （<c>SM_CXSMICON</c>，随 DPI 变化，150% 缩放下是 24px）。
///
/// 32 位 DIB 的 alpha 通道 GDI 不会帮我们维护，所以画完之后按「非黑即不透明」补一遍 alpha，
/// 这样圆角外的像素是彻底透明的，任务栏上不会出现一块黑底。
/// </summary>
internal static class TrayIconFactory
{
    /// <summary>品牌色 #4D6BFE 的 COLORREF（0x00BBGGRR）。</summary>
    private const uint BrandColorRef = 0x00FE6B4D;

    private const uint WhiteColorRef = 0x00FFFFFF;

    /// <summary>画一个图标；任何一步失败都回落到系统默认应用程序图标，保证托盘一定有东西显示。</summary>
    public static IntPtr Create()
    {
        int size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        if (size <= 0) size = 16;
        if (size > 64) size = 64;

        try
        {
            IntPtr icon = Draw(size);
            if (icon != IntPtr.Zero) return icon;
        }
        catch (Exception ex)
        {
            AppServices.Log("自绘托盘图标失败，回落系统默认图标：" + ex.Message);
        }

        return NativeMethods.LoadIconW(IntPtr.Zero, new IntPtr(NativeMethods.IDI_APPLICATION));
    }

    private static IntPtr Draw(int size)
    {
        var header = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // 负数 = 自上而下的 DIB，行序与直觉一致
            biPlanes = 1,
            biBitCount = 32,
            biCompression = NativeMethods.BI_RGB,
        };
        var info = new NativeMethods.BITMAPINFO { bmiHeader = header };

        IntPtr colorBitmap = NativeMethods.CreateDIBSection(
            IntPtr.Zero, ref info, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (colorBitmap == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

        IntPtr maskBitmap = NativeMethods.CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        IntPtr dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (maskBitmap == IntPtr.Zero || dc == IntPtr.Zero)
        {
            if (dc != IntPtr.Zero) NativeMethods.DeleteDC(dc);
            NativeMethods.DeleteObject(colorBitmap);
            if (maskBitmap != IntPtr.Zero) NativeMethods.DeleteObject(maskBitmap);
            return IntPtr.Zero;
        }

        IntPtr oldBitmap = NativeMethods.SelectObject(dc, colorBitmap);
        IntPtr brush = IntPtr.Zero;
        IntPtr pen = IntPtr.Zero;
        IntPtr font = IntPtr.Zero;
        IntPtr oldBrush = IntPtr.Zero;
        IntPtr oldPen = IntPtr.Zero;
        IntPtr oldFont = IntPtr.Zero;

        try
        {
            brush = NativeMethods.CreateSolidBrush(BrandColorRef);
            pen = NativeMethods.GetStockObject(NativeMethods.NULL_PEN);
            oldBrush = NativeMethods.SelectObject(dc, brush);
            oldPen = NativeMethods.SelectObject(dc, pen);

            int radius = Math.Max(2, size / 4);
            NativeMethods.RoundRect(dc, 0, 0, size + 1, size + 1, radius, radius);

            NativeMethods.SetBkMode(dc, NativeMethods.TRANSPARENT);
            NativeMethods.SetTextColor(dc, WhiteColorRef);

            int fontHeight = -(int)Math.Round(size * 0.72);
            font = NativeMethods.CreateFontW(
                fontHeight, 0, 0, 0, NativeMethods.FW_BOLD,
                0, 0, 0,
                NativeMethods.DEFAULT_CHARSET,
                NativeMethods.OUT_TT_PRECIS,
                NativeMethods.CLIP_DEFAULT_PRECIS,
                NativeMethods.CLEARTYPE_QUALITY,
                NativeMethods.DEFAULT_PITCH,
                "Microsoft YaHei");
            if (font == IntPtr.Zero)
            {
                font = NativeMethods.CreateFontW(
                    fontHeight, 0, 0, 0, NativeMethods.FW_BOLD,
                    0, 0, 0, NativeMethods.DEFAULT_CHARSET,
                    NativeMethods.OUT_TT_PRECIS, NativeMethods.CLIP_DEFAULT_PRECIS,
                    NativeMethods.CLEARTYPE_QUALITY, NativeMethods.DEFAULT_PITCH,
                    "SimSun");
            }

            if (font != IntPtr.Zero)
            {
                oldFont = NativeMethods.SelectObject(dc, font);
                var rect = new NativeMethods.RECT(0, 0, size, size);
                NativeMethods.DrawTextW(dc, "\u8BD1", -1, ref rect,
                    NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
            }
        }
        finally
        {
            if (oldFont != IntPtr.Zero) NativeMethods.SelectObject(dc, oldFont);
            if (oldPen != IntPtr.Zero) NativeMethods.SelectObject(dc, oldPen);
            if (oldBrush != IntPtr.Zero) NativeMethods.SelectObject(dc, oldBrush);
            if (font != IntPtr.Zero) NativeMethods.DeleteObject(font);
            if (brush != IntPtr.Zero) NativeMethods.DeleteObject(brush);
            NativeMethods.SelectObject(dc, oldBitmap);
            NativeMethods.DeleteDC(dc);
        }

        // GDI 不管 alpha：DIB 初始全 0，画过的地方才非黑。按这个规则补 alpha 即可。
        ApplyAlpha(bits, size * size);

        var iconInfo = new NativeMethods.ICONINFO
        {
            fIcon = true,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = maskBitmap,
            hbmColor = colorBitmap,
        };

        IntPtr icon = NativeMethods.CreateIconIndirect(ref iconInfo);
        NativeMethods.DeleteObject(colorBitmap);
        NativeMethods.DeleteObject(maskBitmap);
        return icon;
    }

    /// <summary>
    /// 逐像素把「是否为背景」变成 alpha：非黑 = 不透明，全黑 = 全透明。
    /// 走 Marshal.Copy 而不是 unsafe 指针，免得为一个图标给整个工程打开 AllowUnsafeBlocks。
    /// </summary>
    private static void ApplyAlpha(IntPtr bits, int pixelCount)
    {
        var pixels = new int[pixelCount];
        Marshal.Copy(bits, pixels, 0, pixelCount);
        for (int i = 0; i < pixelCount; i++)
        {
            int rgb = pixels[i] & 0x00FFFFFF;
            pixels[i] = rgb == 0 ? 0 : unchecked((int)0xFF000000) | rgb;
        }
        Marshal.Copy(pixels, 0, bits, pixelCount);
    }
}
