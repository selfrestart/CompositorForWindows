# Wave 0 冻结契约

> **本文件是 Wave 0 期间并行开发的唯一接口来源。改动它必须先通知 Lead。**
> 冻结日期：2026-10-11　|　对应目标：`05-roadmap.md` §4
> 上游：`robbietilton/Compositor` @ `3081719`（只读）

---

## 0. 本机环境硬性事实（已实测，违反会浪费你的时间）

| 事实 | 后果 | 证据 |
|---|---|---|
| `DOTNET_ROOT` 必须设为 `C:\Program Files\dotnet` | 否则**任何** `dotnet` 命令失败：`hostpolicy.dll not found in sdk\8.0.425`。根因：注册表 `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\install_location` 为空 | 已持久化到用户环境变量；新开终端自动生效 |
| 仓库根 `nuget.config` 必须存在 | 否则 `dotnet build` 报 `MSB4018` | 已在仓库根放好，勿删勿移 |
| 可用 SDK：7.0.304 / 8.0.425。目标 `net8.0` | 用 SDK 7 构建 net8.0 → `NETSDK1045` | `dotnet --list-sdks` |
| 本机 **无 MSVC**，有 `gcc 10.3.0`（`D:\tdm-gcc\bin\gcc.exe`） | C 内核只能用 gcc 编译验证 | `docs/port/02-toolchain.md` §3 |
| 本机 **无 Windows SDK** | 不能做 MSIX、不能编译 WinUI 3 的 C++ 部分。**Wave 0 不做 GUI** | 同上 |

---

## 1. 项目边界（不可协商）

```
Compositor.sln
├─ src/Compositor.Core/      net8.0            ← 禁止引用任何 Windows API / net*-windows TFM
├─ src/Compositor.Pixels/    net8.0            ← 禁止引用任何 Windows API / net*-windows TFM
├─ src/Compositor.Codecs/    net8.0            ← 允许 SkiaSharp（跨平台）
├─ native/compositor_pixels/ C 源码 + 构建脚本
└─ tests/Compositor.Tests/   net8.0            ← xUnit
```

**硬性规则**：`Compositor.Core` 与 `Compositor.Pixels` 必须能在 `ubuntu-latest` 上 build + test 通过。
任何 `net8.0-windows`、`Microsoft.Windows.*`、`System.Windows.*`、P/Invoke 到 win32 的调用都属于违规。
（`Compositor.Pixels` 对 `compositor_pixels.dll` 的 P/Invoke 是唯一例外，但它必须能在无该 DLL 时被跳过而非崩溃。）

---

## 2. `.comp` 格式契约（读 v1–v11，写 v11）

`ProjectManifest.current = 11`，`supported = 1...11`（`ProjectStore.swift:15-18`）。
**必须写到 v11**：当前 macOS 每次保存都写 v11，只支持到 v9 会拒绝所有新文件。

### 2.1 字节级形态（`ProjectStore.swift:108-109`）

```swift
encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
```

- **两级缩进 + 键排序**。写成别的形态 macOS 仍能读，但会改变 `ProjectDigest` → 触发 macOS 侧"文件被外部修改"提示。
- 无 BOM、UTF-8、`\n` 换行。
- `Double` 用最短往返表示（.NET 默认行为与 Swift 一致，但**不要**用 `"F2"` 之类格式化）。

### 2.2 类型映射（`ProjectStore.swift:13-56`）

```swift
struct ProjectManifest {
    var format = "com.compositor.project"
    var version = 11
    var colorSpace = "sRGB"
    var resolution: Double? = nil      // 缺省 = 72
    let documentID: UUID
    let width: Int
    let height: Int
    let activeLayerID: UUID?           // 可选；若出现必须命中 layers
    var layers: [ProjectLayerRecord]
    var guides: [CanvasGuide]? = nil   // v8+
}

struct ProjectLayerRecord {
    let id: UUID
    let name: String
    var isVisible: Bool
    let transform: LayerTransform
    let imageFile: String?
    var parentID: UUID? = nil
    var isGroup: Bool? = nil
    var opacity: Double? = nil
    var blendMode: LayerBlendMode? = nil
    var maskFile: String? = nil
    var maskEnabled: Bool? = nil
    var maskSourceID: UUID? = nil
    var adjustment: LayerAdjustment? = nil
    var maskPlacement: LayerTransform? = nil
    var maskLinked: Bool? = nil
    var shape: LayerShapeStyle? = nil
    var effects: LayerEffects? = nil
    var text: LayerTextStyle? = nil
}
```

Swift 的 `let x: T?`（无默认值）与 `var x: T? = nil` 在 `Codable` 下行为**不同**：
- `let imageFile: String?` → **编码时必写**，值可为 `null`。
- `var opacity: Double? = nil` → 用 `encodeIfPresent`，**nil 时整个键省略**。

`.NET` 侧对应：必写字段用普通属性；可选字段用 `[JsonIgnore(Condition = WhenWritingNull)]`。

### 2.3 UUID 形态

Swift `UUID` 编码为**大写**字符串（`"4A1B…"`）。`.NET` 的 `Guid.ToString()` 默认小写 →
**必须用 `ToString("D").ToUpperInvariant()`**。
`imageFile` 的命名规则是 `<layer UUID 大写>.png`，掩码是 `<layer UUID 大写>.mask.png`。

### 2.4 ⚠️ 未决项 1 已关闭：`hsvSettings` 的字典编码形态

**这是本次最关键的发现，写错会导致所有 v7+ 调整层解析失败。**

`HueSaturationSettings.adjustments` 与 `.bands` 的类型是 `[ColorRange: …]`（`HueSaturation.swift:176-177`），
而 `ColorRange` 只声明了 `String, CaseIterable, Sendable, Hashable, Codable`（`HueSaturation.swift:5`）——
**没有声明 `CodingKeyRepresentable`**（全仓 0 命中），也没有任何自定义 `Codable` 实现，
`JSONEncoder` 也没有设 `keyEncodingStrategy`（`ProjectStore.swift:108-109` 只设了 `.prettyPrinted, .sortedKeys`）。

结论：SE-0320 的"字典键即 JSON 对象键"只在类型**显式声明** `CodingKeyRepresentable` 时生效。
因此 Swift 的标准 `Dictionary` 编码走的是**键值交替的扁平数组**，不是 JSON 对象：

```jsonc
// adjustments —— 数组，不是对象
"adjustments": [ "Master", { "hue": 0, "saturation": 0, "lightness": 0 }, "Reds", { … } ]

// bands —— 同样是数组
"bands": [ "Master", { "falloffStart": 0, "rangeStart": 0, "rangeEnd": 360, "falloffEnd": 360 }, … ]
```

`ColorRange` 的取值（`HueSaturation.swift:6-7`）：
`"Master"`、`"Reds"`、`"Yellows"`、`"Greens"`、`"Cyans"`、`"Blues"`、`"Magentas"`。

**`.NET` 侧实现要求**：这两个字段**不能**用 `Dictionary<string, T>` 直接反序列化（那样期望的是对象）。
必须写成能同时接受两种形态的自定义 `JsonConverter`，或（Wave 0 建议）先按数组形态实现，
遇到对象形态则抛出明确异常而不是静默丢数据。

> `adjustments` 的成员 `RangeAdjustment` 与 `bands` 的成员 `HueBand` 见 `HueSaturation.swift:26-30, 162-168`。

### 2.5 校验与拒绝规则（`ProjectStore.swift:146-201` + `DocumentLimits.swift`）

**精确值（我直接读了源码，勿凭文档）**：

| 限制 | 值 | 出处 |
|---|---|---|
| 单边上限 | `30_000` px | `DocumentLimits.swift:18` |
| 单表面像素上限 | `200_000_000` | `DocumentLimits.swift:25` |
| 文档总像素预算 | `min(800_000_000, max(200_000_000, 物理内存 / 16))` | `DocumentLimits.swift:36-37` |
| manifest 大小 | 4 MiB | `project-format.md:9` |
| 单资源大小 | 512 MiB | 同上 |
| 图层数 | 10_000 | 同上 |
| 嵌套深度 | 64 层祖先 | `project-format.md:15` |
| guides 数 | 1_000，position 绝对值 ≤ 1_000_000 | `project-format.md:31` |

**⚠️ 关键陷阱**：`docs/project-format.md:9` 写的是"1 亿图像像素 + 1 亿蒙版像素"，**与实现不符**。
实现是上面那条随物理内存缩放的公式，且图像与掩码**各用一个独立计数器**
（`ProjectStore.swift:87, 94-95, 180-181`）。硬编码 1 亿会让 Windows 版比 macOS 严 2–8 倍。

**宽容读、严格写**：以下三处在 macOS 侧**不做校验**，Windows 侧若"更严格"会打不开合法文件：
1. `effects` 不在 `ProjectStore.validate` 里（越界值只在渲染期被当作"无效果"忽略）。
2. `shape` 没有任何 `isValid` / 校验。
3. `blendMode` 的取值集合**不按版本限制**（只限制 `version < 3` 时必须为 Normal）。

### 2.6 混合模式是 24 种，不是 9 种

文档说 v3 引入 9 种；实现见 `LayerAppearance.swift:4-13`，共 **24 种**。**逐字照抄枚举，不要按文档裁剪。**

---

## 3. 冻结的 C# 接口

### 3.1 `Compositor.Pixels` —— C 内核绑定

C 侧导出（`native/compositor_pixels/`，共 42 个入口，签名保持 `AdjustPixels.h:8-119` 原样）：

```c
void adjust_black_white(uint8_t *rgba, size_t width, size_t height, size_t stride,
                        const float *weights, int tint, double tintHue, double tintSaturation);
```

C# 侧统一用 `LibraryImport` + `ref byte` / `nuint`：

```csharp
[LibraryImport("compositor_pixels", EntryPoint = "adjust_black_white")]
internal static partial void AdjustBlackWhite(
    ref byte rgba, nuint width, nuint height, nuint stride,
    ref float weights, int tint, double tintHue, double tintSaturation);
```

调用方用 `MemoryMarshal.GetReference(span)` 传引用。**像素缓冲一律是 4 字节/像素、RGBA、非预乘、行距可大于 `width*4`。**

### 3.2 `Compositor.Core` —— 关键类型

```csharp
public enum BlendMode { Normal, Multiply, Screen, Overlay, Darken, Lighten, Difference, ColorDodge, ColorBurn, /* … 共 24 种，照抄 LayerAppearance.swift:4-13 */ }

public readonly record struct LayerTransform(double OriginX, double OriginY, double Width, double Height, double Rotation, bool FlipHorizontal, bool FlipVertical);
```

### 3.3 `Compositor.Codecs` —— 像素缓冲与 PNG

**SkiaSharp 已实测可用**（`SKBitmap.Decode` / `SKImage.Encode`，运行时加载原生库 OK）。

⚠️ **Skia 解码默认给 `Bgra8888` + `Premul`**，而 `.comp` 内嵌 PNG 与 C 内核都用 **RGBA + Unpremul**。
**每次解码后必须显式转换**，否则红色与蓝色通道互换、半透明像素数值错误。这是个静默错误，测试必须覆盖带透明度的像素。

```csharp
public sealed class PixelBuffer          // 唯一跨模块的像素容器
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }           // 字节，= Width * 4（Wave 0 不做额外行距）
    public byte[] Pixels { get; }        // RGBA, 非预乘
    public Span<byte> Span { get; }
}
```

---

## 5. 混合模式的实现状态与已知缺口

Wave 0 已实现全部 24 种混合模式（`src/Compositor.Codecs/Blend.cs`、`NonSeparable.cs`），
公式取自 W3C 合成规范——即 Photoshop 与 Core Image 共同实现的那一套。

**已用上游记录的实测数值验证**（`SeparableBlend.swift:14-16`）：40% 灰之上、80% 灰的
Color Dodge 得 1.0（100%），Color Burn 得 0.25（25%）。这两个数是判据最锐利的一组，因为线性
空间会分别得到 0.62 和 0.0。

**已交叉验证的两条绘制路径（Wave 2 更新）**：`Surface.Draw` 有两条路径——轴对齐且不缩放时逐像素
合成（走 `LayerCompositor.CompositePixel`），需要旋转/翻转/缩放时交给 Skia。两条路径对 17 种
Skia 能计算的模式**结果一致（≤1 LSB）**，已由 `SurfaceTests.BothPaths_AgreeAboutColour` 逐模式断言。

**SkiaSharp 2.88.9 的缺口（实测）**：7 种模式**没有** Skia 常量——Linear Burn（Skia 的 `Plus`
是 Dodge 那一半，不是 Burn）、Subtract、Divide 三种在 Skia 集合里根本不存在；Vivid Light、
Linear Light、Pin Light、Hard Mix 是 advanced blend，但这个绑定没有暴露。这 7 种被
`SkiaInterop.MapsExactly` 标记为不可映射，`Surface.Draw` 会把它们送进像素精确路径，所以它们的
结果永不依赖近似。

**⚠️ 尚未验证的部分（诚实标注）**：

1. **11 种走 Core Image 的模式未经实物对拍**。上游对 Color Burn、Color Dodge、Soft Light 以及
   Linear Burn/Dodge、Vivid/Linear/Pin Light、Hard Mix、Subtract、Divide 走的是 `CI*BlendMode`
   滤镜（`LayerAppearance.swift:51-69`）。本移植按规范实现，且已证明与 Skia 一致，但 Core Image
   在边界情形（如 Soft Light 的分段阈值、Hard Mix 的取整）是否与规范逐位一致，**只能靠一份
   macOS 真实产物来确认**。
2. **Hue/Saturation/Color/Luminosity 是非可分离模式**，规范定义了它们但 Core Graphics 的
   实现细节（尤其 `SetLum` 的裁剪路径）可能有差异。同样需要实物对拍。
3. **Round/取整策略未对齐**：本移植用 `Math.Round`（银行家舍入，`MidpointRounding.ToEven`），
   而 Core Graphics 的取整行为未经验证。这会在恰好 .5 的像素上产生 ±1 的差——正是退出标准 3
   的 1 LSB 容差能否守住的关键点。
4. **旋转/缩放的插值未对拍**：Skia 路径用 `SKFilterQuality.High`，上游用 Core Graphics 的
   `CGInterpolationQuality`（`LayerTransform.swift:4-12` 的 `.none`/`.low`/`.high` 三档）。
   `LayerSampling` 枚举已映射到语义，但两者的重采样核不保证逐位相同。

**结论**：Wave 0 的合成器足以支撑垂直切片与自洽性测试，但**在拿到 macOS 黄金样本之前，
不应声称与 macOS 逐像素一致**。这四条是 Wave 2 的首要任务。

---

## 6. Wave 2 的部分移植：明确标注的边界

有些上游组件我**只移植了其中可独立验证的一部分**。这不是遗漏，而是刻意的切分——把与尚未移植的子系统（画笔、GPU、UI）耦合的部分留到它真正需要时再做，而不是靠猜。

| 上游组件 | 已移植 | 未移植 | 为什么这样切 |
|---|---|---|---|
| `TiledLayerRenderer`（419 行） | **网格算术**（`TileGrid`）：`support(level)`、`align`、`interiors(near:)`、`piece` 的 region 计算、硬边裁剪的屏幕像素对齐 | **piece 的实际合成**（把整图分辨率像素画进上下文、按 level 减半、逐片硬边裁剪） | 合成步骤与画笔描边、以及 Core Graphics 的**左下原点**翻转纠缠在一起；它服务的绘制系统还没移植。网格算术是其余部分依赖的基础，且可独立测试 |
| `DownsampleCache` | 减半链、level 选择、颜色垫边、掩码重复末行/列 | **LRU 缓存与像素预算淘汰** | 缓存策略需要先有"图像身份"的概念（上游用 `ObjectIdentifier(CGImage)`）；本移植的缓冲是值语义，缓存键的设计要等渲染调用方定型 |
| `LayerCompositor` | 全部 24 种混合模式、图层/文件夹可见性与不透明度、蒙版、**旋转/翻转/缩放的图层变换**（经 `Surface` 重采样） | **调整层、图层效果**；**带蒙版且需要重采样的图层**（掷 `NotSupportedException` 并指名图层，因为蒙版范围与图层自身矩形一致，需走同一变换） | 拒绝比画错好；调整层与图层效果属于 Wave 2/3 的后续件 |
| `LayerSampling`（`nearest`/`smooth`/`high`） | 枚举与 JSON 名称；`Surface` 的重采样路径目前固定用高质量 | **按图层选择采样质量**（`Surface.Draw` 尚无此参数） | 需要时再加；上游默认是 `.high`，所以当前行为与其默认一致 |
| 调整层（12 种 kind） | **5 种**：`Invert`、`Black & White`、`Exposure`、`Levels`、`Curves`（12 种 kind 的枚举与 JSON 名称全部移植） | **7 种**：`Hue/Saturation`（需色带算术）、`Gradient Map`、`Grain`、`Color Balance`（走 native 内核）、`Gaussian Blur`、`Motion Blur`、`Add Noise`（邻域采样） | 已实现的 5 种都表达为逐通道查找表——这正是 native `levels_apply` 的形状（LevelsPixels.c:3-16），所以一次实现同时服务它们。其余 7 种各自需要不同的机制，不是同一件事的变体 |
| 图层效果（6 种） | **全部 6 种**：描边（内/外）、投影、颜色叠加、内阴影、外发光、内发光；六步绘制顺序已按上游落实 | **无** | 纯形态学 + 可分离高斯 + source-over 合成，不依赖 native 内核也不依赖画笔 |
| `Compositor.Pixels` | `NativeKernel`（可用性探测）、`KernelHeap.Free` | **42 个内核函数对外公开**（目前 internal） | 公开 42 个函数是独立的设计决定。它是解锁剩余 4 种可复用内核的调整层（`Gradient Map`、`Grain`、`Color Balance`）的前置 |

### 一处需要 macOS 样本才能定的语义分歧：`Curves` 的通道顺序

上游 `CurvesSettings.apply`（Curves.swift:37）把表构建成
`(1...3).flatMap { channel in (0...255).map { value(value($0, channel: channel), channel: 0) / 255 } }`：

先让每个像素通道过**它自己的**曲线，**再**过**合成**通道的曲线。后果是合成通道被**二次施加**到每个通道上，而且只在各通道曲线**之后**。

摄影师预期的顺序是「先合成、后单通道，各一次」。本移植按后者实现（`Adjustment.Curves`）。

- 两者在**任一曲线是恒等**时结果相同（测试覆盖了这一点）。
- 两者在「非恒等合成 + 非恒等单通道」时**分歧**——该用例需要一份 macOS 真实产物才能定谁对。
- 这是**已知的、有意的**分歧，不是疏忽；`Adjustment.Curves` 的文档注释里写明了。

**已移植部分的验证**：`TileGrid` 的三条不变式各有直接测试——piece 边界落在 halving 网格上、piece 互不重叠且不越出自己的格子、合成区域始终包含它将被裁剪到的 interior。这三条正是"无接缝"保证的全部内容。

---

## 7. Wave 0 退出标准（`05-roadmap.md` §4）

1. 干净 `windows-latest` 上 `dotnet build Compositor.sln -c Release` 零 error。 ✅ **已达成**（本机 0 错误 0 警告）
2. **≥10 项 C 内核 parity 测试**通过（对齐 `spikes/c-kernel`）。 ✅ **已达成**（绑定/parity 测试 38 项全绿）
3. 打开一个 3 图层 `.comp`，合成结果与 macOS 导出 PNG 逐像素差 ≤1 LSB。 🟡 **链路已达成，比对待样本**：读→合成→导出 PNG 全链路已实现并有测试（`CompEndToEndTests`），但**与 macOS 产物的逐像素比对仍需一份 macOS 黄金样本**（见 §5 的三条已知缺口）。
4. `Compositor.Core` 在 `ubuntu-latest` 上 build + test 通过。 ✅ **已具备条件**（三个平台无关工程均无 Windows API；已补 SkiaSharp Linux 原生库；CI 含 grep 守卫）
5. `hsvSettings` 的 JSON 形态用 macOS 真实产物确认。 ✅ **已由源码分析关闭**（§2.4），仍需实物最终确认。
