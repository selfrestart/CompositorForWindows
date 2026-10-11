# Compositor Windows 移植：架构与运行时映射

> 交付物编号：01-architecture　|　状态：初稿　|　全文 43 处 `待验证` 标记，已汇总为附录 A 的 10 项；总工期与两个待标定系数强相关（见 §4.0）
> 上游源码：`src/Compositor`（只读参考，本文件撰写过程中未修改其中任何文件）
> 测量方法：所有行数为**原始行数**（含空行与注释），由 `(Get-Content -Raw) -split "\`n"` 统计，末尾换行会产生 ±1 的固定偏差。所有 API/符号计数为在 `src/Compositor/Compositor/**/*.swift`、`**/*.c`、`**/*.h` 上的正则命中数。

---

## 0. 侦察基线复核（对既有侦察数据的勘误）

在动手前对 Lead 提供的侦察数据做了独立复核，**发现三处需要更正的事实**，它们直接影响第 2 节评级与第 4 节工作量：

| 项 | 既有侦察 | 实测 | 影响 |
|---|---|---|---|
| C 内核是否依赖 Apple 图形 API | 「C 里 CGContext 60 处、CGImage 96 处」 | **0 处**。对 `Rendering/*.c`、`*.h` 搜索 `CGContext\|CGImage\|CoreGraphics\|vImage\|Accelerate\|NSColor\|NSView\|CFRelease`，结果为 `No matches found` | 决定性地把「C 内核」从高风险项降为最低风险项，并直接决定第 3 节的链接方案 |
| 文本子系统的规模 | 「TypeTool + PSDText 3 万行」 | 1,945 行：`TypeTool.swift` 473 + `PSDText.swift` 683 + `InlineTextEditor.swift` 558 + `TypeControls.swift` 250 | 文本不再是第一大成本项；第 6 节风险权重下调 |
| Metal 着色器形态 | 「Rendering 目录下有 Metal 着色器代码」 | 无 `.metal` 文件；**17 个 MSL kernel 以 Swift 字符串字面量内联**，运行时经 `device.makeLibrary(source:)` 编译（`MetalLayerEffects.swift:39`），与 `docs/brush-performance.md:7`「requires Xcode's optional Metal toolchain」一致 | 移植面从「着色器工程」缩为「17 个函数的 HLSL 重写」，且没有 `.metal` 构建集成问题 |

### 0.1 精确基线

| 子系统 | 文件数 | 原始行数 |
|---|---:|---:|
| `Document/` | 58 `.swift` | 14,283 |
| `UI/` | 49 `.swift` | 8,971 |
| `Rendering/` | 22 `.swift` | 7,545 |
| `Rendering/` | 9 `.c` | 2,688 |
| `Rendering/` | 9 `.h` | 310 |
| `IO/`（不含 `IO/PSD`） | 14 `.swift` | 2,098 |
| `IO/PSD/` | 6 `.swift` | 1,999 |
| 根（`CompositorApp.swift` 372、`ContentView.swift` 488、桥接头 10） | 3 | 870 |
| **应用合计** | **170** | **38,763** |
| `CompositorTests/` | 81 `.swift` | 14,994 |
| `CompositorUITests/` | 1 `.swift` | 103 |

其他被证实的关键事实：

- **没有 `Package.swift`**。`glob **/Package.swift` 返回空。唯一的外部依赖是 Sparkle 2.10.0，通过 `Compositor.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved` 的 `XCRemoteSwiftPackageReference` 固定（revision `eef1a539a373c1f1a320624b1130fc5de7b2e100`）。
- 3 个 target：`Compositor`（`com.apple.product-type.application`）、`CompositorTests`（unit-test bundle）、`CompositorUITests`（ui-testing bundle）。构建配置：`MACOSX_DEPLOYMENT_TARGET = 26.0`、`ARCHS = arm64`、`SWIFT_VERSION = 5.0`、`SWIFT_OBJC_BRIDGING_HEADER = Compositor/Compositor-Bridging-Header.h`。
- CI（`.github/workflows/verify.yml`）：`runs-on: macos-26`（:23）、Xcode 26.6（:55）、`GCC_OPTIMIZATION_LEVEL=s` + `SWIFT_OPTIMIZATION_LEVEL=-O`（:76-77）——**C 内核与 Swift 在测试时都是优化构建**，因为「unoptimized, pixel loops take many times longer」（:64-65）。
- `scripts/ci-tests.sh` 把 81 个测试文件中的 **43 个显式命名**地分到 4 个进程（`FIRST_SUITES` 33 + `FILTER_SUITES` 8 + `WINDOW_SUITES` 2），其余落入 `rest`（:6 注明「New suites land in "rest" without being listed here」）。这 43 个名字就是现成的**兼容性契约清单**。

---

## 1. 框架映射矩阵

计数口径：`files` = 命中文件数，`occ` = 出现次数。AppKit 通过 `import AppKit` 覆盖 84 个文件，是全仓最广的依赖面。

### 1.1 SwiftUI / AppKit → WinUI 3 (XAML) 或 Avalonia

| 事实依据 | 值 |
|---|---|
| `import SwiftUI` | 50 文件 |
| `import AppKit` | 84 文件 |
| `: View` 结构体 | 62 个 |
| `NSViewRepresentable` / `NSHostingView` / `NSHostingController` | 21 处，分布在 14 个文件 |
| `NSView` 子类（含 `NSTextView` / `NSScrollView` / `NSTableCellView` / `NSPanel`） | 19 个 |
| `NSView` occ / `NSWindow` occ / `NSEvent` occ / `NSApp.` occ | 86 / 30 / 138 / 48 |
| `NSMenu` | 8 文件，57 处 |

**推荐：WinUI 3（Windows App SDK）作为壳与面板层，Avalonia 作为备选，决策闸门放在 Wave 0。**

理由（与上述数字对应）：

1. **自绘画布是主线，不是壳**。`EditorCanvas.swift`（3,153 行）是 `NSViewRepresentable`（:4）包着 `CanvasView: NSView`（:21），内部有 `draw(_:)`（:866）、`cacheDisplay(in:to:)`（:1278）、`resetCursorRects()`（:1462）、`updateTrackingAreas()`（:1488）和 12 个鼠标/滚轮/触控板 override（`mouseDown` :1742、`mouseDragged` :1853、`scrollWheel` :2136…）。这类代码在 XAML 里**没有对应物**，无论选哪个框架都必须写成自定义控件 + 自绘表面。框架选择的真实权重因此落在「谁能把 D3D/D2D 表面塞进可视化树」。
2. WinUI 3 有官方文档化的 `SwapChainPanel`，是把 D3D 交换链放进 XAML 树的既定路径；这是选它的**唯一决定性理由**。Avalonia 侧的对外互操作面主要是 `OpenGlControlBase`（GL 而非 D3D）——**待验证**：Avalonia 11.x 是否有稳定的 Direct3D 交换链宿主控件；验证方法：查阅 Avalonia 官方文档的 custom drawing / GPU interop 章节并做一个最小 spike。
3. 多窗口：本应用需要多工程标签页（`UI/ProjectTabs.swift` 429 行，含 `TitleBarDragView: NSView` 与 `AnchorView: NSView`）与浮动面板（`UI/FloatingPanel.swift` 的 `NSPanel`、`UI/CommandPaletteView.swift` 的 `PalettePanel: NSPanel`）。Windows App SDK 的 `AppWindow` 支持多窗口；Avalonia 原生支持多窗口。两者都能满足——**待验证**：WinUI 3 下 `NSPanel` 语义（无标题栏、不抢焦点、随父窗口层级）能否用 `AppWindow` + `OverlappedPresenter` 复现。
4. `NSTableView` 只出现在 1 个文件（`UI/NativeLayerList.swift`，1,388 行，24 处），但那是图层树的主体。**该控件必须自建**：它同时承担缩略图、内联重命名（`LayerCell: NSTableCellView, NSTextFieldDelegate`）、拖放（`LayerEffectRow: NSView, NSDraggingSource`、`registerForDraggedTypes`、`draggingSession`）、行边线（`RowEdgeLine: NSView`）。WinUI 3 的 `TreeView` 与 Avalonia 的 `TreeView` 都不足以直接覆盖，结论对两者相同，因此**不构成选型依据**。
5. `NSMenu` 57 处集中在 `IO/CompositorApplicationDelegate.swift`（224 行，程序化构建 `NSApplication` 菜单，:161 `isSystemExtra` 过滤）与 `CompositorApp.swift`（372 行）。XAML 的 `MenuBar` + `KeyboardAccelerator` 可覆盖，属于中等成本。
6. `NSEvent` 138 处是整个输入模型的骨架：修饰键（`UI/HeldModifiers.swift`）、光标（`resetCursorRects`、`NSCursor`）、滚轮/缩放、触控板。Windows 侧对应 `PointerEventArgs` / `KeyRoutedEventArgs` / `CoreWindow` 光标 API。**待验证**：`NSEvent.modifierFlags` 的 `.option`/`.command` 语义在 Windows 上如何映射（Compositor 全篇是 Photoshop 快捷键，`⌘`/`⌥`/`⇧`/`⌃` 需要一对一落到 Win/SHIFT_CTRL/ALT），以及 macOS 的 `Cmd`-drag 惯用法在 Windows 上是否应改绑到 `Ctrl`。

> **决策闸门（Wave 0 退出前必须关闭）**：用 WinUI 3 + `SwapChainPanel` 做一个最小自绘画布，测三件事——(a) 连续 `PointerMoved` 到上屏的延迟；(b) `WM_DPICHANGED` / `XamlRoot.RasterizationScale` 变化后画面是否立即正确；(c) 无边框窗口拖动（对应 `TitleBarDragView`）是否可用。三项中任一不达标即切换到 Avalonia 并重跑同一 spike（预计 1 周）。**该 spike 的成本已计入 Wave 0。**

### 1.2 CoreGraphics（`CGContext` / `CGImage`）→ WIC + Direct2D 或 SkiaSharp

| 事实依据 | 值 |
|---|---|
| `CGImage` | 68 文件 / 500 处 |
| `CGContext` | 33 文件 / 113 处 |
| `CIImage` | 18 文件 / 144 处 |
| `CGAffineTransform` / `CGPath` / `CGColorSpace` | 24 文件 / 61 处、10 文件 / 37 处、23 文件 / 78 处 |
| `Rendering/` 内 `-> CGImage` / `: CGImage` 边界函数 | 12 处，分布在 8 个文件 |

**推荐：分层——WIC 只做编解码与元数据，SkiaSharp 做 CPU 光栅与合成，D3D12 compute 做 GPU 内核。**

依据：

- `CGContext` 在本项目里几乎全部是**位图后备上下文**的立即模式绘制：矩阵、裁剪栈、`setBlendMode`、`setAlpha`、`draw(image, in:)`。`SeparableBlend.swift:22-46` 是典型样本（`context.makeImage()` → 混合 → `context.setBlendMode(.copy)` → 回写）。`SKCanvas` + `SKBitmap` + `SKBlendMode` 的语义与之最接近，是 C# 里迁移改动最小的对应物；Direct2D 需要经 COM interop（Vortice.Windows 或 CsWin32 手写投影），同等功能代码量更高——**待验证**：把 `EditorCanvas.draw(_:)` 的绘制部分用一个 200 行样本分别用 SkiaSharp 与 Direct2D 实现，比较代码行数；这是 Wave 0 的第二项 spike（0.5 周）。
- `CGColorSpace` 78 处的核心诉求是**在 sRGB 空间做混合**。`SeparableBlend.swift:14-16` 用注释写死了这条约束：Core Image 默认线性空间会把 80% 灰的 Color Dodge 算成 62% 而不是 Photoshop 的 100%。Skia 的默认行为（`SKColorSpace` 为 null 即按 sRGB 数值直接运算）天然符合；Direct2D 需要显式把 `D2D1_COLOR_SPACE` 设为非 linear。**这是移植中最容易静默出错的一点，必须写成测试。**
- `CGImage` 500 处中，绝大多数是像素容器的传递，WIC 的 `IWICBitmap` / 自建 `Surface` 类可以承接。
- **SkiaSharp 的代价**：需要分发 `libSkiaSharp.dll` 原生二进制（体积与签名问题，**待验证**：具体版本与体积），以及 MIT 许可下的再分发义务；相对地，Direct2D/WIC 是系统组件，零分发成本。这个取舍是真实的，因此把 Direct2D 列为**并列可行方案**：如果团队更重视「零第三方原生二进制」，全 Direct2D/DirectWrite/D3D11 栈是自洽的，代价是渲染层代码量上浮（**待验证**，用上面的 spike 量化）。
- **SkiaSharp 覆盖不了的**：`SKBlendMode` 是否覆盖本项目需要的全部混合模式。实测本项目用到 **23 个 CI 混合模式 filter**（见 §1.3），其中 Color Burn、Color Dodge、Linear Burn、Linear Dodge、Vivid Light、Linear Light、Pin Light、Hard Mix、Subtract、Divide 在 `SeparableBlend.swift:6-8` 的注释里被明确标为「Core Graphics 画不对或根本没有」。**待验证**：逐项核对 `SKBlendMode` 枚举与 `D2D1_BLEND_MODE` 的覆盖；未被覆盖的用 `SKRuntimeEffect` / 自定义 `ID2D1Effect` 补齐。

### 1.3 CoreImage（`CIImage`）→ D2D 效果链 / ComputeSharp / 自写 compute

| 事实依据 | 值 |
|---|---|
| `import CoreImage` | 21 文件 |
| CI filter 字符串名 | **39 个**（23 个混合模式 + 3 个 mask blend + 13 个其它）。`Select-String '"CI[A-Za-z]+"' -AllMatches` 去重后得 40 个字符串（第 40 个是 `"circle"`，非 filter 名），共出现 58 次 |

39 个 filter 名逐个列出：

- **混合模式（23）**：`CIColorBlendMode`、`CIColorBurnBlendMode`、`CIColorDodgeBlendMode`、`CIDarkenBlendMode`、`CIDifferenceBlendMode`、`CIDivideBlendMode`、`CIExclusionBlendMode`、`CIHardLightBlendMode`、`CIHardMixBlendMode`、`CIHueBlendMode`、`CILightenBlendMode`、`CILinearBurnBlendMode`、`CILinearDodgeBlendMode`、`CILinearLightBlendMode`、`CILuminosityBlendMode`、`CIMultiplyBlendMode`、`CIOverlayBlendMode`、`CIPinLightBlendMode`、`CISaturationBlendMode`、`CIScreenBlendMode`、`CISoftLightBlendMode`、`CISubtractBlendMode`、`CIVividLightBlendMode`
- **Mask 混合（3）**：`CIBlendWithAlphaMask`、`CIBlendWithMask`、`CIBlendWithRedMask`
- **其它（13）**：`CIAffineTile`、`CIBloom`、`CIColorClamp`、`CIColorCube`、`CIColorCurves`、`CIColorMatrix`、`CIEdgePreserveUpsampleFilter`、`CILanczosScaleTransform`、`CILinearGradient`、`CIMotionBlur`、`CIPerspectiveTransform`、`CIPerspectiveTransformWithExtent`、`CIRadialGradient`

**推荐：不引入任何图像处理框架，把 39 个 filter 全部落成两类实现——CPU 侧落成 C 内核或 C# 位图运算，GPU 侧落成 HLSL compute。**

理由：这 39 个名字里**没有一个需要 CoreImage 的图求值能力**（惰性求值、自动 ROI 传播、tile 化）。逐个看用途：

- 23 个混合模式 → 像素级公式，本来就是「Core Graphics 画不对才绕道 CoreImage」（`SeparableBlend.swift:4-9`），移植后直接写成混合公式表反而更简单。
- 3 个 `CIBlendWith*Mask` → `LayerMask` / `LiveLayerMask` 的覆盖率相乘，语义直白。
- `CIColorCube` / `CIColorMatrix` / `CIColorCurves` → Camera Raw 的颜色查找表。这类表在本项目里**已经在 C 里实现**：`AdjustPixels.h:33-43` 定义了 `CameraRawStage`（最多 8 张 `size³` sRGB 表 + 权重）与 `camera_raw_compose`。
- `CILanczosScaleTransform` → 对应 `DownsampleCache.swift` 的缩放路径（该文件同时用 vImage，见 §1.5）。
- `CIEdgePreserveUpsampleFilter` → 只出现在 `ObjectSelection.swift:71`，是分割掩膜的上采样，可随 AI 模块一起替换。
- `CIPerspectiveTransform*` → `Distort.swift`（400 行）的自由扭曲，是矩阵运算。
- `CIBloom` → `UI`/`Document` 的 Bloom 滤镜，可写成 compute kernel。
- `CIAffineTile` → 仅 1 处：`EditorCanvas.swift:2694`，把一张棋盘格平铺成透明区域的背景图案。这是纯几何平铺，Skia/D2D 各有现成的 tile brush。
- `CILinearGradient` / `CIRadialGradient` → `Gradient.swift` / `ShapeTool.swift` 的绘制。
- `CIMotionBlur` → `Filters.swift` 的运动模糊（文档格式 v9 的 `Motion Blur` 调整层，`docs/project-format.md:33`）。

替代方案对比：

| 方案 | 覆盖 39 个 filter 的方式 | 取舍 |
|---|---|---|
| ComputeSharp（C# 写 HLSL，源生成器出 D3D12 pipeline） | 23 混模 + 模糊 + 透视各写一个 kernel | 无 C++/COM；**待验证**：.NET 版本与 NativeAOT 支持 |
| Direct2D effects 链 | `D2D1BlendEffect` 等内置 effect + 自定义 `ID2D1Effect` | 系统组件；**待验证**：`D2D1_BLEND_MODE` 覆盖的混合模式集合 |
| SkiaSharp `SKImageFilter` / `SKRuntimeEffect` | 与 1.2 共用一套运行时 | 与 CPU 路径统一；**待验证**：`SKBlendMode` 覆盖集合 |
| 自写 HLSL + Silk.NET | 完全控制 | 绑定代码量大，只在 ComputeSharp 不满足时启用 |

**结论：优先 ComputeSharp（GPU，对应 Metal kernel）与 SkiaSharp/`SKRuntimeEffect`（CPU 回退），把「39 个 filter 的语义」写成一个跨实现的规格表 + 一致性测试，而不是绑定到某个框架的 filter 名。**

### 1.4 Metal → HLSL compute（D3D12）

| 事实依据 | 值 |
|---|---|
| `import Metal` | 5 文件：`GPUCanvas.swift`、`GPUNoise.swift`、`MetalBrushCoverage.swift`、`MetalLayerEffects.swift`、`MetalWarp.swift` |
| `MTLDevice` / `MTLLibrary` / `MTLCommandQueue` / `MTLComputePipelineState` | 同 5 文件 |
| MSL kernel | **17 个**，内联为 Swift 字符串，运行期 `makeLibrary(source:)` |
| `import QuartzCore` | 1 文件（`GPUCanvas.swift`，:4） |

17 个 kernel 的完整清单与归属：

| 文件 | kernel | 用途 |
|---|---|---|
| `MetalBrushCoverage.swift` | `continuousBrush` | 沿平滑指针路径扫连续圆头笔尖，256×256 tile（`docs/brush-performance.md:3`） |
| `MetalLayerEffects.swift` | `effects_alpha`、`effects_spread_rows`、`effects_spread_columns`、`effects_ring`、`effects_shift`、`effects_blur_rows`、`effects_blur_columns`、`effects_inside`、`effects_compose` | 描边/阴影/内阴影/发光/颜色叠加的可分离两遍卷积 |
| `MetalWarp.swift` | `warp_pick_up`、`warp_smudge`、`warp_copy`、`warp_clear`、`warp_push` | 液化/涂抹 |
| `GPUNoise.swift` | `add_noise`、`add_grain` | 噪声与颗粒 |

**推荐：ComputeSharp（C# + HLSL 源生成器）为主，Silk.NET 为备。**

- 移植量只有 17 个 kernel，其中 `effects_*` 9 个共享同一套 uniform 结构（`MetalLayerEffects.swift:22-35` 的 `Spread`/`Shift`/`Blur`/`Compose`），实际是把 Swift `struct` + MSL kernel 一对一改写成 C# `[ComputeShader]` + HLSL，**没有算法重写**。
- MSL → HLSL 的真实差异点（必须在 Wave 3 逐条验证，**待验证**）：(a) `[[thread_position_in_grid]]` ↔ `SV_DispatchThreadID`；(b) MSL 的 `device`/`constant` 地址空间限定符在 HLSL 中不存在；(c) `half` 精度在 HLSL 中是可选项，D3D 编译器可能提升为 float（影响 1.2 提到的 sRGB 混合一致性）；(d) MSL 的 `texture2d<T, access::read>` ↔ HLSL `Texture2D<T>` + `RWTexture2D<T>`；(e) 采样器在 MSL 中与纹理绑在一起，HLSL 中分离。
- **`memoryBarrier` 是必须逐条对应的一项**：`MetalLayerEffects.swift:84` 用 `encoder.memoryBarrier(scope: .buffers)`，注释（:82-83）明确记录「没有它，某个 pass 会在前一个 pass 写一半时读，表现为效果只在描边结束时才出现」。HLSL 侧对应 `RWTexture2D` 的 UAV barrier（`ID3D12GraphicsCommandList::ResourceBarrier` 或 D3D12 的 `D3D12_RESOURCE_BARRIER_TYPE_UAV`）——遗漏会产生同类间歇性错误。这一条列为 Wave 3 的强制性回归项。
- **`GPUCanvas.swift` 不是纯 Metal**：它同时 `import CoreImage`，构造 `CIContext(mtlCommandQueue: queue, ...)`（:55），并用 `context.render(..., to: texture, commandBuffer: buffer, ...)`（:151、:279、:308、:324）把 CoreImage 直接渲染进 Metal 纹理。也就是说**画布合成是「CoreImage 求值 + Metal 纹理缓存」的混合体**：纹理缓存键是 `ObjectIdentifier(image)` + level（:96），90 帧淘汰（:36），内存压力源用 `DispatchSource.makeMemoryPressureSource`（:76）。移植时这一层要整体重写为「D3D12 纹理缓存 + 自己的合成 pass」，CoreImage 那部分没有直接替代物。**这是渲染层最大的单点工作量**，估计占 Wave 3 的 40%——**待验证**，方法：Wave 3 开始时先按 `GPUCanvas.swift` 的 583 行做一次逐方法清点。

### 1.5 Accelerate / vImage / vDSP → 自写 SIMD 或 IPP

| 事实依据 | 值 |
|---|---|
| `import Accelerate` | **2 文件** |
| `vImage` occ | 27 |

用到的 API 只有 6 个：

- `Document/PixelInvert.swift`：`vImageTableLookUp_Planar8`、`vImageMatrixMultiply_ARGB8888`、`vImage_Buffer`、`vImagePixelCount`、`vImage_Error`、`vImageNoFlags`
- `Rendering/DownsampleCache.swift`：`vImageScale_Planar8`、`vImageScale_ARGB8888`、`vImageHighQualityResampling`、`vImage_Buffer`、`vImageNoError`

**推荐：不引入 IPP（许可与分发成本），自写两个函数。**

- `vImageTableLookUp_Planar8` = 256 项 LUT 查表，`vImageMatrixMultiply_ARGB8888` = 4×4 矩阵乘；两者合计在 `PixelInvert.swift`（47 行）里。写成 C 内核里的一个循环即可，与既有 C 风格一致。
- `vImageScale_*` 是高质量降采样（`vImageHighQualityResampling`）。替代候选：WIC 的 `IWICBitmapScaler`（`WICBitmapInterpolationModeHighQualityCubic`）、Direct2D 的 `D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC`、Skia 的 `SKFilterQuality.High` + mipmap。**注意 `DownsampleCache.swift` 的语义是「逐级减半的锐利降采样」（`GPUCanvas.swift:85-86, 92-93` 注释明确说 CPU 侧 `DownsampleCache` 与 GPU 侧都是 sharp halvings），不是任意比例缩放**。逐级减半 + 每级 WIC/D2D 高质量核，与 vImage 的 Lanczos 结果不会逐位相同。**待验证**：用 `CompositorTests/DownsampleTests.swift`（82 行）的断言做对照，确定可接受的容差。若容差不足，自写 2-pass Lanczos-3（约 150 行 C，与既有 C 内核同构）。
- **不需要 vDSP**：全仓无 `import vDSP`。

### 1.6 CoreText / NSFont → DirectWrite

| 事实依据 | 值 |
|---|---|
| `import CoreText` | **1 文件**：`Document/Dither.swift`（:2），实际只用于 2 处：`CTLineCreateWithAttributedString`（:173）、`CTLineDraw`（:177）、`CTLineGetTypographicBounds`（:176） |
| `NSFont` | 8 文件 / 24 处 |
| 文本度量 API（`size(withAttributes:)`、`boundingRect(with:)`、`.ascender` 等） | 38 个文件命中，但集中在少数：`CameraRawControls.swift` 24、`TypeTool.swift` 16、`TypeControls.swift` 15、`NativeLayerList.swift` 12、`PSDText.swift` 12、`ProjectTabs.swift` 12 |
| `NSTextView` | 5 文件 / 18 处（`Rendering/InlineTextEditor.swift` 的 `CanvasTextView: NSTextView`） |
| `NSTextStorage` / `NSLayoutManager` | 1 处：`PSDText.swift:237`（用 `NSTextStorage` 量一段样本文本以估算 PSD 文本层尺寸） |

**推荐：DirectWrite（`IDWriteFactory` / `IDWriteTextLayout` / `DWRITE_FONT_METRICS`），经 `Vortice.Windows` 或 `Microsoft.Windows.CsWin32` 生成互操作。**

- 关键映射：`NSFont` → `IDWriteFontCollection::FindFamilyName`（注意 Compositor 存的是 **PostScript 字体名**，`docs/project-format.md:48`「PostScript font name」——DirectWrite 的家族名匹配与 PostScript 名不完全等价，需要 `IDWriteFontFace` 的 `GetInformationalStrings(DWRITE_INFORMATIONAL_STRING_POSTSCRIPT_NAME)` 反查）；`size(withAttributes:)` → `IDWriteTextLayout::GetMetrics`；`CTLineGetTypographicBounds` → `DWRITE_TEXT_METRICS` + `DWRITE_FONT_METRICS`（ascent/descent/lineGap/capHeight/xHeight）。
- `PSDText.swift:237` 用 `NSTextStorage` 量样本字符串来推断 PSD 文本层的包围盒。DirectWrite 侧用 `IDWriteTextLayout` 等价替换；**但结果的包围盒会不同**（见第 6 节风险 R1）。
- `Rendering/InlineTextEditor.swift`（558 行，`CanvasTextView: NSTextView` + `InlineTextEditor: NSView, NSTextViewDelegate`）是画布内的原地多行编辑器：WinUI 侧用 `TextBox` 叠加在画布控件上，或 `RichEditBox`；**待验证**：WinUI 的 `TextBox` 能否做到「随画布缩放而缩放字号但保持屏幕像素清晰」（Compositor 的文本编辑器是画布上的、跟随变换的）。
- **待验证**：`.NET` 的 DirectWrite 互操作首选库（`Vortice.Windows` / `TerraFX.Interop.DirectX` / `Microsoft.Windows.CsWin32`）的当前版本与许可。

### 1.7 Vision（ObjectSelection / SubjectRemoval）→ ONNX Runtime + 分割模型

| 事实依据 | 值 |
|---|---|
| `import Vision` | **2 文件** |
| `\bVision\b` 词边界命中 | 11 处（`SubjectRemoval.swift` 7、`ObjectSelection.swift` 4） |
| 实际用到的 API | **只有 2 个**：`VNImageRequestHandler`（`ObjectSelection.swift:39`、`SubjectRemoval.swift:31`）+ `VNGenerateForegroundInstanceMaskRequest`（`ObjectSelection.swift:40`、`SubjectRemoval.swift:32`） |

**推荐：ONNX Runtime + 一个前景/实例分割模型。**

- 依赖面极小：只有 4 个调用点。`VNGenerateForegroundInstanceMaskRequest` 是 Apple 的「前景实例掩膜」请求，返回一个可按实例选取的掩膜；`SubjectRemoval.swift:34` 要求 `result.allInstances` 非空。替代模型需要提供同等的「实例级」输出，而不是单一前景 mask。**待验证**：选择一个同时给出实例分离与高分辨率掩膜的 ONNX 模型（候选：U²-Net 系列 / ISNet / MobileSAM；需核对各自权重许可），并核对它是否能在 CPU 上把 30,000 px 边长上限（`DocumentLimits.swift:18`）内的图像跑到可接受耗时。
- `ObjectSelection.swift` 还有一条落在 CoreImage 上的后处理：`CIEdgePreserveUpsampleFilter`（:71）用于把低分辨率掩膜上采样到全分辨率并保边。需要一并实现（`guided filter` 或 `joint bilateral`）。
- **不要依赖 Windows AI APIs**：那些 API 依赖 Copilot+ PC 的 NPU 与特定硬件，会把这个功能变成「部分机器可用」。**待验证**：`Microsoft.Windows.AI.*` 的硬件门槛与许可条款是否允许 MIT 应用分发。
- `Document/MaskTracing.swift`（96 行）与 `Document/GuidedMatte.swift`（119 行）是纯算法，不受此影响。

### 1.8 ImageIO / UTType → WIC + 扩展名与魔数嗅探

| 事实依据 | 值 |
|---|---|
| `import ImageIO` | 5 文件：`ImageExporter.swift`、`ImageImporter.swift`、`ProjectStore.swift`、`RawImporter.swift`、`UI/NewCanvasSheet.swift` |
| `CGImageSource` / `CGImageDestination` | 5 文件 / 27 处、2 文件 / 8 处 |
| `import UniformTypeIdentifiers` | 11 文件 |
| `UTType` | 9 文件 / 30 处 |

**推荐：WIC 管容器与像素，自建格式嗅探管「这是什么文件」。**

- `RawImporter.swift:79-80` 用 `UTType(filenameExtension:)` + `conforms(to: .rawImage)` 判 RAW；`PSDReader.swift:10-22` 已经有 `matches(_ url:)` 与 `matches(magicOf:)` 两套（扩展名 + 魔数）。移植时把 `UTType` 的职责拆成「扩展名表 + 魔数表」，PSD 那套逻辑可以直接复用。
- `ProjectStore.swift:98-102` 用 `CGImageDestinationCreateWithData(..., UTType.png.identifier, ...)` 写 PNG，读侧用 `CGImageSourceCreateWithData` + `kCGImageSourceShouldCache` + `kCGImageSourceThumbnailMaxPixelSize: 96`（:173-188）。WIC 侧对应 `IWICBitmapEncoder`/`IWICBitmapDecoder`、`IWICBitmapSourceTransform` 或 `IWICBitmapScaler` 做缩略图。
- `ProjectStore.swift:179` 有一条硬校验：`(kCGImagePropertyDepth) <= 8`，即工程内嵌 PNG 必须是 ≤8 位。WIC 侧要读到同等元数据（`IWICBitmapFrameDecode::GetPixelFormat` + 位深）。
- **格式覆盖是风险点**：README:73 声称导入 JPEG、PNG、HEIC、TIFF、WebP、SVG、camera RAW、PSD/PSB。Windows 侧 HEIF/HEIC、AVIF、WebP 的**开箱可用性取决于目标 Windows 版本与是否安装了 Store 扩展**，SVG 更是 WIC 不原生支持。**待验证**：逐个格式在目标最低 Windows 版本（建议 Windows 11 22H2 起）上用裸 WIC 试开，列出需要附带编解码器的清单。SVG 建议用第三方库（如 SkiaSharp 的 `SKSvgCanvas` 或 `Svg.Skia`）。
- `IO/ImageFileDrop.swift`（61 行）用拖放导入，Windows 侧是 `DataTransferManager` / `DragDrop`。

### 1.9 Sparkle → Velopack 或自建签名更新器

| 事实依据 | 值 |
|---|---|
| `import Sparkle` | 3 文件：`CompositorApp.swift`、`IO/CompositorApplicationDelegate.swift`、`IO/UpdateDriver.swift`（156 行） |
| Sparkle 版本 | 2.10.0（`Package.resolved`），revision `eef1a539a373c1f1a320624b1130fc5de7b2e100` |
| 集成方式 | `SPUUpdater`（`CompositorApplicationDelegate.swift:13-14`）、`SPUUserDriver`（`UpdateDriver.swift:8`）、`SPUStandardUserDriver`（:9）、`SPUUpdatePermissionRequest`（:116）、`SPUDownloadData`（:120）、`SPUUpdaterDelegate.feedURLString`（:147-148） |
| 更新源 | 仓库根有 `appcast.xml` |

`UpdateDriver.swift` 是自定义 UI 层，不是核心逻辑：它把「发现更新 / 权限请求 / 发布说明 / 准备重启」转成自己的文案（:12-28），并解析 appcast 里的变更条目（:81 `changes(in:)`）。README:79 声明更新是「signed and notarized」。

**推荐：Velopack；如果 Ed25519 签名的 appcast 是硬需求，则退回 WinSparkle。**

- Velopack 提供 .NET 优先的安装器 + delta 更新，与 C# 技术栈一致。**待验证**：Velopack 是否支持 Ed25519/EdDSA 签名的更新清单（Sparkle 的默认签名机制），还是强制依赖 Authenticode；以及 delta 更新与代码签名的配合方式。
- WinSparkle 是 Sparkle 的 Windows 移植，理论上 appcast/EdDSA 语义最接近，但引入 C 依赖且是第三方维护。**待验证**：WinSparkle 的维护活跃度与 .NET 集成方式。
- 无论选哪个，**Windows 侧的代码签名（Authenticode / EV 证书）是新增成本**，macOS 侧的 Developer ID + notarization 流水线（`scripts/release.sh`）不能平移。签名证书的年费与流程应计入发布计划的非工程成本。

### 1.10 CryptoKit → System.Security.Cryptography

| 事实依据 | 值 |
|---|---|
| `import CryptoKit` | **1 文件**：`IO/ProjectDigest.swift`（33 行） |

**推荐：直接换 `System.Security.Cryptography`（SHA256 + `Convert.ToHexString`）。** 这是全仓最容易的一项移植：33 行、单文件、单一用途（工程摘要）。**待验证**：`ProjectDigest` 的输出是否被持久化进 `.comp`；若被写入 manifest，则必须逐字节一致（`SHA256.HashData` 与 .NET 的 `SHA256` 结果天然一致，风险极低）。

### 1.11 Observation / Combine → INotifyPropertyChanged / CommunityToolkit.Mvvm

| 事实依据 | 值 |
|---|---|
| `import Observation` | 8 文件 |
| `@Observable` | **12 文件**：`Document/ColorPalette.swift`、`Document/ColorRangeSelection.swift`、`Document/DocumentHistory.swift`、`Document/EditorSession.swift`、`Document/Filters.swift`、`Document/HueSaturation.swift`、`Document/Levels.swift`、`Document/ProjectWorkspace.swift`、`IO/RecentProjects.swift`、`UI/CommandPalette.swift`、`UI/HeldModifiers.swift`、`UI/KeyboardShortcuts.swift` |
| `import Combine` | **1 文件**：`UI/ProjectTabs.swift` |

**推荐：CommunityToolkit.Mvvm（MIT）的 `ObservableObject` + `[ObservableProperty]` + `[RelayCommand]`。**

- 观察面很小（12 个类），但**其中 `EditorSession.swift` 是 1,043 行的中枢**，`ProjectWorkspace.swift`（280）、`Filters.swift`（739）、`DocumentHistory.swift`（124）都挂在上面。移植的重点不是「换个特性标注」，而是**确认 SwiftUI 的细粒度依赖追踪（`@Observable` 只让读过的属性触发重绘）在 XAML 的粗粒度绑定下不会把画布拖慢**。Compositor 的画布重绘是性能敏感的（见 `docs/brush-performance.md` 的毫秒级基准），XAML 的绑定触发需要显式设计。**待验证**：在 Wave 4 用一次 4K 工程的拖动实测，确认 `INotifyPropertyChanged` 的触发面没有落到画布控件上。
- `UI/ProjectTabs.swift` 的 `import Combine` 需单独换（用 `event` 或 `ObservableCollection`）。

### 1.12 Dispatch / Swift Concurrency → Task / Channel / 线程池

| 事实依据 | 值 |
|---|---|
| Swift 并发规模 | `await` 219 处 / 41 文件；`async` 89 / 30；`actor` 82 / 44；`@MainActor` 47 / 28；`nonisolated` 227 / 74；`Sendable` 150 / 55 |
| `Task {}` / `Task.detached` | 73 处 / 30 文件、20 处 / 15 文件 |
| `DispatchQueue` | 11 文件 / 21 处 |
| `DispatchSource` | 2 文件 / 4 处（`GPUCanvas.swift:76` 的内存压力源；`IO/ProjectWatcher.swift`） |
| `NSLock` | 11 文件 / 11 处 |

`Sendable` 150 处说明这套代码的并发正确性是**编译期强制**的。移植到 C# 后这个保障消失，这是**结构性风险**，不只是换个 API：

**推荐**：

- `actor` → C# 类 + `SemaphoreSlim`/`lock`，或 `System.Threading.Channels` 的单一消费者。**推荐用 Channel 而不是锁**，因为 `actor` 的语义是串行化，Channel 能把这一点表达出来。
- `@MainActor` → `DispatcherQueue.TryEnqueue`（WinUI 3）。
- `Task.detached` → `Task.Run`，`Task {}` → `async` 方法。
- `DispatchSource.makeMemoryPressureSource`（`GPUCanvas.swift:76`）→ 没有直接对应物。**待验证**：用 `GC.GetGCMemoryInfo()` 轮询 + `JobObject` 内存限制，或 `CreateMemoryResourceNotification`（Win32，低内存通知）。这是「显存/内存压力下主动释放纹理缓存」这一行为的唯一替代路径。
- `Sendable` 的编译期保护 → 建议 (a) 启用 `<Nullable>enable</Nullable>`；(b) 对跨线程共享的像素缓冲统一走 `Surface` 所有权模型；(c) 关键路径加 `Debug.Assert` + 压测。
- **C 侧的 4 处 `dispatch_apply`**：`AdjustPixels.c:11`、`:348`、`:386`、`DitherPixels.c:12`。这是 C 内核**唯一**的平台依赖。替换为 OpenMP（`#pragma omp parallel for`，MSVC 原生支持 `/openmp`）或一个 20 行的 `std::thread` 分带函数。

### 1.13 其它 Foundation / AppKit 耦合

| 符号 | 文件 / 次数 | 建议替代 |
|---|---|---|
| `NSFileCoordinator` | 2 / 3 | 无对应物。作用见 `ProjectStore.swift:132-141`（协调读写、避免与外部写入冲突）。替代：`FileStream` + `FileShare` + 已有的 `ProjectWatcher.swift`（86 行，用 `DispatchSource` 做文件监视 → `FileSystemWatcher`） |
| `FileManager` | 4 / 6 | `System.IO` |
| `UserDefaults` | 6 / 14 | JSON 设置文件（`%LOCALAPPDATA%`） |
| `NSPasteboard` | 5 / 22 | WinRT `Clipboard` / OLE。**待验证**：图层跨工程拖放（README:30）在 Windows 上是否要同时支持 OLE 与 WinRT 剪贴板两种路径 |
| `ProcessInfo.processInfo.physicalMemory` | `DocumentLimits.swift:36-37` | `GlobalMemoryStatusEx`。**注意语义**：`documentPixelBudget = min(800_000_000, max(200_000_000, physicalMemory/16))`，Windows 上应保持同一条公式以维持跨平台行为一致 |
| `NSImage(systemSymbolName:)` | `EditorCanvas.swift:483` | Windows 侧无 SF Symbols，需自备图标资源（**待验证**：全仓 `systemSymbolName` 的使用点清单） |
| `NSBezierPath` | 5 / 24 | Skia `SKPath` / D2D `ID2D1PathGeometry` |
| `NSAlert` | 4 / 9 | `ContentDialog` |

---

## 2. 逐子系统可移植性评级

### 2.1 分类方法与实测结果

对 151 个应用侧 `.swift` 文件按「Apple 平台符号密度」分类。符号集为 37 个正则项：`CGImage|CGContext|CIImage|CIFilter|NSColor|NSView|NSImage|NSWindow|NSFont|NSBezierPath|NSApplication|NSApp|NSEvent|NSMenu|NSPasteboard|NSTableView|NSTextView|NSGraphicsContext|CGAffineTransform|CGPath|CGColorSpace|UTType|MTLDevice|VN*Request|vImage*|NSAlert|NSPanel|NSScrollView|NSFontManager|NSWorkspace|NSFileCoordinator|CGImageDestination|CGImageSource|CGColor|NSRect|NSSize|NSRange`。

| 桶 | 判定 | 文件数 | 行数 | 平均密度（命中/千行） |
|---|---|---:|---:|---:|
| A 纯逻辑 | 0 命中 | 41 | 5,449 | 0 |
| B 轻耦合 | 1–10 命中 | 68 | 13,990 | 22.2 |
| C 重耦合 | >10 命中 | 42 | 16,317 | 77.5 |
| — | 合计（应用 Swift） | 151 | 35,756 | — |
| C 内核 | `.c` / `.h` | 9 / 9 | 2,688 / 310 | 平台符号 0（见 §0） |

按子系统拆分（行数）：

| 子系统 | A | B | C | 合计 |
|---|---:|---:|---:|---:|
| `Document/` | 2,018 | 7,032 | 5,233 | 14,283 |
| `UI/` | 3,174 | 2,390 | 3,407 | 8,971 |
| `Rendering/`（Swift） | 138 | 1,249 | 6,158 | 7,545 |
| `IO/` | 119 | 2,831 | 1,147 | 4,097 |
| 根（`CompositorApp` + `ContentView`） | 0 | 488 | 372 | 860 |

耦合最重的 20 个文件（行数 / 命中数）：

| 文件 | 行 | 命中 |
|---|---:|---:|
| `Rendering/EditorCanvas.swift` | 3,153 | 242 |
| `UI/NativeLayerList.swift` | 1,388 | 127 |
| `Rendering/GPUCanvas.swift` | 583 | 76 |
| `Rendering/InlineTextEditor.swift` | 558 | 52 |
| `Document/BrushStroke.swift` | 1,019 | 47 |
| `Rendering/TransformOverlay.swift` | 468 | 44 |
| `Document/LayerEffects.swift` | 666 | 39 |
| `Document/Distort.swift` | 400 | 33 |
| `IO/CompositorApplicationDelegate.swift` | 224 | 33 |
| `CompositorApp.swift` | 372 | 28 |
| `IO/ImageExporter.swift` | 252 | 27 |
| `Document/LayerMask.swift` | 427 | 27 |
| `UI/ProjectTabs.swift` | 429 | 26 |
| `Rendering/TiledLayerRenderer.swift` | 420 | 25 |
| `Document/TypeTool.swift` | 473 | 24 |
| `Document/SubjectRemoval.swift` | 144 | 22 |
| `UI/CameraRawSlider.swift` | 207 | 21 |
| `Document/Filters.swift` | 739 | 20 |
| `UI/FloatingPanel.swift` | 177 | 20 |
| `Document/PixelAdjust.swift` | 66 | 19 |

### 2.2 评级表

评级含义：★5 = 近乎机械翻译，★1 = 需要重新设计。

| 子系统 | 行数 | 评级 | 依据 |
|---|---:|---|---|
| **C 像素内核** | 2,688 `.c` + 310 `.h` | ★★★★★ | 24 个导出函数的签名全部是 `uint8_t*/float*/size_t/int/double`（`AdjustPixels.h:8-119`、`WandPixels.h`、`DitherPixels.h` 等），**零 Apple 类型**；`*.c` 中 `CGContext|CGImage|CoreGraphics|vImage|Accelerate|NSColor|NSView|CFRelease` 命中数为 **0**；仅 4 处 `dispatch_apply` 需替换；依赖头文件只有 `stdint.h/stddef.h/math.h/stdlib.h/string.h/float.h/dispatch/dispatch.h`。**直接编译，不重写。** |
| **IO / 工程格式** | 4,097 | ★★★★☆ | 已经是「文件系统 + JSON + PNG」：`ProjectStore.swift` 用 `JSONDecoder`/`JSONEncoder` 读写 manifest（限 4 MiB，:111）、用 `CGImageSource/Destination` 读写 PNG（限 512 MiB/资产，:167）、限 30,000 px 边长与 1 亿像素（`docs/project-format.md:9`）。耦合点集中在 3 个函数（读写 PNG、读元数据、文件协调），A+B 类占 2,950 / 4,097 行。 |
| **IO/PSD** | 1,999 | ★★★★☆ | 6 个文件全是字节游标解析（`PSDReader.swift` 有 `PSDCursor` 结构，:408-436 逐字节 `u8/u16/i16/u32`）。**解析逻辑与平台无关**；只有 `PSDText.swift:237` 的 `NSTextStorage` 和通道解码涉及平台。测试侧已有 `PSDFixture.swift`（582 行）与 `PSDVectorFixtures.swift`（23 行）做二进制 fixture 构造，可直接搬到 C#。 |
| **Document（模型与算法）** | 14,283 | ★★★☆☆ | A+B 类 9,050 行（63%）是可翻译的逻辑；C 类 5,233 行里真正难的是 `BrushStroke.swift`（1,019，47 命中）、`LayerEffects.swift`（666，39）、`Distort.swift`（400，33）、`LayerMask.swift`（427，27）、`Filters.swift`（739，20）、`CameraRaw.swift`（602）。这些文件的算法本身与平台无关，耦合来自「用 `CGImage` 当像素容器」和「用 `CIFilter` 当算子」。**换掉这两样，它们就退回 A/B 类。** |
| **Rendering** | 7,545 Swift + 2,688 C | ★★☆☆☆ | 最难的子系统：C 类 6,158 / 7,545 行（82%）。`EditorCanvas.swift` 3,153 行是**自绘视图 + 完整输入状态机**（12 个鼠标 override、光标矩形、tracking area、`cacheDisplay`）；`GPUCanvas.swift` 583 行是 CoreImage+Metal 混合；`TiledLayerRenderer.swift` 420 行是分块合成；还有 17 个 MSL kernel。**这是移植的技术核心。** |
| **UI** | 8,922 | ★★☆☆☆ | 49 个文件、**57** 个 `View` 结构体、**11** 处 AppKit 桥接、**12** 个 `NSView` 子类。（此前写作 62/21/19，那是**全仓非测试口径**而非 `UI/` 单独口径，见 `06-ui-calibration.md` 附录 C。）**没有机械转换路径**：SwiftUI 的 `@Observable` 细粒度追踪、`NSViewRepresentable` 嵌入、`NSPanel` 语义、`NSTableView` 拖放都需要重新设计。C 类 3,407 行 + 手工布局成本。 |
| **根（App 壳 + 菜单）** | 860 | ★★☆☆☆ | `CompositorApp.swift` 372 行（SwiftUI `App` + `NSApplicationDelegateAdaptor` + 菜单 + Sparkle 接线）、`ContentView.swift` 488 行（主布局）。菜单结构本身可迁移，接线全换。 |
| **测试** | 14,994 | ★★★☆☆ | 值钱的不是代码而是**语义**。81 个文件里 43 个已在 `scripts/ci-tests.sh` 显式命名（见 §0.1）。算法断言可机械搬运；平台断言（`CursorTests` 245 行、`FloatingPanelTests` 151、`SliderSnapTests` 29、`TitleBarDragTests` 75）必须重写。 |

### 2.3 一句话结论

**这个移植的成本分布严重偏向「呈现层」而不是「算法层」：**

- 「算法 + 数据」一侧（C 内核 2,688 + Document A/B 9,050 + IO A/B 2,950 + IO/PSD 1,999）≈ **16,700 行，可机械翻译**。
- 「呈现 + 像素」一侧（Rendering 全量 10,233 + UI 8,971 + 根 860 + Document C 5,233）≈ **25,300 行，需重新设计**。

---

## 3. C# 解决方案与工程布局

### 3.1 目录与项目

```
Compositor.sln
│
├─ src/
│  ├─ Compositor.Core/                    net9.0，无 Windows API
│  │    图层模型（图层/组/蒙版/剪贴/调整层/效果）、几何（Rect/Point/Matrix）、
│  │    混合模式枚举（23 种）、文档限制（DocumentLimits 等价物）、
│  │    .comp 读写（manifest v1–11）、ProjectDigest、历史/撤销
│  │
│  ├─ Compositor.Pixels/                  net9.0
│  │    C 内核的托管封装：LibraryImport + unsafe Span<byte> 视图
│  │
│  ├─ Compositor.Raster/                  net9.0-windows
│  │    Surface 抽象（CGContext 替身）：位图后备、矩阵栈、裁剪栈、混合模式、绘制图像
│  │    SkiaSharp 实现 + 可选 Direct2D 实现
│  │    降采样（DownsampleCache 等价物）、可分离混合（SeparableBlend 等价物）
│  │
│  ├─ Compositor.Codecs/                  net9.0-windows
│  │    WIC 编解码 + 元数据、格式嗅探（扩展名 + 魔数）、RAW develop、SVG
│  │
│  ├─ Compositor.Psd/                     net9.0
│  │    PSD/PSB 解析（纯托管，字节游标，无平台依赖）
│  │
│  ├─ Compositor.Compute/                 net9.0-windows
│  │    ComputeSharp：17 个 kernel 的 HLSL 对应物 + 纹理缓存 + 合成 pass
│  │
│  ├─ Compositor.Text/                    net9.0-windows
│  │    DirectWrite 布局与度量、字体匹配（PostScript 名）、段落框换行
│  │
│  ├─ Compositor.Ai/                      net9.0-windows
│  │    ONNX Runtime 分割模型、掩膜上采样、MaskTracing
│  │
│  ├─ Compositor.App/                     net9.0-windows10.0.19041.0，WinUI 3
│  │    XAML 壳、面板、对话框、菜单、快捷键、命令面板、自绘画布控件
│  │
│  └─ Compositor.Update/                  net9.0-windows
│       更新器接线（Velopack）
│
├─ native/
│  └─ compositor_pixels/
│        CMakeLists.txt 或 compositor_pixels.vcxproj
│        *.c / *.h：**原样复制**自 src/Compositor/Compositor/Rendering/
│        唯一改动：dispatch_apply → OpenMP / 线程池分带
│        产物：compositor_pixels.dll（薄 C ABI）+ 可选静态库
│
├─ tests/
│  ├─ Compositor.Core.Tests/              xUnit：模型、.comp 往返、校验规则
│  ├─ Compositor.Parity.Tests/            xUnit：C 内核与 golden 向量逐位/容差比对
│  ├─ Compositor.Raster.Tests/            xUnit：混合模式、降采样、Surface
│  ├─ Compositor.Psd.Tests/               xUnit：迁移 PSDFixture 的二进制 fixture
│  └─ Compositor.App.UiTests/             WinAppDriver 或 Playwright-for-Windows：截图与交互
│
├─ tools/
│  └─ golden/                             从 macOS 生成 golden 图像与向量的脚本 + 数据
│
└─ docs/port/                             本系列文档
```

### 3.2 平台无关内核与 Windows 壳的边界

**边界规则（一句话）：`Compositor.Core`、`Compositor.Psd`、`Compositor.Pixels` 三个项目禁止引用任何 `net*-windows` TFM 或 `Microsoft.Windows.*` 包；它们的单元测试在 `windows-latest` 与 `ubuntu-latest` 上都要绿。**

这样划线的依据：A 类（纯逻辑 5,449 行）+ IO/PSD（1,999 行）+ C 内核（2,688 行）≈ **10,136 行完全不依赖平台的代码**，它们承载了这个应用全部的格式契约与算法正确性。让它们跑在 Linux 上，等于用最低的成本守住「.comp 契约 + 算法语义」这条主线，而 Windows 特有的部分（渲染、UI、编解码）由 parity 测试与截图测试守。

具体边界：

| 关注点 | 归属 | 理由 |
|---|---|---|
| 图层树、变换、混合模式**枚举**、蒙版、剪贴、调整层参数、效果参数 | Core | 全是值与算法（`docs/project-format.md` 第 7–11 版规定的字段） |
| `.comp` manifest 的解析、校验、拒绝规则 | Core | `ProjectStore.swift:146-201` 的 `validate` 全是纯逻辑 |
| 实际像素缓冲 | Pixels / Raster | `Surface` 持有 `byte[]`/`Span<byte>`，不持有 GPU 或 WIC 句柄 |
| PNG 编解码 | Codecs | WIC 是 Windows 专属 |
| 混合模式**实现** | Raster + Compute | CPU 与 GPU 两份实现，共享同一张规格表 |
| 图层合成 | Raster（CPU 回退）/ Compute（GPU） | 与 `GPUCanvas` 的 CPU/GPU 双路径对应 |
| 输入与布局 | App | |
| 撤销栈 | Core（`DocumentHistory.swift` 124 行是纯数据） | |

**一个必须定下的接口：`Surface`。**

`CGContext` 113 处、`CGImage` 500 处的替代物。建议最小面：

```csharp
public abstract class Surface : IDisposable {
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public PixelFormat Format { get; }          // 仅 PremultipliedBgra8888 / Gray8 两种
    public Span<byte> Pixels { get; }           // CPU 路径直接可写
    public Matrix3x2 Transform { get; set; }    // 对应 context.ctm
    public void Save(); public void Restore();
    public void Clip(Rect rect); public void ResetClip();
    public void SetBlendMode(BlendMode mode);
    public void SetAlpha(float alpha);
    public void DrawImage(Surface source, Rect dest);
    public Surface CreateCompatible(int w, int h);   // 对应 CGContext(data: nil, ...)
}
```

`SeparableBlend.swift:22-46` 与 `MetalLayerEffects.swift:64-66`（`BrushRaster.context(width:height:mask:)` + `BrushRaster.draw(...)`）是这套接口要覆盖的两个真实调用样本——**建议在 Wave 0 直接用它们做接口的形状验证**。

### 3.3 C 内核如何链接：推荐 MSVC 编译现成 C

**推荐：用 MSVC（v143/v144 工具集）把现有 9 个 `.c` 编译成一个导出薄 C ABI 的 `compositor_pixels.dll`，C# 通过 `LibraryImport` 调用。不重写为 C#，也不做 NativeAOT-unsafe。**

理由（按证据强度排序）：

1. **C 内核里没有一行 Apple 代码**。对 `Rendering/*.c`、`*.h` 搜索 `CGContext|CGImage|CoreGraphics|vImage|Accelerate|NSColor|NSView|CFRelease` → **0 命中**（`grep` 工具返回 `No matches found`）。头文件依赖是 `stdint.h`、`stddef.h`、`math.h`、`stdlib.h`、`string.h`、`float.h`。
2. **导出签名已经是裸指针 ABI**。`AdjustPixels.h` 里 24 个声明全部形如 `void adjust_black_white(uint8_t *rgba, size_t width, size_t height, size_t stride, const float *weights, int tint, double tintHue, double tintSaturation)`（:22-23）；`CameraRawStage` 是纯 POD（:35-38）。**这就是一个现成的 C ABI**，`LibraryImport` 可以直接绑定，不需要 `Marshal`、不需要 `unsafe struct`。
3. **唯一改动是 4 处 `dispatch_apply`**：`AdjustPixels.c:11`、`:348`、`:386`、`DitherPixels.c:12`。用 `#ifdef _OPENMP` 包一层 `#pragma omp parallel for`（MSVC `/openmp`），或写一个 20 行的分带辅助函数。**改动量按行算是个位数。**
4. **重写为 C# 会更贵且更容易错**。这 2,688 行里有 677 处 `float` 运算和 246 处 `uint8_t`。算法包含 `camera_raw_compose` 的表合成、`adjust_camera_raw_effects` 的局部对比/去雾/发光/晕影（`AdjustPixels.h:81-86` 有 16 个 `double` 参数）等重度数值路径。逐条重写必须**保持浮点运算的顺序**才能通过现有测试向量；任何一处结合律改变都会产生最后一个 bit 的差异，而现有测试（`CompositorTests` 14,994 行）正是在断言像素级的输出。用编译器原样编译，这个风险为零。
5. **NativeAOT-unsafe-C# 在这里没有收益**。它的价值是「消除原生二进制、单文件发布、启动快」；但这个应用本身要分发 `libSkiaSharp.dll`（如果选 SkiaSharp）、ONNX Runtime 原生库、以及可能的 RAW 解码器——**原生二进制已经在分发包里了**，多一个 200 KB 的 `compositor_pixels.dll` 不改变分发形态。

**风险与缓解**：

- MSVC 与 Apple clang 的浮点行为可能不同（`/fp:precise` vs clang 默认、FMA 收缩、`float` 中间精度、`long double` 宽度差异）。**这是本方案唯一的真实风险**，列为第 6 节 R8，Wave 0 必须有实测。
- 缓解手段：(a) `#pragma float_control(precise, on)` 或 `/fp:strict`；(b) 禁用 FMA 收缩（`/fp:contract` 关闭或 `#pragma fp_contract(off)`）；(c) parity 测试用容差（先测出实际最大 LSB 差，再定容差），**但 Camera Raw 的 LUT 合成路径要求 0 容差**，因为它是查表。
- 建议的调用形态（`LibraryImport` 源生成）：
  ```csharp
  [LibraryImport("compositor_pixels", EntryPoint = "adjust_black_white")]
  private static partial void AdjustBlackWhite(
      ref byte rgba, nuint width, nuint height, nuint stride,
      ref float weights, int tint, double tintHue, double tintSaturation);
  ```
- **待验证**：是否需要 `DisableRuntimeMarshalling`；`nuint` 与 C 的 `size_t` 在 x64 上是否直接对应（理论上是，但要在 Wave 0 用 `adjust_gradient_map` 跑一次真调用确认）。

### 3.4 目标框架与工具链基线（建议）

| 项 | 建议 | 依据 / 备注 |
|---|---|---|
| .NET | **.NET 9（LTS 口径以实际发布为准）** | **待验证**：当前 LTS/STS 版本与 Windows App SDK 的匹配关系 |
| UI 框架 | WinUI 3（Windows App SDK），Avalonia 备选 | §1.1 的决策闸门 |
| 最低 Windows | Windows 11 22H2（build 22621） | 与 WinUI 3 的支持矩阵绑定，**待验证** |
| 目标架构 | x64 + arm64 | macOS 侧只出 arm64（`ARCHS = arm64`），Windows 侧需两个；arm64 可用 Skia/ONNX 的 arm64 构建 |
| 原生构建 | MSVC（v143+）+ CMake | §3.3 |
| 测试框架 | xUnit | 对应 XCTest；`CompositorTests` 14,994 行需翻译 |
| CI | GitHub Actions `windows-latest` | **注意**：`windows-latest` 无 GPU，GPU kernel 测试需要 (a) WARP 软件适配器（D3D12 WARP 支持 compute），或 (b) 自托管 runner。**待验证**：ComputeSharp 在 WARP 上的可用性 |

---

## 4. 分波次执行计划

### 4.0 工作量口径与估计依据

**口径**：「单人全职」= 1 名熟悉 C# 与 Windows 图形栈的工程师，每周 5 个工作日；「多人并行」= 3–4 人，但**受关键路径约束**（见 §5.3）。

**估计依据**（两个来源，互为校验）：

**(a) 实测代码分桶 × 迁移系数**。系数是「每千行产出多少个人周」，含翻译、接线、单测、集成与返工：

| 类别 | 行数 | 系数（人周/千行） | 区间内的工作量（人周） | 系数依据 |
|---|---:|---|---:|---|
| C 内核 | 2,688 | 0.15–0.30 | 0.4–0.8 | 只有 4 处 `dispatch_apply`；头文件已指出全部 24 个函数签名；`AdjustPixels.h:5-119` 的注释同时充当规格说明 |
| A 纯逻辑 | 5,449 | 0.35–0.60 | 1.9–3.3 | 无平台符号；Swift→C# 的语法距离小（结构体、枚举、`Codable`→`System.Text.Json`）；测试可机械搬运 |
| B 轻耦合 | 13,990 | 0.70–1.20 | 9.8–16.8 | 需替换 `CGRect/CGPoint/CGAffineTransform`（24 文件/61 处）、`NSColor`（19/124）、`UTType`（9/30）等取值层 |
| C 重耦合 | 16,317 | 1.80–3.20 | 29.4–52.2 | 需重写渲染/输入/文本路径；`Rendering/` 的 C 类密度 77.5 命中/千行（§2.1） |
| 应用侧小计（A+B+C 类，含上面单独列出的 C 内核） | **38,444**（= 35,756 行 Swift + 2,688 行 C） | — | **41.5–73.1** | |
| 测试 | 14,994 | 0.35–0.70 | 5.2–10.5 | 算法断言可搬运，平台断言重写；43 个 suite 名单已在 `scripts/ci-tests.sh` 现成 |
| **自下而上合计** | **53,438** | — | **46.7–83.6** | |

> **两个口径的对账**：自下而上按行×系数得 **46.7–83.6 人周**；而 §4.1–4.10 按波次逐项累加得 **65–100 人周**。差额来自波次口径额外包含的部分：跨波集成与联调、golden 数据集的两端生成与比对、`CompositorTests` 14,994 行的**逐套件搬运与审查**（§6 R11）、以及上游同步的持续成本（§7）。**总工期以波次口径为准（65–100 人周）；行×系数口径用于校验单个子系统的估计是否离谱**——若某一波的实际耗时显著偏离同类的行×系数值，说明该子系统有未被识别的耦合。

**(b) UI 层单独建模——不能按行线性换算**。理由：SwiftUI 的 62 个 `View` 结构体、21 处 `NSViewRepresentable` 桥接、19 个 `NSView` 子类里，**只有一部分有 XAML 对应物**；`EditorCanvas.swift`（3,153 行）和 `NativeLayerList.swift`（1,388 行）是自绘控件，XAML 侧不是「翻译」而是「用另一套原语重新实现同一行为」。

UI 层规模口径：

| 组成 | 行数 |
|---|---:|
| `UI/` 全部 49 个文件 | 8,971 |
| `Rendering/EditorCanvas.swift` | 3,153 |
| `Rendering/InlineTextEditor.swift` | 558 |
| `Rendering/TransformOverlay.swift` | 468 |
| `Rendering/BrushCursorOverlay.swift` + `CanvasLinesOverlay.swift` + `SampleRingOverlay.swift` | 139 |
| `Rendering/CanvasViewport.swift` + `NavigatorGeometry.swift` | 138 |
| `CompositorApp.swift` + `ContentView.swift` | 860 |
| `IO/CompositorApplicationDelegate.swift`（程序化菜单） | 224 |
| `IO/ProjectController+ExternalChanges.swift` | 113 |
| **UI 层合计** | **14,624** |

占应用总量 38,763 行的 **37.7%**；若把 `Document/` 中直接服务于交互的文件（`TypeTool.swift` 473 中 24 个平台命中、`BrushStroke.swift` 1,019 中 47 个）计入，呈现与交互面约 **17,000 行 ≈ 44%**。

**转换系数（明确给出，并标注为待标定）**：

- **声明式部分**（`UI/` 内 **57** 个 `View` 结构体；全仓非测试口径 59 个。**注意行数口径**：`UI/` 原始行数按「以换行结尾的文件」计为 **8,922**，本文件早期数字 8,971 与 62 个结构体均为**全仓/工具偏差口径**，详见 `06-ui-calibration.md` 附录 C）：**边际**产出率 **0.8–1.4 人周/千行**，**外加一次性可复用 XAML 基建 F_decl = 1.3–2.7 人周**。上限依据修正：原写「`NSViewRepresentable` 21 处」是全仓口径，`UI/` 单独只有 **11** 处（另 4 处已计在自绘桶内），**该上限依据原先被高估约 2 倍**；下限依据：纯表单/面板（`CameraRawSlider.swift` 207 行、`SliderSnap.swift` 43 行这类）迁移接近机械。
- **自绘控件部分**（`EditorCanvas.swift` 3,153 + `NativeLayerList.swift` 1,388 + `InlineTextEditor.swift` 558 + `TransformOverlay.swift` 468 + 覆盖层 139 ≈ **5,699 行**）：**该桶内部是两个相差 27 倍的子类，不能用单一费率**——「纯绘制型」占 **10.6%**，3.0–4.7 人周/千行（不含 marching-ants LOD）；「输入状态机型」占 **89.4%**，**尚无度量**。原 2.5–4.0 的区间**只对前者成立**。依据（后者）：完整输入状态机（`EditorCanvas.swift` 12 个鼠标 override + 4 个 `scrollWheel`/`magnify`/`keyDown`/`keyUp`、`resetCursorRects()`、`updateTrackingAreas()`、`cacheDisplay(in:to:)`）与拖放协议（`NativeLayerList.swift` 41 处 `NSTableView`/pasteboard/dragging 命中）。
- UI 层合计：**声明式部分 ≈ 8,900 行 × 0.8–1.4 = 7.1–12.5 人周；自绘控件部分 ≈ 5,706 行 × 2.5–4.0 = 14.3–22.8 人周；小计 21.4–35.3 人周**。
- **这个 UI 系数口径与 (a) 的 C 类口径是两套独立算法**（(a) 按文件级平台耦合分桶，(b) 按 UI 内部形态二分），两者在 UI 上的结果会重叠，**不要相加**。对账方法见 §4.11「UI 层是最大成本项」第 3 条：波次口径得 24.7–35.5 人周，系数口径得 21.4–35.3 人周，二者重合，说明 UI 的估计是稳的。

> **待验证（已部分标定，2026-10，见 `docs/port/06-ui-calibration.md`）**：两个转换系数的**桶级适用性**已由结构度量复核。
>
> **① 原区间都得到支持，但用法必须限定。** 声明式 0.8–1.4 人周/千行**成立**，但只作为「可复用件摊销之后的**边际**代码产出率」，**不得用于单文件换算**；桶级须另加一次性基建项 **F_decl = 1.3–2.7 人周**。自绘控件 2.5–4.0 未被推翻，但**只对「纯绘制」子类**（占桶 10.6%）成立；占桶 **89.4%** 的「输入状态机型」**尚未度量**。按修正口径重算总工期 **66.3–102.7 人周**，与 §4.11 的 65–100 差异 <3%。
>
> **② ⚠️ 本节原来给出的验证方法已废弃，按字面执行会错 3.9–13.9 倍。** 原文建议「用 `FilterSheet.swift` 标定声明式系数」。实测该文件单次实施为 2.2–4.5 人周，除以它自己的 0.406 千行得 **5.4–11.1 人周/千行**。原因不是面板难，而是其中约 60%（1.3–2.7 人周）是**一次性可复用控件**（滑块行控件、双色轨道滑块、共享标签宽度、色板模板、10 个私有 DSL 行为），会在 `UI/` 其余 30+ 个面板上复用。**若把这个单文件费率乘上 8.86 千行，总工期会被推到 106–186 人周——足以导致错误否决整个移植项目。**
>
> **③ ⚠️ `TransformOverlay.swift` 也不适合作为自绘桶的标定物。** 实测它有 **0 个 mouse/key override、0 个 tracking/cursor override**（`hitTest` 返回 `nil`，纯绘制）——而原文给 2.5–4.0 的上限依据恰恰是「完整输入状态机」。自绘桶内两类相差 **27 倍**（输入状态机型 9.0 绘制调用/千行、38 个输入 override；纯绘制型 245.4/千行、0 输入），而 `TransformOverlay` 贡献桶内 56.7% 的绘制调用却只占 8.2% 的行。**用一个 10.6% 类别的样本去标定 89.4% 是另一类别的桶。** 替代方案见 `06-ui-calibration.md` §5.3（改用 `EditorCanvas` 最小切片：缩放/平移 + ≥6 个指针 override + cursor/tracking 三件套 + 三档 DPI）。
>
> **④ 敏感度**：自绘系数摆动 8.6 人周，是声明式（5.3 人周）的 **1.61 倍**；总区间 35 人周里 **13.9 人周（39%）来自这两个系数**。且这是**单侧风险**——现有证据只支持「不比 0.8/2.5 更便宜」，给不出更便宜的证据。

### 4.0.1 未实测成本（结构度量覆盖不到，但确定存在）

以下成本在 65–100 人周里**没有单价**，只以桶级系数的形式被间接吸收（详见 `06-ui-calibration.md` §8）：

- 逐面板**验收**成本：约 30 个面板 × 0.15–0.35 人周 ≈ **4.5–10.5 人周**。这解释了为什么 W6 的波次口径（含截图存证与 macOS 并列比对）比系数口径高 1.8–4.4 倍。
- WinUI 3 缺失的开发者体验：无 SwiftUI 的 XAML 热重载、`x:Bind` 编译期错误诊断、DPI 适配与无障碍属性均需手工处理。
- 上游只读的持续追踪成本（§7 明令不提 Swift PR，因此每次上游变更都靠人工跟）。

### 4.1 Wave 0 —— 垂直切片（骨架 + 一个真滤镜端到端）

| | |
|---|---|
| **范围** | ① `Compositor.sln` + 全部 csproj 骨架落盘；② `native/compositor_pixels` 用 MSVC 编译通过，产出 DLL；③ `LibraryImport` 绑定 + 第一个真调用；④ `.comp` 读取最小路径（`manifest.json` + `images/*.png`，只支持 v11 的顶层图层，无组无蒙版），经 WIC 解码成一个图层；⑤ 在 WinUI 3 / Avalonia 自绘画布上把这一层画出来；⑥ GitHub Actions `windows-latest` 上 build + test 全绿；⑦ **1.1 节的框架决策闸门关闭**；⑧ **UI 转换系数标定**（§4.0 的待验证项） |
| **进入标准** | 无（起点） |
| **退出标准**（可执行，逐条可判真假） | 1. `dotnet build Compositor.sln -c Release` 在干净 `windows-latest` 上 0 error；<br>2. `dotnet test` 通过 **≥1 个 C 内核 parity 测试**：对 `adjust_black_white`（`AdjustPixels.h:22`）用 `CompositorTests` 里的权重向量（Photoshop 默认 40% 红 → 40% 灰，见 `AdjustPixels.h:20`）跑出与 macOS golden **逐位相同**的结果；<br>3. 手动打开一个 3 图层、无组无蒙版的 `.comp`，窗口里显示的合成图像与 macOS 上同一工程的导出 PNG **逐像素差 ≤1 LSB**；<br>4. 两份 UI 转换系数的标定数据落盘（人日/千行：声明式一个、自绘控件一个）；<br>5. 决策闸门的三项测量（输入延迟、DPI 变化、无边框拖动）有书面结论 |
| **依赖** | 无 |
| **涉及原文件** | `Rendering/AdjustPixels.c`（1,380 行，只取 `adjust_black_white`）、`Rendering/AdjustPixels.h`、`Compositor-Bridging-Header.h`、`IO/ProjectStore.swift`（279 行，取 manifest 解析）、`docs/project-format.md`、`UI/FilterSheet.swift`（407 行，标定用）、`Rendering/TransformOverlay.swift`（468 行，标定用）、`CompositorTests/ProjectTests.swift`（313 行）作为向量来源 |
| **工作量（单人全职）** | **3–5 周**（其中 1 周是框架决策 spike + 标定，可与其他工作并行） |
| **风险** | 若 §3.3 的浮点 parity 在 Wave 0 就失败，整个「原样编译 C 内核」的策略要在 Wave 1 前重估——**这是本计划最高优先级的早期失败信号，故意放在 Wave 0** |

### 4.2 Wave 1 —— 文档内核与 `.comp` 契约

| | |
|---|---|
| **范围** | ① `Compositor.Core` 全部模型：图层、组、可见性继承、变换（origin/size/旋转/翻转/sampling）、不透明度、23 种混合模式**枚举**、蒙版、剪贴蒙版（`maskSourceID`）、调整层（12 种 kind）、图层效果（6 种）、文本元数据与 `colorRuns`/`fontRuns`、guides、shape；② `.comp` 读取 v1–v11 **全部**，写入 v11；③ 全部校验与拒绝规则；④ 撤销/重做（`DocumentHistory.swift` 124 行）；⑤ `ProjectDigest`（`CryptoKit` → `System.Security.Cryptography`） |
| **进入标准** | Wave 0 退出标准 1–3 全部达成 |
| **退出标准** | 1. `CompositorTests` 中 `ProjectTests`（313）、`ProjectWorkspaceTests`（180）、`LayerTests`（559）、`GroupTests`（191）、`LayerMaskTests`（305）、`LiveMaskTests`（131）、`MaskTransformTests`（135）、`LayerAppearanceTests`（135）、`AdjustmentLayerTests`（269）、`LayerEffectsSurviveTests`（157）、`GuideTests`（241）、`HistoryTests`（160）、`CanvasSizeTests`（108）、`ImageSizeTests`（83）、`CropTests`（167）、`ImageTrimTests`（181）、`CanvasRotationTests`（76）、`ExternalChangeTests`（175）、`SmartEditTests`（82）的等价断言在 xUnit 下通过（**合计约 3,866 行测试的等价物**）；<br>2. **v1–v11 全版本往返**：对每个版本各构造一个工程 → 读取 → 以 v11 写出 → 再读取，两次读取的模型对象相等；<br>3. 拒绝规则逐条有测试：非法版本（`ProjectStore.swift:156`）、非 sRGB（:201）、缺资产（:167）、路径逃逸、超限（4 MiB manifest / 512 MiB 资产 / 30,000 px / 1 亿像素，`docs/project-format.md:9`）；<br>4. `Compositor.Core` 在 `ubuntu-latest` 上构建与测试通过（证明无平台泄漏） |
| **依赖** | Wave 0 |
| **涉及原文件** | `Document/` 全部 58 个文件中与模型/持久化相关的部分（重点：`DocumentHistory.swift` 124、`LayerGroups.swift` 325、`LayerMask.swift` 427、`LiveLayerMask.swift` 239、`LayerAppearance.swift` 139、`LayerTransform.swift` 247、`LayerAdjustment.swift` 214、`Guides.swift` 316、`ShapeTool.swift` 225、`DocumentLimits.swift` 43）；`IO/ProjectStore.swift` 279、`IO/ProjectDigest.swift` 33、`IO/RecentProjects.swift` 34、`IO/ProjectWatcher.swift` 86、`IO/ProjectController.swift` 420、`IO/ProjectController+ExternalChanges.swift` 113；`docs/project-format.md`（52 行，**逐条对照**）；`CompositorTests/` 上述 19 个文件 |
| **工作量（单人全职）** | **6–9 周** |
| **关键判据** | `docs/project-format.md` 是唯一不需要猜测的规格文档，**这一波应当是全项目确定性最高的部分**。若进度超出区间，说明模型层有隐藏耦合，需要重新评估整体估计 |

### 4.3 Wave 2 —— CPU 光栅管线与全部像素内核

| | |
|---|---|
| **范围** | ① `Surface` 抽象与其 SkiaSharp/D2D 实现；② 23 种混合模式的 CPU 实现 + `SeparableBlend` 等价物（**必须在 sRGB 空间**，见 `SeparableBlend.swift:14-16`）；③ 剩余 8 个 C 内核接入（`DitherPixels` 547、`HealPixels` 260、`WandPixels` 206、`ContentFill` 94、`LevelsPixels` 61、`BrushPixels` 51、`NoisePixels` 50、`LensPixels` 38）；④ Camera Raw 的 C 路径（`AdjustPixels.c` 1,380 行的全部 15 个导出函数）；⑤ 降采样（`DownsampleCache.swift`，对应 `vImageScale_ARGB8888`）；⑥ 图层效果 CPU 回退；⑦ `TiledLayerRenderer` 等价物（分块合成 + LRU 淘汰，420 行）；⑧ 导出 PNG/JPEG/PDF（`ImageExporter.swift` 252 行，README:75 声称的 Export As + 打印尺寸 PDF） |
| **进入标准** | Wave 1 退出标准 1、2 达成 |
| **退出标准** | 1. **golden 图像比对**：对 ≥30 个代表性 `.comp` 工程（覆盖 23 种混合模式、6 种图层效果、12 种调整层、蒙版、剪贴蒙版），Windows 侧渲染结果与 macOS 侧导出 PNG 逐像素比较，**容差 ≤1 LSB**；对含高斯/运动模糊与 Camera Raw 的路径，容差 ≤2 LSB 且需书面记录实际最大值；<br>2. `CompositorTests` 中 `FilterTests`（159）、`FinishingFilterTests`（123）、`LevelsTests`（201）、`HueSaturationTests`（284）、`ImageAdjustmentTests`（242）、`CameraRawTests`（862）、`CameraRawSliderTests`（75）、`ScanlinesTests`（154）、`TiledLayerTests`（310）、`DownsampleTests`（82）、`RasterSnapshotTests`（136）、`ExportTests`（104）、`JPEGExportTests`（64）、`OuterGlowTests`（346）、`InnerGlowTests`（250）、`ColorOverlayTests`（51）、`SpotHealingTests`（62）、`MagicWandTests`（148）、`BlurBrushTests`（42）的等价断言通过（**约 3,791 行测试的等价物**）；<br>3. 导出 PNG 的像素与画布显示**一致**（对应 `docs/brush-performance.md:41`「display/export agreement」的既有要求）；<br>4. 一个 4K 工程的合并渲染（`Merge Layers`）在可接受时间内完成，耗时记录在案 |
| **依赖** | Wave 1 |
| **涉及原文件** | `Rendering/AdjustPixels.c` 1,380 + `.h` 121、`DitherPixels.c` 547 + `.h` 89、`HealPixels.c` 260 + `.h` 20、`WandPixels.c` 206 + `.h` 29、`ContentFill.c` 94 + `.h` 5、`LevelsPixels.c` 61 + `.h` 6、`BrushPixels.c` 51 + `.h` 11、`NoisePixels.c` 50 + `.h` 16、`LensPixels.c` 38 + `.h` 13；`Rendering/SeparableBlend.swift` 60、`DownsampleCache.swift`、`TiledLayerRenderer.swift` 420、`LayerRenderer.swift`、`RasterSnapshot.swift`、`LayerEffectsSurface.swift`、`AdjustmentSurface.swift`、`EffectsPreviewCache.swift`、`LiveMaskRenderer.swift`；`Document/Filters.swift` 739、`CameraRaw*.swift`（602 + 328 + 277 + 255 + 127 = 1,589）、`HueSaturation.swift` 614、`Levels.swift` 221、`LevelsAutomatic.swift` 85、`Curves.swift` 43、`Dither.swift` 185、`Scanlines.swift` 99、`LayerEffects.swift` 666、`LayerAdjustment.swift` 214、`Distort.swift` 400、`PixelAdjust.swift` 66、`PixelInvert.swift` 47、`ImageAdjustments.swift` 211、`ContentFill.swift` 28；`IO/ImageExporter.swift` 252、`ImageResizer.swift` 122、`CanvasResizer.swift` 74 |
| **工作量（单人全职）** | **6–10 周** |

> **这一波的最大陷阱**：`SeparableBlend.swift:14-16` 用一段注释记下了「在线性空间混合会算错」的教训——40% 灰之上，80% 灰的 Color Dodge 在线性空间得 62%，Photoshop 得 100%；Color Burn 得 0% 而不是 25%。移植到 D2D 时若误用 linear color space，**画面看起来「差不多」但数值全错**，只有 golden 比对能抓到。列为强制回归项。

### 4.4 Wave 3 —— GPU 管线（Metal → HLSL）

| | |
|---|---|
| **范围** | ① 17 个 MSL kernel → HLSL（`continuousBrush` 1、`effects_*` 9、`warp_*` 5、`add_noise`/`add_grain` 2）；② D3D12 资源与管线管理；③ **纹理缓存重写**（替代 `GPUCanvas.swift` 的 `CIContext` + `ObjectIdentifier` 键缓存 + 90 帧淘汰 + 内存压力源）；④ GPU 合成 pass（替代 `CIContext.render(to: texture, commandBuffer:)`）；⑤ 笔刷的 256×256 tile 与 float 密度缓冲（`docs/brush-performance.md:3`、:45）；⑥ CPU 回退路径（`MetalLayerEffects` 明确「Falls back to the CPU renderer when Metal isn't available」，`MetalLayerEffects.swift:5-6`） |
| **进入标准** | Wave 2 退出标准 1、3 达成 |
| **退出标准** | 1. `CompositorTests` 中 `GPUCanvasTests`（588）、`MetalWarpTests`（108）、`BrushIntersectionTests`（108）、`BrushTests`（462）、`BrushPerformanceTests`（61）、`LargeCanvasBrushTests`（70）、`NativeResolutionPaintTests`（232）的等价断言通过；<br>2. **UAV barrier 回归**：`effects_*` 链在连续 1,000 帧压力测试下无「效果只出现一次」类间歇错误（对应 `MetalLayerEffects.swift:82-84` 记录过的 bug）；<br>3. **性能退出标准（可测）**：4,000×4,000 文档、800 px 笔刷、0% 硬度、100% 不透明度、120 次指针更新 —— 中位指针更新耗时 ≤ **4.0 ms**，mouse-up ≤ **15 ms**。参照值来自 `docs/brush-performance.md:13-19` 的 macOS Debug 实测（2.64 ms 中位 / 8.71 ms mouse-up）；**允许 1.5× 的余量**，因为两平台硬件与框架不可比；<br>4. CPU 回退路径在同一组测试下也通过（数值可放宽到 ≤2 LSB） |
| **依赖** | Wave 2（Surface 抽象）+ Wave 1 |
| **涉及原文件** | `Rendering/MetalLayerEffects.swift` 398、`MetalWarp.swift` 262、`MetalBrushCoverage.swift` 163、`GPUNoise.swift` 172、`GPUCanvas.swift` 583；`docs/brush-performance.md`（51 行，**含可复现的基准方法**，:26-39）；`CompositorTests/GPUCanvasTests.swift` 588、`MetalWarpTests.swift` 108、`BrushPerformanceTests.swift` 61 |
| **工作量（单人全职）** | **5–8 周** |
| **依据** | 17 个 kernel 无算法重写，主要是 MSL/HLSL 语义映射（§1.4 列了 5 条差异）；但 `GPUCanvas.swift` 583 行的缓存与合成层（占本波 40%，**待验证**）要整体重写 |

### 4.5 Wave 4 —— 画布与工具（交互层主体）

| | |
|---|---|
| **范围** | ① 自绘画布控件（`EditorCanvas.swift` 3,153 行的等价物）：缩放/平移、DPI 变化、像素网格（:1417 `drawPixelGrid`）、标尺与参考线、变换把手、导航小地图（`NavigatorMinimap.swift` 79 + `NavigatorGeometry.swift` 53）、吸附（`UI/SliderSnap.swift` 43、`ResizeSnapTests` 64）；② 光标与 tracking（`resetCursorRects()` :1462、`updateTrackingAreas()` :1488、`cursorUpdate` :1624、4 种 overlay NSView）；③ 工具状态机：笔刷/橡皮（B/E）、克隆图章、修复笔刷、模糊工具、渐变、形状、裁剪、矩形/椭圆选框、套索、魔棒、对象选择、吸管、文本；④ 修饰键与快捷键（`UI/HeldModifiers.swift` 40、`UI/KeyboardShortcuts.swift` 321）；⑤ 变换（移动/缩放/旋转/翻转/自由扭曲，`Rendering/TransformOverlay.swift` 468、`Document/Distort.swift` 400、`Document/LayerTransform.swift` 247、`Document/LayerFlip.swift` 81、`Document/CanvasRotation.swift` 63） |
| **进入标准** | Wave 3 退出标准 1、3 达成 |
| **退出标准** | 1. `CompositorTests` 中 `CursorTests`（245）、`TransformTests`（344）、`TransformPressTests`（107）、`SelectionTests`（480）、`SelectionEditTests`（418）、`SelectionClipboardTests`（218）、`SelectionFeatherTests`（48）、`GroupingSelectionTests`（71）、`FloatingSelectionTests` 对应物、`ShapeToolTests`（106）、`CropTests`（167）、`CropToCanvasImportTests`（143）、`ResizeSnapTests`（64）、`CanvasEntryTests`（93）、`MiddleButtonTests`（53）、`CloneStampTests`（79）、`MagicWandTests`（148）、`NavigatorTests`（134）、`CanvasThumbnailTests`（72）的等价断言通过；<br>2. **DPI/多显示器清单**：在 100% / 150% / 200% 缩放下各截一次图，画布、标尺、光标、像素网格、文本编辑器位置全部正确；把一个窗口从 100% 显示器拖到 150% 显示器，画面立即正确（对应 `EditorCanvas.swift:848` 的 `viewDidChangeBackingProperties`）；<br>3. **手势清单**：滚轮缩放（以指针为中心）、触控板/`Ctrl`+滚轮、抓手平移、空格临时抓手、`Alt` 取样、`Shift` 约束——逐项手工验证通过 |
| **依赖** | Wave 3 |
| **涉及原文件** | `Rendering/EditorCanvas.swift` 3,153、`TransformOverlay.swift` 468、`BrushCursorOverlay.swift` 96、`CanvasLinesOverlay.swift` 13、`SampleRingOverlay.swift` 30、`CanvasViewport.swift` 85、`NavigatorGeometry.swift` 53；`UI/` 中 `CanvasRulers.swift` 186、`NavigatorMinimap.swift` 79、`LassoControls.swift` 272、`CropControls.swift` 25、`ShapeControls.swift` 65、`GradientControls.swift` 109、`BrushControls.swift` 153、`TransformInspector.swift` 146、`NumericScrub.swift` 64、`SliderSnap.swift` 43、`HeldModifiers.swift` 40、`KeyboardShortcuts.swift` 321、`GridSettingsSheet.swift` 126、`NewCanvasSheet.swift` 266；`Document/` 中 `Selection.swift` 364、`SelectionEdits.swift` 226、`SelectionClipboard.swift` 282、`FloatingSelection.swift` 161、`GuidedMatte.swift` 119、`MaskTracing.swift` 96、`MagicWand.swift` 139、`ObjectSelection.swift` 299、`Crop.swift` 250、`CanvasSize.swift` 83、`ImageTrim.swift` 211、`Gradient.swift` 127、`ShapeTool.swift` 225、`CloneStamp.swift` 50、`BlurTool.swift` 50、`SmudgeLiquify.swift` 248、`LayerMerge.swift` 73、`GuidedMatte.swift`；`IO/CanvasResizer.swift` 74 |
| **工作量（单人全职）** | **10–16 周** |
| **说明** | 这是**单波最大的一波**，也是并行化收益最高的一波：画布控件本身（`EditorCanvas` 等价物 + overlay + 几何）与各工具（选择/裁剪/形状/变换）之间依赖很弱，可由 2–3 人按工具切分，共享同一份自绘画布与 `Surface` 接口 |

### 4.6 Wave 5 —— 文档外壳与图层面板

| | |
|---|---|
| **范围** | ① 图层树控件（`NativeLayerList.swift` 1,388 行的等价物）：缩略图、拖放重排与嵌套、`Option` 拖放复制、内联重命名、右键菜单、效果子行、行边线；② 多工程标签页（`ProjectTabs.swift` 429 含 `TitleBarDragView`/`AnchorView`、`ProjectTabLayout.swift` 79）；③ 主窗口与浮动面板（`FloatingPanel.swift` 177、`ProjectWindowBridge.swift` 63 的 `ProjectWindowView`）；④ 程序化菜单（`CompositorApplicationDelegate.swift` 224，`NSMenu` 57 处）；⑤ 快捷键重映射（`KeyboardShortcuts.swift` 321）；⑥ 命令面板（`CommandPalette.swift` 254 + `CommandPaletteView.swift` 145 的 `PalettePanel: NSPanel`）；⑦ 全屏与「仅画布」模式（`CanvasOnlyFrameView: NSView`）；⑧ 图层跨工程复制粘贴（`NSPasteboard` 22 处） |
| **进入标准** | Wave 1 退出标准 1 达成；Wave 4 可并行进行（两者共享的设计面只有「画布控件」） |
| **退出标准** | 1. `CompositorTests` 中 `CommandPaletteTests`（79）、`ProjectTabLayoutTests`（70）、`BlendShortcutTests`（48）、`TitleBarDragTests`（75）、`FloatingPanelTests`（151）、`SliderSnapTests`（29）、`GroupingSelectionTests`（71）、`GroupTests`（191）的等价断言通过；<br>2. **手工 UI 清单**（逐项截图存档）：创建/重命名/删除/嵌套图层；拖放跨组移动；`Option` 拖放复制；效果子行的显示与开关；图层跨工程复制粘贴；标签页拖拽重排；命令面板搜索并执行一条命令；快捷键重映射后立即生效；全屏进出；<br>3. 与 macOS 侧的同名界面截图并列对比，记录每一处**有意**的差异（Windows 惯例导致，例如 `⌘`→`Ctrl`） |
| **依赖** | Wave 1；与 Wave 4 弱耦合，可并行 |
| **涉及原文件** | `UI/NativeLayerList.swift` 1,388、`LayersPanel.swift` 100、`LayerAppearanceControls.swift` 60、`LayerMaskMenu.swift` 78、`BlendModePicker.swift` 72、`ProjectTabs.swift` 429、`ProjectTabLayout.swift` 79、`ProjectWindowBridge.swift` 63、`FloatingPanel.swift` 177、`CommandPalette.swift` 254、`CommandPaletteView.swift` 145、`KeyboardShortcuts.swift` 321、`IndicatorlessScrollView.swift` 49、`ToolHeaderStyle.swift` 44、`ToolHeaderStyle` 相关；`IO/CompositorApplicationDelegate.swift` 224；`CompositorApp.swift` 372、`ContentView.swift` 488 |
| **工作量（单人全职）** | **8–12 周** |

### 4.7 Wave 6 —— 面板、滤镜 UI 与对话框

| | |
|---|---|
| **范围** | `UI/` 剩余 40 个文件中的面板与表：Camera Raw 三组控件（`CameraRawColorControls.swift` 538 + `CameraRawControls.swift` 387 + `CameraRawDetailOpticsControls.swift` 190 + `CameraRawGeometryCalibrationControls.swift` 148 + `CameraRawSlider.swift` 207 = 1,470）、`FilterSheet.swift` 407、`EffectsSheet.swift` 194、`ExportAsSheet.swift` 309、`ColorPickerSheet.swift` 219、`HueSaturationSheet.swift` 211、`ImageSizeSheet.swift` 172、`CanvasSizeSheet.swift` 147、`LevelsSheet.swift` 147、`PSDConversionSheet.swift` 70、`RawDevelopSheet.swift` 102、`ColorRangeSheet.swift` 92、`TrimSheet.swift` 69、`CurvesControls.swift` 70、`ColorPaletteControls.swift` 80、`GradientControls.swift` 109、`NavigationToolHeader.swift` 67、`CanvasThumbnail.swift` 62、**以及 Camera Raw 面板的直方图/矢量示波器**（对应 `AdjustPixels.h:47-50` 的 `camera_raw_scope`） |
| **进入标准** | Wave 2 退出标准 1 达成（面板依赖滤镜结果）；Wave 5 的面板宿主模式确定 |
| **退出标准** | 1. `CompositorTests` 中 `ColorPickerTests`（87）、`CameraRawSliderTests`（75）、`HueSaturationTests`（284）、`LevelsTests`（201）、`FilterTests`（159）、`FinishingFilterTests`（123）中与「面板参数 ↔ 模型」相关的断言通过；<br>2. **数值一致性**：Camera Raw 面板的每一个滑块（Light/Color/Curves/Mixer/Grading/Detail/Optics/Geometry/Calibration）在相同输入下产出与 macOS 相同的渲染结果（走 Wave 2 的 C 路径，因此这是 Wave 2 golden 的延伸，**不是新工作**）；<br>3. 每个面板有截图存证，并与 macOS 侧的对应面板并列 |
| **依赖** | Wave 2（滤镜结果）、Wave 5（面板宿主） |
| **涉及原文件** | `UI/` 上述 20+ 个文件（合计约 5,000 行）；`Document/` 中对应的参数模型（`CameraRaw.swift` 602、`CameraRawColor.swift` 328、`CameraRawTables.swift` 277、`CameraRawDetailOptics.swift` 127、`CameraRawGeometryCalibration.swift` 255、`HueSaturation.swift` 614、`Levels.swift` 221、`Curves.swift` 43、`AdjustmentEditing.swift` 132、`ColorPalette.swift` 375）；`IO/CompositorApplicationDelegate.swift`（表的分发） |
| **工作量（单人全职）** | **10–14 周** |
| **并行性** | 本波内部高度可并行：Camera Raw 一组、调整面板一组、导出/画布尺寸一组、颜色一组。建议 3 人各领一组 |

### 4.8 Wave 7 —— IO 全量（PSD / PSB / RAW / 扩展格式）

| | |
|---|---|
| **范围** | ① PSD/PSB 读取（`PSDReader` 561 + `PSDChannelCoder` 195 + `PSDDocumentBuilder` 172 + `PSDTypes` 105 + `PSDVector` 283 = 1,316 行，另 `PSDText` 683 与 Wave 8 共享）；② 转换报告（README:73「A conversion report is shown before anything is applied」+ `UI/PSDConversionSheet.swift` 70）；③ Camera RAW develop（替换 `CIRAWFilter`）；④ HEIC / WebP / SVG 导入；⑤ 拖放导入（`ImageFileDrop.swift` 61）；⑥ 大文件路径：超限 PSD 裁到画布（README:74） |
| **进入标准** | Wave 2 退出标准 1 达成；Wave 8 的 DirectWrite 度量可用（PSD 文本层需要） |
| **退出标准** | 1. `CompositorTests` 中 `PSDRoundTripTests`（709）、`PSBImportTests`（86）、`PSDAdjustmentTests`（60）、`ImageImportTests`（184）、`RawDitherTests`（59）、`CropToCanvasImportTests`（143）的等价断言通过；<br>2. `PSDFixture.swift`（582）与 `PSDVectorFixtures.swift`（23）构造的**全部二进制 fixture** 在 C# 侧可用并通过；<br>3. **两端对照**：用同一批 PSD（含图层组、蒙版、混合模式、填充矩形/椭圆、水平文本、CMYK 文件）在 macOS 与 Windows 上各导入一次，逐层比对像素与元数据，输出差异报告；<br>4. README:73 列出的 8 类格式各有一个成功样例与一个失败样例的测试 |
| **依赖** | Wave 1（模型）、Wave 2（像素） |
| **涉及原文件** | `IO/PSD/` 全部 6 个文件（1,999 行）；`IO/RawImporter.swift` 149、`ImageImporter.swift` 95、`ImageFileDrop.swift` 61；`UI/PSDConversionSheet.swift` 70、`RawDevelopSheet.swift` 102；`Document/RawDitherTests` 对应物；`CompositorTests/PSDFixture.swift` 582、`PSDVectorFixtures.swift` 23、`PSDRoundTripTests.swift` 709、`PSBImportTests.swift` 86、`PSDAdjustmentTests.swift` 60 |
| **工作量（单人全职）** | **8–12 周**（其中 PSD 5–7 周、RAW 2–3 周、其余格式 1–2 周） |

### 4.9 Wave 8 —— 文本与字体

| | |
|---|---|
| **范围** | ① DirectWrite 度量与布局；② `TypeTool.swift` 473 的 `LayerTextStyle`（PostScript 字体名、字号、颜色、对齐、tracking、leading、`boxSize` 段落框、`colorRuns` v10、`fontRuns` v11）；③ 文本栅格化（生成图层 PNG）；④ 画布内原地多行编辑器（`InlineTextEditor.swift` 558，`CanvasTextView: NSTextView`，随画布变换）；⑤ 文本工具头（`TypeControls.swift` 250）；⑥ PSD 文本层（`PSDText.swift` 683）与 `NSTextStorage` 度量替代；⑦ `Dither.swift` 的 ASCII 字形路径（`CTLineCreateWithAttributedString` :173、`CTLineDraw` :177） |
| **进入标准** | Wave 2 达成；Wave 4 的画布控件可用（原地编辑器要挂在画布上） |
| **退出标准** | 1. `CompositorTests/TypeToolTests.swift`（**506 行，是本子系统唯一完整套件**）的全部等价断言通过：内容校验、`colorRuns`/`fontRuns` 的重叠/越界规则（`TypeTool.swift:43-62`）、字号范围 1–2000（:37）、tracking −100…1000（:39）、leading 0–5000（:40）、`boxSize` 16…30,000（:27-28）；<br>2. 文本层渲染结果与 macOS 侧同一 `.comp` 的 PNG 对比，**差异以「重排」而非「错字」为判据**：逐字符位置差异报告 + 人工确认无缺字/错字；<br>3. **已保存 PNG 不变原则**：打开一个含文本层的 v11 工程，不做任何编辑，保存后 `images/<uuid>.png` 字节不变（对应 `docs/project-format.md:48`「The PNG remains the display and export fallback」）；<br>4. 原地编辑器在 100%/150%/200% 缩放下字号、光标、选区位置正确 |
| **依赖** | Wave 2、Wave 4 |
| **涉及原文件** | `Document/TypeTool.swift` 473、`Document/ColorPalette.swift` 375（颜色运行）；`IO/PSD/PSDText.swift` 683；`Rendering/InlineTextEditor.swift` 558；`UI/TypeControls.swift` 250；`Document/Dither.swift` 185（:161-177 字形路径）；`CompositorTests/TypeToolTests.swift` 506 |
| **工作量（单人全职）** | **5–8 周** |
| **说明** | **本波与上游的正确关系是「不追求像素一致」**。字体栅格化在两平台不可能一致；`.comp` 的设计已经把 PNG 定为真值、`text` 元数据定为可选的重编辑信息（`docs/project-format.md:48`），移植必须守住这条边界，才能把这一波从「无解」变成「可交付」 |

### 4.10 Wave 9 —— 平台服务、打包、更新

| | |
|---|---|
| **范围** | ① 更新器（Velopack / WinSparkle）+ `appcast.xml` 等价物；② 代码签名（Authenticode/EV）与安装包；③ 设置持久化（`UserDefaults` 14 处 → JSON）；④ 内存预算（`DocumentLimits.documentPixelBudget` 的 `ProcessInfo.physicalMemory` → `GlobalMemoryStatusEx`）；⑤ 内存压力通知（`DispatchSource.makeMemoryPressureSource` → 待验证方案）；⑥ 最近工程（`RecentProjects.swift` 34）与文件关联；⑦ 崩溃处理与日志；⑧ CI/发布流水线（对齐 `verify.yml` 的双 job 结构：单测 + 截图） |
| **进入标准** | Wave 5 达成（需要完整壳） |
| **退出标准** | 1. 在一台干净的 Windows 11 机器上：安装 → 导入一张 RAW → 编辑 → 保存 `.comp` → 关闭 → 从最近工程重开 → 卸载，全程无残留；<br>2. 从 v1 升级到 v2 的自动更新成功执行一次（含签名验证失败时的拒绝路径）；<br>3. 4K 工程 + 20 图层下，`documentPixelBudget` 与实际内存占用吻合，超预算时给出与 macOS 一致的错误信息（`ProjectStore.swift:71` 的 `.tooLarge` 文案等价物）；<br>4. CI 有 unit + screenshots 两个 job，screenshots 是 `continue-on-error`（对齐 `verify.yml:102`） |
| **依赖** | Wave 5 |
| **涉及原文件** | `IO/UpdateDriver.swift` 156、`IO/CompositorApplicationDelegate.swift` 224、`IO/RecentProjects.swift` 34、`Document/DocumentLimits.swift` 43、`Rendering/GPUCanvas.swift`（:57-76 的 idle Timer 与内存压力源）、`scripts/release.sh`、`scripts/publish.sh`、`scripts/ExportOptions.plist`、`appcast.xml`、`.github/workflows/verify.yml` |
| **工作量（单人全职）** | **4–6 周** |

### 4.11 总工期

| 口径 | 区间 | 折算 |
|---|---|---|
| 单人全职（各波工作量相加：3+6+6+5+10+8+10+8+5+4 = **65** 低，5+9+10+8+16+12+14+12+8+6 = **100** 高） | **65–100 人周** | **约 15–23 个月** |
| ↑ 经 `06-ui-calibration.md` 用修正后的 UI 口径独立重算 | **66.3–102.7 人周** | **差异 <3%——原区间得到支持** |
| 3–4 人并行 | **受关键路径约束**（见 §5.3：W0→W1→W2→W3→W4 串行 ≈ 30–48 周，是硬下限） | **约 9–14 个月** |

> **这个区间的可信度（2026-10 标定后）**：35 人周的总宽度里，**13.9 人周（39%）纯粹来自两个 UI 系数**，而这恰好是全套估计中度量覆盖最差的部分——自绘桶 89.4% 的行属于「输入状态机型」，其费率**尚未度量**。且这是**单侧风险**：现有证据只支持「不比 0.8/2.5 更便宜」。因此**把 65 周当作乐观边界、把 100 周当作需要预算的边界**，不要反用。详见 `06-ui-calibration.md` §7.2。

**「UI 层是最大成本项」的依据**：

1. **规模**：UI 层 14,624 行，占应用 38,763 行的 **37.7%**（§4.0 的口径表）；是全仓最大的单一成本块。
2. **波次落地**：把各波中属于呈现与交互的部分加总——
   - **W4 的 UI 部分**：该波呈现与交互代码共 **5,059 行**（`EditorCanvas.swift` 3,153 + 三个 overlay 139 + `CanvasViewport.swift` 85 + `NavigatorGeometry.swift` 53 + `UI/` 下的画布与工具控件 `CanvasRulers` 186、`KeyboardShortcuts` 321、`LassoControls` 272、`BrushControls` 153、`TransformInspector` 146、`GridSettingsSheet` 126、`GradientControls` 109、`NavigatorMinimap` 79、`ShapeControls` 65、`NumericScrub` 64、`SliderSnap` 43、`HeldModifiers` 40、`CropControls` 25，合计 1,629）。该波总量约 10,800 行（其余是 UI 无关的算法：`BrushStroke.swift` 1,019、`Distort.swift` 400、`Selection.swift` 364、`SelectionClipboard.swift` 282、`Crop.swift` 250、`SmudgeLiquify.swift` 248、`LayerTransform.swift` 247、`SelectionEdits.swift` 226、`ImageTrim.swift` 211、`MagicWand.swift` 139、`GuidedMatte.swift` 119、`MaskTracing.swift` 96 等）。按行数占比 ≈47% 折算 **4.7–7.5 周**。
   - **W5**：8–12 周（整个波都是 UI）
   - **W6**：10–14 周（整个波都是 UI）
   - **W8 的 UI 部分**：`InlineTextEditor.swift` 558 + `TypeControls.swift` 250，从其 5–8 周中取约 **2 周**
   - **合计 24.7–35.5 人周**，占单人全职总量 65–100 的 **36–38%**。
3. **与系数模型互相印证**：§4.0 的系数口径独立算出 **21.4–35.2 人周**——声明式部分 8,900 行 × 0.8–1.4 = 7.1–12.5 加 F_decl 1.3–2.7 = **8.4–15.1**，自绘控件部分 5,699 行 × 2.5–4.0 = 14.2–22.8（**注意：该费率只对桶内 10.6% 的纯绘制子类成立，对占 89.4% 的输入状态机型尚无度量——所以这是下限而非点估计**）。修正后重算总工期 **66.3–102.7 人周**（`06-ui-calibration.md` §7.2）。
   **补充口径差**：系数口径 = 纯代码产出，波次口径 = 代码 + **逐面板验收**。两者相减得验收成本约 **4.5–10.5 人周**（~30 面板 × 0.15–0.35 人周），这正好解释为什么 W6 的波次口径比系数口径高 1.8–4.4 倍。**所以「两口径重合」应改述为「系数口径 + 逐面板验收 ≈ 波次口径」。**
4. **单位成本最高**：自绘控件系数 2.5–4.0 人周/千行，是 C 内核（0.15–0.30）的 8–27 倍，是纯逻辑（0.35–0.60）的 4–11 倍。
5. **无机械路径**：62 个 `View` 结构体 + 21 处 `NSViewRepresentable` + 19 个 `NSView` 子类，`EditorCanvas.swift` 3,153 行的输入状态机与 `NativeLayerList.swift` 1,388 行的拖放协议在 XAML 里都没有对应物。
6. **不可压缩**：与 C 内核（编译即用）、`.comp`（有 52 行完整规格）不同，UI 没有规格可依，只能逐屏实现 + 截图验收。

> **待验证**：§4.0 的两个转换系数（声明式 0.8–1.4、自绘控件 2.5–4.0 人周/千行）没有任何实测支撑，本文件的工期区间完全建立在它们之上。**Wave 0 的标定是修订整个计划的前提**。

---

## 5. 移植顺序的拓扑依赖图

### 5.1 依赖图

```
L0  工程骨架 + MSVC 构建 C 内核                        [无依赖]
     │
     ├──────────────┬────────────────────┬─────────────────────┐
     ▼              ▼                    ▼                     ▼
L1  Core 数据类型   L3  Surface 抽象      L4  C 内核托管封装     L16 打包/签名骨架
    (几何/颜色/         (CGContext 替身)      (P/Invoke)             [无依赖，可随时做]
     图层模型)         │                    │
     │               │                    │
     ├──────┬────────┘                    │
     ▼      ▼                             │
L2  .comp   L5  混合模式 + 合成器 ◄────────┘
    读写        (CPU)
    (v1–v11)     │
     │          ├──────────────┐
     │          ▼              ▼
     │      L8  GPU kernels   L6  撤销/重做
     │          (HLSL 17)        [依赖 L1]
     │          │              │
     └────┬─────┴──────────────┘
          ▼
     L7  文档会话 (EditorSession 等价物)
          │
     ┌────┼──────────┬──────────────┬─────────────┐
     ▼    ▼          ▼              ▼             ▼
L9  自绘画布  L11 图层面板  L12 面板/对话框  L13 文本   L14 PSD IO
    │            │            │            (DirectWrite)  │
    ▼            │            │              │            │
L10 工具/手势    │            │              │            │
    │            │            │              │            │
    └────────────┴────────────┴──────────────┴────────────┘
                            ▼
                   L15 RAW / HEIF / WebP
                            ▼
                      Wave 9 发布
```

### 5.2 必须先做的模块（排序理由）

| 顺序 | 模块 | 为什么必须在前 |
|---|---|---|
| 1 | **L0 工程骨架 + C 内核构建** | 不先验证「C 内核能否被 MSVC 编译并产出正确结果」，后面所有渲染工作都建在流沙上。这是 §3.3 全部论证的前提，必须最早证伪 |
| 2 | **L3 Surface 抽象** | `CGContext` 113 处、`CGImage` 500 处的替代物。它的接口形状一旦定错，Rendering 全量（10,233 行）与 Document C 类（5,233 行）都要返工。**用 `SeparableBlend.swift:22-46` 与 `MetalLayerEffects.swift:64-66` 两个真实调用样本反推接口** |
| 3 | **L1 → L2 模型与 `.comp`** | `.comp` 是唯一有完整规格（`docs/project-format.md` 52 行）的契约，也是跨平台真值的载体（golden 工程、测试 fixture 都靠它）。且它无平台依赖，可在 Linux 上验证 |
| 4 | **L5 混合模式 + 合成器** | 一切渲染的公共底座；且 sRGB 空间正确性（§4.3 陷阱）需要尽早锁定 |
| 5 | **L7 文档会话** | `EditorSession.swift` 1,043 行是全应用的模型中枢，UI 与工具都挂在它上面；它必须在画布之前定形 |
| 6 | **L9 自绘画布** | 交互层的地基；W4/W5/W6/W8 都依赖它的输入与坐标系约定（尤其是 DPI 与文档坐标↔屏幕坐标的换算，`Rendering/CanvasViewport.swift` 85 行） |

### 5.3 关键路径（决定工期的下限）

**串行链**：`L0 → L1 → L2 → L5 → L7 → L9 → L10 → 发布`

对应波次（取区间中点）：W0(4) + W1(7.5) + W2(8) + W3(6.5) + W4(13) ≈ **39 周**；若再串上 W8 的原地文本编辑器（依赖 W4）则约 **45 周**；若串到 W5 → W9 则约 **54 周**。

**结论**：

- 单人全职口径 65–100 人周，其中 **约 39–48 周在任何并行度下都无法压缩**（Wave 0–4）。
- 3–4 人并行下，理论聚合工作量 82.5 人周 ÷ 4 ≈ 21 周，但被关键路径卡在 39 周以上，**实际日历时间 9–14 个月**，且瓶颈期只有 1–2 人满载。
- **可并行的部分**：W5（图层面板）与 W6（面板）只需 L1/L2/L5，可在 W3/W4 进行时提前开工；W7（PSD IO）只需 L1/L2；W8 的 DirectWrite 度量层只需 L3。**这是把 3–4 人用满的唯一办法**：让 1 人守关键路径（画布与工具），2 人做 W5/W6/W7，1 人做 W8 的度量层与 W9。

---

## 6. 风险登记表

评级：概率 高/中/低，影响 高/中/低。**验证方式必须是可执行的。**

### R1 —— 文本与字体度量差异

| 项 | 内容 |
|---|---|
| **触发条件** | 打开含文本层的 `.comp`；或在 Windows 上编辑文本、改段落框大小 |
| **依据** | `PSDText.swift:237` 用 `NSTextStorage` 量样本字符串推断 PSD 文本层包围盒；`TypeTool.swift:16-22` 定义了 `leading`（0 = Auto = 120% 字号）与 `padding = 12`；`Dither.swift:164` 用 `("M" as NSString).size(withAttributes: [.font: font])` 量等宽字格。三者都依赖平台文本引擎的**具体度量值** |
| **影响** | 中。换行位置、行高、字宽不同 → 文本层外观偏移；PSD 文本层导入后的包围盒不同。**但 `.comp` 的设计已把 PNG 定为显示与导出真值、`text` 定为可选重编辑元数据**（`docs/project-format.md:48`「The PNG remains the display and export fallback」），因此已保存工程的**显示**不受影响 |
| **缓解** | (1) 严格守住「PNG 真值」边界：打开未编辑的工程不得改变 PNG 字节（Wave 8 退出标准 3）；(2) text 元数据里的 `fontSize`/`tracking`/`leading`/`boxSize` 原样保留，不因度量差异回写；(3) 字体缺失时用系统字体（对应 `docs/project-format.md:48`「Missing fonts use the system font when edited」），但要显式提示；(4) 明确**不承诺**跨平台文本渲染一致，写入发布说明 |
| **验证方式** | Wave 8 退出标准 2、3；另加一个专项测试：同一 `.comp` 在两端打开、保存，比对 `images/<text-layer-uuid>.png` 的 SHA-256 是否相同（**应当相同**，因为不做编辑就不应重栅格化） |

### R2 —— Camera RAW 解码

| 项 | 内容 |
|---|---|
| **触发条件** | 导入任何相机 RAW 文件（NEF/CR2/CR3/ARW/DNG/RAF…） |
| **依据** | `RawImporter.swift:38-42` 用 `CIRAWFilter`，注释说明「Every camera RAW the system can develop — 30 formats」（:76-77）；`:52` 记下「building a new CIRAWFilter decodes the file again (about 1.5s here)」；`:60` 用 `filter.neutralTemperature`/`neutralTint` 取相机白平衡；`:97` 用 `boostAmount`（Apple 的 tone curve）；`:120-123` 说明为什么必须 16 位渲染 + 抖动到 8 位（**「a smooth sky came out in visible steps」**） |
| **影响** | 高。RAW 是目标用户的核心输入；Windows 侧任何解码器给出的像素值与 Apple 不同，develop 面板的滑块数值就不可比，用户的既有工作流会断 |
| **缓解** | (1) 把 RAW develop **定义为「尽力而为」**：不承诺与 macOS 逐位一致，只承诺「滑块语义一致（曝光 = 挡、色温 = Kelvin、色调 = green-magenta）」；(2) 保留 Apple 的三段处理结构：解码 → 8 段颜色表插值（`AdjustPixels.h:33-41` 的 `CameraRawStage` / `camera_raw_compose`）→ 16 位渲染 + C 抖动量化（`dither_quantize16`，见 `RawImporter.swift:135`），**把解码器做成可替换接口**；(3) 候选解码器：LibRaw（**待验证**：许可条款——LibRaw 是 LGPL-2.1 与 CDDL 双许可，需确认分发方式）、Adobe DNG SDK（**待验证**：许可）、Windows RAW Image Extension（**待验证**：格式覆盖与是否经 WIC 暴露） |
| **验证方式** | Wave 7 退出标准 4；专项：用 5 种相机各 1 个样张，在两端 develop（全默认设置），记录 PSNR 与最大 LSB 差，形成对照表；**结论写入发布说明**，不做跨平台一致性承诺 |

### R3 —— GPU 效果数值一致性

| 项 | 内容 |
|---|---|
| **触发条件** | 图层效果（描边/阴影/内阴影/发光/颜色叠加）、涂抹/液化、噪声、笔刷在 GPU 与 CPU 回退路径之间切换 |
| **依据** | 双路径并存：`MetalLayerEffects.swift:5-6`「Falls back to the CPU renderer when Metal isn't available」；`docs/brush-performance.md:3` 说明「the software fallback uses 2.5% soft / 1.5% hard tip spacing」，即 GPU 与 CPU **本就使用不同的采样参数**。`effects_blur_rows/columns`（`MetalLayerEffects.swift:51-52`）是可分离高斯，`sigma = shadow.blur / 2`（:105）、`radius = round(sigma * 3)`（:108） |
| **影响** | 中。同一工程在不同机器/不同路径下渲染出不同结果 → 用户可见的不一致；且 `docs/brush-performance.md:41` 明确要求「display/export agreement」 |
| **缓解** | (1) 把「CPU 回退与 GPU 主路径的差异」**定义为一个明确的容差**，而不是要求相等；(2) 容差必须写进测试并在 CI 上跑（GPU 测试用 WARP 软件适配器，**待验证**：ComputeSharp 在 WARP 上的可用性）；(3) HLSL 侧禁用快速数学（`/Gfp:precise` 等价设置），并避免编译器重排浮点表达式 |
| **验证方式** | Wave 3 退出标准 1、4；专项：同一组输入在 GPU 与 CPU 两条路径各渲染一次，逐像素比较并记录最大差与差的分布 |

### R4 —— Metal → HLSL 重写

| 项 | 内容 |
|---|---|
| **触发条件** | 重写 17 个 kernel；尤其是 `effects_*` 的 9 个 pass 链 |
| **依据** | `MetalLayerEffects.swift:82-84` 明确记录过一次真实 bug：**缺少 `encoder.memoryBarrier(scope: .buffers)` 时「a pass reads half-written pixels — which showed up as effects that only appeared once the stroke ended」**。这是移植中最可能被漏掉、且最难复现的一处。MSL→HLSL 的其它差异（`:75-81` 的 `dispatchThreads` + `threadsPerThreadgroup 16×16`、`setBytes(..., index: 9)` 的 buffer 索引约定、`SIMD4<Float>`/`SIMD4<UInt32>` 对齐）见 §1.4 |
| **影响** | 中–高。算法本身不复杂（17 个 kernel，无一超过 100 行），但语义映射的细节多，错误表现为**间歇性**而非确定性 |
| **缓解** | (1) 逐条建立 MSL→HLSL 对照表（§1.4 已给 5 条），每条附一个最小复现测试；(2) 每个 kernel 单独建一个 HLSL 单元测试（相同输入 → 比较 GPU 输出与 CPU 参考实现）；(3) **UAV barrier 必须有压力测试**，不能只跑单帧 |
| **验证方式** | Wave 3 退出标准 2（连续 1,000 帧压力）与退出标准 3（可测的性能指标，对齐 `docs/brush-performance.md:13-19` 的记录方式） |

### R5 —— DPI / 缩放与多显示器

| 项 | 内容 |
|---|---|
| **触发条件** | 用户在 150%/200% 缩放显示器上使用；或把窗口从一台显示器拖到另一台；或改变系统缩放 |
| **依据** | `EditorCanvas.swift:848` 有 `viewDidChangeBackingProperties()` 并在其中调 `syncGeometry()`；`Rendering/CanvasViewport.swift` 85 行负责文档坐标↔屏幕坐标；`EditorCanvas.swift:1417` 的 `drawPixelGrid`（1:1 像素网格）与 `:1278` 的 `cacheDisplay(in:to:)`（位图缓存）都对缩放敏感；`backingScaleFactor` 3 文件 3 处 |
| **影响** | 高（对图像编辑器）。错误的缩放会导致像素网格错位、光标错位、画笔落点偏移——这类错误直接毁掉「像素级精确」这个核心卖点（README:5「the tools needed to create a pixel-perfect final image」） |
| **缓解** | (1) 进程声明 Per-Monitor DPI Awareness v2（**待验证**：WinUI 3 的默认行为与 manifest 写法）；(2) 把所有坐标换算集中到 `CanvasViewport` 等价物的一个类里，禁止在别处出现裸缩放乘法；(3) `WM_DPICHANGED` / `XamlRoot.RasterizationScale` 变化时重建位图缓存 |
| **验证方式** | Wave 4 退出标准 2（三档缩放截图 + 跨显示器拖动）；专项：在 200% 缩放下放一个 1 px 的选区并验证其屏幕位置与文档坐标的换算误差 ≤0.5 px |

### R6 —— PSD 兼容性回归

| 项 | 内容 |
|---|---|
| **触发条件** | 导入真实用户的 PSD/PSB |
| **依据** | README:73 承诺「Photoshop folders, masks, blend modes, fill rectangles/ellipses, and simple horizontal text stay editable; other vectors and vertical text become pixels」+「A conversion report is shown before anything is applied」；`PSDReader.swift` 有 `cropToCanvas`（:250）与 `fitsBudget`（:226-240）两条降级路径；`ProjectStore.swift:179` 要求内嵌 PNG ≤8 位 |
| **影响** | 中。已有 `PSDRoundTripTests.swift`（709）+ `PSDFixture.swift`（582）+ `PSDVectorFixtures.swift`（23）作为回归网，但 fixture 是**自己构造的**，覆盖不到真实 Photoshop 输出的全部变体 |
| **缓解** | (1) 把 1,126 行 fixture 与测试整体搬到 C#（它们是二进制纯数据，与平台无关），作为**逐字节契约**；(2) 建立真实 PSD 语料库（**建议 ≥30 个真实文件，覆盖 PSB 大文件、CMYK 拒绝路径、智能对象栅格化、竖排文本、图层样式**）；(3) 转换报告逐条断言（哪些图层被栅格化、为什么） |
| **验证方式** | Wave 7 退出标准 3（两端对照报告）；另加一个持续回归：语料库每个文件在每次发布前跑一次，输出「本次与上次的差异」 |

### R7 —— 单作者维护两套 UI 的长期负担

| 项 | 内容 |
|---|---|
| **触发条件** | 上游（macOS 侧）继续开发新功能；Windows 侧要跟进 |
| **依据** | README:91 明确「It's maintained by one person」（针对翻译 PR 的说明，但反映了维护结构）；`verify.yml` 的 filter-suite 智能跳过机制（:32-52）说明上游对一个功能的改动面有清晰认知，但那是 **Swift 侧**的认知，Windows 侧没有对应机制；每新增一个功能，Windows 侧要多做一遍 UI（§4.11：UI 占 37.7%） |
| **影响** | 高（长期）。这不是一次性移植风险，而是项目寿命风险：UI 重写成本约为功能本身成本的 40%，且 Windows 侧永远落后 |
| **缓解** | (1) **契约优先**（详见 §7）：以 `.comp` 格式 + 测试向量为唯一同步面，UI 逐平台独立演化，接受外观差异；(2) Windows 侧不要试图 1:1 复刻 macOS 界面，而是复刻**工作流**（工具、快捷键语义、面板分组）；(3) 建立「上游变更分类器」：只有 `docs/project-format.md` 的版本变化与 `.comp` 语义变化需要跟进，其余 Swift 内部重构不跟进；(4) 明确 Windows 侧的功能裁剪清单（**建议从 Wave 4/6 的验收里剔除 macOS 特有的交互，例如 `⌘`-drag 的自由扭曲改绑 `Ctrl`-drag） |
| **验证方式** | Wave 9 之后设一个季度检查：统计上游当季提交中「被 Windows 侧跟进的比例」，若连续两季低于 50%，说明分叉已不可控，需要重新评估策略 |

### R8 —— MSVC 与 clang 的浮点行为差异（C 内核 parity）

| 项 | 内容 |
|---|---|
| **触发条件** | 用 MSVC 编译 `AdjustPixels.c` 等 9 个 C 文件后跑现有测试向量 |
| **依据** | 这是 §3.3 唯一未被消除的风险。C 内核含 677 处 `float` 运算、`AdjustPixels.h:81-86` 的 16 个 `double` 参数路径；而 `scripts/ci-tests.sh` 的优化设置（`verify.yml:76-77` `GCC_OPTIMIZATION_LEVEL=s` + `SWIFT_OPTIMIZATION_LEVEL=-O`）说明上游在**优化构建**下跑测试断言，即断言是在优化后的浮点行为上通过的 |
| **影响** | 中。若差异超过现有测试容差，所有滤镜测试都要重定容差；若 Camera Raw 的 LUT 查表路径出现差异，则 develop 面板的数值与 macOS 不可比 |
| **缓解** | (1) `/fp:precise` 或 `/fp:strict`；(2) 关闭 FMA 收缩（`#pragma fp_contract(off)`）；(3) 避免 `long double`；(4) parity 测试**先测差异再定容差**，不预设 0；(5) 若差异不可接受，备选方案是按 clang 的语义改写成 C# 并用同一组向量验证——但那会引入新风险，所以优先走编译路线 |
| **验证方式** | **Wave 0 退出标准 2**——这是全计划中优先级最高的早期失败信号，故意放在最前面。方法：从 macOS 生成一组输入向量与输出（用 `AdjustPixels.h:20` 的断言「Pure red at the default 40% comes out 40% gray」作为第一个用例），在 Windows 上跑，逐位比较 |

### R9 —— 混合空间与混合模式语义（sRGB vs linear）

| 项 | 内容 |
|---|---|
| **触发条件** | 在新的渲染栈上实现 23 种混合模式 |
| **依据** | `SeparableBlend.swift:14-16` 用注释记下了真实教训：**「Core Image works in a linear space unless told otherwise, and these two modes are not separable from the gamma they are computed in: over 40% grey, an 80% grey layer dodges to 62% instead of Photoshop's 100%, and burns to 0% instead of 25%」**。`GPUCanvas.swift:11-12` 也写明「Blending happens in sRGB, not linear light, as it does on the canvas and in Photoshop」 |
| **影响** | 高。这类错误**视觉上不易察觉但数值全错**，会让所有混合结果与 Photoshop/上游不一致，且会污染 golden 数据集 |
| **缓解** | (1) 把「在 sRGB 空间做混合」写成一条不可协商的规格，落在 `Surface` 接口的文档注释里；(2) Skia 侧确保 `SKColorSpace` 不设置（按数值直接运算）；D2D 侧确保不启用 linear color space；(3) 用 `SeparableBlend.swift:14-16` 给出的两个具体数值（80% 灰 over 40% 灰，Color Dodge 应得 100%，Color Burn 应得 25%）写成**两个精确断言测试** |
| **验证方式** | Wave 2 退出标准 1（golden 比对 ≤1 LSB）+ 上述两个精确数值断言 |

### R10 —— 内存预算与压力通知

| 项 | 内容 |
|---|---|
| **触发条件** | 打开接近上限的工程（30,000 px 边长、1 亿像素、10,000 图层，`docs/project-format.md:9`），或长期使用后缓存膨胀 |
| **依据** | `DocumentLimits.swift:36-37`：`documentPixelBudget = min(800_000_000, max(200_000_000, physicalMemory/16))`——直接依赖 `ProcessInfo.processInfo.physicalMemory`；`GPUCanvas.swift:66-76` 用 `DispatchSource.makeMemoryPressureSource`（:76）在系统内存压力下通过 `setEventHandler`（:66）释放纹理，`:57-63` 用 3 秒 idle Timer 做 LRU 淘汰；`GPUCanvas.swift:36` 的 `keepFrames = 90` |
| **影响** | 中。预算公式若用错（例如忘了 Windows 的提交内存 vs 工作集差异），会在 16 GB 机器上过早拒绝大文档，或在 64 GB 机器上分配失败 |
| **缓解** | (1) **保持同一公式**，只替换物理内存的读取方式（`GlobalMemoryStatusEx` 的 `ullTotalPhys`）；(2) 内存压力源用 `CreateMemoryResourceNotification`（Win32 低内存通知）或 `GC.GetGCMemoryInfo()` 轮询——**待验证**；GPU 侧用 `IDXGIAdapter3::QueryVideoMemoryInfo` 做显存预算；(3) 保留 3 秒 idle 淘汰与 90 帧保留期这两个已调优的参数（`GPUCanvas.swift:36, 57`） |
| **验证方式** | Wave 9 退出标准 3 |

### R11 —— 测试套件的迁移本身

| 项 | 内容 |
|---|---|
| **触发条件** | 翻译 14,994 行 XCTest 到 xUnit |
| **依据** | 81 个文件、43 个已在 `scripts/ci-tests.sh` 显式命名；其中 `PSDFixture.swift`（582）与 `PSDVectorFixtures.swift`（23）是二进制 fixture 构造器，`CameraRawTests.swift`（862）、`PSDRoundTripTests.swift`（709）、`GPUCanvasTests.swift`（588）、`LayerTests.swift`（559）、`TypeToolTests.swift`（506）是最大的 5 个 |
| **影响** | 中。测试是这次移植唯一的正确性保障；翻译过程中「改断言以让它通过」会让整个验证网失效 |
| **缓解** | (1) **先搬运断言，再改实现**——对每个套件先原样翻译并用一个桩实现跑（预期全红），再逐步让它们变绿；(2) 平台相关的套件（`CursorTests` 245、`FloatingPanelTests` 151、`TitleBarDragTests` 75、`SliderSnapTests` 29）**明确标记为「重写」而非「搬运」**，不能悄悄降级；(3) 每个套件在 PR 里单独审 |
| **验证方式** | 每个 Wave 的退出标准都列出了具体的套件名与行数；Wave 0–9 覆盖的套件合计应 ≥ `CompositorTests` 的全部 81 个文件 |

---

## 7. 与上游的关系策略

### 7.1 核心判断：**不要 rebase，也不要同步 Swift 代码**

Windows 侧不是一个「平台分支」，而是**另一个实现**。两边唯一的共同资产是格式与语义，不是代码。因此：

- **不做**：`git rebase` 上游分支（Windows 侧改的是 `.cs`/`.xaml`/`.c`，与 `.swift` 没有可合并的改动，rebase 只会制造冲突）
- **不做**：试图维护一份「共享源码」（除 C 内核与测试向量，见 §7.2）
- **要做**：把上游当作**规格来源 + golden 生成器**，用固定 cadence 拉取并只提取契约变更

### 7.2 三种可以真正共享的资产

**(A) C 内核源码（2688 行）——物理共享**

`src/Compositor/Compositor/Rendering/*.c|*.h` 在 Windows 侧**原样复制**到 `native/compositor_pixels/`，唯一改动是 4 处 `dispatch_apply`（`AdjustPixels.c:11/348/386`、`DitherPixels.c:12`）。

同步机制：一个脚本按文件哈希比较两侧的 `.c/.h`，输出「上游已变更的文件」清单。由于：
- 这 9 个文件的 C 内核对 Apple API 的依赖是 **0**（§0 复核结论），
- CI 的 filter-suite 触发规则（`verify.yml:44-45`）已经把 `Rendering/(AdjustPixels|LevelsPixels|DitherPixels|LensPixels|NoisePixels|GPUNoise|AdjustmentSurface)` 列为「滤镜相关」，

因此可以建立一个**低成本同步环**：每周拉取上游 → 若这些文件有变更 → 重新编译 + 跑 parity 测试 → 提交。`verify.yml:45` 的清单可以直接当作同步范围的白名单。

**(B) `.comp` 工程格式（`docs/project-format.md` 52 行）——语义共享**

这是全项目最有价值的一份文档，也是唯一可以当作接口契约的东西。它的形态非常适合做契约：

- 明确的版本号与门槛：`ProjectStore.swift:15` `static let current = 11`，`:18` `supported = 1...11`
- 每一版的新增字段、默认值、以及**「老版本不能包含什么」**（例如 `docs/project-format.md:17`「Files declaring older versions cannot contain non-default appearance values」）
- 明确的量化上限：30,000 px 边长、1 亿像素、10,000 图层、4 MiB manifest、512 MiB/资产（:9）
- 明确的拒绝条件：非法版本、非 sRGB、缺资产、路径逃逸、超限（`ProjectStore.swift:146-201`）
- 一份「加性字段」清单（:39-44），说明老读者应当忽略它们

**Windows 侧的契约承诺：读 v1–v11，写 v11。** 这条承诺应当有一个专门的测试套件守住：对每个版本各构造一个最小工程 → 读 → 写 → 读 → 断言相等。

**(C) 测试向量——数据共享**

从 `CompositorTests`（14,994 行）提取**平台无关的输入/输出对**：

| 来源 | 提取什么 | 用途 |
|---|---|---|
| `CameraRawTests.swift`（862）+ `CameraRawSliderTests.swift`（75） | Camera Raw 的输入参数 → 输出像素 | Wave 2 的 golden |
| `FilterTests.swift`（159）+ `FinishingFilterTests.swift`（123） | 滤镜参数 → 输出像素 | Wave 2 |
| `HueSaturationTests.swift`（284）+ `LevelsTests.swift`（201）+ `ImageAdjustmentTests.swift`（242） | 调整层参数 → 输出 | Wave 2 |
| `DownsampleTests.swift`（82） | 输入尺寸 → 降采样结果 | Wave 2 的 `vImage` 替代验证 |
| `PSDFixture.swift`（582）+ `PSDRoundTripTests.swift`（709） | 二进制 PSD fixture + 期望的图层结构 | Wave 7 |
| `BrushIntersectionTests.swift`（108）+ `BrushPerformanceTests.swift`（61） | 笔刷输入 → 覆盖率 + 性能基线 | Wave 3 |
| `TypeToolTests.swift`（506） | 文本样式校验规则 | Wave 8 |

形态建议：`tools/golden/` 下一组 `.json`（参数）+ `.png`（期望输出）文件，由 macOS 侧的 CI job 生成并提交；Windows 侧的 `Compositor.Parity.Tests` 消费它们。**golden 数据是唯一需要跨平台 pin 住的东西。**

### 7.3 具体做法

1. **仓库结构**：Windows 侧独立仓库（或独立分支），保留 `upstream` remote 指向 `robbietilton/Compositor`。
2. **同步 cadence**：每周一次（对齐 `verify.yml:7-8` 的 `cron: '0 9 * * 1'`），人工审查上游 diff 的**三个特定范围**：
   - `docs/project-format.md` → 有变化就必须处理（格式版本）
   - `Compositor/Rendering/*.c|*.h` → 有变化就走 (A) 的同步环
   - `Compositor/IO/ProjectStore.swift` 的 `validate`/`current`/`supported` → 有变化就更新 Windows 侧校验
   - **其余一律不跟进**（Swift 内部重构、UI 改动、算法微调——不跟进，除非它改变了 `.comp` 的语义）
3. **格式版本治理**：
   - Windows 侧写入前先跑「v11 最小往返」；如果上游升到 v12，Windows 侧有两个选择：(i) 跟进 v12（一个波次的 1–2 周），或 (ii) 把 v12 的**加性字段**原样透传（读进来、原样写回，不理解语义）——(ii) 是低成本的临时方案，适用于「上游只是加了一个可选字段」的情形。
   - **禁止**：Windows 侧写一个上游不认识的版本号或字段。宁可写 v11 + 丢弃新字段，也不要写 v12 让 macOS 侧拒绝打开。
4. **C 内核同步**：见 (A)。哈希清单 + 自动重编译 + parity 测试，作为 CI 的一个 job。
5. **契约测试的 CI 化**：一个定时 job（每周）拉取 upstream、重新生成 golden 数据集、对格式兼容性跑一次，**失败即告警**。这是「避免长期分叉」的自动化保障。
6. **发布节奏**：Windows 侧的版本号独立演进（不要试图与 macOS 版本对齐），但在 release notes 里标注「兼容 `.comp` v1–v11，写入 v11」。
7. **明确不做的事**（写进 `docs/port/` 的项目章程）：
   - 不追求 UI 一致（§6 R7）
   - 不追求文本渲染一致（§6 R1）
   - 不追求 RAW 解码逐位一致（§6 R2）
   - 不向上游提 Swift 侧 PR 以「方便移植」——那会把 Windows 侧的成本转嫁给单一维护者

---

## 附录 A：待验证清单（10 项）

| # | 待验证项 | 位置 | 验证方法 | 阻塞哪一波 |
|---|---|---|---|---|
| 1 | WinUI 3 + `SwapChainPanel` 的输入延迟 / DPI / 无边框拖动是否达标；Avalonia 是否有等价的 D3D 交换链宿主 | §1.1 | Wave 0 的三项测量 spike（1 周） | W0 决策闸门 |
| 2 | SwiftUI→XAML 的两个转换系数（声明式 0.8–1.4、自绘 2.5–4.0 人周/千行） | §4.0 | Wave 0 用 `UI/FilterSheet.swift`（407）与 `Rendering/TransformOverlay.swift`（468）标定 | 全部工时的准确性 |
| 3 | MSVC 与 clang 在 9 个 C 内核上的实际浮点差异 | §3.3 / §6 R8 | Wave 0 用 `adjust_black_white` 逐位比对 | **W0 退出标准 2** |
| 4 | `SKBlendMode` / `D2D1_BLEND_MODE` 是否覆盖 23 种混合模式 | §1.2 / §1.3 | 逐项列表对照官方枚举文档，缺的用 `SKRuntimeEffect`/自定义 effect 补 | W2 |
| 5 | HEIF/HEIC、AVIF、WebP、SVG 在目标 Windows 版本上的开箱可用性 | §1.8 | 在目标最低 Windows 版本上裸 WIC 试开，列出需附带编解码器的清单 | W7 |
| 6 | Camera RAW 解码器选型（LibRaw / DNG SDK / Windows RAW 扩展）与许可 | §6 R2 | 许可审查 + 5 个相机样张的 develop 对照 | W7 |
| 7 | 分割模型（替代 `VNGenerateForegroundInstanceMaskRequest`）的实例级输出与许可 | §1.7 | 候选模型（U²-Net / ISNet / MobileSAM）在 4K 图上的耗时与实例分离能力实测 | W4 的 ObjectSelection |
| 8 | 更新器的 Ed25519/EdDSA 签名支持（Velopack vs WinSparkle） | §1.9 | 文档 + 一次真实的签名验证失败路径测试 | W9 |
| 9 | ComputeSharp 的 .NET 版本支持、NativeAOT 可用性、WARP 上的可测试性 | §1.4 / §3.4 | 用 1 个 kernel 在 CI 的 WARP 上跑通 | W3 |
| 10 | .NET DirectWrite 互操作库（Vortice / TerraFX / CsWin32）的版本与许可 | §1.6 | 三个库各写一个最小 `IDWriteTextLayout` 样本，比较代码量与包体积 | W8 |

## 附录 B：本文件所有量化数据的测量命令

```powershell
# 原始行数（应用侧，按子系统）
$r='D:\Compositor For Windows\src\Compositor\Compositor'
Get-ChildItem $r -Recurse -File -Filter *.swift | ForEach-Object {
  $rel=$_.FullName.Substring($r.Length+1); $top=($rel -split '\\')[0]
  [PSCustomObject]@{Top=$top; Lines=((Get-Content $_.FullName -Raw) -split "`n").Count}
} | Group-Object Top | ForEach-Object { "{0}`t{1}`t{2}" -f $_.Name,$_.Count,($_.Group|Measure-Object Lines -Sum).Sum }

# 平台耦合分桶（A/B/C）
$syms='\bCGImage\b|\bCGContext\b|\bCIImage\b|\bNSColor\b|\bNSView\b|\bNSEvent\b|\bMTLDevice\b|...'  # 见 §2.1 的 38 项

# C 内核是否含 Apple API（决定性结论）
Select-String -Path "$r\Rendering\*.c","$r\Rendering\*.h" -Pattern 'CGContext|CGImage|CoreGraphics|vImage|Accelerate|NSColor|NSView|CFRelease'
#  → 0 命中

# MSL kernel 清点
Select-String -Path "$r\Rendering\GPUCanvas.swift","$r\Rendering\GPUNoise.swift",
  "$r\Rendering\MetalBrushCoverage.swift","$r\Rendering\MetalLayerEffects.swift",
  "$r\Rendering\MetalWarp.swift" -Pattern 'kernel void\s+(\w+)' -AllMatches
#  → 17 个
```

**所有数据均由本次复核实测，未修改 `src/` 下任何文件。**
