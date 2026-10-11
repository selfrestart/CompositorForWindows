# Compositor Windows 移植 · 主路线图（Lead 汇总）

> 状态：**已可执行**。本文是四份专项文档的汇总与裁决，冲突处以本文为准。
> 上游：`robbietilton/Compositor` @ `3081719`（MIT），已克隆到 `src/Compositor`，全流程未修改（`git status --porcelain` 为空）。
> 专项文档：`01-architecture.md`（1001 行）· `02-toolchain.md`（608 行）· `03-test-harness.md` · `04-comp-format.md`
> 可运行证据：`spikes/c-kernel/`（`run-spike.ps1` → 10/10 通过，exit 0）
> 日期：2026-10-11

---

## 0. 先说结论

**这个移植比最初预期的容易得多，但"容易"集中在算法，成本集中在界面。**

三条最关键的判断：

1. **像素内核几乎白送。** 9 个 C 文件（2,678 行）对 Apple 框架**零依赖**，已在 Windows 上编译成 DLL 并被 C# 调用，10/10 数值校验通过——**这是实测，不是评估**。整个内核的平台依赖只有 4 处 `dispatch_apply`，且聚敛在 2 个文件的一个 7 行函数里。
2. **`Rendering` 与 `UI` 是真正的战场。** 22 + 49 个文件、16,516 行，耦合密度是全仓最高的（`Rendering` 的 C 类文件密度 77.5 命中/千行）。这里面没有机械转换路径，只有"用另一套原语重新实现同一行为"。
3. **工程量大，但不是研究项目。** 单人全职 **65–100 周**；3–4 人并行 **9–14 个月**，且被关键路径硬卡在 30–48 周。**其中 UI 占 36–38%。**

---

## 1. 已经证实的事（可以直接依赖）

这些不是推断，每一项都有可复现的证据。

| # | 结论 | 证据 |
|---|---|---|
| 1 | 全部上游源码可用 | 已克隆 288 文件 / 6.2 MB / HEAD `3081719` |
| 2 | **C 内核 9/9 编译成功、42 个入口导出、10/10 数值校验通过** | `spikes/c-kernel/run-spike.ps1`，退出码 0 |
| 3 | **C 内核对 CoreGraphics 零依赖** | 9 个 `.c` + 9 个 `.h`（2,979 行）中 `CGContext`/`CGImage`/`CGRect`/`CF*`/`#include <CoreGraphics>` 全部 = 0 |
| 4 | 改写未动算术 | `verify-transform-roundtrip.py` 逆向还原后与原文**逐字节相同** |
| 5 | 串行 shim 与并行原版**逐位相同** | `dither_quantize16` 12,600 字节逐字节一致；`camera_raw_statistics` 32-band 偏差 3.1e-15 |
| 6 | `.comp` 工程格式有完整规格 | `docs/project-format.md`，v1–v11，含校验与拒绝规则 |
| 7 | **行为契约有 567 个真实用例** | 上游用 **Swift Testing**：`@Test` 567、`#expect` 2525、`#require` 996（**不是 XCTest**：`XCTAssert` = 0） |
| 8 | 出网完全正常 | git clone / NuGet 还原 / pip 三条路径实测通过 |

> 第 7 条修正了整个验证策略：契约不是 81 个文件，而是 **567 个可逐条对照的断言**。

---

## 2. 必须避开的六个坑（已实测）

按危害排序。**这六条是本次侦察最有价值的部分**——每一条都能让一个不了解情况的团队白干几周。

### 坑 1 · 任何 `dotnet build` 都会失败（P0，已在本次会话中独立复现）

机器级 NuGet 配置指向一个不存在的目录：

```
C:\Program Files (x86)\NuGet\Config\Microsoft.VisualStudio.FallbackLocation.config
  → value="D:\Dev C++\Microsoft VS\NuGetPackages"   （Test-Path = False）
```

后果：`error MSB4018: ResolvePackageAssets 任务意外失败`。**这不是本项目的坑，而是这台机器的坑**，但会挡住第一步。

修法（0 下载、无需管理员、就近覆盖即可）：

```xml
<!-- nuget.config，放在解决方案根目录 -->
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
</configuration>
```

### 坑 2 · 装了运行时 ≠ 能编译（P0）

本机有 .NET **10.0.1 运行时**和 **8.0.14 运行时**，但只有 **7.0.304 SDK**。用 SDK 7 构建 `net8.0` → `error NETSDK1045`（0.7 秒即失败）。

→ 必须装 SDK（`winget install --exact --id Microsoft.DotNet.SDK.8`，215 MB）。`.NET 7 已 EOL`，不要留在 net7.0。

### 坑 3 · `cl.exe` 编译不了这些 C 文件（会推翻一个看似合理的方案）

`01-architecture.md` 推荐"MSVC 原样编译"。**该推荐需要修正**：`cl.exe` 既不支持 Apple 的 Clang blocks（`^`），**也不支持 GCC 的嵌套函数**——所以连工具链 teammate 的变换产物都不能给它用。

见第 3 节的裁决。

### 坑 4 · 像素预算不能按文档硬编码（会让 Windows 版比 macOS 严 2–8 倍）

`docs/project-format.md` 写的是"1 亿像素 + 1 亿蒙版像素"。**实现不是这样**：

```swift
// DocumentLimits.swift:36-37  —— 随物理内存缩放
documentPixelBudget = min(800_000_000, max(200_000_000, physicalMemory / 16))
```

且图像与蒙版**各用一个独立计数器**（`ProjectStore.swift:87, 94-95, 180-181`）。按文档硬编码 100M，会让 64 GB 机器上的合法工程被 Windows 版拒绝。

### 坑 5 · 混合模式是 24 种，不是 9 种

文档说 v3 引入 9 种；实现 `LayerAppearance.swift:4-13` 有 **24 种**，且校验器**不按版本限制取值集合**（只限制"v<3 必须 Normal"）。只实现 9 种会拒绝合法文件。

### 坑 6 · `effects` 与 `shape` 根本不进校验器

- `ProjectStore.validate`（278 行）中**没有 `effects` 引用**；越界值只在渲染期被 `effects.isValid` 当作"无效果"忽略（`EditorCanvas.swift:1216`）。
- `shape` 没有 `isValid`，零校验（`ShapeTool.swift:19-32`）。

→ Windows 侧若"更严格地校验"，会把 macOS 能打开的文件判为非法。**宽容读、严格写**。

> 另外 4 处较小的不一致（色带只查 `isFinite` 不查范围、图层图不校验通道、`activeLayerID` 必须命中 layers 等）见 `04-comp-format.md`。

---

## 3. 裁决：C 内核用哪个编译器

三份文档在此冲突，我查了源码后裁决如下。

### 事实基础（我亲自核实）

`in_bands()` 在 `AdjustPixels.c:9-15` 与 `DitherPixels.c:10-16` 是**逐字相同的 7 行**，负责 22/26 个 blocks；余下 2 处直接 `dispatch_apply`（`AdjustPixels.c:348`、`:386`）各自带函数本地累加器。**整个内核的平台依赖 = 一个 7 行函数 + 2 个带本地状态的调用点。**

### 三条路径

| 路径 | 改动 | 本机可验证 | 风险 |
|---|---|---|---|
| **A. MinGW/GCC + 脚本变换** | 26 个 block → GCC 嵌套函数 | ✅ **已跑通 10/10** | 依赖 GCC 私有扩展；MSVC 用不了；与上游 `.c` 语义分叉 |
| **B. 只重写 `in_bands`（推荐） | ~50 行，`#pragma omp parallel for` | 部分（GCC 可测 OpenMP） | 需人工验证行为等价，**用 567 用例兜底** |
| **C. clang-cl + `-fblocks`** | 0 行（文件逐字节原样） | ❌ 本机无 clang | 需 LLVM + libdispatch shim；浮点与 Apple clang 同源 |

### 推荐：**B 为主，C 为生产目标，A 只用于立刻开工**

理由：

1. **B 的改动面比听起来还小**——`in_bands` 重写成普通 C 函数（body 改为函数指针 + `void *ctx`），2 处直接调用改为 OpenMP 或线程池；其余 7 个文件、753 行零改动。这比 26 处散点改写小一个数量级，且在 `cl.exe`、`gcc`、`clang` 上**都能编译**。
2. **B 保留了上游同步能力**：改动集中在 2 个文件的两小段，跟着上游走时冲突面极小。
3. **C 是"零改写"的终极形态**，且 clang 与 Apple clang 同源，浮点行为最可能一致——如果它在 CI 上验证通过，应迁移到 C（`clang-cl` 是 MSVC 兼容驱动，`/openmp` 可用，不需要 libdispatch）。
4. **A 现在就能用**，所以 Wave 0 **不应被编译器选型阻塞**：先用已验证的 GCC 路径把垂直切片打通，编译器迁移作为独立任务并行推进。

> **必须记账的一条**：浮点 parity **至今未验证**。spike 里通过的两项是精确身份运算（`k=0`）与整数路径（0 字节差异）；真正的浮点路径（`camera_raw_*` 的 677 处 `float`）**没有 macOS golden 数据可比**。这条是 Wave 0 的头号失败信号。

---

## 4. Wave 0：现在就能开工的最小骨架

**目标**：证明端到端——`.comp` 能被读、像素能被算、图像能被导出。**不做 GUI。**

### 前置（两项，合计 215 MB）

1. 在仓库根放 `nuget.config`（第 2 节坑 1 的内容）——0 下载。
2. `winget install --exact --id Microsoft.DotNet.SDK.8`——215 MB，无需重启。

### 范围

| # | 内容 | 状态 |
|---|---|---|
| 1 | `Compositor.sln` + `Core` / `Pixels` / `Codecs` / `Psd` 四个 csproj 骨架 | 新建 |
| 2 | C 内核以路径 B 改写 `in_bands` → 编译成 DLL → `LibraryImport` 绑定 | 已有 GCC 版可参照 |
| 3 | `.comp` 读取最小路径（v11 顶层图层，无组无蒙版）→ WIC 解码 PNG | 新建 |
| 4 | 图层合成（先做 Normal + Multiply）+ 导出 PNG | 新建 |
| 5 | xUnit parity 测试：C 内核 vs 参考向量 | 新建 |
| 6 | CI `windows-latest`：build + test 全绿 | 新建 |

### 退出标准（逐条可判真假）

1. 干净 `windows-latest` 上 `dotnet build Compositor.sln -c Release` 零 error。
2. **≥10 项 C 内核 parity 测试通过**（对齐 `spikes/c-kernel` 的 10 项）。
3. 打开一个 3 图层 `.comp`，合成结果与 macOS 导出 PNG 逐像素差 ≤1 LSB。
4. **`Compositor.Core` 在 `ubuntu-latest` 上也能 build + test 通过**——证明平台无关边界没有泄漏。
5. `hsvSettings` 的 JSON 形态用 macOS 真实产物确认（见第 7 节未决项 1）。

### 工作量

**2–3 周**（不含 GUI）。C 内核部分已有可跑通的代码，主要是 C# 侧与工程骨架。

---

## 5. 分波次计划

| Wave | 内容 | 单人全职 | 关键退出标准 |
|---|---|---|---|
| **W0** | 骨架 + C 内核 + 最小 `.comp` + 导出 | 3–5 周 | 上节 5 条 |
| **W1** | 文档内核：`.comp` v1–v11 全支持、撤销、digest | 6–9 周 | 19 个测试套件等价物；11 个版本往返 || **W2** | CPU 光栅管线 + 全部像素内核 + 导出 | 6–10 周 | 30 个工程 golden 比对 ≤1 LSB |
| **W3** | GPU 管线（17 个 MSL kernel → HLSL） | 5–8 周 | UAV barrier 千帧压测；笔刷性能 ≤4.0 ms |
| **W4** | 自绘画布 + 全部工具与手势 | **10–16 周** | 三档 DPI 截图；手势清单 |
| **W5** | 图层面板 + 多标签页 + 菜单 + 命令面板 | 8–12 周 | 手工 UI 清单逐项截图 |
| **W6** | 面板、滤镜 UI、对话框 | **10–14 周** | Camera Raw 全滑块数值一致 |
| **W7** | PSD/PSB/RAW/扩展格式 | 8–12 周 | 真实 PSD 语料库两端对照 |
| **W8** | 文本与字体（DirectWrite） | 5–8 周 | 不编辑的文本层保存后 PNG 字节不变 |
| **W9** | 平台服务、打包、签名、更新 | 4–6 周 | 干净机器安装→编辑→升级→卸载 |
| | **合计** | **65–100 周** | |

**并行口径**：3–4 人 → **9–14 个月**。关键路径 `W0→W1→W2→W3→W4` 串行 30–48 周是硬下限；W5/W6/W7 可在 W3/W4 期间提前开工，这是用满人力的唯一办法。

**最大成本项是 UI（占 36–38%）**，由两个独立口径互相印证：波次口径 24.7–35.5 人周、系数口径 21.4–35.3 人周。自绘控件的单位成本（2.5–4.0 人周/千行）是 C 内核（0.15–0.30）的 8–27 倍。

---

## 6. 三条必须写进项目章程的纪律

1. **`Compositor.Core` / `Psd` / `Pixels` 禁止引用任何 `net*-windows` TFM 或 `Microsoft.Windows.*` 包**，且这三个项目的测试在 `ubuntu-latest` 上必须绿。这是防止平台耦合扩散的唯一机制。
2. **`.comp` 契约：读 v1–v11，写 v11。** 禁止写入上游不认识的版本号或字段——宁可丢弃新字段，也不要让 macOS 侧拒绝打开。
3. **`SeparableBlend` 的教训要变成测试**：混合**必须在 sRGB 空间**做，不能在 linear 空间。`SeparableBlend.swift:14-16` 记录了真实数值（40% 灰之上，80% 灰的 Color Dodge 在线性空间得 62%，Photoshop 得 100%；Color Burn 得 0% 而非 25%）。这类错误肉眼几乎看不出来但数值全错，只有 golden 比对能抓到。

**不做的事**（明确写下来，避免范围蔓延）：不追求 UI 与 macOS 一致；不追求文本渲染一致；不追求 RAW 解码逐位一致；不向上游提"方便移植"的 Swift PR。

---

## 7. 未决项（开工前必须关闭）

| # | 未决项 | 影响 | 怎么关 |
|---|---|---|---|
| 1 | **`hsvSettings.adjustments`/`bands` 的 JSON 形态**：Swift 用 String 原始值枚举作字典键，编码成对象还是键值交替数组（SE-0320）无法在不运行 Swift 的情况下确证 | **高**——写错会导致所有 v7+ 调整层解析失败 | 用 macOS 真实产物导出一份 v7+ 工程，看 `manifest.json` 原文 |
| 2 | **浮点 parity**：GCC 10.3 / clang-cl 与 Apple clang 在 677 处 `float` 上的差异 | **高**——"原样编译"策略的前提 | 需要 macOS golden 数据；Wave 0 第一步 |
| 3 | **manifest 字节级形态**（缩进、Double 表示） | 中——影响 macOS 侧"外部变更"提示频率 | 同上，一份黄金样本 |
| 4 | **UI 转换系数**：声明式 0.8–1.4、自绘 2.5–4.0 人周/千行 | **中**——原为最高不确定性来源 | ✅ **已部分关闭**（`06-ui-calibration.md`）：两个原区间**都得到支持**，修正口径重算总工期 **66.3–102.7 人周**（原 65–100，差异 <3%）。但**验证协议本身写错了**——见下 |
| 5 | **WinUI 3 vs Avalonia** | 中——框架选错返工一周 | Wave 0 决策闸门：输入延迟 / DPI 切换 / 无边框拖动三项实测 |
| 6 | 其余 32 个导出函数（42 个中只校验了 10 个） | 中 | W2 补 parity 测试 |
| 7 | 并行吞吐（shim 目前串行） | 低——不影响正确性 | W3 换 OpenMP/线程池后测 |

---

## 8. 与上游的关系

**不要 rebase，不共享 Swift 代码。** Windows 侧不是分支，而是另一个实现；两边唯一的共同资产是**格式与语义**。

可以真正共享的三样：
- **C 内核源码**（2,678 行）——物理共享，按哈希同步，`verify.yml:44-45` 的滤镜清单可直接当同步白名单。
- **`.comp` 格式**（`docs/project-format.md`）——语义共享，这是全项目最有价值的文档。
- **测试向量**——数据共享，从 567 个用例提取平台无关的输入/输出对，做成 golden 数据由 CI 消费。

每周审查上游 diff 的**三个范围**即可：`docs/project-format.md`、`Rendering/*.c|*.h`、`ProjectStore.swift` 的 `validate`/`current`/`supported`。**其余一律不跟进。**

---

## 9. 建议的下一步

### 9.0 ⚠️ 一条必须先读的更正：不要按原验证协议标定 UI 系数

`01-architecture.md` §4.0 原来建议「Wave 0 用 `FilterSheet.swift` 标定声明式系数」，**该协议已废弃**——按字面执行会把工期算成 **106–186 人周**（原 65–100），足以导致错误否决整个移植项目。原因：该文件约 60% 的成本是**一次性可复用 XAML 控件**，把它当作单文件费率再乘上 8.86 千行会错 3.9–13.9 倍。`TransformOverlay.swift` 同样不适合——它 **0 个输入 override**，而自绘桶 89.4% 的行属于「输入状态机型」。详见 `06-ui-calibration.md` §5.3、§7.3。

**结论**：UI 系数不需要再标定才能开工；它已足够支撑「65 周乐观 / 100 周预算」的决策。若将来要把它做硬，唯一有意义的方向是**度量输入状态机型那一类**（用 `EditorCanvas` 最小切片），不是再测一个面板。

### 其余下一步

**立刻可做（今天）**：
1. 放 `nuget.config`，装 .NET SDK 8。（215 MB，解开一切构建）—— ✅ 已完成
2. 跑 `spikes/c-kernel/run-spike.ps1` 复现 10/10——确认环境基线。 ✅ 已完成

**阻塞项（唯一）**：向作者要一份 macOS 的真实 `.comp` + 导出的 PNG。它一次性能关闭：`hsvSettings` 实物确认、浮点 parity、11 种 CI 混合模式的边界一致性、以及取整策略对齐。**这是纯外部依赖，越早拿到越好。**

**可选**：`gh` 已登录（账号 `selfrestart`），若建仓推送，CI 的 `windows-latest` + `ubuntu-latest` 两任务即可真跑。本机无法验证 Linux 路径（Docker 守护进程未运行、WSL 仅有 `docker-desktop` 发行版）。
