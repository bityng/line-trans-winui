<#
    逐行翻译 · Windows PC 端（LineTrans WinUI）发布脚本
    ==================================================================

    作用：把 PC 端编译成一个免安装、解压即用的发布文件夹，并打成 zip。
          产物是 unpackaged（非打包）桌面程序，双击 LineTrans.App.exe 就能跑。

    用法（放在仓库的 tools 目录下，脚本自己定位仓库根，从哪调用都行）：

        # 方式一：右键本文件 ->「使用 PowerShell 运行」，或在终端里
        powershell -ExecutionPolicy Bypass -File tools\publish.ps1

        # 方式二：什么都不带，走默认参数
        .\tools\publish.ps1

    可选参数：

        -Configuration <名字>     构建配置，默认 Release
        -RuntimeIdentifier <RID>  目标运行时，默认 win-x64（本工程只支持 x64）
        -OutputDirectory <路径>   发布目录，默认 <仓库根>\publish\LineTrans-WinUI-win-x64
        -ZipPath <路径>           zip 路径，默认 <仓库根>\publish\LineTrans-WinUI-v<版本>.zip
        -SelfContained            连 .NET 运行时一起发布（目标机没装 .NET 8 桌面运行时时用）
        -SkipZip                  只生成发布目录，不打包 zip
        -Clean                    发布前先删掉旧的发布目录
        -NoPause                  跑完立刻退出（CI / 自动化用；不加会停在「按回车键退出」）
        -Installer                额外用 Inno Setup 打一个中文安装包（publish\LineTrans-WinUI-v<版本>-Setup.exe）
        -IsccPath <路径>          Inno Setup 的 ISCC.exe 路径，默认自动查找
        -InstallerPath <路径>     安装包输出路径，默认 <仓库根>\publish\LineTrans-WinUI-v<版本>-Setup.exe

    版本号自动取自 src\LineTrans.App\LineTrans.App.csproj 的 <Version>。
    同一个版本号同时用在发布目录、zip 名和 Setup.exe 名上，安装包里的版本也由它决定。

    退出码：

        0  成功
        1  环境检查失败（找不到 dotnet / 工程文件等）
        2  dotnet publish 失败
        3  发布产物缺关键文件（LineTrans.App.exe，或 dict\core.tsv / dict\lemma.tsv）
        4  打 zip 失败
        5  生成安装包失败（没装 Inno Setup、ISCC 编译失败，或产物没生成）

    加了 -Installer 才需要 Inno Setup 6；不加的话本脚本的行为和以前完全一样。

    维护提醒：本文件必须保持 UTF-8 with BOM。
    Windows PowerShell 5.1 读「无 BOM 的 UTF-8」脚本时会按 GBK 解码，中文提示会全变乱码。
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory,
    [string]$ZipPath,
    [switch]$SelfContained,
    [switch]$SkipZip,
    [switch]$Clean,
    [switch]$NoPause,
    [switch]$Installer,
    [string]$IsccPath,
    [string]$InstallerPath
)

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------------
# 小工具
# ----------------------------------------------------------------------

function Write-Step {
    param([string]$Text)
    Write-Host ''
    Write-Host ('==> ' + $Text) -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host ('    [OK] ' + $Text) -ForegroundColor Green
}

function Write-Note {
    param([string]$Text)
    Write-Host ('    ' + $Text) -ForegroundColor Yellow
}

function Exit-WithError {
    param([int]$Code, [string]$Text)
    Write-Host ''
    Write-Host ('[失败] ' + $Text) -ForegroundColor Red
    Write-Host ''
    if (-not $NoPause) { [void](Read-Host '按回车键退出') }
    exit $Code
}

function Format-Size {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return ([math]::Round($Bytes / 1GB, 2)).ToString() + ' GB' }
    if ($Bytes -ge 1MB) { return ([math]::Round($Bytes / 1MB, 1)).ToString() + ' MB' }
    if ($Bytes -ge 1KB) { return ([math]::Round($Bytes / 1KB, 1)).ToString() + ' KB' }
    return ([math]::Round($Bytes, 0)).ToString() + ' 字节'
}

<#
    找 Inno Setup 的 ISCC.exe。

    找不到就返回 $null，由调用方决定怎么报错——本函数自己不写退出码，
    以后别的地方也能复用。

    查找顺序：显式参数 -> 环境变量 ISCC_PATH -> 注册表卸载项 -> 常见安装位置 -> PATH。
    注册表那一步不能省：Inno Setup 允许「仅为我安装」，那种装法落在
    %LOCALAPPDATA%\Programs\Inno Setup 6 下，不查 HKCU 是找不到的。
#>
function Find-Iscc {
    param([string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        if (Test-Path -LiteralPath $Explicit -PathType Leaf) { return [System.IO.Path]::GetFullPath($Explicit) }
        Write-Note ('-IsccPath 指向的文件不存在：' + $Explicit)
        return $null
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ISCC_PATH) -and (Test-Path -LiteralPath $env:ISCC_PATH -PathType Leaf)) {
        return [System.IO.Path]::GetFullPath($env:ISCC_PATH)
    }

    $uninstallRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($uninstallRoot in $uninstallRoots) {
        $subKeys = @(Get-ChildItem -LiteralPath $uninstallRoot -ErrorAction SilentlyContinue)
        foreach ($subKey in $subKeys) {
            if ($subKey.PSChildName -notlike 'Inno Setup*') { continue }
            $props = Get-ItemProperty -LiteralPath $subKey.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $props) { continue }

            $installDir = [string]$props.InstallLocation
            if ([string]::IsNullOrWhiteSpace($installDir)) {
                $uninstall = [string]$props.UninstallString
                if ($uninstall.Length -gt 0) { $installDir = Split-Path -Parent ($uninstall.Trim('"')) }
            }
            if ([string]::IsNullOrWhiteSpace($installDir)) { continue }

            $candidate = Join-Path $installDir 'ISCC.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }

    $guesses = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $guesses.Add((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $guesses.Add((Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $guesses.Add((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))
    }
    foreach ($guess in $guesses) {
        if (Test-Path -LiteralPath $guess -PathType Leaf) { return $guess }
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    return $null
}

# ----------------------------------------------------------------------
# 定位仓库
# ----------------------------------------------------------------------

$ToolDir  = $PSScriptRoot
$RepoRoot = Split-Path -Parent $ToolDir

Write-Host ''
Write-Host '逐行翻译 · Windows PC 端 发布脚本' -ForegroundColor White
Write-Host '------------------------------------------------------------------' -ForegroundColor DarkGray

if ([string]::IsNullOrWhiteSpace($RepoRoot) -or -not (Test-Path -LiteralPath (Join-Path $RepoRoot 'LineTrans.sln'))) {
    Exit-WithError 1 ('没找到 LineTrans.sln。本脚本应放在仓库的 tools\ 目录下，当前推算的仓库根是：' + $RepoRoot)
}

$ProjectPath = Join-Path $RepoRoot 'src\LineTrans.App\LineTrans.App.csproj'
if (-not (Test-Path -LiteralPath $ProjectPath)) {
    Exit-WithError 1 ('找不到工程文件：' + $ProjectPath)
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Exit-WithError 1 '找不到 dotnet 命令。请先安装 .NET SDK 8.0 或更高版本，再重新运行本脚本。'
}

# ----------------------------------------------------------------------
# 版本号
# ----------------------------------------------------------------------

$Version = '0.0.0'
try {
    [xml]$csproj = Get-Content -LiteralPath $ProjectPath -Raw -Encoding UTF8
    $versions = @($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($versions.Count -gt 0) { $Version = ([string]$versions[0]).Trim() }
} catch {
    Write-Note ('读取版本号失败（' + $_.Exception.Message + '），按 ' + $Version + ' 继续。')
}

# ----------------------------------------------------------------------
# 默认路径
# ----------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepoRoot 'publish\LineTrans-WinUI-win-x64'
}
if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = Join-Path $RepoRoot ('publish\LineTrans-WinUI-v' + $Version + '.zip')
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$ZipPath         = [System.IO.Path]::GetFullPath($ZipPath)

Write-Host ''
Write-Host ('  仓库根目录 : ' + $RepoRoot)
Write-Host ('  工程文件   : ' + $ProjectPath)
Write-Host ('  版本号     : ' + $Version)
Write-Host ('  构建配置   : ' + $Configuration)
Write-Host ('  目标运行时 : ' + $RuntimeIdentifier)
Write-Host ('  发布模式   : ' + $(if ($SelfContained) { '自包含（连 .NET 运行时一起发）' } else { '框架依赖（目标机需要 .NET 8 桌面运行时）' }))
Write-Host ('  发布目录   : ' + $OutputDirectory)
if ($SkipZip) {
    Write-Host '  zip        : （已指定 -SkipZip，跳过打包）'
} else {
    Write-Host ('  zip        : ' + $ZipPath)
}

# ----------------------------------------------------------------------
# 清理
# ----------------------------------------------------------------------

if ($Clean -and (Test-Path -LiteralPath $OutputDirectory)) {
    Write-Step '清理旧的发布目录'
    try {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
        Write-Ok ('已删除 ' + $OutputDirectory)
    } catch {
        Exit-WithError 1 ('删除旧发布目录失败：' + $_.Exception.Message)
    }
}

# ----------------------------------------------------------------------
# 发布
# ----------------------------------------------------------------------

Write-Step '开始发布（dotnet publish）'

$publishArgs = @(
    'publish', $ProjectPath,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '-o', $OutputDirectory,
    '--nologo'
)
if ($SelfContained) {
    $publishArgs += '--self-contained'
    $publishArgs += 'true'
}

Write-Host ('    dotnet ' + ($publishArgs -join ' ')) -ForegroundColor DarkGray
Write-Host ''

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
& dotnet @publishArgs
$publishExit = $LASTEXITCODE
$stopwatch.Stop()

if ($publishExit -ne 0) {
    Exit-WithError 2 ('dotnet publish 失败（退出码 ' + $publishExit + '）。请检查上面的编译输出。')
}
Write-Ok ('发布成功，用时 ' + [math]::Round($stopwatch.Elapsed.TotalSeconds, 1) + ' 秒。')

# ----------------------------------------------------------------------
# 校验产物
# ----------------------------------------------------------------------

Write-Step '校验发布产物'

$exePath = Join-Path $OutputDirectory 'LineTrans.App.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    Exit-WithError 3 ('发布目录里没有 LineTrans.App.exe：' + $OutputDirectory)
}
$exeItem = Get-Item -LiteralPath $exePath
Write-Ok ('LineTrans.App.exe  ' + $exeItem.Length + ' 字节')

# 内置离线词库是运行期依赖：缺了划词查义直接废掉，所以这里硬校验。
$dictFiles = @('dict\core.tsv', 'dict\lemma.tsv')
$dictMissing = @()
foreach ($relative in $dictFiles) {
    $full = Join-Path $OutputDirectory $relative
    if (Test-Path -LiteralPath $full -PathType Leaf) {
        $size = (Get-Item -LiteralPath $full).Length
        Write-Ok ($relative + '  ' + $size + ' 字节（' + (Format-Size $size) + '）')
    } else {
        $dictMissing += $relative
        Write-Host ('    [缺失] ' + $relative) -ForegroundColor Red
    }
}

if ($dictMissing.Count -gt 0) {
    Exit-WithError 3 ('发布目录缺少内置离线词库：' + ($dictMissing -join '、') + '。划词查义会失效，请检查 LineTrans.App.csproj 里的 Content 项。')
}

$noisePath = Join-Path $OutputDirectory 'dict\README.md'
if (Test-Path -LiteralPath $noisePath -PathType Leaf) {
    Write-Note '注：dict\README.md 也被复制进发布目录了（约 1KB 噪声），可在 csproj 里排除。'
} else {
    Write-Ok 'dict\README.md 未进入发布目录（已在 csproj 里排除）。'
}

$allFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File)
$totalBytes = 0.0
foreach ($f in $allFiles) { $totalBytes += $f.Length }
Write-Ok ('发布目录共 ' + $allFiles.Count + ' 个文件，合计 ' + (Format-Size $totalBytes) + '。')

# ----------------------------------------------------------------------
# 打包 zip
# ----------------------------------------------------------------------

if (-not $SkipZip) {
    Write-Step '打包 zip'

    $zipDir = Split-Path -Parent $ZipPath
    if (-not [string]::IsNullOrWhiteSpace($zipDir) -and -not (Test-Path -LiteralPath $zipDir)) {
        [void](New-Item -ItemType Directory -Path $zipDir -Force)
    }

    if (Test-Path -LiteralPath $ZipPath) {
        try {
            Remove-Item -LiteralPath $ZipPath -Force
        } catch {
            Exit-WithError 4 ('旧的 zip 删不掉（可能正被解压软件占用）：' + $ZipPath)
        }
    }

    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $OutputDirectory,
            $ZipPath,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false)
    } catch {
        Exit-WithError 4 ('打 zip 失败：' + $_.Exception.Message)
    }

    $zipItem = Get-Item -LiteralPath $ZipPath
    Write-Ok ('zip 体积：' + (Format-Size $zipItem.Length) + '（' + $zipItem.Length + ' 字节）')
}

# ----------------------------------------------------------------------
# 打安装包（Inno Setup）
# ----------------------------------------------------------------------

if ($Installer) {
    Write-Step '打安装包（Inno Setup）'

    $issPath = Join-Path $ToolDir 'installer.iss'
    if (-not (Test-Path -LiteralPath $issPath -PathType Leaf)) {
        Exit-WithError 5 ('找不到安装包脚本：' + $issPath)
    }

    $iscc = Find-Iscc -Explicit $IsccPath
    if ([string]::IsNullOrWhiteSpace($iscc)) {
        Write-Note '没找到 Inno Setup 的 ISCC.exe。'
        Exit-WithError 5 '请先装 Inno Setup 6（winget install --id JRSoftware.InnoSetup），装完重开一个终端再跑；或用 -IsccPath 直接指定 ISCC.exe 的路径。'
    }
    Write-Ok ('ISCC：' + $iscc)

    if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
        $InstallerPath = Join-Path $RepoRoot ('publish\LineTrans-WinUI-v' + $Version + '-Setup.exe')
    }
    $InstallerPath = [System.IO.Path]::GetFullPath($InstallerPath)
    $installerDir  = Split-Path -Parent $InstallerPath
    if (-not [string]::IsNullOrWhiteSpace($installerDir) -and -not (Test-Path -LiteralPath $installerDir)) {
        [void](New-Item -ItemType Directory -Path $installerDir -Force)
    }

    # Inno 的 LicenseFile 只认 .txt / .rtf，仓库里的 LICENSE 没有扩展名，
    # 这里生成一份逐字副本。放 publish\ 下——publish\ 已被 .gitignore 忽略，不会脏仓库。
    $licenseSource = Join-Path $RepoRoot 'LICENSE'
    if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) {
        Exit-WithError 5 ('找不到许可协议文件：' + $licenseSource)
    }
    $buildDir = Join-Path $installerDir 'installer-build'
    [void](New-Item -ItemType Directory -Path $buildDir -Force)
    $licenseCopy = Join-Path $buildDir 'LICENSE.txt'
    Copy-Item -LiteralPath $licenseSource -Destination $licenseCopy -Force

    if (Test-Path -LiteralPath $InstallerPath) {
        try {
            Remove-Item -LiteralPath $InstallerPath -Force
        } catch {
            Exit-WithError 5 ('旧的安装包删不掉（可能正在运行，或被杀软占用）：' + $InstallerPath)
        }
    }

    $isccArgs = @(
        '/Qp',
        ('/DMyAppVersion=' + $Version),
        ('/DSourceDir=' + $OutputDirectory),
        ('/DOutputDir=' + $installerDir),
        ('/DLicenseFile=' + $licenseCopy),
        ('/F' + [System.IO.Path]::GetFileNameWithoutExtension($InstallerPath)),
        $issPath
    )
    Write-Host ('    ' + $iscc + ' ' + ($isccArgs -join ' ')) -ForegroundColor DarkGray
    Write-Host ''
    Write-Note '整个发布目录要 lzma2 整包压缩：149 MB 的发布目录本机实测约 15 分钟，不是卡死了。嫌慢把 installer.iss 里的 Compression 调成 lzma2/normal。'

    & $iscc @isccArgs
    $isccExit = $LASTEXITCODE
    if ($isccExit -ne 0) {
        Exit-WithError 5 ('ISCC 编译安装包失败（退出码 ' + $isccExit + '）。请检查上面的编译输出。')
    }

    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
        Exit-WithError 5 ('ISCC 返回成功，但没看到安装包：' + $InstallerPath)
    }

    $setupItem = Get-Item -LiteralPath $InstallerPath
    Write-Ok ('安装包版本：' + $Version + '（AppId 固定，可直接覆盖升级）')
    Write-Ok ('安装包体积：' + (Format-Size $setupItem.Length) + '（' + $setupItem.Length + ' 字节）')
}

# ----------------------------------------------------------------------
# 完成
# ----------------------------------------------------------------------

Write-Step '全部完成'
Write-Host ''
Write-Host ('  发布目录 : ' + $OutputDirectory) -ForegroundColor White
if (-not $SkipZip) {
    Write-Host ('  zip      : ' + $ZipPath) -ForegroundColor White
}
if ($Installer) {
    Write-Host ('  安装包   : ' + $InstallerPath) -ForegroundColor White
}
Write-Host ''
Write-Host '  把发布目录整份拷给别人，或解压 zip 后双击 LineTrans.App.exe 即可运行（免安装）。' -ForegroundColor Gray
if ($Installer) {
    Write-Host '  安装包（Setup.exe）双击就是中文安装向导：可改安装目录、可选桌面快捷方式与开机自启，卸载干净。' -ForegroundColor Gray
}
if (-not $SelfContained) {
    Write-Host '  注意：默认是框架依赖模式，目标机需要装 .NET 8 桌面运行时；' -ForegroundColor Gray
    Write-Host '        要连运行时一起发，请加 -SelfContained 重新跑一次。' -ForegroundColor Gray
}
Write-Host ''

if (-not $NoPause) { [void](Read-Host '按回车键退出') }
exit 0
