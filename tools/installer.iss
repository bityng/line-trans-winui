; ============================================================================
;  逐行翻译 · Windows PC 端 · Inno Setup 安装包脚本
; ============================================================================
;
;  产物：publish\LineTrans-WinUI-v<版本>-Setup.exe
;
;  一般不用手敲 ISCC，交给发布脚本一步出包：
;      powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Installer
;
;  手动编译（在 tools\ 目录下）：
;      ISCC.exe /DMyAppVersion=0.2.0 ^
;               /DSourceDir=<仓库根>\publish\LineTrans-WinUI-win-x64 ^
;               /DOutputDir=<仓库根>\publish ^
;               /DLicenseFile=<仓库根>\publish\installer-build\LICENSE.txt ^
;               installer.iss
;
;  版本号不在这里写死：由 publish.ps1 从 src\LineTrans.App\LineTrans.App.csproj
;  的 <Version> 读出来，再用 /D 宏传进来。下面三个 #ifndef 只是给「不带 /D 的
;  手动编译」兜底，正常情况下都会被命令行覆盖。
;
;  维护提醒（两条，都很容易踩）：
;    1. 本文件必须保持 UTF-8 with BOM。存成无 BOM 的 UTF-8，ISCC 会按系统 ANSI
;       代码页解码，向导里的中文会全变乱码。
;    2. AppId 是这套安装的身份证，v0.1.1 起就固定成这样，**任何情况下都不要改**：
;       改了会被 Windows 当成另一个程序，覆盖安装会并存两份，卸载也只卸得掉一份。
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\LineTrans-WinUI-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\publish"
#endif
#ifndef LicenseFile
  #define LicenseFile "..\publish\installer-build\LICENSE.txt"
#endif

#define MyAppName        "逐行翻译"
#define MyAppNameEn      "LineTrans"
#define MyAppExeName     "LineTrans.App.exe"
#define MyAppPublisher   "LineTrans contributors"
#define MyAppUrl         "https://github.com/bityng/line-trans-winui"
#define MyAppId          "{{4F2C9A61-7D3B-4E58-9C0A-15B6E8D24F73}"

; 编译期兜底：源目录缺了运行必需的文件就直接编译失败，别产出一个装完跑不起来的包。
#if !FileExists(AddBackslash(SourceDir) + MyAppExeName)
  #error SourceDir 里没有 LineTrans.App.exe，请先跑 tools\publish.ps1 生成发布目录。
#endif
#if !FileExists(AddBackslash(SourceDir) + "dict\\core.tsv")
  #error 发布目录缺 dict\\core.tsv，划词查义会失效，拒绝生成安装包。
#endif
#if !FileExists(AddBackslash(SourceDir) + "dict\\lemma.tsv")
  #error 发布目录缺 dict\\lemma.tsv，划词查义会失效，拒绝生成安装包。
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}/releases
; 安装目录：默认在「程序文件」下，向导里用户可以改（DisableDirPage=no）。
DefaultDirName={autopf}\LineTrans
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes
; 默认按当前用户安装（不需要管理员）；有管理员的用户可以走覆盖对话框装到本机所有用户，
; 命令行也留了口子（/ALLUSERS /CURRENTUSER）。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
; 不带 MSIX、不写自签名证书：双击 Setup.exe 直接装，没有信任证书那一关。
OutputDir={#OutputDir}
OutputBaseFilename=LineTrans-WinUI-v{#MyAppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\LineTrans.App\Assets\LineTrans.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; AGPL-3.0 全文（许可协议页）+ 三个源仓库入口说明（信息页，见 installer-agpl-notice.txt）
LicenseFile={#LicenseFile}
InfoBeforeFile=installer-agpl-notice.txt
; 卸载入口显示成中文名，图标用主程序自己的
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCopyright=Copyright (C) 2026 LineTrans contributors - AGPL-3.0-or-later
DisableWelcomePage=no
ShowLanguageDialog=no

[Languages]
; Inno 6 官方语言包里没有简体中文（在「非官方语言包」里）。
; tools\isl\ChineseSimplified.isl 取自 jrsoftware.org 的翻译索引页
; （https://jrsoftware.org/files/istrans/ → 简体中文，维护者 Zhenghan Yang），
; 随仓库一起带上，编译时不再依赖网络。
Name: "chinese"; MessagesFile: "isl\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce
Name: "autostart"; Description: "开机时自动启动{#MyAppName}（写当前用户的注册表，不需要管理员权限）"; GroupDescription: "启动选项：" 

[Files]
; 整个发布目录递归装进去：LineTrans.App.exe 与全部 Windows App SDK 依赖、内置离线词库 dict\*.tsv
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "{#MyAppName} {#MyAppVersion}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"; Comment: "卸载 {#MyAppName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; 开机自启只写 HKCU，绝不碰 HKLM：
; 命令行与应用自身「开机自启」开关写的是同一个值（HKCU\...\Run\LineTrans，同样带 --minimized），
; 两边内容一致，不会互相覆盖；Flags: uninsdeletevalue 保证卸载时清干净。
; Check 是给静默安装兜底的，见文件末尾 [Code] 里的说明。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LineTrans"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart; Check: AutoStartEntryAllowed

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行{#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载不留残渣：{app} 整个目录清掉（含运行时写进去的日志等）
Type: filesandordirs; Name: "{app}"

[Code]
// 静默安装（/VERYSILENT）时 Inno 会把 [Tasks] 里的任务**全部**选中，连默认不勾的
// 「开机自启」也一起装上，跟向导里看到的默认状态不一致。
//
// 实测踩坑记录（别再走一遍）：只在 InitializeWizard 里调
//   WizardSelectTasks('desktopicon,!autostart')
// 是没用的——任务状态在那之后又被 Inno 的静默默认值覆盖回去，装完照样写进
// HKCU\...\Run\LineTrans。所以这里干脆不碰任务状态，改成给 [Registry] 那条加 Check：
//   · 交互式安装：勾选框说了算（[Registry] 上的 Tasks: autostart 把关），Check 一律放行；
//   · 静默安装：只有部署方显式写了 /TASKS="...,autostart" 或 /MERGETASKS="autostart" 才写注册表。
function AutoStartEntryAllowed: Boolean;
var
  Tail: String;
begin
  if not WizardSilent then
  begin
    Result := True;
    Exit;
  end;

  Tail := UpperCase(GetCmdTail);
  Result := (Pos('AUTOSTART', Tail) > 0) and (Pos('!AUTOSTART', Tail) = 0);
end;
