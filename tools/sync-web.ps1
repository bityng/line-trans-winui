<#
  sync-web.ps1 —— 把网页端 line-trans-web 的 public/ 同步到 PC 端内嵌的 web/。

  三端共用同一套网页界面（网页端 public/ ≡ 安卓 assets/web/ ≡ PC web/），
  改界面时先改网页端，再跑这个脚本，最后几个仓库各自发版。

  用法（在 PC 端仓库根目录）：
    powershell -ExecutionPolicy Bypass -File tools\sync-web.ps1
    powershell -ExecutionPolicy Bypass -File tools\sync-web.ps1 -Source "D:\其它副本\line-trans-web\public"
#>
param(
    [string]$Source = "F:\逐行翻译\工程文件\网页端\line-trans-web\public"
)

$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot '..\src\LineTrans.App\web'

if (-not (Test-Path $Source)) { throw "找不到网页端 public 目录：$Source（用 -Source 指定）" }
if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target | Out-Null }

$patterns = @('*.html', '*.js', '*.css', '*.svg', '*.json', '*.png')
$copied = 0
foreach ($pattern in $patterns) {
    Get-ChildItem -Path $Source -Filter $pattern -File | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $target $_.Name) -Force
        $copied++
    }
}

Write-Host "已同步 $copied 个文件到 src\LineTrans.App\web\" -ForegroundColor Green
Get-ChildItem -Path $target -File | ForEach-Object { Write-Host ("  " + $_.Name + "  " + $_.Length + " B") }
