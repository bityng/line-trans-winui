# 逐行翻译 · Windows PC 端（LineTrans WinUI）

用 **原生 WinUI 3** 重写的 Windows 桌面客户端。不使用 WebView2，不依赖 Node.js ——
所有界面都是 XAML 控件，直接调用 Windows App SDK。

> 当前版本：**v0.1.1**（写在 `src/LineTrans.App/LineTrans.App.csproj` 的 `<Version>`，
> 「关于」页从程序集信息里读出来显示）。
>
> 当前进度：**四个页面（文档 / 翻译 / 设置 / 关于）都已是真实实现**，
> `LineTrans.Core` 与 `LineTrans.Dictionary` 两个纯逻辑类库已经通过 `ProjectReference` 接入，
> 划词查义浮层可用，文档可导出 6 种格式，
> **系统托盘常驻、全局快捷键、全局划词、开机自启**均已可用，
> 发布走 `tools\publish.ps1`（非打包 unpackaged，双击 exe 即可运行）。

---

## 这是什么

逐行翻译（LineTrans）是一套「逐行 / 逐句对照翻译」工具：把文章、小说、字幕按行或按句拆开，
人工逐条翻译（可用 AI 辅助），最后导出对照文本。

本项目是**第三个端**：

| 端 | 仓库 | 技术栈 |
| --- | --- | --- |
| 安卓客户端 | [bityng/line-trans-android](https://github.com/bityng/line-trans-android) | Kotlin + Jetpack Compose |
| 网页服务端 | [bityng/line-trans-web](https://github.com/bityng/line-trans-web) | Node.js 18+（零依赖） |
| **Windows PC 端** | [bityng/line-trans-winui](https://github.com/bityng/line-trans-winui) | C# + WinUI 3 + Windows App SDK |

设计系统与网页端保持视觉一致（主色 `#4d6bfe`、rgba 描边、8/12/16/24 圆角），
令牌定义在 `src/LineTrans.App/Themes/Colors.xaml`，与网页端 `public/style.css` 的 `:root` 变量一一对应。

---

## 功能现状

### 四个页面

| 页面 | 已实现 |
| --- | --- |
| **文档** | 按文件夹分组列出全部文档、搜索（文档名 / 文件夹 / 正文）、筛选（全部 / 未完成 / 收藏）、排序（最近更新 / 按名称 / 按进度）、新建文档、从文件导入（`.txt` / `.md` / `.srt` / `.csv`，可选智能清理）、行内进度条，以及每行的 打开 / 置顶 / 重命名 / 移动 / 导出 / 删除 |
| **翻译** | 左侧「全部句子」列表、中间原文（只读、可选中复制）、右侧译文（可直接编辑），中间可拖拽分割；上一句 / 下一句 / 跳转、逐行 ⇄ 逐句切换、单句 AI 翻译、批量翻译剩余（可随时停止）、复制 / 粘贴原文 / 收藏 / 标记完成、导出；底部状态栏显示当前模型与 token 用量、费用估算 |
| **设置** | AI 服务（协议 / BaseUrl / API Key / 模型 / 温度 / 最大 token / 输入输出单价）、语言与提示词（源语言 / 目标语言 / 自动检测 / 前文参考条数 / 提示词模板 / 系统提示词 / 术语表）、划词查义（总开关 / 本地词库 / AI 兜底 / 释义语言）、界面（主题 / 字号缩放）、数据（导出设置 / 导入设置 / 重新载入 / 恢复默认，导入与重置前都会自动备份） |
| **关于** | 版本号、许可、三个源码仓库入口（AGPL 第 13 条）、离线词库加载状态、打开数据目录、查看运行日志 |

### 划词查义

翻译页的原文框与译文框都支持**双击任意单词**弹出释义浮层，来源链与安卓端一致：
我的词库 → 本地离线词库（`dict\core.tsv` + `dict\lemma.tsv`，带词形还原）→ 牛津 → Wiktionary → AI 兜底。

- 浮层控件：`src/LineTrans.App/Controls/WordLookupPanel.xaml`
- 查询入口：`AppServices.LookupWordAsync()`（关掉 AI 兜底时只查本地词库，离线秒出）
- 释义语言由设置页的「释义语言」控制（仅中文 / 中英对照 / 仅英文）

### 系统托盘

关闭主窗口时**默认最小化到托盘**，而不是退出（设置页可改成「直接退出」）。托盘图标常驻通知区域，
右键菜单四项：

| 菜单项 | 作用 |
| --- | --- |
| 显示主窗口 | 把主窗口恢复出来并置前 |
| 全局划词 | 等同按一次全局热键 |
| 设置 | 打开主窗口并跳到「设置」页 |
| 退出 | 摘掉托盘图标与热键后真正退出 |

- 图标是 **GDI 自绘**的 `HICON`：品牌色 `#4D6BFE` 方块 + 白色「译」字，尺寸取系统托盘图标尺寸
  （`src/LineTrans.App/Services/TrayIconFactory.cs`）。自绘任何一步失败都会回落到系统默认应用程序图标，
  保证托盘一定有东西显示。
- **explorer 崩溃重启后会自动重新注册**：托盘窗口过程接收 shell 广播的 `TaskbarCreated`，
  收到后重新 `Shell_NotifyIcon(NIM_ADD)`（`src/LineTrans.App/Services/TrayIconHost.cs`）。
- 托盘宿主跑在**自己的线程**上（`RegisterHotKey` 的 `WM_HOTKEY` 只会投递到注册它的那个线程），
  UI 侧统一通过 `App.Tray` 与它通信。

### 全局快捷键

- 默认 **`Ctrl+Alt+C`**，在**任意程序**里按下都会触发全局划词（设置页可改，也可整个关掉）。
- **被别的程序占用时自动回退**：按 `Ctrl+Alt+D` → `Ctrl+Alt+Q` → `Ctrl+Alt+H` → `Ctrl+Shift+C` → `Alt+Shift+D`
  的顺序依次往下试（`HotkeySpec.Fallbacks`），第一个注册成功的即为实际生效值。
- **配置值与实际生效值都会写明**，不会出现「按了没反应却不知道为什么」：
  - 设置页的「托盘状态」会显示「设置里填的 `Ctrl+Alt+C` 被别的程序占用了，当前实际生效的是 `Ctrl+Alt+D`」；
  - 回退发生的同时会冒一次托盘气泡；
  - 候选全部被占用时显示 `RegisterHotKey` 的错误码与中文说明。
  - **以设置页显示的实际生效值为准**，不要只看设置里填的那一行。

### 全局划词

在任意程序里选中文字 → 按全局热键 → 弹出释义 / 翻译窗：

- **单行走离线词库**（短文本且全是拉丁字母 / 数字时按单词处理），其余（含中文、多词、整句）**走 AI 翻译**。
- **不污染剪贴板**：先给剪贴板做快照（**文本 / 文件 / 位图 / 空**四类），再发 `Ctrl+C` 取词，
  用 `GetClipboardSequenceNumber()` 判断是否**真的**复制到了内容，读完后**立刻把快照原样写回去**。
- 弹窗**不抢焦点**（带 `WS_EX_NOACTIVATE`，也不进 Alt+Tab）、**置顶**、**单实例**
  （连续按热键只刷新内容，不会叠出一堆窗）、**`Esc` 关闭**（弹出期间临时注册一个全局 `Esc` 热键）。
- 相关文件：`Services/GlobalCaptureService.cs`（取词与调度）、`Services/ClipboardGuard.cs`（快照 / 还原）、
  `Views/LookupPopupWindow.xaml(.cs)`（弹窗）。

### 开机自启

设置页打开后写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `LineTrans` 项
——**只写 HKCU**，不碰 HKLM，不需要管理员权限，也不影响这台机器上的其他用户。
注册的命令行带 `--minimized`，开机拉起时直接缩进托盘，不弹主窗口打扰人。

### 导出

两个入口，最终都调 `LineTrans.Core.ExportManager`：

- **文档页**：文档行的「更多」菜单 →「导出」
- **翻译页**：顶部工具栏的「导出」按钮

菜单里一共 7 项 —— 6 种格式 + 1 条快捷方式：

| 菜单项 | `ExportFormat` | 扩展名 |
| --- | --- | --- |
| 原文 + 译文 TXT | `TXT_BILINGUAL` | `.txt` |
| 仅译文 TXT | `TXT_TRANSLATED_ONLY` | `.txt` |
| 已译替换源文 TXT | `TXT_SOURCE_FALLBACK` | `.txt` |
| Markdown 表格 | `MARKDOWN_TABLE` | `.md` |
| CSV 表格 | `CSV` | `.csv` |
| JSON（含元数据） | `JSON` | `.json` |
| 导出到数据目录（不弹保存对话框） | 按设置里的默认格式 | — |

- 前 6 项会弹 `FileSavePicker` 让用户选位置。unpackaged 应用必须先通过
  `WinRT.Interop.InitializeWithWindow.Initialize` 把窗口句柄交给选择器，否则调用直接抛异常 ——
  这段已在 `src/LineTrans.App/Services/ExportService.cs` 里处理。
- 最后一项直接写到 `%APPDATA%\LineTrans\exports\`，重名自动加序号，适合批量 / 自动化场景。
- **选择器打不开、或所选位置写不进去时，会自动退到数据目录并在提示里说明原因**，结果不会丢。
- 导出成功后：翻译页弹 InfoBar、文档页弹对话框，都带「打开所在文件夹」按钮。

---

## 自测

两个控制台自测工程，手写断言、**零第三方测试框架依赖**：

```powershell
cd 工程文件\PC端\LineTrans-WinUI

# Core：切分 / 智能清理 / 导出 / 计费 / 文档仓库 / 设置仓库 / AI 调用（含本地假 HTTP 服务）
dotnet run --project tests\LineTrans.Core.Tests

# Dictionary：离线词库加载 / 词形还原 / 查词链路 / 生词本（跑的是真实 4 万行词库，不是玩具数据）
dotnet run --project tests\LineTrans.Dictionary.Tests
```

两者都是「退出码 0 = 全部通过，1 = 有失败项」。当前基线：**Core 331 项、Dictionary 201 项，全部通过**。
Core 自测还有一个语料模式，
用来跟安卓端做差分比对：

```powershell
dotnet run --project tests\LineTrans.Core.Tests -- --corpus tests\corpus.json --out tests\cs-output.json
```

改过 `src/LineTrans.App/Assets/dict/` 里的词库后，请重跑 Dictionary 自测确认没有回归。

---

## 自检

上面的两个自测工程覆盖纯逻辑（切分 / 导出 / 计费 / 词库）；**托盘、全局热键、剪贴板、弹窗**这些
必须跑在真 Windows 会话里的东西，走 exe 自检：

```powershell
cd 工程文件\PC端\LineTrans-WinUI

# 拿发布产物跑（推荐），或者 Debug 产物
.\publish\LineTrans-WinUI-win-x64\LineTrans.App.exe --selftest
.\src\LineTrans.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\LineTrans.App.exe --selftest
```

- **10 步**：托盘图标 / 全局热键 / 开机自启 / 剪贴板文本往返 / 剪贴板文件往返 /
  无选中文字 / 捕获查词 / 弹窗单实例 / 整句翻译 / 关闭到托盘。
- **约 30 秒**跑完（开头有一段等待，让离线词库预热与托盘线程就绪），跑完自动退出。
- 报告写在 **`%APPDATA%\LineTrans\selftest-report.json`**，同时在 `%APPDATA%\LineTrans\pc-app.log` 里逐条留痕。
  报告含 `startedAt` / `processId` / `executablePath` / `traySettingsFile` / `logFile` / `steps[]` / `notes[]`，
  每一步的 `status` 是 `passed` / `failed` / `skipped`
  （例如托盘宿主没起来时，依赖它的后续步骤会整组 `skipped` 而不是假装通过）。
- 自检**用的都是真东西**：真的 `Shell_NotifyIcon`、真的 `RegisterHotKey`、真的往托盘窗口投递 `WM_HOTKEY`、
  真的读写系统剪贴板、真的查离线词库、真的弹窗。唯一模拟的是「谁是前台程序」——
  自检开一个自己的窗口当事前台，避免去动用户正在用的记事本、也不把测试文字打进别人的文档里。
- 开始前会保存用户原本的剪贴板内容，结束时**原样还原**。
- 局限见「已知限制」第 11 条：**真实物理按键与托盘右键菜单没有模拟**。

---

## 目录结构

```
LineTrans-WinUI/
├── LineTrans.sln
├── nuget.config                  ← 必须保留，见「已知限制」
├── LICENSE                       ← AGPL-3.0，逐字复制自网页端
├── README.md
├── tools/
│   └── publish.ps1               ← 一键发布：发布目录 + zip（见「如何发布」）
├── src/
│   ├── LineTrans.Core/           ← 纯逻辑类库（net8.0，不含 Windows 专有 API）
│   │   ├── Models.cs             ← TranslationDoc / TranslationUnit / AppSettings / ExportFormat
│   │   ├── TextParser.cs         ← 切分、smartClean、CSV 转义
│   │   ├── ExportManager.cs      ← 6 种格式的文本生成与文件名生成
│   │   ├── DocRepository.cs      ← 文档仓库（防抖写盘 + 原子替换）
│   │   ├── SettingsRepository.cs ← 设置仓库
│   │   ├── AiClient.cs           ← OpenAI 兼容 / Anthropic 调用、翻译记忆、前文参考
│   │   └── CostCalculator.cs     ← 费用估算
│   ├── LineTrans.Dictionary/     ← 纯逻辑类库（net8.0，离线词库 + 查词调度）
│   │   ├── LocalDictionary.cs    ← core.tsv / lemma.tsv 加载与词形还原
│   │   ├── DictionaryService.cs  ← 查词来源链（我的词库 → 本地 → 牛津 → Wiktionary → AI）
│   │   ├── Wordbook.cs           ← 生词本
│   │   ├── DictionaryModels.cs
│   │   └── LruCache.cs
│   └── LineTrans.App/            ← WinUI 3 桌面程序
│       ├── LineTrans.App.csproj
│       ├── app.manifest          ← PerMonitorV2 DPI 感知
│       ├── App.xaml / App.xaml.cs
│       ├── MainWindow.xaml(.cs)  ← 自定义标题栏 + NavigationView 导航壳
│       ├── Views/                ← HomePage / TranslationPage / SettingsPage / AboutPage /
│       │                            LookupPopupWindow（全局划词弹窗）
│       ├── Controls/             ← WordLookupPanel（划词查义浮层）/ DragSplitter（分栏拖拽）
│       ├── ViewModels/           ← DocRow / UnitRow 等列表行模型
│       ├── Services/             ← AppServices（服务容器）/ ExportService / AiExplainProvider /
│       │                            TrayIconHost（托盘线程）/ TrayIconFactory（GDI 自绘图标）/
│       │                            HotkeySpec（热键解析与回退）/ GlobalCaptureService（取词调度）/
│       │                            ClipboardGuard（剪贴板快照与还原）/ AutoStartManager（HKCU 自启）/
│       │                            TraySettings（tray.json）/ SelfTest（--selftest）
│       ├── Interop/              ← NativeMethods（Win32 P/Invoke：托盘 / 热键 / GDI / 剪贴板）
│       ├── Themes/               ← Colors.xaml（设计令牌）/ Styles.xaml（排版与控件样式）
│       └── Assets/dict/          ← 内置离线词库 core.tsv / lemma.tsv
└── tests/
    ├── corpus.json               ← 与安卓端共用的差分语料
    ├── LineTrans.Core.Tests/     ← Core 自测
    └── LineTrans.Dictionary.Tests/ ← Dictionary 自测
```

---

## 如何构建

### 前置条件

- Windows 10 1809（build 17763）或更高版本
- **.NET SDK 8.0 或更高**（本机在 10.0.301 上验证通过）
- **不需要** Visual Studio，**不需要** `dotnet workload install`
- **不需要**预装 Windows App Runtime：工程设了 `WindowsAppSDKSelfContained=true`，运行时会随程序复制

### 命令

```powershell
cd 工程文件\PC端\LineTrans-WinUI

# 还原 + 编译（推荐，直接编 App 工程）
dotnet build src\LineTrans.App\LineTrans.App.csproj -c Debug

# 或者走解决方案（会连两个类库与两个自测工程一起编）
dotnet build LineTrans.sln -c Debug
```

两种方式都是 **0 警告 0 错误**。首次构建较慢（要还原 Windows App SDK 并复制自包含运行时）；
之后增量构建很快（本机实测 2~3 分钟，注意**不要并发跑多个 dotnet build**）。

### 产物位置

```
# 直接编 App 工程：
src\LineTrans.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\LineTrans.App.exe

# 走解决方案（Platforms=x64，会多一层平台目录）：
src\LineTrans.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\LineTrans.App.exe
```

两种方式都会把内置词库复制到 `<输出目录>\dict\core.tsv` 与 `<输出目录>\dict\lemma.tsv`。
**缺了这两个文件，划词查义的本地词库部分会失效。**

---

## 如何运行

```powershell
# 方式一：dotnet run
dotnet run --project src\LineTrans.App\LineTrans.App.csproj

# 方式二：直接跑编译产物（推荐，启动更快）
.\src\LineTrans.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\LineTrans.App.exe
```

启动后：窗口标题「逐行翻译」，默认尺寸 1280×800，最小 900×600（按 DPI 缩放），
左侧导航栏四个入口 —— **文档 / 翻译 / 设置 / 关于**，默认停在「文档」。

---

## 如何发布

发布走 `tools\publish.ps1`（可双击、也可在终端里跑），产出一个**非打包（unpackaged）**的
发布文件夹与 zip —— 免安装，解压后双击 `LineTrans.App.exe` 即可运行。

```powershell
cd 工程文件\PC端\LineTrans-WinUI

# 默认参数（Release + win-x64），产出：
#   publish\LineTrans-WinUI-win-x64\        ← 发布目录，整份拷给别人即可
#   publish\LineTrans-WinUI-v0.1.1.zip       ← zip 名里的版本号取自 csproj 的 <Version>
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

可用参数：

| 参数 | 说明 |
| --- | --- |
| `-Configuration <名字>` | 构建配置，默认 `Release` |
| `-RuntimeIdentifier <RID>` | 目标运行时，默认 `win-x64`（本工程只支持 x64） |
| `-OutputDirectory <路径>` | 发布目录，默认 `publish\LineTrans-WinUI-win-x64` |
| `-ZipPath <路径>` | zip 路径，默认 `publish\LineTrans-WinUI-v<版本>.zip` |
| `-SelfContained` | 连 .NET 运行时一起发布（目标机没装 .NET 8 桌面运行时时用） |
| `-SkipZip` | 只生成发布目录，不打 zip |
| `-Clean` | 发布前先删掉旧的发布目录 |
| `-NoPause` | 跑完立刻退出（给 CI / 自动化用） |

脚本依次做四件事：

1. `dotnet publish src\LineTrans.App\LineTrans.App.csproj -c Release -r win-x64 -o <发布目录>`
2. **硬校验** `LineTrans.App.exe` 与 `dict\core.tsv`、`dict\lemma.tsv` 存在并打印字节数
   （词库是运行期依赖，缺了划词查义直接废掉，所以这里不允许放过）
3. 打 zip
4. 打印发布目录、zip 路径与体积汇总

任何一步失败都会给出**中文提示**并以非 0 退出码结束：
`1` 环境检查失败、`2` 发布失败、`3` 产物缺关键文件、`4` 打 zip 失败。

`publish/` 与 `*.zip` 都在 `.gitignore` 里，不会进版本库。

> **注意**：默认是**框架依赖**发布，目标机需要装 .NET 8 桌面运行时；
> 要连运行时一起发（体积更大、开箱即用），加 `-SelfContained` 再跑一次。

---

## 数据目录

所有用户数据都在 `%APPDATA%\LineTrans\`（设置页与关于页都有「打开数据目录」按钮）：

| 路径 | 内容 |
| --- | --- |
| `settings.json` | 设置（首次保存后生成） |
| `tray.json` | 托盘 / 全局热键 / 开机自启设置（与 `settings.json` 分开存，删掉只会让这几项回到默认值） |
| `docs\<id>.json` | 每篇文档一个文件（原文 + 全部译文 + 收藏 / 完成标记） |
| `wordbook.txt` | 我的词库（生词本） |
| `exports\` | 「导出到数据目录」落盘的位置 |
| `pc-app.log` | 运行日志（启动、词库加载、未处理异常） |
| `selftest-report.json` | `--selftest` 的自检报告（跑过自检才有） |

---

## 已知限制

1. **必须保留 `nuget.config`。**
   本机 `C:\Program Files (x86)\NuGet\Config\Microsoft.VisualStudio.FallbackLocation.config`
   指向了不存在的 Visual Studio 目录（`E:\Packages\Shared\NuGetPackages`）。
   如果不清空 `fallbackPackageFolders`，任何 restore 都会失败并报
   `NU1301: 本地源"E:\Packages\Shared\NuGetPackages"不存在`。
   仓库根目录的 `nuget.config` 做两件事：`<packageSources><clear/>` 只留 nuget.org，`<fallbackPackageFolders><clear/>`。

2. **只支持 x64。** `Platforms` 固定为 `x64`，`RuntimeIdentifier` 固定为 `win-x64`（Windows App SDK 自包含的要求）。

3. **不做 MSIX 打包。** `WindowsPackageType=None`，以 unpackaged 方式运行，双击 exe 即可。
   代价：没有开始菜单快捷方式与自动更新，需要自己分发（见 `tools\publish.ps1`）。

4. **首次构建体积大。** `WindowsAppSDKSelfContained=true` 会把整套 Windows App Runtime 复制到输出目录，
   发布目录约 140 MB（zip 后约 53 MB）。

5. **划词取词用双击，没有 hover。** 沿用「只读 / 可编辑 TextBox + 双击取词」的方案；
   CJK 没有词边界，双击会选中一整串汉字，本地词库通常查不到（会走 AI 兜底或提示未收录）。

6. **AI 功能需要自备 API Key。** 设置页填 BaseUrl / API Key / 模型后才能用单句与批量翻译；
   不填时本地功能（文档管理、切分、导出、离线划词查义）都照常可用。

7. **默认发布是框架依赖模式。** 目标机没装 .NET 8 桌面运行时会启动失败，
   加 `-SelfContained` 重新发布即可。

8. **全屏独占程序、以管理员权限运行的程序里拿不到选中文字。**
   这是 Windows 的 UIPI（用户界面特权隔离）限制：低完整性级别的进程无法向更高完整性级别的窗口
   发送输入、也读不到它的选区。普通窗口不受影响。

9. **未知剪贴板格式无法完整还原。** 全局划词只对「文本 / 文件 / 位图 / 空」四类做快照与还原；
   HTML 片段、富文本、程序自己注册的私有格式等无法完整复刻，此时会**跳过还原**并写日志
   （也就是说原内容会被取词用的 `Ctrl+C` 覆盖掉）。

10. **全局热键若被占用会自动回退，实际生效值以设置页显示为准。**
    设置里填的是「期望值」；屏幕上真正生效的组合是设置页「托盘状态」里显示的那个（回退顺序见「全局快捷键」）。

11. **托盘右键菜单与物理按键未做自动化验证。** `--selftest` 覆盖到了相同的消息路径
    （真的 `Shell_NotifyIcon`、真的 `RegisterHotKey`、真的往托盘窗口投递 `WM_HOTKEY`），
    但**没有模拟真实的物理按键**，也没有用 UI Automation 去点托盘右键菜单——
    这两项每次发版前需要人工过一遍。

---

## 许可

本项目使用 **GNU Affero General Public License v3.0 or later（AGPL-3.0-or-later）**，全文见 [LICENSE](LICENSE)。

**AGPL 第 13 条**：把本项目或其修改版作为网络服务提供给他人使用时，
必须让使用者能够获取对应源码。因此本端与另外两端一样，在「关于」页保留**三个源仓库入口**，
**不要移除**（`src/LineTrans.App/Views/AboutPage.xaml`）：

- 安卓客户端：<https://github.com/bityng/line-trans-android>
- 网页服务端：<https://github.com/bityng/line-trans-web>
- Windows PC 端：<https://github.com/bityng/line-trans-winui>

内置离线词库数据来自 ECDICT（MIT 许可）。**代码遵循 AGPL，词典数据部分仍遵循 MIT**，再分发时请一并保留这段说明。

Copyright (C) 2026 LineTrans contributors
