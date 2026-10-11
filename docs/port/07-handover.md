# 交接文档：现状、缺口、下一步

> 写于自动目标轮次用尽（12/12）时。**这份文件是给下一个会话或下一个人看的**，不是给最终用户看的。
> 目标**未完成**，但地基已可验证。上游：`robbietilton/Compositor` @ `3081719`，`src/Compositor/` 全程零改动。

---

## 1. 一句话现状

**格式契约、算法层、渲染层地基已经可用且被 407 项测试覆盖；UI 层一行未写。** 这不是一个能跑的应用，是一个**可验证的引擎 + 一份被复核过的计划**。

| 层 | 状态 | 证据 |
|---|---|---|
| C 像素内核 | ✅ 42 导出、parity 10/10 | `native/compositor_pixels/run-tests.ps1` |
| `.comp` 格式 | ✅ 读 v1–v11、写 v11、版本门槛、字节级往返 | `CompVersionGateTests`、`ProjectPackageTests` |
| 撤销栈 + 包摘要 | ✅ | `DocumentHistoryTests`、`ProjectDigestTests` |
| PNG 编解码（含真灰度蒙版） | ✅ | `PngCodecTests`、`DownsamplerTests` |
| 24 种混合模式 + 图层合成 | ✅ | `BlendMathTests`、`CompEndToEndTests` |
| `Surface`（CGContext 替身） | ✅ 双路径 | `SurfaceTests` |
| 降采样 + 分块网格 | ✅ | `DownsamplerTests`、`TileGridTests` |
| 调整层 | 🟡 **5/12 种** | `AdjustmentTests` |
| 图层效果 | ✅ 6 种 | `LayerEffectsTests` |
| **UI** | ⬜ **零** | — |

`dotnet build Compositor.sln -c Release` → 0 错误 0 警告。
`dotnet test Compositor.sln -c Release` → **407 通过 / 0 失败 / 0 跳过**。

---

## 2. 三条硬阻塞（按价值排序）

> **2026-10-11 更新：仓库已建并推送，CI 已跑三轮。** 见下面"阻塞 B"的新内容——`ubuntu-latest` 与 `windows-latest` 都已暴露**本机永远不会出现**的真实缺陷。

### 阻塞 A：macOS 黄金样本 —— 一份文件能关掉四项未决

**需要的东西**：一个在 macOS 上真实产出的 `.comp` 工程 + 它导出的 PNG。

没有它，以下四项**无法关闭**（全部记录在 `wave0-contracts.md` §5）：

1. **浮点 parity**：GCC/clang 与 Apple clang 在 677 处 `float` 上的差异。目前只有整数路径与精确恒等运算被验证过。
2. **11 种走 Core Image 的混合模式边界行为**：本移植按 W3C 规范实现，且已证明与 Skia 逐点一致（25/25），但 Core Image 的边界情形（Soft Light 分段阈值、Hard Mix 取整）未对拍。
3. **取整策略**：本移植用 `Math.Round`（银行家舍入），Core Graphics 行为未验证——**这是"≤1 LSB"容差能否守住的关键点**。
4. **`Curves` 的通道顺序**：本移植按「先合成、后单通道各一次」实现，上游 `Curves.swift:37` 的表达式看起来会**二次施加合成通道**。这是**有意的分歧**，需要实物样本才能定谁对（`Adjustment.cs` 的文档注释里写明了）。

**怎么拿**：向上游作者索取，或自己在 Mac 上做一份。这是纯外部依赖，本机无法自产。

### 阻塞 B：CI —— 仓库已建，两个平台各有残留

仓库：**https://github.com/selfrestart/CompositorForWindows**（public）。三轮 CI 的结果：

**已修好的（都是本机永远测不到的）：**

1. **`.gitattributes` 的规则顺序**。Git 用**最后匹配**的规则。我最初把通配写在例外之后，`-text` 被覆盖。证据：一个 70,306 字节的源文件暂存成 68,931 字节，差值恰好等于它的 1,375 个 CRLF——而这会让 CI 编译的**不是**被验证过的那份文件，逐字节相同的证据会作废。
2. **`core.autocrlf=true`**。这台机器上一直是它把上游的 LF 变成 CRLF。**意味着"spike 副本与上游逐字节相同"这个证据此前是在跨换行的比对上成立的。** 已全局关闭，重新克隆后确认 spike 副本与上游一致。
3. **`diff_vs_upstream.py` 硬编码了 CRLF**（`split(CRLF)`）。它把 `\r\n` 当作上游的形态——那是本机的产物。**在 Linux CI 上（以及任何正常检出上）第 2 步验证必然失败。** 已改为内部统一 `\n`。
4. **控制台编码**。报告里含 U+2212，Windows 控制台默认代码页无法编码，打印即抛 `UnicodeEncodeError`——**在通过的代码树上失败**。已在脚本内强制 UTF-8 流。
5. **ubuntu 的 `MSB1008`**：折叠的 YAML 标量把三个工程路径拼成一条命令。
6. **ubuntu 的 77 个 `DllNotFoundException`**：我给 `SkiaSharp.NativeAssets.Linux` 加了 `PrivateAssets="all"`，导致它传不到真正做解码的 `Compositor.Tests`。
7. **`build.ps1` 的 `Split-Path` 空路径**：MSVC 分支从不设置 `$cc`，于是每次 MSVC 构建都报错并跳过导出验证。

**未解决（两个平台各一个）：**

1. **windows：`AccessViolationException`，在原生代码里。** 测试宿主启动约 5 秒后死亡，一条测试都没报告。已加 `DOTNET_DbgEnableMiniDump` 与 `--diagnostic` 捕获转储，下次运行会给出触发它的具体调用。
   - **注意**：MSVC 分支**本身是成功的**——`0 Error(s)`、42 个入口点、DLL 已产出。`build.ps1` 里那句 "NOT VERIFIED ON THIS MACHINE" 已被真实 CI 推翻，可以去掉这个警告了。
2. **ubuntu：1 个测试失败**（407 中 406 通过）。`HealCoverageBounds_ReportsFullCoverage` 期望 `[0,0,32,24]`，得到 `[0,0,0,0]`。
   - **根因是跨平台 ABI 差异**：`RemainingEntryPointTests.cs:208` 的注释写着「4-byte ints because the C parameter is `long`, which is 32-bit on Windows」。**Linux 上 `long` 是 8 字节**，所以内核往 16 字节的缓冲区里写了 32 字节——**这是 Linux 上的缓冲区越界**，只是恰好表现为断言失败而非崩溃。
   - **修法**（下一轮做）：需要一个可移植性 shim，把 `heal_coverage_bounds`、`wand_trace`、`wand_mask`、`color_range_mask` 的 C `long` 收窄为 `int32_t`。改 `csrc/` 会让它与上游不再逐字节相同，所以正确做法是加一个包装头（`portab.h`）在这些函数的边界上做转换，或改用 `<stdint.h>` 的固定宽度类型并接受这一步的分叉。

**留下的空白**：`Compositor.Core` 与 `Compositor.Codecs` 是平台无关的，但 `Compositor.Pixels` 的绑定对 `long` 的宽度做了平台假设。要么把绑定做成真正可移植的，要么明确只支持 Windows 并把 Linux CI 范围缩到前两个工程。

### 阻塞 C：UI 层（占总成本 36–38%，一行未写）

这是**整个移植的主体工作量**，也是唯一没有规格可依的部分。`06-ui-calibration.md` 已把系数从猜测收紧为可复核的结构度量（工期 66.3–102.7 人周），但**编译 XAML 并验证的活还没开始**。

**第一件事**：`06-ui-calibration.md` §5.3 指出，若要真正把自绘系数的空白补上，唯一有意义的方向是做一个 `EditorCanvas` 最小切片（缩放/平移 + ≥6 个指针 override + cursor/tracking 三件套 + 三档 DPI）。**不是再测一个面板。**

---

## 3. 已刻意留下的边界（不是遗漏）

`wave0-contracts.md` §6 有一张完整表格。摘要：

| 组件 | 缺什么 | 为什么现在不做 |
|---|---|---|
| `TiledLayerRenderer` | piece 的实际合成 | 与画笔描边、Core Graphics 左下原点翻转耦合；它服务的绘制系统还没移植 |
| `DownsampleCache` | LRU 缓存与像素预算淘汰 | 需要先有"图像身份"概念；本移植的缓冲是值语义 |
| 调整层 7 种 | `Hue/Saturation`、`Gradient Map`、`Grain`、`Color Balance`（走 native 内核）、两种模糊、`Add Noise` | 各自需要不同机制，不是同一件事的变体 |
| `LayerSampling` | 按图层选择采样质量 | 上游默认是 `.high`，当前行为与其默认一致 |
| `Compositor.Pixels` | 42 个内核函数对外公开 | 目前只公开 `NativeKernel`/`KernelHeap`。公开 42 个函数是个独立设计决定——**这是解锁剩余 7 种调整层（其中 4 种）的前置** |

---

## 4. 环境笔记（踩过的坑，别重踩）

**这台机器特有的三件事**，全部写进了 `README.md` 与 `02-toolchain.md`：

1. **仓库根 `nuget.config` 不能删**。机器级配置指向不存在的 `D:\Dev C++\Microsoft VS\NuGetPackages`，任何 `dotnet build` 都会报 `MSB4018`。
2. **`DOTNET_ROOT=C:\Program Files\dotnet` 必须设置**（已持久化到用户环境变量）。注册表 `install_location` 为空，不设则任何 `dotnet` 命令报 `hostpolicy.dll not found`。
3. **只有 SDK 8，运行时再多也不能编译更高 TFM**（`NETSDK1045`）。`net8.0` 是当前目标。

**API 可用性**（.NET 8 与 SkiaSharp 2.88.9 的实测边界）：

- `JsonStringEnumMemberName`（.NET 9）、`JsonElement.DeepEquals`（.NET 9）、`JsonTypeInfo.Converters`（不存在）——都**不可用**，已各自绕开。
- SkiaSharp 2.88.9 **没有** `VividLight`/`LinearLight`/`PinLight`/`HardMix` 四个混合模式常量；`SKBitmap(SKPixmap)` 构造不存在（用 `InstallPixels`）；`SKFilterQuality` 仍在。
- `Rect?` 是 `Nullable<Rect>`，xUnit 的 `Assert.NotNull` 不做流分析（用 `HasValue` + `.Value`）。

---

## 5. 建议的下一步顺序

**若要继续移植**（不依赖任何外部输入）：

1. **公开 `Compositor.Pixels` 的内核函数**（42 个），然后做剩余 4 种能复用内核的调整层：`Gradient Map`、`Grain`、`Color Balance`，以及用 `AdjustPixels` 的 `adjust_colored_vignette` 等。
2. **`Hue/Saturation`**：需要 `HueSaturation.swift` 的色带算术（613 行），且 `hsvSettings` 的数组形态已在 `wave0-contracts.md` §2.4 分析清楚（`Dictionary` 用 `SwiftEnumDictionaryConverter` 落地）。
3. **两种模糊 + `Add Noise`**：邻域采样，可参考 `EffectCoverage.Blur` 的可分离高斯。
4. **`DownsampleCache` 的 LRU**：等渲染调用方定型后再做。
5. **`TiledLayerRenderer` 的 piece 合成**：等画笔系统。

**若要解锁决策**（需要用户）：

- 建仓推送 → CI 真跑。
- 索取 macOS 黄金样本 → 关闭四项未决。

**若要动 UI**（最大的一块）：

- 先做 `EditorCanvas` 最小切片 spike，标定自绘系数，再决定 WinUI 3 vs Avalonia（`01-architecture.md` §1.1 的决策闸门）。

---

## 6. 文档地图

| 文件 | 内容 |
|---|---|
| `05-roadmap.md` | **主路线图**（冲突以它为准）：结论、六个坑、编译器裁决、10 波次与工期 |
| `wave0-contracts.md` | 冻结契约：环境事实、`.comp` 字节级细节、部分移植边界、四条未验证项 |
| `01-architecture.md` | 框架映射、可移植性评级、C# 工程布局、风险 R1–R11（已按 `06` 回写） |
| `06-ui-calibration.md` | UI 系数复核（**并指出原验证协议本身是错的**） |
| `02-toolchain.md` | 工具链缺口、C 内核链接论证、出网与编译实测 |
| `03-test-harness.md` | 81 个上游测试文件分类（上游用 Swift Testing，567 个 `@Test`） |
| `04-comp-format.md` | `.comp` v1–v11 兼容矩阵、全量 schema、7 处文档与实现不一致 |
| `spikes/c-kernel/README.md` | 可一键复现的最初可行性证据（10/10） |
