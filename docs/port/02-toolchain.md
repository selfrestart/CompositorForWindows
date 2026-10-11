# 02 · Windows 工具链与构建可行性

> **上游 revision**：`3081719d5178b466214860fe5fcf5691394dc558`（"CI: screenshots of the main screens"）
> **实测机器**：Windows 11 Home China，NT 10.0.26300.0，x64；C: 剩 98.7 GB，D: 剩 280.1 GB
> **实测日期**：2026-10-11
> **验证工程**：`spikes/c-kernel/`（可独立运行，不依赖 `src/` 被修改）
>
> 本文每条结论都标注 **【已实测】**（附真实命令与真实输出）或 **【未验证】**（说明原因）。
> 未标注的推断性陈述一律不使用。

---

## 0. 结论摘要（先读这一节）

**Wave 0（一个能加载 `.comp` 并渲染导出的最小骨架）能在当前这台机器上完成。**
两个前置条件，合计约 215 MB 下载量、不需要重启：

| # | 前置条件 | 成本 | 状态 |
|---|---|---|---|
| P0 | 一个 6 行 `nuget.config`，修掉机器级 NuGet 配置指向不存在路径的问题 | **0 下载** | **【已实测】已修复并验证** |
| P1 | 安装 .NET SDK 8（`Microsoft.DotNet.SDK.8`，官方 215.1 MB） | 215 MB | 命令已验证，安装未执行 |

**本任务最关键问题的答案（C 内核的 CoreGraphics 依赖）：**

> 任务描述里的前提 —— "C 里 CGContext 出现 60 次、CGImage 96 次" —— **数字是对的，但归属搞错了**。
> 我把它精确定位出来了：这两个数**恰好是 `Rendering\` 目录下 22 个 `.swift` 文件里
> "包含该符号的行数"**（`CGContext` = 60 行，`CGImage` = 96 行）。**它们不来自 `.c` 文件。**
>
> 实测：**9 个 `.c` 文件和 9 个 `.h` 文件里，`CGContext` = 0 次，`CGImage` = 0 次，`CGRect` = 0 次，
> `CGColor` = 0 次，`CF*` = 0 次，`#include <CoreGraphics…>` = 0 次。**
> 全部 CoreGraphics 用法都在 **Swift 调用层**（整个 `src\Compositor` 下 233 个 `.swift` 文件，
> `CGContext` 163 次 / `CGImage` 640 次）—— 而那一层本来就要被 C# 重写。
>
> **C 像素内核是一个纯粹的 buffer API**：`(const uint8_t *src, uint8_t *dst, size_t width, size_t height, size_t stride, …)`。
> 因此"C 源码直接编译复用"**不仅可行，而且是九成新的、几乎零成本的路径**。
> 唯一需要动手的地方是 **2 个文件里的 26 个 Clang block 字面量**（Apple 私有语法），不是 CoreGraphics。
>
> 这一点已用可运行的工程闭环验证：**9 个 `.c` 全部编译成 `.dll`（275,861 字节，导出 42 个入口），
> 并从 C# 用 P/Invoke 调用，10/10 项数值校验通过，包括对改写后的并行循环做逐字节比对。**

---

## 1. 环境复核与缺口清单

### 1.1 P0 阻塞项：机器级 NuGet 配置指向不存在的路径【已实测】

这是本机**唯一一个真正阻断所有 .NET 构建**的问题，而且它影响每一个 `dotnet build`，不只是本工程。

**症状**（`dotnet new console` 成功、`dotnet build` 失败）：

```text
error MSB4018: "ResolvePackageAssets" 任务意外失败。
NuGet.Packaging.Core.PackagingException: 无法找到回退包文件夹"D:\Dev C++\Microsoft VS\NuGetPackages"。
   at NuGet.Packaging.FallbackPackagePathResolver..ctor(String userPackageFolder, IEnumerable`1 fallbackPackageFolders)
   at Microsoft.NET.Build.Tasks.NuGetPackageResolver.CreateResolver(IEnumerable`1 packageFolders)
```

**根因**（已定位到具体文件，非猜测）：

```powershell
Get-Content 'C:\Program Files (x86)\NuGet\Config\Microsoft.VisualStudio.FallbackLocation.config'
```

```xml
<configuration>
  <fallbackPackageFolders>
    <add key="Microsoft Visual Studio Fallback Folder" value="D:\Dev C++\Microsoft VS\NuGetPackages" />
  </fallbackPackageFolders>
</configuration>
```

`D:\Dev C++\Microsoft VS\NuGetPackages` **不存在**（已实测 `Test-Path` 为 `False`）。这是 Dev-C++ 附带安装
Visual Studio 组件时留下的残骸：它注册了 fallback folder，但那个目录被删掉了。NuGet 的
`FallbackPackagePathResolver` 在构造时**不做存在性检查**，直接抛异常，于是构建死在解析阶段。

**修复**（**0 下载、不需要管理员权限、不需要重启**）：在工程目录放一个更近的 `nuget.config`。
NuGet 配置就近优先，`<clear />` 会清空累积的 fallback 列表。

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
</configuration>
```

**验证**（真实输出）：

```text
MSBuild version 17.6.3+07e294721 for .NET
已还原 ...\FixedApp.csproj (用时 218 ms)。
  FixedApp -> ...\bin\Debug\net7.0\FixedApp.dll
已成功生成。
    0 个警告
    0 个错误
BUILD_EXITCODE=0
=== dotnet run ===
Hello, World!
RUN_EXITCODE=0
```

修复前 `BUILD_EXITCODE=1`，修复后 `=0`，同一台机器、同一个 SDK、同一个模板。【已实测】

> **建议**：这个 `nuget.config` 必须进仓库根目录（或 `Directory.Build.props` 同级的解决方案目录），
> 否则每个新同事、每台新机器都会先撞一次这个墙。
> 彻底修法（需要管理员，可选）：删除或修正
> `C:\Program Files (x86)\NuGet\Config\Microsoft.VisualStudio.FallbackLocation.config` 中那一行。

### 1.2 对 Lead 环境简报的复核结果：3 处修正

| Lead 简报 | 复核结果 | 证据 |
|---|---|---|
| Windows 11 Home China，NT 10.0.26300.0 | ✅ 正确 | `[System.Environment]::OSVersion` + `Win32_OperatingSystem.Caption` |
| dotnet SDK 只有 7.0.304；运行时 6.0.18/7.0.7/8.0.14/10.0.1；WindowsDesktop 6/7/10 | ✅ 正确 | `dotnet --list-sdks` / `--list-runtimes` 逐条核对一致 |
| **没有** Visual Studio、MSBuild、Windows SDK、cl.exe、swift | ⚠️ **3 处需修正**，见下 | — |
| 网络：DNS fake-IP，git clone 与 TCP 443 正常 | ✅ 正确 | `github.com → 198.18.0.168` + clone 实测成功 |
| 缺 `swift` | ✅ 正确 | `Get-Command swift` / `swiftc` 均 NOT FOUND |

**修正 1（重要，且是正面的）：本机有可用的 C 编译器 —— `gcc`。**

```text
gcc => D:\tdm-gcc\bin\gcc.exe
gcc.exe (tdm64-1) 10.3.0        # x86_64-w64-mingw32
```

Lead 简报没有提到它。**这是"C 内核方案"能在这台机器上被真实端到端验证、而不是只能停留在纸面论证的原因。**
【已实测】

**修正 2：`C:\Program Files (x86)\Windows Kits` 目录存在，但里面只有上古遗留，没有现代 Windows SDK。**

```text
Get-ChildItem 'C:\Program Files (x86)\Windows Kits'  →
  8.1
  NETFXSDK

Test-Path 'C:\Program Files (x86)\Windows Kits\10'          → False
Get-ChildItem 'C:\Program Files (x86)\Windows Kits' -Recurse -Filter windows.h | Measure-Object
                                                            → Count = 0
```

"没有 Windows SDK"这个**结论正确**，但准确的表述是：`Windows Kits` 下**根本没有 `10` 目录**
（只有 Windows 8.1 时代的残留和 .NET Framework SDK），整棵树下**找不到一个 `windows.h`**。
后续脚本若用"`Windows Kits` 目录是否存在"来判断 SDK 有无，**会得到"有 SDK"的错误答案**；
应改为检查 `Windows Kits\10\Include\*\um\windows.h` 是否存在。【已实测】

**修正 3：MSBuild 并非"没有"，而是"只有随 SDK 附带的那个"。**

`Get-Command msbuild` 找不到（因为不在 PATH），但 `dotnet build` 实际使用的就是
`MSBuild version 17.6.3+07e294721 for .NET`，随 `C:\Program Files\dotnet\sdk\7.0.304` 分发。
准确的表述是：**没有独立的 / Visual Studio 的 MSBuild**（判断依据：`vswhere.exe` 不存在，见下）。【已实测】

**另外两项新增实测事实：**

* `C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe` **不存在** —— 这是"整机没有
  Visual Studio / Build Tools"的硬证据（任何 VS 安装都会创建它）。【已实测】
* `C:\Users\23717\.nuget\packages` **原本不存在**，在第一次成功 restore 后才被创建 ——
  说明本机从来没成功跑过一次需要包还原的构建。【已实测】

### 1.3 缺口清单

**已实测存在的工具**（无需安装）：`gcc 10.3.0`、`cmake`、`ninja`、`git 2.41.0`、`node`、`python 3.13`、
`dotnet SDK 7.0.304`、`winget v1.29.380`、`curl.exe`、`pip`。

下表只列**缺什么**。"体积"一列：**下载体积来自官方 `Content-Length`（已实测）**；
**安装后占用是估算**（见该列标注）。

#### A. Wave 0 必需

| 组件 | 为什么必需 | winget ID（**已用 `winget search --exact` 验证存在**） | 下载体积 | 安装后占用 | 重启 |
|---|---|---|---|---|---|
| NuGet 配置修复 | 否则**任何** `dotnet build` 都失败 | —（写文件，无需安装） | 0 | 0 | 否 |
| .NET SDK 8 | Wave 0 目标框架；见 §2 | `Microsoft.DotNet.SDK.8`（8.0.425） | **215.1 MB**（官方） | ~1.0 GB（估算） | 否 |

```powershell
winget install --exact --id Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
```

#### B. 后续 Wave 才需要（**Wave 0 不要装**）

| 组件 | 什么时候要 | winget ID（已验证） | 下载体积 | 安装后占用 | 重启 |
|---|---|---|---|---|---|
| Windows SDK 10.0.22621 | 生成 MSIX / 调 Win32 API / XAML 编译 | `Microsoft.WindowsSDK.10.0.22621`（10.0.22621.2428） | 1.3 MB 引导器（**官方**）；实际组件另计 | 3–5 GB（估算） | 建议重启 |
| VS 2022 Build Tools + VCTools | 用 **MSVC** 编译 C 内核（可选，见 §3.6） | `Microsoft.VisualStudio.2022.BuildTools`（17.14.41） | 4.3 MB 引导器（**官方**）；实际组件另计 | 4–7 GB（估算） | 建议重启 |
| Windows App Runtime 1.8 | 运行时框架依赖型部署（FDD）时随包分发 | `Microsoft.WindowsAppRuntime.1.8`（1.8.10） | **102.0 MB**（官方） | ~200 MB（估算） | 否 |
| Inno Setup 6 | 传统 `.exe` 安装包（见 §5.1） | `JRSoftware.InnoSetup`（6.7.3） | **10.1 MB**（官方） | ~30 MB（估算） | 否 |
| WebView2 Runtime | 只有用 WebView2 才要 | `Microsoft.EdgeWebView2Runtime`（155.0.4283.45） | — | — | 否 |

精确安装命令（**后续 Wave 按需执行，不要现在跑**）：

```powershell
# Windows SDK
winget install --exact --id Microsoft.WindowsSDK.10.0.22621 --accept-package-agreements --accept-source-agreements

# VS Build Tools：只要 C++ 编译能力，用 --override 精确裁剪，避免装成完整 VS
winget install --exact --id Microsoft.VisualStudio.2022.BuildTools `
  --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended" `
  --accept-package-agreements --accept-source-agreements

# 后续按需
winget install --exact --id Microsoft.WindowsAppRuntime.1.8 --accept-package-agreements --accept-source-agreements
winget install --exact --id JRSoftware.InnoSetup          --accept-package-agreements --accept-source-agreements
```

> **注意 `Microsoft.WindowsAppSDK` 不是一个 winget 包。** 用
> `winget search --exact --id Microsoft.WindowsAppSDK` 实测返回"找不到与输入条件匹配的程序包"。
> **Windows App SDK（开发用的 SDK）是 NuGet 包**，通过 `<PackageReference>` 引入；
> winget 上的 `Microsoft.WindowsAppRuntime.*` 装的是**运行时**（终端用户机器上要有的那部分）。
> 这两个名字很容易混，写脚本时别搞错。【已实测】

### 1.4 安装判定命令（可复制粘贴，逐条给出期望值）

```powershell
# 1) NuGet 配置修复是否生效：期望 BUILD_EXITCODE=0
dotnet new console -o $env:TEMP\proof -n Proof --force
Push-Location $env:TEMP\proof
@{ '<?xml version="1.0" encoding="utf-8"?><configuration><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>' } |
  Set-Content nuget.config -Encoding UTF8
dotnet build -v:m; Write-Host "BUILD_EXITCODE=$LASTEXITCODE"; Pop-Location

# 2) .NET SDK 8 是否装上：期望看到 8.0.4xx 这一行
dotnet --list-sdks

# 3) 能否 target net8.0：期望 NET8_EXIT=0（现在是 NETSDK1045 报错，见 §2.1）
dotnet new console -o $env:TEMP\t8 -n t8 --force -f net8.0
Push-Location $env:TEMP\t8; dotnet build -v:m; Write-Host "NET8_EXIT=$LASTEXITCODE"; Pop-Location

# 4) MSVC 是否可用：期望打印 vswhere 路径（当前为"不存在"）
Test-Path "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

# 5) Windows SDK 是否可用（注意：不能只判断 Kits 目录是否存在，见 §1.2 修正 2）
Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\Include' -ErrorAction SilentlyContinue

# 6) C 编译器是否可用：期望 gcc 版本行
gcc --version
```

---

## 2. 目标框架选型与 WinUI 3 前置条件

### 2.1 决定性实测：装了运行时 ≠ 能编译

Lead 简报里"运行时含 NETCore.App 8.0.14 / 10.0.1"容易被读成"能跑 net8.0/net10.0"。**不能编译。**
用一个真实工程实测（已配好 1.1 的 nuget.config，排除了 NuGet 干扰）：

```powershell
# net8.0 工程，用 SDK 7.0.304 构建
dotnet build -v:m
```

```text
error NETSDK1045: 当前 .NET SDK 不支持将 .NET 8.0 设置为目标。
                   请将 .NET 7.0 或更低版本设置为目标，或使用支持 .NET 8.0 的 .NET SDK 版本。
NET8_BUILD_EXIT=1   （耗时 0.7 s）
```

`net10.0` 同样结果：

```text
error NETSDK1045: 当前 .NET SDK 不支持将 .NET 10.0 设置为目标。
NET10_BUILD_EXIT=1
```

**【已实测】结论：SDK 与运行时是两个独立的门槛；运行时再多也不能把 `TargetFramework` 提到 SDK 之上。**
要用 net8.0，**必须装 .NET SDK 8**，215 MB，没有捷径（NuGet 拉 `Microsoft.NETCore.App.Ref` 也不会解锁，
因为拦截发生在 `Microsoft.NET.TargetFrameworkInference.targets` 的 SDK 版本检查处，早于包还原）。

### 2.2 三个选项

| 选项 | 代价 | 评价 |
|---|---|---|
| **A. 留在 net7.0** | 0 | ❌ 不建议。.NET 7 **已结束支持**（2024-05 EOL，非 LTS），没有安全更新；WinUI 3 新版已要求 net8.0+；且这台机器已装 net8 运行时，装 SDK 后可直接跑 |
| **B. 装 .NET SDK 8 → net8.0（LTS）** | 215 MB，无需重启 | ✅ **推荐**。LTS 支持到 2026-11；Windows App SDK 1.6/1.7/1.8 与 Velopack 都以 net8.0 为主力目标；本机已有 8.0.14 运行时 |
| **C. 装 .NET SDK 10 → net10.0** | 约同量级 | ⚠️ 可选。本机有 10.0.1 运行时，`Microsoft.DotNet.SDK.10` 在 winget 上存在（10.0.401）。但 .NET 10 是较新的 LTS，第三方生态（含部分 Windows App SDK 版本）的 net10.0 目标支持面**未验证** |

### 2.3 推荐

**`net8.0`（选项 B）**，具体：

* 主程序：`net8.0-windows10.0.19041.0`（WinUI 3 所需的最低 Windows 目标版本）
* 内核互操作层（若拆成独立程序集）：`net8.0`
* 保留 `net7.0` 仅作过渡期回退，不作长期目标

判定命令见 §1.4 第 3 条。

### 2.4 WinUI 3（Windows App SDK）前置条件

**【已实测】能确认的：**

* `Microsoft.WindowsAppSDK` 是一个 **NuGet 包**，不是 winget 包。实测 `dotnet restore` 一个引用
  `Microsoft.WindowsAppSDK 1.5.240802000` 的工程**成功**（13.62 s，`RESTORE_EXIT=0`），
  证明包源可达、包存在。**但 restore 成功 ≠ 能构建**（构建还需要匹配的 SDK 和 Windows SDK）。
* Windows App Runtime 的 winget 包存在：`Microsoft.WindowsAppRuntime.1.8`（1.8.10）、
  `Microsoft.WindowsAppRuntime.2`（2.4.0）等，官方下载体积 102.0 MB。
* 本机**没有** Windows SDK 的 `10\` 目录、**没有** MSVC。

**【未验证】—— 声明清楚：**本机无法端到端验证 WinUI 3 构建，因为
（a）WinUI 3 需要 net8.0+ 而本机 SDK 只到 7.0.304；（b）没有 Windows SDK。
所以下面给的是**基于组件依赖关系的判断，不是实测结论**：

| 前置条件 | 是否必须 | 说明 |
|---|---|---|
| .NET SDK 8+ | **必须** | 见 §2.1 实测 |
| `Microsoft.WindowsAppSDK` NuGet 包 | **必须** | 提供 WinUI 3 的 C#/WinRT 投影与构建目标 |
| `Microsoft.Windows.SDK.BuildTools` NuGet 包 | 通常随 WindowsAppSDK 传递引入 | 提供 `makepri`/`makeappx` 等打包工具 |
| Windows SDK（`Microsoft.WindowsSDK.10.0.22621`） | 打包 MSIX / 用 C++ 互操作时需要 | 若只 `dotnet build` 一个非打包 WinUI 3 应用，很多情况下 NuGet 包已足够 |
| VS 2022 Build Tools（`VCTools`） | **不是 WinUI 3 的必需项** | 只有当项目含 C++ 组件时才是 |
| 完整的 Visual Studio IDE | **不必须** | IDE 提供 XAML 设计器与调试器，但命令行 `dotnet build` 可以独立构建 |

> 换言之：**"必须装完整 VS"这个常见说法不成立**；但"完全不装任何东西就能构建 WinUI 3"也不成立。
> 最小集是 **.NET SDK 8 + Windows App SDK NuGet（+ 打包时补 Windows SDK）**。

---

## 3. C 像素内核链接方案论证（本任务最关键的一节）

### 3.1 结论

**推荐方案 A 的一个变体（记为 A′，见 §3.6）：把 C 源码直接编译成本地库，用 P/Invoke 调用。**
理由不是"CoreGraphics 依赖集中在边界"，而是更强的一条：

> **CoreGraphics 依赖根本不存在于 C 内核里。** 一处都没有。

因此 §3 原本要回答的"CoreGraphics 是集中在入口还是渗透进循环"这个问题，
**在这 9 个文件上不成立 —— 它是 0，不是"集中"**。任务描述里的 60/96 次出现在 Swift 文件中。

### 3.2 证据一：CoreGraphics 符号在 `.c` / `.h` 中的出现次数为 0

对 `src\Compositor\Compositor\Rendering\` 逐文件精确计数（原始子串出现次数，非行数）：

| 文件 | 行数 | `CGContext` | `CGImage` | `CGRect` | `CGColor` | `CGSize` | `CGBitmapContext` | `CGColorSpace` | `CFRelease`/`CFRetain` |
|---|---|---|---|---|---|---|---|---|---|
| AdjustPixels.c | 1379 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| DitherPixels.c | 546 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| HealPixels.c | 259 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| WandPixels.c | 205 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| ContentFill.c | 93 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| LevelsPixels.c | 60 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| BrushPixels.c | 50 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| NoisePixels.c | 49 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| LensPixels.c | 37 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| **合计** | **2678** | **0** | **0** | **0** | **0** | **0** | **0** | **0** | **0** |

9 个 `.h` 文件同样全部为 0（合计 301 行）。`#include <CoreGraphics…>` 在所有 `.c`/`.h` 中出现 **0 次**。

**"60 / 96" 的真实出处（已精确定位，不是猜测）：**

我把每个可能范围都数了一遍，最后找到了完全吻合的组合 —— **它是行数，而且来自 `.swift` 文件**：

| 统计范围 | 口径 | `CGContext` | `CGImage` |
|---|---|---|---|
| `Rendering\*.c` + `*.h`（9+9 个文件，2979 行） | 原始子串次数 | **0** | **0** |
| **`Rendering\*.swift`（22 个文件）** | **包含该符号的行数** | **60** | **96** |
| ↑ 同一范围，两者并集 | 包含任一符号的行数 | 144 | 144 |
| `Rendering\*.swift` | 原始子串次数 | 52 | 105 |
| 整个 `src\Compositor` 下 `.swift`（233 个文件） | 原始子串次数 | 163 | 640 |
| 整个 `src\Compositor` 下 `.md` | 原始子串次数 | 0 | 1 |

**第 2 行的 60 / 96 与任务描述里的"60 次 / 96 次"完全一致**，误差为 0。
也就是说：**这两个数字是真实测量值，但被归到了错误的文件类型上** —— 量的是
`Rendering\` 目录（`.c` 和 `.swift` 混放）里 `.swift` 文件的行数，却被记成了"C 里"。

**这一点不改变结论，反而让结论更硬**：
"CoreGraphics 集中在入口还是渗透进循环"这个问题，**在 C 内核上不成立，因为它的值是 0**
（直接数出来的，且用"逐文件 `-Filter`"与"全树按扩展名分组"两种独立方法交叉验证一致，见 §4.3 第 2 条教训）。
CoreGraphics 完全活在 Swift 调用层，而那一层是本来就要被 C# 重写的部分。
换句话说：**CoreGraphics 的移植成本不在内核里，在视图/渲染编排层里** —— 那是 Wave 1+ 的
"用 Skia/Composition 重写绘制层"工作，与"能不能直接复用 C 内核"这个问题无关。【已实测】

### 3.3 证据二：C 内核真实的依赖面 —— 只有标准头 + GCD

所有 `#include` 的完整清单（这就是整个内核的外部依赖）：

```text
#include <stdint.h>            # 9 个 .h 全部
#include <stddef.h>            # 9 个 .h 全部
#include <math.h>              # AdjustPixels / DitherPixels / HealPixels / LensPixels / LevelsPixels / NoisePixels
#include <stdlib.h>            # AdjustPixels / ContentFill / DitherPixels / HealPixels / WandPixels
#include <string.h>            # AdjustPixels / ContentFill / DitherPixels / HealPixels / WandPixels
#include <dispatch/dispatch.h> # 仅 2 个文件：AdjustPixels.c:3、DitherPixels.c:5
#include "XxxPixels.h"         # 各自的同名声明头
```

`vDSP` / `simd_` / `NSUInteger` / `CF*` / `objc_` / `__attribute__` —— **全部 0 次**。

**整个内核唯一的 Apple 平台依赖，就是 `dispatch/dispatch.h`（GCD），出现在 2 个文件里，共 4 处直接调用。**

### 3.4 证据三：真正要动手的是 26 个 Clang block 字面量

GCD 的 `dispatch_apply` 在这些文件里用 **Clang blocks**（`^(…) { … }`）传并行体。这是 Clang 私有扩展，
**MSVC 的 `cl.exe` 和 GCC 都不支持**。逐文件精确计数：

| 文件 | 行数 | block 字面量 `^(` | block 形参声明 `(^name)` | `__block` 变量 | 直接 `dispatch_apply` | 经 `in_bands` 传递 |
|---|---|---|---|---|---|---|
| **AdjustPixels.c** | 1379 | **16** | 1（:9） | 0 | 3（:11, :348, :386） | 13 |
| **DitherPixels.c** | 546 | **10** | 1（:10） | 3（:359, :400, :526） | 1（:12） | 9 |
| 其余 7 个文件 | 753 | **0** | 0 | 0 | 0 | 0 |
| **合计** | 2678 | **26** | 2 | 3 | 4 | 22 |

**关键结构性事实：26 个 block 中有 22 个是喂给同一个 `in_bands()` 辅助函数的**，
而这个辅助函数在 2 个文件里各只有一份定义（`AdjustPixels.c:9`、`DitherPixels.c:10`）：

```c
// AdjustPixels.c:9 与 DitherPixels.c:10，两者几乎相同
static void in_bands(size_t count, void (^body)(size_t start, size_t end)) {
    size_t bands = count < 64 ? 1 : 32, size = (count + bands - 1) / bands;
    dispatch_apply(bands, DISPATCH_APPLY_AUTO, ^(size_t band) {
        size_t start = band * size, end = start + size < count ? start + size : count;
        if (start < end) body(start, end);
    });
}
```

**即：需要移植的并行面不是 26 个散点，而是"2 个辅助函数 + 4 个直接调用点 + 22 处 block→具名函数的机械改写"。**

### 3.5 已实测的端到端闭环（不是纸面论证）

`spikes/c-kernel/` 里做了一个能跑的完整验证：

1. **`port-clang-blocks.py`** —— 机械源码变换：block 字面量 → GCC 嵌套函数（放在语句表达式里，
   保留词法捕获语义）；block 形参声明 → 函数指针声明；删除 `__block` 限定符。**只动语法，不动一句算术。**
2. **`shim/dispatch/dispatch.h`** —— 覆盖 `dispatch_apply` + `DISPATCH_APPLY_AUTO` 的最小 shim，
   串行执行。因为 `in_bands` 划分的 band 区间**互不重叠且完整覆盖**，串行与并行结果**逐位相同**。
3. **`build-kernel.ps1`** —— 用本机 `gcc 10.3.0` 把 9 个 `.c` 编成 `compositor_kernel.dll`。
4. **`KernelProbe/`** —— C# 控制台工程，P/Invoke 调用并做数值校验。

**真实输出（构建）：**

```text
=== [1/3] port Clang blocks -> plain C (AdjustPixels.c, DitherPixels.c) ===
AdjustPixels.c     PORTED (1 block declarations, 16 block literals rewritten)
DitherPixels.c     PORTED (1 block declarations, 10 block literals rewritten)
BrushPixels.c      SKIP   (no blocks; compiled verbatim from csrc/)
ContentFill.c      SKIP   (no blocks; compiled verbatim from csrc/)
HealPixels.c       SKIP   (no blocks; compiled verbatim from csrc/)
LensPixels.c       SKIP   (no blocks; compiled verbatim from csrc/)
LevelsPixels.c     SKIP   (no blocks; compiled verbatim from csrc/)
NoisePixels.c      SKIP   (no blocks; compiled verbatim from csrc/)
WandPixels.c       SKIP   (no blocks; compiled verbatim from csrc/)

=== [2/3] compile with gcc ===
compiler: D:\tdm-gcc\bin\gcc.exe
gcc.exe (tdm64-1) 10.3.0
  AdjustPixels.c     OK  (ported  )
  BrushPixels.c      OK  (verbatim)
  ContentFill.c      OK  (verbatim)
  DitherPixels.c     OK  (ported  )
  HealPixels.c       OK  (verbatim)
  LensPixels.c       OK  (verbatim)
  LevelsPixels.c     OK  (verbatim)
  NoisePixels.c      OK  (verbatim)
  WandPixels.c       OK  (verbatim)
  exporting 42 kernel entry points
  -> D:\Compositor For Windows\spikes\c-kernel\build\bin\compositor_kernel.dll  (275,861 bytes)
```

42 个导出名已逐个在 DLL 导出表中核对存在（`adjust_gradient_map`、`dither_apply`、`lens_distort`、
`wand_mask`、`spot_heal` … 见 `build/bin/compositor_kernel.def`）。

**真实输出（C# P/Invoke 数值校验）：**

```text
KernelProbe -- Compositor C pixel kernel on Windows via P/Invoke
runtime : .NET 7.0.7
process : X64
loaded  : ...\build\bin\compositor_kernel.dll
  [PASS] lens_distort k=0 is an exact identity
         64x48 RGBA, 12288 bytes compared, 0 differing bytes (expected 0)
  [PASS] lens_distort k=0.35 resamples and pins the centre pixel
         moved 2872/3072 px, alpha-out-of-range 0, centre unchanged=True
  [PASS] levels_apply matches an independent C# reference exactly
         3072 channel writes, 0 mismatched.
  [PASS] dither_quantize16 (ported band loop) matches reference byte-for-byte
         12600 bytes compared, 0 mismatched.
  [PASS] camera_raw_statistics (32-band GCD path) matches single-loop reference
         h=90 across 32 bands; got [0.837197814637, 0.694278947660, 0.736555596729]; max |diff| = 3.109E-015
  [PASS] brush_alpha_bounds finds [left,top,right,bottom]
         got [4,3,12,8], expected [4,3,12,8] (right/bottom are exclusive)
  [PASS] layer_extract_alpha copies the alpha plane
         240 bytes, 0 mismatched
  [PASS] layer_restore_alpha rescales by the alpha plane
         960 bytes checked, 0 mismatched
  [PASS] dither_glow is deterministic and never exceeds alpha
         nondeterministic px=0, channel>alpha=0, channels changed=2999
  [PASS] content_fill executes and reports a coherent result
         returned 0 (smoke test: no independent reference written)

==== 10 passed, 0 failed ====
```

其中两项是**针对改写后的并行路径**的精确校验，含金量最高：

* `dither_quantize16` 走 `DitherPixels.c` 改写后的 `in_bands`，与用 C# 独立重写的参考实现
  （含逐位复刻的 `hash_noise`）**12600 字节逐字节相同**。
* `camera_raw_statistics` 走 `AdjustPixels.c` 改写后的 `dispatch_apply(32 bands)`，
  与单循环参考实现的最大偏差 **3.109e-015**（即双精度舍入级），证明 32 个 band 的区间
  **既不重叠也不遗漏**（`h=90` 特意选了不能被 32 整除的值）。

**第 4 项验证：变换是无损的（round-trip 证明）。**

`verify-transform-roundtrip.py` 把生成文件里的每个嵌套函数**逆向还原**成 block 字面量，
再与 `src/` 中的原始文件逐字节比较：

```text
AdjustPixels.c     16 block literals + 1 block declarations inverted -> BYTE-IDENTICAL to original
DitherPixels.c     10 block literals + 1 block declarations inverted -> BYTE-IDENTICAL to original

ROUND TRIP: PASS -- pixel math unchanged
```

**这证明变换没有改动任何一个算术表达式、循环边界或注释**，只有闭包写法变了。

### 3.6 方案对比

| 方案 | 内容 | 实测状态 | 评价 |
|---|---|---|---|
| **A. MSVC 编静态库/DLL + P/Invoke** | `cl.exe` 编译 9 个 `.c` | **未验证**（本机无 MSVC） | ❌ 有个隐藏坑：**`cl.exe` 不支持 Clang blocks**，所以 §3.5 的嵌套函数变换产物**也不能给 cl.exe 用**（嵌套函数是 GCC 扩展）。走 MSVC 需要把 26 处 block 改成"文件级静态函数 + 上下文结构体" |
| **A′. MinGW/Clang 编 DLL + P/Invoke** | `gcc` 编译 → DLL → C# P/Invoke | ✅ **已实测通过（10/10）** | ✅ **推荐**。本机已具备，零安装成本 |
| **A″. clang-cl + `-fblocks`** | 保留 block 语法**零改写**编译 | **未验证**（本机无 clang） | ⚠️ 理论上最省事（2 个文件一字不改），但需要 LLVM + BlocksRuntime + libdispatch shim，依赖面反而更大 |
| **B. `/clr` 或改写为 unsafe C#** | 用 C# 重写像素循环 | **未验证** | ❌ **不建议**。理由见下 |

**为什么不推荐 B（改写为 unsafe C#）：**

1. **风险不对称。** 内核里有误差扩散抖动（`DitherPixels.c` 的 Atkinson/Floyd 卷积）、sRGB 往返、
   双线性重采样。这些对浮点舍入顺序、`float` vs `double` 的选择、`lroundf` 的"半数远离零"语义极其敏感
   —— 我们在 Mac 上**没有黄金测试向量**可比对，改写引入的漂移会静默改变画质。
   保留 C 源码是保住行为等价的最短路径。
2. **收益为零。** §3.2 已证明 C 内核没有任何 CoreGraphics 耦合，所以改写**换不来**任何依赖上的收益。
   它只是把已验证的 C 重写成未验证的 C#。
3. **`/clr` 更差。** 给纯 C 加 `/clr` 会让每个函数都可能被托管层介入，带来 GC 交互与性能不确定性，
   而 P/Invoke 本来就能直接调用同一个 DLL —— 没有任何理由用 `/clr`。
4. **方案 A′ 的成本已经测得**：7/9 文件**零改动**；2/9 文件共 26 处机械改写，
   且改写正确性已被 round-trip + 数值校验双重证明。

### 3.7 推荐落地路径

1. **7 个文件（753 行）零改动**，直接从 `src/` 引用编译（`-std=gnu11`，见下）。
2. **2 个文件按 §3.5 的方式改 26 处**：
   * 若继续用 MinGW/GCC：`port-clang-blocks.py` 已可直接产出可用源码（**已实测**）。
   * 若要用 MSVC：把嵌套函数换成 `typedef void (*band_fn)(void *ctx, size_t first, size_t last);`
     + 每个 block 对应的文件级静态函数 + 一个小上下文结构体。这是同一个变换的另一种落点，
     **本机因无 MSVC 无法实测**，但改动点数量与位置完全相同（26 处）。
3. **注意一个已实测的编译细节**：`HealPixels.c:168` 使用 `M_PI`。严格 `-std=c11` 下 GCC 会报
   `error: 'M_PI' undeclared`；改用 `-std=gnu11`（或 MSVC 下定义 `_USE_MATH_DEFINES`）即可。
   这不是代码缺陷，是 POSIX/ANSI 可见性差异。**MSVC 同样需要 `_USE_MATH_DEFINES`**，
   否则会在同一行失败。【已实测（GCC 侧）；MSVC 侧为依据其头文件行为的推断，未验证】

---

## 4. 出网与编译实测

### 4.1 出网：三条独立路径全部打通

**（a）`git clone`（真实克隆，不是 DNS 探测）**

```powershell
git clone --depth 1 --filter=blob:none --no-checkout https://github.com/dotnet/samples.git samples-probe
```

```text
EXITCODE=0        ELAPSED_SEC=2.2
git count-objects -v → in-pack: 2567, packs: 1, size-pack: 429 (KB)
git log --oneline -1  → acb39ce update action location and use full commit SHA (#7138)
```

DNS 走 fake-IP 的旁证：`Resolve-DnsName github.com` → `198.18.0.168`（A 记录）、`2001:2::a3`（AAAA）。
但 **TCP 与 TLS 真实可用**：2567 个对象、429 KB pack 落盘，HEAD 可读。【已实测】

**（b）NuGet 包还原（真实包，真实运行）**

```powershell
# 引用 Newtonsoft.Json 13.0.3 的 net7.0 工程
dotnet build -v:m
```

```text
已还原 ...\nugetprobe.csproj (用时 3.72 sec)。
nugetprobe -> ...\bin\Debug\net7.0\nugetprobe.dll
已成功生成。
NUGET_BUILD_EXIT=0        ELAPSED_SEC=6.0
=== run ===
nuget ok: {"a":1}
NUGET_RUN_EXIT=0
```

`C:\Users\23717\.nuget\packages\newtonsoft.json` 在还原后出现。【已实测】

**（c）pip**

```powershell
python -m pip download --no-deps --dest <dir> six
```

```text
Saved ...\six-1.17.0-py2.py3-none-any.whl (11 kB)
Successfully downloaded six
PIP_EXIT=0
```

**【已实测】出网完全正常。** 另外确认了 Lead 的判断：宿主 `web_fetch` 工具被 SSRF 策略拒绝是
**工具层限制**，与网络无关 —— 命令行工具（git / curl / dotnet / pip / winget）全部可用。

### 4.2 .NET 构建与运行

见 §1.1（修复后 `BUILD_EXITCODE=0`、`RUN_EXITCODE=0`，输出 `Hello, World!`）。
整个过程中唯一失败的是机器级 NuGet 配置，修掉之后一次通过。【已实测】

### 4.3 C 内核编译与 C# 互操作

见 §3.5。**9/9 编译成功，42/42 导出符号存在，10/10 数值校验通过。** 【已实测】

**过程中真实踩到并解决的坑**（原样记录，这些是可复现的）：

1. `-std=c11` 下 `HealPixels.c:168` 的 `M_PI` 未声明 → 改 `-std=gnu11`。
2. PowerShell `Get-ChildItem -Include` 在未加 `-Recurse` 且路径无通配符时**静默返回空**，
   导致第一次"扫描 include"得到假阴性（0 个 include）。改用 `-Filter` / `Where-Object` 后得到真实结果。
   **教训：本仓库里任何"计数为 0"的结论都必须用两种独立方法交叉验证** —— 这也是 §3.2 的 0
   之所以可信的原因（逐文件 `-Filter` 计数与全树分扩展名计数两种方法一致）。
3. PowerShell 中 `$ErrorActionPreference='Stop'` 会把**编译器写到 stderr 的普通 warning**
   变成终止性错误，导致构建在一条警告上中断。脚本内已改为 `'Continue'` 并显式检查 `$LASTEXITCODE`。
4. PowerShell 的逗号运算符优先级高于 `+`，`@($a,'-Wl,--out-implib,' + $p)` 会被拆成 3 个参数，
   导致链接器把 implib 路径当成输入文件。已改为单个插值字符串。
5. NuGet 的 `nuget.config` 若 XML 注释里含 `--`，会直接报
   `NuGet.Config 不是有效的 XML`。已改写注释。

---

## 5. 构建 / 打包 / 签名 / 更新链条

> 本节以**方案推荐**为主，除标注处外均为**未验证**（本机没有 Windows SDK / 签名证书 / CI 凭据，
> 无法端到端执行打包与签名）。已实测的只有"包/工具存在性"。

### 5.1 安装包：推荐 Velopack，MSIX 备选，Inno Setup 兜底

| 方案 | 优势 | 代价 | 建议 |
|---|---|---|---|
| **Velopack** | 安装 + **自动更新**一体；增量更新；`dotnet tool install -g vpk` 即得 CLI | 非微软官方方案 | ✅ **推荐主选**。已实测 NuGet 上存在 `vpk` 1.2.161（下载量 153 万） |
| MSIX | 微软官方，支持 Store、干净的安装/卸载、包身份 | 需要 Windows SDK + 签名；对"图像编辑器"这类需要任意文件系统访问的应用，包虚拟化会带来额外适配 | ⚠️ 备选（若要走 Microsoft Store 则必选） |
| Inno Setup | 极成熟，脚本可控，体积小 | 需自己接自动更新 | ✅ 兜底。已实测 `JRSoftware.InnoSetup` 6.7.3 存在，官方 10.1 MB |

**推荐组合：Velopack（主）+ Inno Setup（备用/企业分发）**，MSIX 仅在需要 Store 上架时启用。

### 5.2 代码签名与 SmartScreen

**【未验证】** 无证书，无法实测。

* **只买一张证书的话：买 OV/EV 都行，但优先 EV** —— EV 证书配合微软的信任链可以**立刻**消除
  SmartScreen 警告；OV 证书需要累积下载信誉（数周到数月，取决于下载量）。
* 2023 年后 CA/B Forum 要求**私钥必须存放在硬件令牌或云 HSM（如 Azure Trusted Signing）**，
  不能是磁盘上的 `.pfx`。这会直接影响 CI 设计：CI 里不能直接读 `.pfx`，要走
  云签名服务（**Azure Trusted Signing**，按签名次数计费）或自建 HSM 签名机。
* CI 中签名的实际形态：`signtool.exe sign /fd SHA256 /tr <RFC3161 时间戳 URL> /td SHA256 <file>`
  —— `signtool` 来自 Windows SDK，所以**签名环节依赖 §1.3 的 Windows SDK**。
* SmartScreen 对**首次发布**的新签名普遍仍需时间；EV 能显著缩短但不能保证当天清零。
  这属于运营风险，不是技术阻塞。

### 5.3 自动更新

* 首选 **Velopack 内建更新**（自带 delta、分阶段推送、回滚）。
* 若走 MSIX：用 **App Installer / `.appinstaller` 自动更新**，但要求应用以 MSIX 分发。
* 若走 Inno Setup：需要自建更新器（如 **NetSparkle**）或自研。
* 上游 macOS 版有 `appcast.xml`（Sparkle 风格），**Windows 侧不要复用这个文件格式**，
  两者更新机制无关。

### 5.4 CI（GitHub Actions）

**【未验证】** 仓库当前 CI 是 macOS 构建（`.github/` 下是 Xcode 流程），未在本机执行过 Windows runner。

推荐工作流轮廓：

```yaml
name: windows
on: [push, pull_request]
jobs:
  build:
    runs-on: windows-latest        # 自带 VS 2022 + Windows SDK + .NET SDK，无需自建 runner
    steps:
      - uses: actions/checkout@v4
      # 机器级 NuGet 问题在干净 runner 上不存在，但本地开发机有；
      # 仓库里保留 nuget.config 可让两条路径行为一致
      - run: dotnet build -c Release
      - run: dotnet test  -c Release
      - run: pwsh ./spikes/c-kernel/build-kernel.ps1   # 原生内核构建 + 校验
      - run: pwsh ./spikes/c-kernel/run-spike.ps1
```

要点：
* `windows-latest` 镜像**已包含** Visual Studio 2022、Windows SDK 与 .NET SDK，
  所以 CI 上不需要执行 §1.3 的 winget 安装步骤 —— 那些是**本机开发环境**的缺口，不是 CI 的。
* `windows-latest` 上的 MSVC 可用 ⇒ CI 可以编译 §3.7 里 MSVC 版本的 26 处改写，
  这一点本机做不到。**建议把 MSVC 编译路径的首次验证放到 CI 上做。**
* 签名步骤需要密钥材料，应用 GitHub Environments + OIDC 接云签名服务，不要把证书塞进 Secrets。

---

## 6. 明确结论：Wave 0 能否在当前这台机器上完成

### 能。但必须先补 2 个前置条件。

| 判定项 | 结论 | 依据 |
|---|---|---|
| 加载 `.comp`（解析 JSON/清单/图像） | ✅ **可做** | 纯 .NET 逻辑，SDK 7 或 8 均可 |
| 像素渲染（图层合成、调整、抖动、修复） | ✅ **可做，且已被验证** | §3.5：9 个 C 内核编译成 DLL，C# P/Invoke 10/10 校验通过 |
| 导出图像 | ✅ **可做** | 需要 PNG/JPEG 编解码，纯 .NET 生态（如 SkiaSharp / ImageSharp），走 NuGet —— 出网已实测可用 |
| 图形界面 | ⚠️ **需要补 SDK** | 见下 |
| 打包安装程序 | ❌ **当前不能** | 缺 Windows SDK / 签名方案（属 Wave 1+，不阻塞 Wave 0） |

### 最小阻塞项（按优先级）

1. **【必须，0 下载】机器级 NuGet 配置修复** —— 不修，**任何** `dotnet build` 都失败（§1.1，已实测修复有效）。
2. **【必须，215 MB】安装 .NET SDK 8** —— 不装，就只能停在 net7.0（已 EOL），且无法构建 WinUI 3（§2.1，已实测 `NETSDK1045`）。

```powershell
# 第 1 项：在任何解决方案目录放 nuget.config（内容见 §1.1）
# 第 2 项：
winget install --exact --id Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
```

### 装什么能解开什么

| 想做的事 | 需要装 | 体积 | 现在能不能 |
|---|---|---|---|
| Wave 0：加载 `.comp` + 光栅渲染 + 导出（**无 GUI**） | 只需上面 2 项 | 215 MB | ✅ **现在就能**（C 内核已验证） |
| Wave 0 + GUI（WinUI 3） | 上面 2 项 + `Microsoft.WindowsAppSDK` NuGet | 215 MB + ~100 MB | ⚠️ 装完 SDK 8 后即可尝试 |
| MSIX 打包 | + Windows SDK | +3–5 GB | ❌ 需先装 SDK |
| MSVC 编译 C 内核（替代 MinGW） | + VS Build Tools (VCTools) | +4–7 GB | ❌ 但**非必需** —— MinGW 路径已验证可用 |
| 代码签名发布 | + EV/OV 证书 + 云 HSM | — | ❌ 需采购 |

**一句话**：**这台机器现在就能做完 Wave 0 的无 GUI 最小骨架**（C 内核的编译与互操作已经跑通），
**唯一真正拦路的是一个 6 行的配置文件和一次 215 MB 的 SDK 安装。**

---

## 附录 A：`spikes/c-kernel/` 使用说明

```text
spikes/c-kernel/
├── README.md                        # 工程说明与快速开始
├── csrc/                            # src/Rendering 下 9 个 .c + 9 个 .h 的逐字节副本
│                                    #（SHA256 已与原件逐一比对；src/ 未被修改）
├── shim/dispatch/dispatch.h         # dispatch_apply 的最小 Windows shim（串行）
├── port-clang-blocks.py             # 机械变换：Clang blocks -> 纯 C（26 处）
├── verify-transform-roundtrip.py    # 逆向还原证明：像素算术一字未改
├── build-kernel.ps1                 # 9 个 .c -> compositor_kernel.dll（42 个导出）
├── run-spike.ps1                    # 一键：编译内核 + 构建 C# + 跑校验
├── KernelProbe/                     # C# P/Invoke 校验工程（10 项断言）
└── build/                           # 生成物（ported/ obj/ bin/）
```

一键运行：

```powershell
cd "D:\Compositor For Windows\spikes\c-kernel"
powershell -ExecutionPolicy Bypass -File .\run-spike.ps1
```

期望结尾：`SPIKE RESULT: ALL CHECKS PASSED`，退出码 0。

**独立性**：spike 只读取 `src/` 一次（复制 + 哈希比对），之后全部在自己的目录里工作。
**`src/` 下没有任何文件被修改**（18 个 `.c`/`.h` 的 SHA256 已记录在 `logs/src-rendering-hashes.txt`）。

---

## 附录 B：证据索引（文件 : 行号）

| 结论 | 位置 |
|---|---|
| C 内核无 CoreGraphics（0 次） | `Rendering/*.c`、`Rendering/*.h` 全部文件（2678 + 301 行） |
| "60 / 96" 的真实出处（`.swift` 行数） | `Rendering/*.swift` 22 个文件，逐文件按行统计（§3.2） |
| C 内核公开 API 形状（纯 buffer） | `LensPixels.h:11`、`LensPixels.c:4`、`LevelsPixels.c:3`、`LevelsPixels.c:31` |
| 唯一 Apple 依赖 `dispatch/dispatch.h` | `AdjustPixels.c:3`、`DitherPixels.c:5` |
| `in_bands` 辅助函数（22 个 block 的汇聚点） | `AdjustPixels.c:9`、`DitherPixels.c:10` |
| 直接 `dispatch_apply` 调用 | `AdjustPixels.c:11,348,386`、`DitherPixels.c:12` |
| `__block` 变量 | `DitherPixels.c:359,400,526` |
| `M_PI` 需要 `-std=gnu11` | `HealPixels.c:168` |
| sRGB / Rec709 参考实现 | `AdjustPixels.c:214,218,224` |
| 32-band 统计（校验 band 划分） | `AdjustPixels.c:339-377` |
| `hash_noise`（逐位复刻校验） | `DitherPixels.c:314-318` |
| 16 位量化（校验改写后的 band 路径） | `DitherPixels.c:320-338` |
| 机器级 NuGet 配置 | `C:\Program Files (x86)\NuGet\Config\Microsoft.VisualStudio.FallbackLocation.config` |

---

## 附录 C：未验证项清单（如实列出）

| 项 | 为什么未验证 |
|---|---|
| **MSVC (`cl.exe`) 编译这 9 个 `.c`** | 本机没有 MSVC（`vswhere.exe` 不存在）。**且有一个已知风险**：`cl.exe` 不支持 Clang blocks **也不支持 GCC 嵌套函数**，所以 §3.5 的变换产物不能直接给 MSVC 用 —— 走 MSVC 需要把 26 处改成"静态函数 + 上下文结构体"。建议在 CI（`windows-latest` 自带 MSVC）上首次验证 |
| **`clang-cl` + `-fblocks` 零改写路线** | 本机没有 clang/LLVM |
| **WinUI 3 / Windows App SDK 端到端构建** | 需要 net8.0+ SDK（本机只有 7.0.304）与 Windows SDK，二者都缺。仅验证了 `Microsoft.WindowsAppSDK` NuGet 包**可还原** |
| **MSIX / Inno Setup / Velopack 实际打包** | 缺 Windows SDK 与打包目标工程；仅验证了 winget/NuGet 上工具存在 |
| **代码签名与 SmartScreen 行为** | 无代码签名证书，无云 HSM，无法实测 |
| **GitHub Actions windows runner 工作流** | 未在本机执行 CI；仓库现有 CI 是 macOS 流程 |
| **多线程性能（shim 目前串行）** | 本次目标是**正确性**移植。串行 shim 与并行原版输出逐位相同，但**没有测过吞吐**。恢复并行可换 OpenMP / 线程池，属 Wave 1 优化 |
| **其余 40 个导出函数的数值校验** | 校验覆盖了 10 项（含 2 项针对改写路径的精确比对）；`adjust_camera_raw*`、`scanlines_apply`、`wand_trace` 等**只编译、未做数值断言** |
| **估计的安装后占用体积** | 下载体积是官方 `Content-Length`（已实测）；"安装后占用"没有实际安装，**是估算**，已在表中标注 |
