<#
  verify-icon.ps1 —— 图标取证：把 .ico 资产与 exe 里真正嵌进去的图标资源都读回来。

  验三件事：
    1) Assets\LineTrans.ico 的容器结构（尺寸清单 / 每条的格式与偏移 / 文件哈希）；
    2) 构建产物 exe 里【真的有】图标资源：Icon.ExtractAssociatedIcon 读回来存 PNG + 哈希；
    3) exe 的图标组里有几个、分别多大（ExtractIconEx 枚举）。

  用法：pwsh -File tests\IconTools\verify-icon.ps1 [-ExePath <exe>] [-OutDir <目录>]
#>
[CmdletBinding()]
param(
    [string]$IcoPath = (Join-Path $PSScriptRoot '..\..\src\LineTrans.App\Assets\LineTrans.ico'),
    [string]$ExePath = (Join-Path $PSScriptRoot '..\..\src\LineTrans.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\LineTrans.App.exe'),
    [string]$OutDir  = (Join-Path $env:TEMP 'lt-icon-verify')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

if (-not (Test-Path -LiteralPath $IcoPath)) { throw "找不到 .ico：$IcoPath" }
if (-not (Test-Path -LiteralPath $ExePath)) { throw "找不到 exe（先构建）：$ExePath" }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Write-Output '=== 1) .ico 资产 ==='
$info = Get-Item -LiteralPath $IcoPath
Write-Output ("路径   : {0}" -f $info.FullName)
Write-Output ("字节数 : {0}" -f $info.Length)
Write-Output ("SHA256 : {0}" -f (Get-Sha256 $info.FullName))

$bytes = [System.IO.File]::ReadAllBytes($info.FullName)
$count = [BitConverter]::ToUInt16($bytes, 4)
Write-Output ("容器   : type={0} entries={1}" -f [BitConverter]::ToUInt16($bytes, 2), $count)
for ($i = 0; $i -lt $count; $i++) {
    $o = 6 + 16 * $i
    $w = $bytes[$o]; $h = $bytes[$o + 1]
    if ($w -eq 0) { $w = 256 }
    if ($h -eq 0) { $h = 256 }
    $bpp    = [BitConverter]::ToUInt16($bytes, $o + 6)
    $len    = [BitConverter]::ToUInt32($bytes, $o + 8)
    $offset = [BitConverter]::ToUInt32($bytes, $o + 12)
    $magic  = [BitConverter]::ToUInt32($bytes, $offset)
    $kind   = if ($magic -eq 0x474E5089) { 'PNG' } elseif ($magic -eq 0x00000028) { 'DIB(40B header)' } else { ('未知 0x{0:X8}' -f $magic) }
    $end = $offset + $len
    Write-Output ("  #{0}  {1,3}x{1,-3} {2}bpp  {3,7} bytes @ offset {4,6}  结束={5}/{6}  {7}" -f $i, $w, $bpp, $len, $offset, $end, $bytes.Length, $kind)
}

Write-Output ''
Write-Output '=== 2) exe 里的图标资源（ExtractAssociatedIcon 读回）==='
$ico = [System.Drawing.Icon]::ExtractAssociatedIcon($exePath)
if ($null -eq $ico) { throw 'exe 里没有关联图标资源（ApplicationIcon 没生效）' }
Write-Output ("读回尺寸 : {0}x{1}" -f $ico.Width, $ico.Height)
$bmp = $ico.ToBitmap()
$png = Join-Path $OutDir 'exe-extracted-32.png'
$bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output ("PNG      : {0}  ({1} bytes, {2}x{3}, SHA256 {4})" -f $png, (Get-Item $png).Length, $bmp.Width, $bmp.Height, (Get-Sha256 $png))
$bmp.Dispose(); $ico.Dispose()

Write-Output ''
Write-Output '=== 3) exe 图标组里的每一档（ExtractIconEx 枚举）==='
$src = @'
using System;
using System.Runtime.InteropServices;
public static class IconEnum {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string szFileName, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
'@
Add-Type -TypeDefinition $src -Language CSharp

$n = [IconEnum]::ExtractIconEx($exePath, -1, $null, $null, 0)
Write-Output ("exe 图标组里的图标数量 : {0}" -f $n)
for ($i = 0; $i -lt $n; $i++) {
    $large = New-Object IntPtr[] 1
    $small = New-Object IntPtr[] 1
    [void][IconEnum]::ExtractIconEx($exePath, $i, $large, $small, 1)
    foreach ($pair in @(@('large', $large[0]), @('small', $small[0]))) {
        $kind = $pair[0]; $h = [IntPtr]$pair[1]
        if ($h -eq [IntPtr]::Zero) { Write-Output ("  #{0} {1}: 空" -f $i, $kind); continue }
        $icon = [System.Drawing.Icon]::FromHandle($h)
        $b = $icon.ToBitmap()
        $file = Join-Path $OutDir ("exe-icon-{0}-{1}-{2}x{3}.png" -f $i, $kind, $b.Width, $b.Height)
        $b.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output ("  #{0} {1}: {2}x{3} -> {4} ({5} bytes, SHA256 {6})" -f $i, $kind, $b.Width, $b.Height, (Split-Path -Leaf $file), (Get-Item $file).Length, (Get-Sha256 $file))
        $b.Dispose(); $icon.Dispose()
        [void][IconEnum]::DestroyIcon($h)
    }
}
