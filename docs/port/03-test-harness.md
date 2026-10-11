# 03 · 测试验收基线与 Windows 测试工程

> 目标：把上游 `CompositorTests` 的 81 个文件变成 Windows 实现可以照抄的"对错判据"。
> 上游路径（只读）：`src/Compositor/CompositorTests/`、`src/Compositor/CompositorUITests/`。
> 本文所有数字都给出统计口径与命令；判断类结论给出源码行号。未实测的部分显式标注为**推断**。

---

## 0. 三个必须先纠正的前提（否则整份移植计划会跑偏）

### 0.1 上游测试框架不是 XCTest，而是 Swift Testing

任务描述里举例的 `XCTAssertEqual` 在上游**一次都没有出现**。实测：

| 关键字 | 出现次数 | 说明 |
|---|---|---|
| `import Testing` | 79 | 79 个文件（81 − 2 个 fixture） |
| `import XCTest` | **0** | — |
| `XCTAssert` | **0** | — |
| `@Test` | **567** | 真实的测试函数总数（不是 81） |
| `#expect` | **2525** | 断言（对应 xUnit `Assert.*`） |
| `#require` | **996** | 解包并中断（对应 `Assert.NotNull` + 提前返回） |
| `@Suite` | 9 | 显式套件声明；其余用 `struct XxxTests` 隐式成套件 |
| `.measure` | **0** | 无 XCTest 性能度量 |

统计命令（PowerShell，对本目录全部 `*.swift` 逐文件 `[regex]::Matches` 计数后求和）：

```powershell
$t = "D:\Compositor For Windows\src\Compositor\CompositorTests"
$files = Get-ChildItem $t -Filter *.swift
foreach ($p in @('import Testing','import XCTest','XCTAssert','@Test','#expect','#require','@Suite','\.measure')) {
  $n = 0; foreach ($f in $files) { $n += ([regex]::Matches((Get-Content $f.FullName -Raw), $p)).Count }
  "{0,-16} {1}" -f $p, $n
}
```

**移植含义**：文档与任务书里的"1:1 翻译成 xUnit/NUnit"是成立的，但映射对象是 Swift Testing 宏，不是 XCTest：

| Swift Testing | xUnit / NUnit 等价物 |
|---|---|
| `@Test func name() throws` | `[Fact] public void Name()` |
| `@Test(arguments: [a, b])`（30 处、13 个文件） | `[Theory]` + `[InlineData]` |
| `#expect(x == y)` / `#expect(..., "消息")` | `Assert.Equal(y, x)`（消息进第 3 参/`because`） |
| `#require(x)` | `Assert.NotNull(x)` 或返回值解包助手 |
| `#expect(throws: E.self) { }` | `Assert.Throws<E>(...)` |
| `Issue.record("...")`（见 `ProjectTests.swift:128`） | `Assert.Fail("...")` |
| `@Suite(.serialized)`（7 个文件） | `[Collection]`（禁并行） |
| `@MainActor`（76 个文件） | 无直接等价；Windows 侧对应"必须在 UI 线程/STA 上跑"的集合 |

### 0.2 `@MainActor` 是全局约束：76/81 个文件都标了它

上游 CI 的注释解释了原因（`scripts/ci-tests.sh:4`："Nearly every suite runs on the main actor, so within one process they take turns"）。**这直接决定了 Windows 侧的并行策略**：光靠 xUnit 的类级并行不够，被测对象（`EditorSession`）本身是单线程状态机，Windows 侧必须把 `EditorSession` 设计成"单线程所有权 + 显式调度"，否则测试之间会互相踩。

### 0.3 上游 81 个文件的真实规模

```
raw 行数合计 14913 ；空行 960 ；非空行 13953   ← 任务书里的"约 13953 行"是非空行口径
```
统计命令：`(Get-Content $f).Count` 求和 vs `Measure-Object -Line` 求和。注意 **`Measure-Object -Line` 会跳过空行**，两个口径差 960，引用时请统一。

其中 `PSDFixture.swift`(581 行) 与 `PSDVectorFixtures.swift`(22 行) **不含任何 `@Test`**，是纯 fixture；真正的测试文件是 **79** 个。

---

## 1. 统计：像素级断言与纯算法测试

### 1.1 口径 A：可 grep 的客观指标

判定"含像素级断言"= 文件内出现下列任一：
- 定义返回字节/平面的访问器：`-> [UInt8]`、`-> [Int]`、`-> [[Int]]`、`-> Data`、`-> (Int, Int) -> [Int]`、`-> (r: UInt8, ...)`、`-> (Double, Double)`；
- 或直接触碰位图字节：`dataProvider`、`CFDataGetBytePtr`；
- 或经 AppKit 位图取样：`NSBitmapImageRep`、`.colorAt(x:`。

```powershell
$byteRe = '->\s*\[UInt8\]|->\s*\[Int\]|->\s*\[\[Int\]\]|->\s*Data|->\s*\(Int, Int\) -> \[Int\]|->\s*\(r: UInt8|->\s*\(Double, Double\)|->\s*\(red: Int, alpha: Int\)|dataProvider|CFDataGetBytePtr'
$bitmapRe = 'NSBitmapImageRep|\.colorAt\(x:'
```

结果：**48 个文件命中**（其中 `PSDFixture.swift` 是 fixture，不是测试）→ **47 个测试文件**。

### 1.2 口径 B：人工通读补充（口径 A 的已知漏检）

口径 A 会漏掉"读像素但返回标量/集合"的写法。逐个通读后确认另有 **8 个**文件属于像素级断言：

| 文件 | 漏检原因 | 证据 |
|---|---|---|
| `BlurBrushTests.swift` | `spread(...) -> Int` 返回扩散列数 | `BlurBrushTests.swift:9` 起，内部读 `context.data` |
| `BrushIntersectionTests.swift` | `alpha(_ context:_:_:) -> Int` | `BrushIntersectionTests.swift:33` |
| `DistortTests.swift` | 测试内部局部函数 `alpha(_:_:)` | `DistortTests.swift:59` |
| `LargeCanvasBrushTests.swift` | 手写 `wholeData[...]` 逐字节比较 | `LargeCanvasBrushTests.swift:61-67`（`largest <= 1`） |
| `MaskTransformTests.swift` | `gray(_ image:x:y:) -> Int` | `MaskTransformTests.swift:29-33`，断言 `< 5` / `> 250` |
| `RasterSnapshotTests.swift` | `memcmp` 整块比较 + alpha 极差 | `RasterSnapshotTests.swift:65`、`:114-117`、`:130-133` |
| `SmartEditTests.swift` | 就地读 `ctx.data` 统计命中数 | `SmartEditTests.swift:22-27`、`:53-57` |
| `RawDitherTests.swift` | `rowMeans(...) -> [Double]` | `RawDitherTests.swift:19-25`、`:50-52` |

### 1.3 最终数字（本文采用）

| 指标 | 数字 | 口径 |
|---|---|---|
| 测试文件总数 | **79**（+2 个纯 fixture = 81） | 含 `@Test` 才算测试文件 |
| `@Test` 测试函数 | **567** | 正则 `@Test` |
| **含像素级断言的文件** | **55**（55/79 = 69.6%） | 口径 A 的 47 + 口径 B 的 8 |
| 不含像素级断言的文件 | **24** | 79 − 55 |
| 其中"严格纯算法"（无像素、无任何 AppKit UI 符号） | **11** | 见 1.4 |
| 依赖 AppKit/UI/窗口文件 | 见 §3 表（窗口/事件硬依赖 15 个） | 见 1.5 |

### 1.4 严格纯算法文件（11 个）：不碰像素，也不碰窗口/事件/视图符号

`CompositorTests.swift`、`ExternalChangeTests.swift`、`GroupingSelectionTests.swift`、`HistoryTests.swift`、`ImageImportTests.swift`、`LayerEffectsSurviveTests.swift`、`NewCanvasUnitTests.swift`、`ProjectTabLayoutTests.swift`、`ProjectWorkspaceTests.swift`、`ResizeSnapTests.swift`、`SelectionFeatherTests.swift`（11 个）。

另有 2 个**实质纯算法但 symbol 统计擦边**的文件已在本表中一并计为低难度：`ColorPickerTests.swift`（hex/HSB 往返）、`CommandPaletteTests.swift`（模糊搜索打分）。`SelectionTests.swift`（选区几何与整像素轮廓）因断言像素语义，本表计为"中"。→ 本表低难度合计 **12 个测试文件**（+1 个 fixture）。

> **两个口径不要混**：本节（§1.4）是"symbol 口径"——只要没有像素断言、也没有窗口/事件符号就算。§3.1 的"低"是**难度口径**（只需 BCL）。二者差 1 个文件：`ImageImportTests` 在 symbol 口径下合格，但它需要图像编解码器，故 §3 中计为"中"。以 §3 的难度口径为准做工作量估算。

### 1.5 UI/宿主耦合的三档口径

```powershell
$uiRe       = 'NSWindow|NSPanel|NSApp\.|NSApplication|NSEvent|CGEvent|NSPasteboard|NSView|NSHostingView|NSSlider|NSTextField|NSCursor|NSScreen|NSMenu'
```

| 口径 | 文件数 | 含义 |
|---|---|---|
| 只要 import AppKit | 65 | 很多只是为 `CGColor`/`NSColor`/`NSBitmapImageRep` 而 link，**不等于需要窗口** |
| 出现窗口/事件/视图/剪贴板符号 | 26 | 口径偏宽，含"只为取 `NSColor` 而擦到 `NSView`"的个案 |
| **真·窗口/事件/GPU 硬依赖**（见 §3 判为"高"的 15 个） | 15 | 需要真实窗口、事件循环、Metal 或系统剪贴板 |

另外：`import SwiftUI` 只有 **8 个**文件（`BrushTests`、`CursorTests`、`FloatingPanelTests`、`LevelsTests`、`MaskAloneTests`、`NavigatorTests`、`TitleBarDragTests`、`TypeToolTests`）；`import Metal` 只有 **1 个**测试文件（`GPUCanvasTests.swift:3`）。`@Suite(.serialized)` 7 个：`CursorTests`、`FloatingPanelTests`、`MiddleButtonTests`、`PSDRoundTripTests`、`SelectionClipboardTests`、`SliderSnapTests`、`TitleBarDragTests`。

### 1.6 UI 测试目标（`CompositorUITests`）

只有 1 个文件、102 行、`import XCTest`：`ScreenshotTests.swift`。它**不做断言比较**，只是驱动 app 截 7 张图（`ScreenshotTests.swift:10-42`），且 CI 里 `continue-on-error: true`（`.github/workflows/verify.yml:102`）。结论：**上游没有真正的 UI 断言测试**，Windows 侧的 UI 自动化是新增能力，不是移植任务。

---

## 2. 平台 API 面（决定"高难度"的实际依赖）

对 `src/Compositor/Compositor` 全量 grep `^import (Vision|Metal|CoreImage|CoreText|Accelerate|CryptoKit|Sparkle|Observation|…)`，得到需要替代的系统框架：

| 框架 | 出现文件数（示例） | Windows 侧替代 | 对测试的影响 |
|---|---|---|---|
| CoreImage | 21（`Filters.swift:2`、`Selection.swift:2`、`LayerAdjustment.swift:2`、`LayerEffects.swift:3`、`HueSaturation.swift:2`、`ImageImporter.swift:3`、`RawImporter.swift:1`…） | SkiaSharp 图像滤镜 / Direct2D effects | 滤镜像素比对需要容差 |
| Metal | 5（`GPUCanvas.swift:2-3`、`MetalBrushCoverage.swift:2`、`MetalWarp.swift:1-2`、`MetalLayerEffects.swift:2`、`GPUNoise.swift:1-2`） | Direct3D 11/12 或 SkiaSharp GPU backend；或先只做 CPU | `GPUCanvasTests`/`MetalWarpTests`/`RasterSnapshotTests` 的 GPU 断言无 1:1 等价物 |
| Vision | 2（`ObjectSelection.swift:3`、`SubjectRemoval.swift:2`） | Windows ML / ONNX Runtime，或功能降级 | `SmartEditTests.swift:72-79` 直接调 `SubjectRemoval.run` |
| Accelerate | 2（`DownsampleCache.swift:1`、`PixelInvert.swift:2`） | `System.Numerics` / SkiaSharp resize | 降采样结果需要容差 |
| CoreText | 1（`Dither.swift:2`） | DirectWrite | 文本排版（`TypeToolTests`）最难对齐 |
| CryptoKit | 1（`ProjectDigest.swift:1`，SHA-256） | `System.Security.Cryptography.SHA256` | **位兼容**，无难度 |
| Sparkle | 3（`CompositorApp.swift:2`、`UpdateDriver.swift:2`、`CompositorApplicationDelegate.swift:2`） | WinSparkle / 自建 | 无测试覆盖 |
| Observation | 8（`DocumentHistory.swift:3`、`Filters.swift:3`、`Levels.swift:2`、`ProjectWorkspace.swift:2`…） | `INotifyPropertyChanged` / 手写 | 无直接测试断言 |

另有两个"只在上游测试里出现"的硬依赖，务必单列：
- `MetalBrushCoverage.shared != nil`：`RasterSnapshotTests.swift:121`、`BrushPerformanceTests.swift:10` 直接断言 Metal 笔刷可用性。
- `CGEvent` / `NSEvent` 合成事件：`MiddleButtonTests.swift:22-27`、`BlendShortcutTests.swift:21-23`、`CanvasEntryTests.swift:9-13`。

---

## 3. 81 个文件逐个分类表

**难度判据（本文统一采用，避免"平均分配"）**
- **低**：断言只涉及值/几何/状态/结构；被测对象是纯数据模型或纯算术；Windows 侧只需 BCL。
- **中**：需要像素比较，或需要图像编解码/文本排版/二进制格式解析；Windows 侧需要 SkiaSharp/WIC/DirectWrite，浮点与舍入差异要用容差吸收；**不需要窗口**。
- **高**：需要真实窗口/事件循环/GPU(Metal)/系统剪贴板/视觉框架/性能计时；Windows 侧无法 1:1 复现，必须换验证手段。

**Windows 目标测试工程代号**：`Core`=Compositor.Core.Tests（xUnit，无图形）｜`Imaging`=Compositor.Imaging.Tests（SkiaSharp/WIC + 容差像素比较）｜`Interop`=Compositor.Interop.Tests（.comp 包 / PSD-PSB 编解码 / fixture 库）｜`UI`=Compositor.UiTests（FlaUI 或 WinAppDriver）｜`Bench`=Compositor.Benchmarks（BenchmarkDotNet）

| 文件名 | 主要被测对象 | 是否含像素级断言 | 是否依赖 AppKit/UI/窗口 | 移植难度 | 对应 Windows 侧目标测试工程 |
|---|---|---|---|---|---|
| AdjustmentLayerTests.swift | 调整图层：hue/不透明度/蒙版保留原像素、Curves 恒等保 alpha | 是（`pixels(_:) -> [UInt8]` :13；`pixel(of:)` :145） | 否（仅 AppKit 类型） | 中 | Imaging + Core |
| BlendShortcutTests.swift | Shift +/- 循环切换混合模式，焦点在文本框时不生效 | 否 | **是**（`NSApp.sendEvent` + responder chain :21-23、`makeFirstResponder` :27） | 高 | UI（核心逻辑下沉 Core） |
| BlurBrushTests.swift | Blur 笔刷按 Radius 扩散（与 size/strength 无关） | 是（`spread(...) -> Int` :9） | 否 | 中 | Imaging |
| BrushIntersectionTests.swift | 笔迹自交/重叠处的 alpha 行为 | 是（`alpha(_:_:_:) -> Int` :33） | 否 | 中 | Imaging |
| BrushPerformanceTests.swift | 4K 画布交互笔刷的帧时（`BRUSH_BENCHMARK=1` 才跑 :9） | 否 | **是**（`CanvasView` + `NSWindow` :24-29、帧时统计 :31-51） | 高 | Bench（+ 一条 Imaging 冒烟） |
| BrushTests.swift | 笔刷压印、软笔 alpha、取消/撤销 | 是（`pixel(_:x:y:) -> [Int]` :16） | 部分（19 个用例中少数走 `CanvasView`：:1 import SwiftUI） | 中 | Imaging + UI（少量） |
| CameraRawSliderTests.swift | Camera Raw 滑杆轨道的色带端点/方向 | 是（`NSBitmapImageRep` 画轨道并取样 :32、`:38-39`） | 否 | 中 | Imaging |
| CameraRawTests.swift | Camera Raw 解码与调整管线（28 个用例） | 是（`pixels(_:) -> [[Int]]` :19、`byte(_:x:)` :398、`dataProvider` :790） | 否 | 中 | Imaging + Core |
| CanvasEntryTests.swift | 画布获得焦点后的快捷键；剪贴板只认图片不认文本 | 否 | **是**（`NSEvent.keyEvent` :9-13、`NSPasteboard`） | 高 | UI + Interop（剪贴板格式） |
| CanvasRotationTests.swift | 旋转画布 90°：边长互换、像素随动 | 是（逐像素红值校验 :7-24） | 否 | 中 | Imaging |
| CanvasSizeTests.swift | 各锚点扩/缩画布保留源与 transform | 是（`NSBitmapImageRep.colorAt` ×8） | 否 | 中 | Core + Imaging |
| CanvasThumbnailTests.swift | 缩略图尺寸与图层像素落位 | 是（`pixels(_:) -> (width:read:)` :14） | 否 | 中 | Imaging |
| CloneStampTests.swift | 仿制图章像素搬运 | 是（`pixels(_:) -> (Int,Int)->[Int]` :7） | 否 | 中 | Imaging |
| ColorOverlayTests.swift | 颜色叠加对半透明像素的着色与 alpha 保留 | 是（`middle(of:) -> [Int]` :14，读预乘 RGBA 中点） | 否 | 中 | Imaging |
| ColorPickerTests.swift | `PaletteColor(hex:)` 解析、HSB↔8bit 往返 | 否 | 否 | 低 | Core |
| CommandPaletteTests.swift | 命令面板模糊匹配打分（顺序、词首加权） | 否 | 否 | 低 | Core |
| CompositorTests.swift | 画布尺寸校验（`CanvasDocument.validDimension`）、缩放时光标像素不动 | 否（`actualPixelsAndRoundTrip` :20 只算尺寸） | 否 | 低 | Core |
| CropTests.swift | 裁剪拖拽几何：反向、比例、8 个手柄 | 部分（`NSBitmapImageRep` 解 PNG 取样 :59） | 部分（少量画布用例） | 中 | Core + Imaging |
| CropToCanvasImportTests.swift | 导入超大图后裁剪到画布时保留画外像素 | 是（`rgbaPixels` :27、`grayPixels` :36） | 否 | 中 | Imaging |
| CursorTests.swift | 鼠标移到图层面板时光标必须复位（7 个用例） | 否 | **是**（`NSWindow`/`NSEvent.mouseEvent` :9-12，ui 符号 64 次） | 高 | UI |
| DistortTests.swift | 透视/折叠形变与退化形状拒绝 | 是（局部 `alpha(_:_:)` :59） | 否 | 中 | Imaging |
| DownsampleTests.swift | 大比例缩小的锐度与干净度 | 是（`bytes(_:) -> [UInt8]` :20） | 否 | 中 | Imaging |
| ExportTests.swift | PNG/PDF 导出保留尺寸、alpha、朝向、transform | 是（`NSBitmapImageRep` ×13） | 否 | 中 | Imaging + Interop |
| ExternalChangeTests.swift | 工程被外部改写时的重载与"真的变了"判定 | 否 | 否 | 低 | Interop + Core |
| FilterTests.swift | 高斯模糊/噪声/扭曲滤镜作用于字节 | 是（`pixels(_:) -> [UInt8]` :108、`:138`） | 否 | 中 | Imaging |
| FinishingFilterTests.swift | Vignette/Bloom/Tonal Contrast | 是（`pixels(_:) -> [UInt8]` :7） | 否 | 中 | Imaging |
| FloatingPanelTests.swift | 真实浮动面板显示一次并更新 | 否（`sessionWithPixels` 只是命名含 Pixel 的 fixture :9） | **是**（`.serialized`、真实面板 + display pass） | 高 | UI |
| GPUCanvasTests.swift | GPU 画布与 CoreGraphics 画布逐帧一致（19 个用例） | 是（`pattern(...)` 造像素 :7；`pixel(_:_:_:)` :499；PNG 落盘比对 :140） | **是**（`import Metal` :3，GPU 路径） | 高 | Imaging（GPU 路径）+ Bench |
| GradientTests.swift | 渐变（前景→透明等）像素与 alpha | 是（`pixel(_:x:y:) -> [Int]` :16） | 否 | 中 | Imaging |
| GroupingSelectionTests.swift | 单图层/文件夹成组的包裹语义 | 否 | 否 | 低 | Core |
| GroupTests.swift | 嵌套组移动/展开/删除与撤销；旧版 v1 manifest 兼容（:91-101） | 部分（`NSBitmapImageRep` 解隐藏层 PNG :54-55） | 否 | 中 | Core + Interop |
| GuideTests.swift | 参考线增删移动、吸附、随画布尺寸/裁剪偏移；旧 manifest 无 guides（:71） | 否 | 部分（含画布拖动用例） | 中 | Core + Interop |
| HistoryTests.swift | 历史条数上限、保留像素唯一性、每种编辑可回环 | 否 | 否 | 低 | Core |
| HueSaturationTests.swift | HSV 定向调整、取色、色带权重 | 是（`pixel(_:x:y:) -> [Int]` :25、`previewPixel` :133） | 否 | 中 | Imaging |
| ImageAdjustmentTests.swift | 曝光/渐变映射/黑白/色彩平衡/颗粒 | 是（`pixels(_:) -> [[Int]]` :18） | 否 | 中 | Imaging |
| ImageImportTests.swift | 导入 PNG/JPEG/TIFF/HEIC、朝向、P3；**提供共享 fixture**（:10） | 否 | 否 | 中 | Interop（+ 作 fixture 提供方） |
| ImageSizeTests.swift | 只改分辨率/重采样时保留图层身份与撤销 | 是（`NSBitmapImageRep` ×3） | 否 | 中 | Core + Imaging |
| ImageTrimTests.swift | 修剪矩形计算（透明/左上/右下像素） | 是（`makeRGBAImage` 手写像素 :7） | 否 | 中 | Core + Imaging |
| InnerGlowTests.swift | 内发光：默认值、校验、渲染 | 是（`NSBitmapImageRep` ×13） | 否 | 中 | Imaging |
| JPEGExportTests.swift | JPEG 质量与透明底 matte | 是（`NSBitmapImageRep` ×4） | 否 | 中 | Imaging |
| LargeCanvasBrushTests.swift | 最大笔刷在 5000² 画布上的 Blur/Smudge/Liquify | 是（逐字节比 `largest <= 1` :61-67） | 否 | 中 | Imaging（慢用例，可打 trait 分离） |
| LayerAppearanceTests.swift | 混合模式悬停预览是临时的；模式与已知像素匹配 | 是（`pixel(_:)` :65） | 否 | 中 | Core + Imaging |
| LayerEffectsSurviveTests.swift | 效果/文本/形状样式在各种重建型编辑后存活 | 否（比较 `Extras` 结构体） | 否 | 低 | Core |
| LayerMaskTests.swift | 蒙版创建/绘制/缩放/单独查看 | 是（`alphas(_:) -> [Int]` :28） | 部分（ui 符号 1 处） | 中 | Core + Imaging |
| LayerTests.swift | 图层顺序/重命名/可见性/删除/复制/缓存失效 | 是（`centerPixel()` 经 `NSColor` :159、`NSBitmapImageRep` ×2） | 部分（ui 符号 5 处） | 中 | Core（+ UI 覆盖画布失效） |
| LevelsTests.swift | Levels 数学、直方图显示缩放、取色 | 是（`bytes(_:) -> [UInt8]` :15） | 部分（ui 符号 2 处） | 中 | Imaging |
| LiveMaskTests.swift | 剪贴蒙版 alpha 传递、无黑边 | 是（`alpha(_:) -> [UInt8]` :45） | 否 | 中 | Imaging |
| MagicWandTests.swift | 魔棒容差/采样尺寸/轮廓描摹 | 是（`pixels(_:width:height:) -> Set<Int>` :23） | 部分（ui 符号 4 处） | 中 | Core + Imaging |
| MaskAloneTests.swift | Option 点击缩略图单独显示蒙版 | 部分（`NSBitmapImageRep` :65） | 部分（ui 符号 5 处） | 中 | Core + UI |
| MaskTransformTests.swift | 解除链接后蒙版独立变换、重链接后同步、随保存持久化 | 是（`gray(_:x:y:) -> Int` :29） | 否 | 中 | Core + Imaging |
| MetalWarpTests.swift | Smudge/Liquify 的 GPU 与 CPU 结果一致性 | 是（`bytes(_:) -> [UInt8]` :19） | **是**（GPU 路径） | 高 | Imaging（GPU）+ Bench |
| MiddleButtonTests.swift | 中键平移、Cmd/Ctrl+中键缩放（Blender 式） | 否 | **是**（`CGEvent` 合成中键 :22-27、`NSWindow` :13-16） | 高 | UI |
| NativeResolutionPaintTests.swift | 缩放过的图层上作画落在自身像素分辨率 | 是（`alpha(_:) -> [UInt8]` :9、`NSBitmapImageRep` ×2） | 部分（ui 符号 1 处） | 中 | Imaging |
| NavigatorTests.swift | 导航器几何映射与缩略图框 | 是（`rgba(_:_:_:)` 经 `NSBitmapImageRep.colorAt(x:y:)` :57） | 否 | 中 | Core + Imaging |
| NewCanvasUnitTests.swift | 英寸/毫米/厘米/像素在 DPI 下的换算 | 否 | 否 | 低 | Core |
| OuterGlowTests.swift | 外发光：默认值、校验、渲染（10 个用例） | 是（`NSBitmapImageRep` ×32） | 否 | 中 | Imaging |
| ProjectTabLayoutTests.swift | 标签溢出几何（纯函数 `projectTabOverflow`） | 否 | 否 | 低 | Core |
| ProjectTests.swift | .comp 存/取/覆盖/失败保值/拒绝未来版本/路径穿越/缺图 | 是（`NSBitmapImageRep` ×9，`:65-67`、`:289-310`） | 否 | 中 | Interop（+ Core） |
| ProjectWorkspaceTests.swift | 多标签工作区：新建/关闭/切换/复制 | 否 | 否 | 低 | Core |
| PSBImportTests.swift | PSB（大 PSD）读取 | 是（`pixels(_:) -> [UInt8]` :26） | 否 | 中 | Interop |
| PSDAdjustmentTests.swift | PSD 调整层/蒙版描述符（hundredths gamma 等） | 是（`mask.dataProvider` :55） | 否 | 中 | Interop |
| PSDFixture.swift | **非测试**：共享 PSD 写入 fixture（`PSDFixture.data(...)` :6-9、`AdditionalLayerInfo` :11、字节构建器 :531） | 是（fixture 本身造字节） | 否 | 中 | Interop（移植为 C# fixture 库） |
| PSDRoundTripTests.swift | PSD 读取→渲染的往返一致性（31 个用例，最大套件） | 是（`dataProvider` ×3 :55 等） | 否 | 中 | Interop + Imaging |
| PSDVectorFixtures.swift | **非测试**：Photoshop 矢量/描边描述符 base64 常量（`canvas` :3、`circle()` :4、`rectangle()` :17） | 否 | 否 | 低 | Interop（原样搬运） |
| RasterSnapshotTests.swift | 4000² 画布上笔刷快照不可变、显示与导出逐字节一致、无周期横纹 | 是（`memcmp` :65、alpha 极差 :114-117、:130-133） | **是**（`MetalBrushCoverage.shared != nil` :121） | 高 | Imaging + Bench |
| RawDitherTests.swift | RAW 8bit 抖动：行均值跟随色调、可重复、保持不透明 | 是（`rowMeans` :19-25、字节数组相等 :50-52） | 否（但用 `CIContext`/`CIFilter` :8、:13） | 中 | Imaging |
| ResizeSnapTests.swift | 缩放手柄吸附到画布与其他图层 | 否 | 否 | 低 | Core |
| ScanlinesTests.swift | Scanlines 效果逐行亮度 | 是（读 `result.data` :26-32） | 否 | 中 | Imaging |
| SelectionClipboardTests.swift | 系统剪贴板复制/剪切/粘贴/变换（`.serialized`） | 是（`pixel(_:x:y:) -> [Int]` :26、`layerPixel` :39） | **是**（`NSPasteboard` 系统剪贴板） | 高 | UI + Imaging |
| SelectionEditTests.swift | 删除/cmd 拖动/方向键微移/羽化（19 个用例） | 是（`pixel(_:x:y:) -> [Int]` :16） | 否 | 中 | Core + Imaging |
| SelectionFeatherTests.swift | 羽化软化选区及其裁剪内容 | 否 | 否 | 低 | Core |
| SelectionTests.swift | 套索/选框/整像素轮廓/cmd 点击选不透明像素（24 个用例） | 否（本文件无 `NSBitmapImageRep`/字节访问器，但 `:283`/`:319` 断言的是像素语义） | 部分（ui 符号 9 处） | 中 | Core + UI（少量） |
| ShapeToolTests.swift | 形状工具绘制与形状样式元数据 | 是（`pixels(_:) -> (Int,Int)->(red,alpha)` :20） | 否 | 中 | Imaging |
| SliderSnapTests.swift | AppKit 滑杆在原生 tracking 前吸附 | 否 | **是**（`NSSliderCell.startTracking` :25、`NSWindow` :11-16，`.serialized`） | 高 | UI（否则删除该用例） |
| SmartEditTests.swift | 内容感知填充 / 主体移除 | 是（就地读字节统计 :22-27、`:53-57`） | 否（但依赖 **Vision**：`SubjectRemoval.run` :75） | 高 | Imaging（内容填充）+ 视觉能力单列 |
| SpotHealingTests.swift | 污点修复笔在竖条纹上替换瑕疵 | 是（`pixels(_:) -> (Int,Int)->[Int]` :24） | 否 | 中 | Imaging |
| TiledLayerTests.swift | 图层 = 原图 + 替换瓦片，渲染与整图一致（6 个用例） | 是（`noise(...)` 读字节 :14 附近） | 部分（ui 符号 1 处） | 中 | Imaging |
| TitleBarDragTests.swift | 标题栏标签旁空白仍是窗口拖动区 | 否 | **是**（SwiftUI hosting view + 窗口，`.serialized`） | 高 | UI |
| TransformPressTests.swift | Move 工具下按住 Control 可从任意处拖动图层 | 否 | **是**（`CanvasView` + `NSEvent` :12-20，ui 符号 7 处） | 高 | UI |
| TransformTests.swift | 复制变换、撤销、成组变换、渲染旋转/缩放/翻转（13 个用例） | 是（`pixel(_:_:)` 经 `NSColor` :295、`NSBitmapImageRep` ×2） | 部分（ui 符号 5 处） | 中 | Core + Imaging |
| TypeToolTests.swift | 文本图层：排版/换行/逐字颜色与字体 run/旧版 manifest（19 个用例） | 是（`dataProvider` :133、`redAndDarkPixels` :390） | **是**（`import SwiftUI` :2、ui 符号 16 处、文本编辑视图） | 高 | Imaging（DirectWrite 排版）+ Interop（旧 manifest） |

### 3.1 汇总

| 难度 | 行数（含 2 个 fixture） | 说明 |
|---|---|---|
| 低 | **13** = 12 个测试文件 + `PSDVectorFixtures.swift`（fixture） | 12/79 测试文件；纯 BCL 可覆盖 |
| 中 | **53** = 52 个测试文件 + `PSDFixture.swift`（fixture） | 主体工作量：像素等价 + 二进制格式 |
| 高 | **15**（无 fixture） | 必须换验证手段，不能 1:1 翻译 |
| 合计 | **81** 行 = 79 个测试文件 + 2 个 fixture | 13 + 53 + 15 = 81 ✔ |

> 口径说明：本表的"低/中/高"按 §3 开头的判据（低 = 只需 BCL；中 = 需图形栈或格式编解码；高 = 需窗口/事件/GPU/剪贴板/视觉框架）。它是**判断题**，不是从 grep 直接得来的数字；上表的行数是可复核的（对 §3 表格按 `| 低 |` 等模式计数即得 13/53/15）。

---

## 4. 行为契约 vs 平台耦合

### 4.1 行为契约（可 1:1 翻译成 xUnit，直接作为 Windows 实现的对错判据）

**核心契约簇**（建议作为首批验收门禁，按依赖顺序）：

1. **画布与尺寸**：`CompositorTests`（`:23-40` 尺寸校验）、`NewCanvasUnitTests`、`CanvasSizeTests`、`CanvasRotationTests`、`ResizeSnapTests`。
2. **图层与分组**：`LayerTests`、`GroupTests`、`GroupingSelectionTests`、`LayerAppearanceTests`（含 `LayerBlendMode.allCases` 顺序，`BlendShortcutTests.swift:30-39` 用 `.darken` 与 `.last` 钉住顺序）。
3. **蒙版与剪贴蒙版**：`LayerMaskTests`、`MaskTransformTests`、`MaskAloneTests`、`LiveMaskTests`。
4. **选区**：`SelectionTests`、`SelectionEditTests`、`SelectionFeatherTests`、`MagicWandTests`。
5. **变换**：`TransformTests`（`LayerTransform.handles` 顺序、`isValid` 边界，见 `LayerTransform.swift:27-31`、`:96-98`）。
6. **调整与滤镜**：`ImageAdjustmentTests`、`LevelsTests`、`HueSaturationTests`、`Curves`（在 `AdjustmentLayerTests`）、`FilterTests`、`FinishingFilterTests`、`ScanlinesTests`、`RawDitherTests`、`CameraRawTests`。
7. **历史**：`HistoryTests`（撤销条数与保留像素唯一性）。
8. **工程文件格式**：`ProjectTests`、`ExternalChangeTests`、`GuideTests`（`:71` 直接写旧 manifest）、`ImageSizeTests`、`ExportTests`、`JPEGExportTests`。
9. **PSD/PSB 读取**：`PSDRoundTripTests`、`PSBImportTests`、`PSDAdjustmentTests`（+ `PSDFixture`、`PSDVectorFixtures`）。
10. **笔刷与绘制**：`BrushTests`、`BlurBrushTests`、`BrushIntersectionTests`、`CloneStampTests`、`SpotHealingTests`、`NativeResolutionPaintTests`、`TiledLayerTests`、`ShapeToolTests`、`GradientTests`、`CropToCanvasImportTests`、`DownsampleTests`、`DistortTests`、`LargeCanvasBrushTests`、`ImageTrimTests`、`CanvasThumbnailTests`、`NavigatorTests`、`ColorOverlayTests`、`InnerGlowTests`、`OuterGlowTests`。

### 4.2 平台耦合（15 个"高"文件）与替代验证手段

| 上游文件 | 耦合点（证据） | Windows 替代方案 |
|---|---|---|
| `BlendShortcutTests` | `NSApp.sendEvent` 合成按键 + firstResponder（:21-27） | FlaUI `Keyboard.Type`/`SendKeys` 打到真实窗口；核心 `cycleBlendMode` 顺序下沉到 `Core` 单测 |
| `MiddleButtonTests` | `CGEvent(mouseType:.otherMouse*)`（:22-27） | FlaUI 无中键 API 时用 Win32 `SendInput`（`MOUSEEVENTF_MIDDLEDOWN`）+ `P/Invoke`；断言 `Viewport.Zoom/Pan` |
| `CanvasEntryTests` | 键盘事件（:9-13）+ `NSPasteboard` | FlaUI `Keyboard`；剪贴板用 `System.Windows.Forms.Clipboard` / `IDataObject` 单测（PNG 与 DIB 两种格式都要写） |
| `CursorTests` | 光标随鼠标位置复位（ui 符号 64 次） | FlaUI `Mouse.MoveTo` + 读取 `Mouse.Cursor`（`System.Windows.Forms.Cursor.Current`）；或把"光标应由哪一层决定"抽成 `CursorPolicy` 纯函数单测 |
| `FloatingPanelTests` | 真实面板 + 一次 display pass（`.serialized`） | FlaUI 打开面板截图/查控件树；状态断言下沉 `Core` |
| `SliderSnapTests` | `NSSliderCell.startTracking`（:25） | Windows 侧滑杆是自绘/`TrackBar`，"原生 tracking 前吸附"这一问题**不存在**；建议**删除该用例**，改为 `SliderSnap` 纯函数单测 |
| `TitleBarDragTests` | SwiftUI hosting + 标题栏命中区 | FlaUI 命中测试（`HitTest`）+ `WM_NCHITTEST` 单测 |
| `TransformPressTests` | `CanvasView` 收 `NSEvent`（:12-20） | FlaUI 在画布上做 `Mouse.Down/Move/Up`；几何断言下沉 `Core` |
| `SelectionClipboardTests` | 系统剪贴板（`.serialized`） | 剪贴板 `IDataObject` 单测 + FlaUI 端到端一条；serialize 用 xUnit `[Collection]` |
| `GPUCanvasTests` | `import Metal`（:3），GPU 与 CG 逐帧比对 | ①先只做 CPU 参考实现并**保留"CPU 参考"为真值**；②Direct3D 后端用 SkiaSharp `GRContext`；③容差断言（见 §5.2） |
| `MetalWarpTests` | GPU/CPU 一致性 | 同上；无 GPU 时标记 `[Trait("Requires","GPU")]` 并跳过 |
| `RasterSnapshotTests` | `MetalBrushCoverage.shared != nil`（:121）+ `memcmp` 全等（:65） | 保留 `memcmp` 级全等断言给 CPU 路径；GPU 路径降级为容差比较 + 极差断言（`:114-117` 的 `max-min <= 2` 语义可保留） |
| `BrushPerformanceTests` | 帧时统计 + 真实窗口（:31-51） | **BenchmarkDotNet**：`[Benchmark]` 记录单帧绘制耗时，输出中位数/p95（对齐上游 `:50-51` 的口径）；不要放进 CI 门禁，用 `--job short` 手动跑 |
| `SmartEditTests` | Vision（`SubjectRemoval.run` :75） | 内容感知填充（4 个用例中的 3 个）照搬为 `Imaging` 单测；"主体移除"改为 ONNX Runtime/Windows ML 的能力探测 + 允许 `noSubject` 分支（上游 :77-79 已有该分支） |
| `TypeToolTests` | SwiftUI 文本编辑 + CoreText 排版（19 个用例） | DirectWrite 排版 + 容差（文本框包围盒、行数、run 分段可精确断言；字形栅格化像素用容差）；旧 manifest 用例（:430、:442）进 `Interop` |

**UI 自动化选型建议**：FlaUI（UIA3）为主 —— 纯 .NET、无驱动安装、能在无头 CI 上跑（需要交互式桌面会话）；WinAppDriver/Appium 只在需要跨会话遗留控件时补充。上游 CI 需要 `automationmodetool` 提权（`verify.yml:112`），Windows 对应的是"runner 必须允许 UI 自动化且不能是 Session 0 服务"。

### 4.3 性能测试

上游只有 1 个性能用例且默认不跑（`BRUSH_BENCHMARK=1` 门控，`BrushPerformanceTests.swift:9`），CI 只统计"最慢的 10 个套件"（`ci-tests.sh:59-68`），**没有性能门禁**。Windows 侧建议：
- BenchmarkDotNet 项目独立，`[MemoryDiagnoser]` + `[SimpleJob]`；
- 只对 4 个基准负责：4K 帧时（对齐上游）、4000² 笔刷提交、PSD 读取、.comp 保存/加载；
- 门禁阈值用"相对基线 ±20%"而不是绝对值，避免机型差异导致红。

---

## 5. Windows 测试工程结构与断言辅助

### 5.1 工程结构

```
tests/
  Compositor.Core.Tests/          # 低难度：模型/几何/状态机；无图形依赖
    Layers/  Groups/  Selection/  History/  Guides/  Workspace/
  Compositor.Imaging.Tests/       # 中难度：像素管线（SkiaSharp / WIC）
    Support/  PixelAssert.cs  GoldenImage.cs  RasterFixture.cs
    Brushes/  Filters/  Adjustments/  Masks/  Effects/  Shapes/  Text/
  Compositor.Interop.Tests/       # 格式契约：.comp / PSD / PSB / 导出
    Support/  CompFixture.cs  PsdFixture.cs  PsdVectorFixtures.cs
    Comp/  Psd/  Export/  Clipboard/
  Compositor.UiTests/             # 高难度：FlaUI/WinAppDriver
    Support/  AppFixture.cs  Mouse.cs  Keyboard.cs
    Cursor/  Panels/  TitleBar/  CanvasInput/  Clipboard/
  Compositor.Benchmarks/          # BenchmarkDotNet
  Compositor.TestKit/             # 共享 fixture 与断言（被上面四个引用）
```

关键约定：
- 每个 `.Tests` 工程默认 **禁类级并行**（对应上游 `@MainActor`），只对纯算法工程打开 `[assembly: CollectionBehavior(DisableTestParallelization = false)]`；
- 需要窗口/剪贴板/GPU 的用例打 trait：`[Trait("Requires","Window"|"GPU"|"Clipboard"|"Fonts")]`，CI 按 trait 分层跑（§6）；
- 用 `Microsoft.Testing.Platform`（xUnit v3）以便一个进程内按 `--filter` 精确分片，替代 `xcodebuild -only-testing:`。

### 5.2 断言辅助函数设计（本节的每条都对应上游真实写法）

上游读像素的两种惯用法：`CGContext(..., bitmapInfo: premultipliedLast | byteOrder32Big)` 后取 `data`（`FinishingFilterTests.swift:9-14`、`CloneStampTests.swift:7-12`），或 `BrushRaster.copy(image)`（`RawDitherTests.swift:20`）；比较用逐通道容差（`LargeCanvasBrushTests.swift:67`：`largest <= 1`）或整块相等（`RasterSnapshotTests.swift:65`：`memcmp`）。Windows 侧：

```csharp
// 1) 取像素：统一归一化为 RGBA8 非预乘 + sRGB
public readonly struct Rgba8 { public readonly byte R,G,B,A; }

public static class PixelReader
{
    // Skia: SKBitmap.Decode(...) → SKColorType.Bgra8888, SKAlphaType.Premul → 转非预乘
    // WIC : Format32bppPArgb（内存序 BGRA，预乘）
    public static Rgba8[,] Read(ImageBuffer src);      // 行序：上游是"从上到下"（见 MagicWandTests.swift:21 注释）
    public static Rgba8 At(ImageBuffer src, int x, int y);
}
```

**必须踩准的三个坑（上游写法已确认）**：
1. **通道序**：上游 `byteOrder32Big + premultipliedLast` = 内存里 **RGBA**；Windows `Format32bppPArgb` = 内存里 **BGRA**。断言助手一律归一化到 RGBA 暴露，避免测试代码里到处 `*4+2`。
2. **预乘/非预乘**：上游像素缓冲是**预乘**（`CloneStampTests.swift:10`、`RasterSnapshotTests` 全等比较都建立在预乘之上）。Skia 默认 `Premul`，与上游一致；但 `System.Drawing.Bitmap` 的 `Format32bppArgb` 是**非预乘**，直接比较会全错。
3. **行序**：上游多次强调"top row first"（`CanvasThumbnailTests.swift:13`、`MagicWandTests.swift:21`），而 Direct2D/位图坐标系另有差异 —— 助手要显式带 `origin` 参数。

```csharp
public static class PixelAssert
{
    /// 逐通道容差；对齐 LargeCanvasBrushTests.swift:67 / RasterSnapshotTests.swift:116
    public static void Equal(Rgba8 expected, Rgba8 actual, int tolerance = 0, string? because = null);

    /// 整块全等（对齐 memcmp，RasterSnapshotTests.swift:65），用于 CPU 参考路径
    public static void Identical(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual);

    /// 允许 x% 像素超差 + 每通道上限，用于 GPU/Skia 与 CoreGraphics 的跨实现比较
    public static void Close(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b,
                             int maxChannelDelta = 2, double maxFractionDifferent = 0.0);

    /// 通道统计断言：对齐 "alpha 极差 <= 2"（RasterSnapshotTests.swift:114-117）与
    /// "max-min <= 1 无周期横纹"（:130-133）
    public static void UniformAlong(ReadOnlySpan<byte> channel, int from, int to, int maxSpread);
}
```

**图层合成快照**（对应上游 `LayerRenderer.draw` / `ImageExporter.render` 的对比用法，`RasterSnapshotTests.swift:55-65`）：

```csharp
public static class CompositeSnapshot
{
    /// 用被测引擎渲染一个 ProjectSnapshot，返回 RGBA 非预乘缓冲；
    /// 同一会话可重复渲染并断言"渲染前后不变"（对齐 :66-71 的 frozen 比较）
    public static ImageBuffer Render(ProjectSnapshot snapshot, RenderOptions options);
    public static byte[] Freeze(ImageBuffer buffer);      // 冻结副本，用于后续相等断言
}
```

**像素 golden 准则**：
- CPU 路径：允许**全等**（上游 `memcmp`）。
- 跨实现（Skia/Direct2D vs CoreGraphics）：默认 `maxChannelDelta = 2`、`maxFractionDifferent = 0.01`，逐文件白名单放宽；**不要**用感知哈希，因为它掩盖整块错位。
- 超大图（4000²、5000²）的用例打 `[Trait("Size","Large")]`，默认从快速循环里排除，与上游"窗口套件单独跑"同构。

### 5.3 fixture 复用（有现成的，别自己造）

上游**已经存在**可复用的 fixture，移植时按下列清单搬运（这是"照抄"而不是"设计"）：

| 上游文件 | 提供什么 | 行号 | Windows 落地 |
|---|---|---|---|
| `PSDFixture.swift`（581 行，**非测试**） | `PSDFixture.data(_ document:composite:largeDocument:extras:)`；`struct AdditionalLayerInfo`；字节构建器 `bytes(_:)` | `:6-9`、`:12`、`:531` | 移为 `Compositor.TestKit/PsdFixture.cs`（生成 PSD 字节流，供 reader 测试） |
| `PSDVectorFixtures.swift`（22 行，**非测试**） | `canvas`（1920×1080）、`circle()`、`rectangle()`：`vmsk`/`SoCo`/`vstk` 的 base64 常量 | `:3`、`:4`、`:17` | 原样搬 base64 常量（**不要重写**，这些是 Photoshop 真实字节） |
| `ImageImportTests.fixture(_:orientation:p3:)` | 64×32 PNG/JPEG/TIFF fixture 文件生成（含朝向、DisplayP3） | `ImageImportTests.swift:10` | 提取为 `TestKit/ImageFileFixture.cs`；上游是**跨套件调用**（`CanvasSizeTests.swift:10`、`ImageSizeTests.swift:10`、`ProjectTests.swift:24`、`ProjectTests.swift:99`…），xUnit 里必须放独立 fixture 类而不是复用测试类 |
| `GuideTests.swift:71`、`TypeToolTests.swift:430`/`:442` | 直接 `JSONEncoder().encode(legacy).write(to: manifest.json)`：**旧版本 manifest 的构造方式** | 同上 | `TestKit/LegacyManifest.cs`（v1 无 guides / 旧 text 无 colorRuns） |
| `ExternalChangeTests.swift:38-43`、`:64` | 改写已保存工程的 manifest 来模拟外部变更 | 同上 | `TestKit/CompFixture.RewriteManifest(...)` |
| `ProjectTests.swift:159-163` | 不安全路径 fixture（`"../../outside.png"`）与 `version = 42` | 同上 | 直接作为 `Interop` 的非法输入用例 |
| `ScreenshotTests.picture()`（UI 测试） | 2400×1600 合成风景图，可作 UI 测试输入 | `ScreenshotTests.swift:76-101` | 搬到 `TestKit/SampleImage.cs` |

`.comp` 的最小合法 fixture 见 `04-comp-format.md` §5（那里给出目录结构与 manifest 全文）。

### 5.4 建议的断言辅助对应表

| 上游模式 | 出现次数（口径） | Windows 助手 |
|---|---|---|
| `#expect(a == b)` | 2525 条 `#expect` 中的多数 | `Assert.Equal` |
| `try #require(x)` | 996 | `Assert.NotNull` / `Req.Value(x)` |
| 逐通道容差 | ≥6 个文件 | `PixelAssert.Equal(..., tolerance)` |
| `memcmp` 全等 | 1（`RasterSnapshotTests.swift:65`） | `PixelAssert.Identical` |
| alpha 极差 | 2（`RasterSnapshotTests.swift:114-117`、`:130-133`） | `PixelAssert.UniformAlong` |
| 结构体/元数据相等 | 大量（`LayerEffectsSurviveTests` 的 `Extras`） | 记录类型 `record` + `Assert.Equal`（值语义天然对齐） |

---

## 6. 迁移策略：先移植测试，还是先实现？

**推荐：混合式，按"契约层 → 像素层 → 平台层"三档分别处理。**

| 层 | 建议 | 理由（来自上游事实） |
|---|---|---|
| **低难度 12 个文件**（§1.4） | **测试先行（TDD）** | 断言只依赖值与几何，测试可以完全脱离实现写完（如 `CompositorTests` 的尺寸校验、`NewCanvasUnitTests` 的换算、`ProjectTabLayoutTests` 的溢出几何）。先写测试即可冻结 API 形状。 |
| **中难度 50 个文件** | **契约先行，像素后补**：先把每个用例的**语义断言**（对象状态、尺寸、计数、undo 步数）翻过去并跑通；像素断言先以"参考实现生成的 golden"形式存在，等 C# 渲染管线可跑后再收紧容差 | 上游大量用例是"像素 + 状态"混合（如 `SelectionClipboardTests.swift:48-77`：既断言 `undoCount` 又断言像素）。状态部分可以立刻变成对错判据，像素部分在实现出来之前不可能有 golden。 |
| **高难度 15 个文件** | **不移植，重建** | 上游断言的是 AppKit/Metal/Vision 行为本身（如 `SliderSnapTests.swift:25` 是 `NSSliderCell` 的实现细节）。照抄只会把 macOS 实现细节固化进 Windows。 |

**关键理由（反对"全部先写测试"）**：
1. 像素 golden 无法在实现之前生成 —— 需要参考实现或参考渲染器，否则只能靠人工造期望值，错误会被固化。
2. 上游 55/79 个文件含像素断言，其中相当一部分断言的是 **CoreGraphics 的舍入行为**（如 `byteOrder32Big` 预乘、`CGInterpolationQuality.high` 的降采样核）。这些数值在 Windows 上**不可能相等**，需要按 §5.2 的容差策略重新定义"正确"，这是设计决策不是测试细节。
3. 但**必须**先把 §4.1 的 10 个契约簇的**结构断言**翻过去，否则 Windows 实现会与 `.comp` 格式和图层语义脱节 —— 格式契约是跨平台唯一真值来源（见 `04-comp-format.md` §6）。

**执行顺序建议**：`Interop`（.comp 读写 + PSD fixture）→ `Core` 契约簇 → `Imaging` CPU 路径 → 调整/滤镜 → GPU 路径 → UI 自动化 → Bench。

---

## 7. Windows CI：耗时预估与分片策略

### 7.1 上游的做法（已读全文）

- `scripts/ci-tests.sh` 把测试拆成 **4 个进程**并行：`first`（36 个显式套件，`:20-25`）、`rest`（其余全部：`skip FIRST+FILTER+WINDOW`，`:45`）、`windows`（`FloatingPanelTests`+`SliderSnapTests`，**`-parallel-testing-enabled NO` 单独跑**，`:18`、`:46`）、`filters`（8 个滤镜套件，仅当相关文件变更才跑，`:16-17`、`:47-51`）。
- 分片思路写在注释里：**"按套件大小交替下分，免得一半全是大套件"**（`:19`）；窗口套件"需要安静的机器"所以独占进程（`:5-6`）。
- 每个进程内 `-parallel-testing-enabled YES`；因为几乎所有套件都在 main actor 上，进程内并行度实际受主线程串行化限制（`:4`）。
- 构建用 `SWIFT_OPTIMIZATION_LEVEL=-O`（`verify.yml:77`），注释明确说：**不优化时"像素循环（app 的和测试自己的）会慢好几倍"**（`verify.yml:64-65`）。
- 超时：unit job `timeout-minutes: 60`（`verify.yml:24`）；screenshots job 30 分钟且 `continue-on-error`（`:101-102`）。
- 变更感知：`verify.yml:34-52` 用 `git diff --name-only` + 正则决定滤镜套件是否跑；**xcodeproj 或 ci-tests.sh 变更时强制全跑**（`:48`）。

### 7.2 耗时预估（**推断，未实测**）

我没有 macOS 运行环境，也拿不到任何历史耗时数据（CI 只打印最慢 10 个套件，仓库里没有落盘）。因此这里给的是**模型**，不是测量值，落地时必须先在目标机测一次：

| 项 | 依据 | 结论 |
|---|---|---|
| 上游 unit job 上限 | `verify.yml:24` = 60 min（4 进程并行） | 单进程 shard 的实测墙钟必然 **< 60 min**，否则 CI 会超时 |
| 测试规模 | 567 个 `@Test`、13953 非空行、55 个文件含像素断言 | 计时主要由像素循环主导，不是断言条数 |
| 优化档位 | 上游必须开 `-O` 才可接受（`verify.yml:64-65`） | Windows 侧必须用 `Release` 或 `OptimizationLevel=3` 跑测试，**Debug 会慢数倍** |
| Windows 侧相对速度 | 无法推断 | **必须实测**：同一批用例在 `Debug`/`Release`、CPU-only/GPU 两种配置下各测一次 |

**建议的预算表（待填实测值）**：

| 层 | 用例数（`@Test` 口径） | 预估墙钟（Release、单进程） | 备注 |
|---|---|---|---|
| Interop（.comp/PSD） | 约 60 | 待测 | 纯 CPU，稳定 |
| Core 契约 | 约 150 | 待测 | 快 |
| Imaging CPU | 约 300 | 待测 | 含 4000²/5000² 大图用例，建议单独 trait 分层 |
| GPU | 约 22（`GPUCanvasTests` 19 + `MetalWarpTests` 3） | 待测 | 无 GPU 机器上跳过 |
| UI（FlaUI） | 重建后的约 40 | 待测 | 每个用例含启动/等待，通常是分钟级 |
| Bench | 4 个基准 | 手动跑 | **不进 CI 门禁** |

### 7.3 Windows 分片策略（对齐上游思路）

1. **按"套件大小交替下分"照抄**：把已知慢套件（`PSDRoundTripTests` 31 个用例、`CameraRawTests` 28、`SelectionTests` 24、`LayerTests` 21、`TypeToolTests` 19、`SelectionEditTests` 19、`BrushTests` 19、`GPUCanvasTests` 19）交替分到不同 shard，避免"一半全是大套件"。
2. **GPU/窗口/剪贴板套件独占进程**：对应上游 `windows` 分片（`ci-tests.sh:46` 用 `-parallel-testing-enabled NO`）。Windows 上用 `[Trait("Requires","GPU")]` / `"Window"` / `"Clipboard"` 过滤，一条 `dotnet test --filter` 命令跑一类，且 `-p:ParallelizeTestCollections=false`。
3. **变更感知门禁**：照抄 `verify.yml:34-52` 的思路，用 `git diff --name-only` 决定滤镜/PSD 套件是否跑；**工程文件或 CI 脚本自身变更时强制全跑**（这条上游明确写了，`verify.yml:48`）。
4. **每 shard 一个进程**：`dotnet test` 4 次并行（PowerShell `Start-Job` 或 CI 矩阵 4 个 job），最后合并 TRX；上游用 `xcresult` 逐 shard 出日志（`ci-tests.sh:57`、`:69-71`），Windows 用 `--logger trx`。
5. **慢套件排行**：上游每个分片都打印最慢 10 个套件用于调分片（`ci-tests.sh:59-68`）。Windows 侧用 TRX 解析出 top-10 时长，**这是唯一能让分片长期均衡的机制**，第一版就要做。
6. **构建加速**：上游 `build-for-testing` 一次、`test-without-building` 多次（`ci-tests.sh:36`）。Windows 对应 `dotnet build` 一次 + `dotnet test --no-build` 多次；并对 SkiaSharp 原生库做缓存。
7. **UI 自动化前置条件**：runner 必须有交互式桌面会话（不能是 Session 0 服务），否则 FlaUI 全红 —— 这对应上游要 `automationmodetool` 提权（`verify.yml:110-112`）。

---

## 8. 不确定项（需要在 Windows 侧实测/复核）

1. **CI 耗时**：§7.2 全是模型，没有任何上游实测耗时数据（仓库里不存在）。第一件事就是在目标机跑一次 `dotnet test` 全量并记录。
2. **`CameraRawSliderTests` 的像素口径**：口径 A 判定它命中（`NSBitmapImageRep` 3 处），但我没有逐行读完该文件（74 行），其断言主体是 `NSColor` 分量大小关系（`:6-16`）。→ 归类为"部分"更稳妥。
3. **`MagicWandTests`/`LevelsTests`/`CropTests`/`LayerMaskTests` 的 UI 符号命中**：ui 计数 1–4，可能是辅助函数里擦到 `NSView`/`NSMenu`，不代表需要窗口。→ 已按"部分"处理，移植时可下沉到 `Core`。
4. **`SelectionTests` 的像素判定**：口径 A 判为"无像素断言"，但它断言整像素轮廓与"cmd 点击选不透明像素"（`:283`、`:319`），实质与像素语义强相关。→ 标为"否/部分"，实现时按需补 golden。
5. **`GPUCanvasTests` 的 GPU 等价性**：无法判断 Windows GPU 后端能否达到上游 `memcmp` 级别的逐帧一致；默认按容差策略（§5.2）设计，若容差无法收敛则把"GPU 与 CPU 一致"降级为"GPU 与 GPU 自一致 + CPU 为真值"。
6. **`@MainActor` 语义的映射**：上游 76 个文件标 `@MainActor`，意味着"测试代码与被测对象同在主线程"。Windows 侧若把 `EditorSession` 放到专用 STA/单线程调度器上，需要重新验证"测试线程 == 会话线程"这一隐含前提，否则会出现只在测试里复现的竞态。
