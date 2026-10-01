<#
  make-icon.ps1 —— 生成 LineTrans.App 的应用图标（多尺寸 .ico）

  产物：src\LineTrans.App\Assets\LineTrans.ico
  用途：csproj 的 <ApplicationIcon>（exe 图标）、MainWindow 的 AppWindow.SetIcon（任务栏 / Alt+Tab）、
        TrayIconFactory（托盘，失败时回落到 GDI 自绘）、tools\installer.iss 的 SetupIconFile（安装包与卸载项）。

  设计：与托盘图标同一语言 —— 品牌蓝 #4D6BFE 的圆角方块 + 白色「译」。
        大尺寸上给一点点自上而下的渐变（#6B84FF -> #3A53E8，中位即品牌色），小尺寸上看不出差别。

  为什么自己写 ICO 容器：仓库不引入图像库、不联网下载工具，
  .NET 也没有现成的多尺寸 ICO 写出 API。ICO 格式很简单：
    ICONDIR(6B) + ICONDIRENTRY*N(16B) + 各图像数据；
    单张图像可以是 PNG（Vista+）或 32bpp DIB。
  这里 <= 64 一律写 DIB（兼容性最好：Inno Setup 编译 SetupIconFile、老工具都能读），
  128 / 256 写 PNG（体积小，Explorer 原生支持）。

  只依赖 System.Drawing（Windows PowerShell / PowerShell 7 在 Windows 上都自带）。

  用法：
    pwsh -File tests\IconTools\make-icon.ps1
    pwsh -File tests\IconTools\make-icon.ps1 -PreviewDir <目录>   # 额外导出预览 PNG，便于肉眼验收
#>
[CmdletBinding()]
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\..\src\LineTrans.App\Assets\LineTrans.ico'),
    [string]$PreviewDir = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$BrandTop    = [System.Drawing.Color]::FromArgb(255, 0x6B, 0x84, 0xFF)
$BrandBottom = [System.Drawing.Color]::FromArgb(255, 0x3A, 0x53, 0xE8)
$Sizes       = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        # ---- 圆角方块（留 2% 内边距，免得抗锯齿边缘被 ICO 边界裁掉）----
        $inset  = [double]$Size * 0.02
        $radius = [double]$Size * 0.22
        $d      = $radius * 2
        $x0 = $inset
        $y0 = $inset
        $x1 = [double]$Size - $inset
        $y1 = [double]$Size - $inset

        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc($x0, $y0, $d, $d, 180, 90)
        $path.AddArc($x1 - $d, $y0, $d, $d, 270, 90)
        $path.AddArc($x1 - $d, $y1 - $d, $d, $d, 0, 90)
        $path.AddArc($x0, $y1 - $d, $d, $d, 90, 90)
        $path.CloseFigure()

        $rect  = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $BrandTop, $BrandBottom, 90.0)
        $g.FillPath($brush, $path)
        $brush.Dispose()
        $path.Dispose()

        # ---- 白色「译」居中 ----
        $emSize = [double]$Size * 0.64
        $font = New-Object System.Drawing.Font('Microsoft YaHei', $emSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment     = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $fmt.FormatFlags   = [System.Drawing.StringFormatFlags]::NoClip
        $layout = New-Object System.Drawing.RectangleF(0.0, [float]([double]$Size * 0.015), [float]$Size, [float]$Size)
        $g.DrawString([string][char]0x8BD1, $font, [System.Drawing.Brushes]::White, $layout, $fmt)
        $font.Dispose()
        $fmt.Dispose()
    }
    finally {
        $g.Dispose()
    }

    return $bmp
}

# 32bpp DIB（BITMAPINFOHEADER + 自下而上的 BGRA + 全零 AND 掩码）—— ICO 里的经典位图条目
function Get-IconDibBytes {
    param([System.Drawing.Bitmap]$Bmp)

    $size = $Bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $Bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $raw = New-Object byte[] ($stride * $size)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    }
    finally {
        $Bmp.UnlockBits($data)
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40)                       # biSize
    $bw.Write([int32]$size)                     # biWidth
    $bw.Write([int32]($size * 2))               # biHeight = 2 倍（XOR 图 + AND 掩码）
    $bw.Write([uint16]1)                        # biPlanes
    $bw.Write([uint16]32)                       # biBitCount
    $bw.Write([uint32]0)                        # biCompression = BI_RGB
    $bw.Write([uint32]($size * $size * 4))      # biSizeImage
    $bw.Write([int32]0)                         # biXPelsPerMeter
    $bw.Write([int32]0)                         # biYPelsPerMeter
    $bw.Write([uint32]0)                        # biClrUsed
    $bw.Write([uint32]0)                        # biClrImportant

    for ($y = $size - 1; $y -ge 0; $y--) {
        $bw.Write($raw, $y * $stride, $size * 4)
    }

    $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
    $mask = New-Object byte[] ($maskStride * $size)
    $bw.Write($mask, 0, $mask.Length)

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()
    return , $bytes
}

function Get-IconPngBytes {
    param([System.Drawing.Bitmap]$Bmp)
    $ms = New-Object System.IO.MemoryStream
    $Bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

# ---------------------------------------------------------------- 生成
$payloads = @()
foreach ($size in $Sizes) {
    $bmp = New-IconBitmap -Size $size
    try {
        if ($size -ge 128) {
            $bytes = Get-IconPngBytes -Bmp $bmp
            $kind = 'PNG'
        }
        else {
            $bytes = Get-IconDibBytes -Bmp $bmp
            $kind = 'DIB'
        }
    }
    finally {
        if ($PreviewDir) { }
    }

    $payloads += [pscustomobject]@{ Size = $size; Kind = $kind; Bytes = $bytes }

    if ($PreviewDir) {
        if (-not (Test-Path $PreviewDir)) { New-Item -ItemType Directory -Path $PreviewDir -Force | Out-Null }
        $bmp.Save((Join-Path $PreviewDir ("preview-{0}.png" -f $size)), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
}

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$fs = [System.IO.File]::Create($OutFile)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([uint16]0)                    # reserved
    $bw.Write([uint16]1)                    # type = icon
    $bw.Write([uint16]$payloads.Count)      # count

    $offset = 6 + 16 * $payloads.Count
    foreach ($p in $payloads) {
        $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }   # 256 在 ICO 里记 0
        $bw.Write([byte]$dim)               # bWidth
        $bw.Write([byte]$dim)               # bHeight
        $bw.Write([byte]0)                  # bColorCount
        $bw.Write([byte]0)                  # bReserved
        $bw.Write([uint16]1)                # wPlanes
        $bw.Write([uint16]32)               # wBitCount
        $bw.Write([uint32]$p.Bytes.Length)  # dwBytesInRes
        $bw.Write([uint32]$offset)          # dwImageOffset
        $offset += $p.Bytes.Length
    }

    foreach ($p in $payloads) { $bw.Write($p.Bytes, 0, $p.Bytes.Length) }
    $bw.Flush()
}
finally {
    $bw.Dispose()
    $fs.Dispose()
}

$info = New-Object System.IO.FileInfo($OutFile)
Write-Output ("OK  {0}" -f $info.FullName)
Write-Output ("{0} bytes, {1} entries: {2}" -f $info.Length, $payloads.Count, (($payloads | ForEach-Object { "{0}({1})" -f $_.Size, $_.Kind }) -join ', '))
