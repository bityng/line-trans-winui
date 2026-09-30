# 逐行翻译 · Windows PC 端（LineTrans WinUI）

用 **原生 WinUI 3** 重写的 Windows 桌面客户端。不使用 WebView2，不依赖 Node.js ——
所有界面都是 XAML 控件，直接调用 Windows App SDK。

> 当前进度：**第一批（工程骨架 + 导航壳 + 设计系统）**。
> 四个页面（文档 / 翻译 / 设置 / 关于）目前都是占位页，只显示标题与「此页正在开发中」。

---

## 这是什么

逐行翻译（LineTrans）是一套「逐行 / 逐句对照翻译」工具：把文章、小说、字幕按行或按句拆开，
人工逐条翻译（可用 AI 辅助），最后导出对照文本。

本项目是**第三个端**：

| 端 | 仓库 | 技术栈 |
| --- | --- | --- |
| 安卓客户端 | [bityng/line-trans-android](https://github.com/bityng/line-trans-android) | Kotlin + Jetpack Compose |
| 网页服务端 | [bityng/line-trans-web](https://github.com/bityng/line-trans-web) | Node.js 18+（零依赖） |
| **Windows PC 端（本仓库）** | — | C# + WinUI 3 + Windows App SDK |

设计系统与网页端保持视觉一致（主色 `#4d6bfe`、rgba 描边、8/12/16/24 圆角），
令牌定义在 `src/LineTrans.App/Themes/Colors.xaml`，与网页端 `public/style.css` 的 `:root` 变量一一对应。

---

## 目录结构

```
LineTrans-WinUI/
├── LineTrans.sln
├── nuget.config                  ← 必须保留，见「已知限制」
├── LICENSE                       ← AGPL-3.0，逐字复制自网页端
├── README.md
└── src/LineTrans.App/
    ├── LineTrans.App.csproj
    ├── app.manifest              ← PerMonitorV2 DPI 感知
    ├── App.xaml / App.xaml.cs    ← 应用入口（含后续挂载点注释）
    ├── MainWindow.xaml(.cs)      ← 自定义标题栏 + NavigationView 导航壳
    ├── Views/                    ← HomePage / TranslationPage / SettingsPage / AboutPage
    ├── Themes/                   ← Colors.xaml（设计令牌）/ Styles.xaml（排版与控件样式）
    └── Assets/                   ← 预留图标资源目录（当前为空）
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

# 还原 + 编译
dotnet build src\LineTrans.App\LineTrans.App.csproj -c Debug

# 或者走解决方案
dotnet build LineTrans.sln -c Debug
```

首次构建较慢（要下载 Windows App SDK 并复制自包含运行时，约 1 GB）。
之后增量构建很快。

### 产物位置

```
src\LineTrans.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\LineTrans.App.exe
```

---

## 如何运行

```powershell
# 方式一：dotnet run
dotnet run --project src\LineTrans.App\LineTrans.App.csproj

# 方式二：直接跑编译产物（推荐，启动更快）
.\src\LineTrans.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\LineTrans.App.exe
```

启动后：窗口标题「逐行翻译」，默认尺寸 1280×800，最小 900×600（按 DPI 缩放），
左侧导航栏四个入口 —— **文档 / 翻译 / 设置 / 关于**，默认停在「文档」。

### 发布

```powershell
dotnet publish src\LineTrans.App\LineTrans.App.csproj -c Release -r win-x64 --self-contained true
```

产物在 `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\`，整目录拷贝即可分发。

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
   代价：没有开始菜单快捷方式与自动更新，需要自己分发。

4. **首次构建体积大。** `WindowsAppSDKSelfContained=true` 会把整套 Windows App Runtime 复制到输出目录。

5. **四个页面目前是占位页。** 真实功能（文档列表、逐行/逐句翻译台、设置、划词查义）尚未实现。

6. **尚未接入 LineTrans.Core 类库。** `LineTrans.App.csproj` 里有一行被注释掉的
   `<ProjectReference>`，等类库落地后放开即可。

---

## 许可

本项目使用 **GNU Affero General Public License v3.0 or later（AGPL-3.0-or-later）**，全文见 [LICENSE](LICENSE)。

**AGPL 第 13 条**：把本项目或其修改版作为网络服务提供给他人使用时，
必须让使用者能够获取对应源码。因此本端与另外两端一样，在「关于」页保留源仓库入口，
正式实现「关于」页时**不要移除**。

两个源仓库：

- 安卓客户端：<https://github.com/bityng/line-trans-android>
- 网页服务端：<https://github.com/bityng/line-trans-web>

内置离线词库数据来自 ECDICT（MIT 许可）。**代码遵循 AGPL，词典数据部分仍遵循 MIT**，再分发时请一并保留这段说明。

Copyright (C) 2026 LineTrans contributors
