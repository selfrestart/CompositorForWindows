# 04 · .comp 格式契约（v1–v11）与 Windows 侧 DTO/校验器规格

> 唯一权威文档：`src/Compositor/docs/project-format.md`（v1–v11，52 行）、`src/Compositor/docs/writing-comp-files.md`（写文件规则）。
> 交叉验证实现：`Compositor/IO/ProjectStore.swift`（278 行）、`ProjectDigest.swift`（32 行）、以及 `Compositor/Document/` 下 58 个文件中的序列化类型。
> 凡文档与实现不一致处，本文**以实现为准**，并在 §7 单独列出差异清单。

---

## 0. 结论速览

1. `.comp` 是一个**目录包**：`manifest.json` + `images/<UUID>.png`（可选 `images/<UUID>.mask.png`、可选 `QuickLook/Preview.jpg`）。不是 zip，不是单文件（`ProjectStore.swift:147` 要求 `isDirectory == true`）。
2. 平台间传输**不能用打包压缩改变内部结构**：macOS 用 `FileWrapper` + `NSFileCoordinator` 做原子包替换（`ProjectStore.swift:122-131`），Windows 侧必须复现"先写资源，再原子替换 manifest"的顺序（`writing-comp-files.md:79-80`）。
3. **Windows 第一版必须能读 1–11 版、写第 11 版**。理由见 §3.1 与 §6.1：`ProjectManifest.current = 11`（`ProjectStore.swift:15`），当前 macOS 版本**每一次保存都写 version 11**，任何"只支持到 v9"的实现在版本门禁处（`ProjectStore.swift:156`）会拒绝所有新保存的文件。
4. 校验规则共 **35 条**（A 版本/格式 4 + B 结构 6 + C 图层语义 9 + D 特性门槛 7 + E 资源与安全 6 + F 保存侧 3），其中 6 条是资源与安全类（E 组：缺失资源、体积、路径穿越、符号链接、边长/像素预算、图像与蒙版双计数器）。
5. 发现 **7 处文档与实现不一致**（§7），其中 3 处会直接导致 Windows 实现写错校验器。

---

## 1. 包结构与编码约定（写 C# DTO 前必须知道）

```
Example.comp/                      ← 目录（macOS 上是 document package）
├── manifest.json                  ← 必需，UTF-8 JSON，≤ 4 MiB
├── images/                        ← 必需目录（可为空）
│   ├── 6F1D3C2A-….png             ← 图层像素，文件名 = 大写 UUID + ".png"
│   └── 6F1D3C2A-….mask.png        ← 蒙版，文件名 = 大写 UUID + ".mask.png"
└── QuickLook/Preview.jpg          ← 可选，仅给 Finder 预览；加载时忽略（ProjectStore.swift:116-121）
```

### 1.1 编码约定（逐条有据）

| 约定 | 事实 | 依据 |
|---|---|---|
| 编码器 | `JSONEncoder`，`outputFormatting = [.prettyPrinted, .sortedKeys]` | `ProjectStore.swift:108-110` |
| 解码器 | 先解 `Header{format, version}`，再解完整 `ProjectManifest` | `ProjectStore.swift:79-82`、`:152-157` |
| 可选字段 | **nil 时整个键不出现**（Swift 合成的 `encodeIfPresent`）；缺失时按默认值 | `ProjectStore.swift:23`、`:39-55`；`writing-comp-files.md:28-58` 样例中省略了 `parentID` 等 |
| `UUID` | 编码为**大写**带连字符字符串；文件名里的 UUID 也必须大写 | `writing-comp-files.md:70`（"with the ID in uppercase as written in the manifest"）、`ProjectStore.swift:223`、`:243` 用 `uuidString` 拼路径 |
| `CGPoint` / `CGSize` | 编码为 **JSON 数组** `[x, y]` / `[width, height]` | `writing-comp-files.md:48-49` 的 `"origin": [0, 0]`、`"size": [1920, 1080]` |
| 未知键 | Swift 合成解码会**忽略**未知键（不报错）；但它们会在下次保存时消失 | 合成 `Decodable` 语义（推断，见 §8.3） |
| 字符串枚举 | 大小写敏感，见 §3 各表；拼错即整包拒绝 | `ProjectStore.swift:157`（解码失败→`invalid`） |

**C# 侧建议**：`JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }`；`Guid` 用自定义转换器输出 `ToString("D").ToUpperInvariant()`；`PointDto`/`SizeDto` 用数组转换器；把所有可选字段声明为 `Nullable<T>` 并在 `JsonIgnoreCondition.WhenWritingNull` 下序列化（与 Swift 一致）。

### 1.2 位图约束

| 资源 | 约束 | 依据 |
|---|---|---|
| 图层 PNG | 必须 PNG、**单帧**、位深 ≤ 8 | `ProjectStore.swift:173-179`（`CGImageSourceGetType == png`、`GetCount == 1`、`kCGImagePropertyDepth <= 8`） |
| 图层 PNG 通道 | 文档要求 RGBA（`writing-comp-files.md:71`），**实现未强制校验** | 见 §7-6 |
| 蒙版 PNG | 必须 **8bpc 灰度且无 alpha 通道**（`colorSpace.model == .monochrome`、`alphaInfo == .none`） | `LayerMask.swift:23-26` + `ProjectStore.swift:93`、`:189` |
| 缩略图 | 加载时必须能生成（96 px 上限），否则拒绝 | `ProjectStore.swift:184-188` |
| 尺寸 | 每边 1–30000；单资源像素 ≤ 预算；清单 ≤ 4 MiB；单资源 ≤ 512 MiB | `ProjectStore.swift:111`、`:149`、`:167`、`:264-269` |

> **给 C# 写文件器的硬要求**：蒙版必须写成 PNG **color type 0（Grayscale）**，不能是 color type 4（Grayscale+Alpha）——后者 `alphaInfo != .none`，macOS 会拒绝整包（`ProjectStore.swift:189`）。

---

## 2. 版本迁移 / 兼容矩阵（v1–v11）

**门禁总则**（`ProjectStore.swift:15-18`、`:156`、`:200`）：
- `current = 11`，`supported = 1...11`；
- 文件声明的 `version` ∉ 1…11 → **整包拒绝**并报 `ProjectError.version(n)`（`ProjectStore.swift:69`、`ProjectTests.swift:152-158` 用 `version = 42` 钉住该行为）；
- 声明旧版本的文件**不得包含**后续版本才引入的字段值 → 整包拒绝（逐条见下表"拒绝规则"列）；
- 组记录与调整层记录在旧版本下的额外限制见 v3/v7 行。

| 版本 | 新增内容 | 硬约束 | 拒绝规则（实现行号） |
|---|---|---|---|
| **v1** | 基础：`format`/`version`/`colorSpace`/`documentID`/`width`/`height`/`activeLayerID`/`layers`；每层 `id`/`name`/`isVisible`/`transform`/`imageFile` | `format == "com.compositor.project"`；`colorSpace == "sRGB"`；1 ≤ 边长 ≤ 30000；≤ 10000 层 | v1 含 `parentID` 或 `isGroup: true` → 拒绝（`:237`）；`opacity ≠ 1` 或 `blendMode ≠ Normal` → 拒绝（`:231`）；含 `maskFile`（`:222`）、`maskSourceID`（`:236`）、`adjustment`（`:216`）、非空 `guides`（`:251-253`）→ 拒绝 |
| **v2** | 图层可选 `parentID`、`isGroup` | 组**不得有** `imageFile`；父节点必须存在且为组；无环；祖先层数 ≤ 64 | 父不存在/非组、环、深度 >64 → 拒绝（`LayerGroups.swift:30-46`）；组带 `imageFile` → 拒绝（`LayerGroups.swift:34`） |
| **v3** | 图层可选 `opacity`（0–1 有限）、`blendMode` | 缺省为 1 / `Normal`；**组**的 `blendMode` 必须为 `Normal`（folder 是 pass-through） | v<3 出现非默认值 → 拒绝（`:230-232`）；组 `blendMode ≠ Normal` → 拒绝（`:232`） |
| **v4** | 图层可选 `maskFile`、`maskEnabled` | `maskFile` 必须等于 `"<layer uuid 大写>.mask.png"`；`maskEnabled` 存在则 `maskFile` 必须存在；**组**在本版不得有蒙版 | 文件名不符 → 拒绝（`:222-223`）；只有 `maskEnabled` 没有 `maskFile` → 拒绝（`:224`）；v<4 图层有蒙版、v<6 组有蒙版 → 拒绝（`:222`） |
| **v5** | 图层可选 `maskSourceID`（剪贴蒙版/实时 alpha 源） | 源必须是存在的**非组**图层、且不是调整层；不得自指、成环；链长 < 256 | 上述任一违反 → 拒绝（`LiveLayerMask.swift:4-19`）；`version < 5` 出现该字段 → 拒绝（`ProjectStore.swift:236`） |
| **v6** | 允许**组**记录带 `maskFile` / `maskEnabled`（蒙版覆盖文件夹自身 transform 矩形） | 同 v4 的文件名规则 | v<6 的组带蒙版 → 拒绝（`:222`） |
| **v7** | 图层可选 `adjustment`（调整层） | `adjustment` 记录必须：`imageFile == nil`、`isGroup != true`；`kind` ∈ 12 种；`LayerAdjustment.isValid` 全通过 | `version < 7` 出现 `adjustment` → 拒绝（`:216`）；`imageFile` 非空或是组 → 拒绝（`:216`）；`kind` 非法或任一设置越界 → 拒绝（`:216`、`LayerAdjustment.swift:128-141`） |
| **v8** | ① 允许**组**自带 `opacity`（仍须 `blendMode == Normal`）；② 顶层 `guides` 数组 | guides：≤ 1000 条；`id` 唯一；`position` 有限且 \|position\| ≤ 1,000,000；`axis` ∈ {`horizontal`,`vertical`} | v<8 出现非空 `guides` → 拒绝（`:251-253`）；>1000 条 → `tooLarge`（`:255`）；重复 id / 越界 → 拒绝（`:258`） |
| **v9** | 三个采样邻域的调整种类：`Gaussian Blur`、`Motion Blur`、`Add Noise`；字段 `blurRadius`、`motionAngle`、`motionDistance`、`noiseAmount`、`noiseGaussian`、`noiseMonochromatic`、`noiseSeed` | `blurRadius` 0.1–250；`motionAngle` −90–90；`motionDistance` 1–2000；`noiseAmount` 0.1–400 | v<9 出现这三种 `kind` → 拒绝（`:217-219`）；取值越界 → 拒绝（`LayerAdjustment.swift:137-140`） |
| **v10** | `text.colorRuns`（逐字颜色） | run 规则见 §3.5 | `version < 10` 出现 `colorRuns` → 拒绝（`:211`） |
| **v11** | `text.fontRuns`（逐字字体） | run 规则见 §3.5；`fontName` 非空、≤200 字符、不含换行 | `version < 11` 出现 `fontRuns` → 拒绝（`:212`） |

**无版本门槛的"增量字段"**（`project-format.md:39-44`，实现确认不参与版本判断）：
- `resolution`（顶层，1–9600，使用处默认 72：`EditorSession+Projects.swift:29`、`ImageExporter.swift:74`、`ImageResizer.swift:117`）
- `maskPlacement`、`maskLinked`
- `shape`
- `effects`

---

## 3. schema 全量字段清单（可直接写 C# DTO）

**图例**：`必` = 不可省略；`选` = 可省略（`null`/缺失等价）；"版本门槛"= 该字段**出现**时要求的最低版本（`—` 表示无门槛）。

### 3.1 `manifest.json` 顶层（`ProjectStore.swift:13-31`）

| JSON 字段 | C# 类型 | 取值 / 范围 | 可选性 | 默认值 | 版本门槛 |
|---|---|---|---|---|---|
| `format` | `string` | 必须 `"com.compositor.project"` | 必 | `"com.compositor.project"` | — |
| `version` | `int` | 1–11（`supported = 1...current`） | 必 | `11` | — |
| `colorSpace` | `string` | 必须 `"sRGB"`（其他值整包拒绝） | 必 | `"sRGB"` | — |
| `resolution` | `double?` | 1–9600，有限 | 选 | `null` → 使用处按 **72** 处理 | —（增量；`project-format.md:13`） |
| `documentID` | `Guid` | 任意 UUID | 必 | — | — |
| `width` | `int` | 1–30000 | 必 | — | — |
| `height` | `int` | 1–30000 | 必 | — | — |
| `activeLayerID` | `Guid?` | 若存在必须指向 `layers` 中的某层 | 选 | `null` | — |
| `layers` | `List<LayerRecord>` | 数量 ≤ 10000；**数组顺序 = 从下到上**（最后一项画在最上） | 必 | — | — |
| `guides` | `List<Guide>?` | ≤ 1000 条 | 选 | `null` ≡ 空数组 | **≥ 8**（非空时） |

> 注意 `width`/`height` 校验用的是 `1...DocumentLimits.maxSide`，而 `DocumentLimits.maxSide = 30_000`（`DocumentLimits.swift:18`）；**图层 transform 的 size 上限是 300_000**（见 §3.3），两者不同，别混。

### 3.2 图层记录 `layers[i]`（`ProjectStore.swift:33-56`）

| JSON 字段 | C# 类型 | 取值 / 范围 | 可选性 | 默认值 | 版本门槛 |
|---|---|---|---|---|---|
| `id` | `Guid` | 全 `layers` 内唯一 | 必 | — | — |
| `name` | `string` | 去空白后非空；UTF-8 字节数 ≤ 16384 | 必 | — | — |
| `isVisible` | `bool` | — | 必 | — | — |
| `transform` | `Transform` | 见 §3.3；必须 `IsValid` | 必 | — | — |
| `imageFile` | `string?` | 必须等于 `"<id 大写>.png"`；组与调整层必须为 `null` | 选 | `null`（空白图层） | — |
| `parentID` | `Guid?` | 必须指向存在且 `isGroup == true` 的层；不得成环；祖先 ≤ 64 | 选 | `null`（根） | **≥ 2** |
| `isGroup` | `bool?` | `true` 时 `imageFile` 必须为 `null` | 选 | `null`（≡ `false`） | **≥ 2** |
| `opacity` | `double?` | 有限，0–1；组在 v<8 时只能为 1 | 选 | `null` ≡ `1` | **≥ 3**（非 1 时） |
| `blendMode` | `string?` | 24 个值，见 §3.2.1；组必须是 `"Normal"` | 选 | `null` ≡ `"Normal"` | **≥ 3**（非 Normal 时） |
| `maskFile` | `string?` | 必须 `"<id 大写>.mask.png"` | 选 | `null` | **≥ 4**（图层）/ **≥ 6**（组） |
| `maskEnabled` | `bool?` | 存在则 `maskFile` 必须存在；缺省视为 `true` | 选 | `null` ≡ `true` | 随 `maskFile` |
| `maskSourceID` | `Guid?` | 源必须存在、非组、非调整层；无自指/环；链 < 256 | 选 | `null`（无剪贴蒙版） | **≥ 5** |
| `adjustment` | `Adjustment?` | 见 §3.4；要求 `imageFile == null` 且 `isGroup != true` | 选 | `null` | **≥ 7**（三种模糊/噪声 kind 需 ≥ 9） |
| `maskPlacement` | `Transform?` | 存在则要求 `maskFile != null` 且 `Transform.IsValid` | 选 | `null`（跟随图层） | —（增量） |
| `maskLinked` | `bool?` | `false` = 解除链接（用 `maskPlacement` 定位） | 选 | `null` ≡ `true` | —（增量） |
| `shape` | `ShapeStyle?` | 见 §3.6；**实现不校验**（§7-5） | 选 | `null` | —（增量） |
| `effects` | `Effects?` | 见 §3.7；**ProjectStore 不校验**（§7-5） | 选 | `null` | —（增量） |
| `text` | `TextStyle?` | 见 §3.5；要求 `imageFile != null`、`isGroup != true`、`adjustment == null` | 选 | `null` | —（增量；`colorRuns` ≥10、`fontRuns` ≥11） |

#### 3.2.1 `blendMode` 的 24 个合法值（`LayerAppearance.swift:4-13`）

`Normal`、`Darken`、`Multiply`、`Color Burn`、`Linear Burn`、`Lighten`、`Screen`、`Color Dodge`、`Linear Dodge (Add)`、`Overlay`、`Soft Light`、`Hard Light`、`Vivid Light`、`Linear Light`、`Pin Light`、`Hard Mix`、`Difference`、`Exclusion`、`Subtract`、`Divide`、`Hue`、`Saturation`、`Color`、`Luminosity`（共 24 个，`writing-comp-files.md:72` 逐一列出并与实现一致）。

> **顺带影响 `BlendShortcutTests`**：Shift +/- 的循环顺序就是该枚举的声明顺序（`LayerAppearance.swift:5-13` → `cycleBlendMode`，`:124-129`），Windows 侧必须保持同一顺序。

### 3.3 `transform`（`LayerTransform.swift:4-31`）

| JSON 字段 | C# 类型 | 取值 / 范围 | 可选性 | 默认值 |
|---|---|---|---|---|
| `origin` | `[double, double]` | 有限；\|x\| ≤ 1,000,000，\|y\| ≤ 1,000,000 | 必 | — |
| `size` | `[double, double]` | 有限；**1 ≤ w,h ≤ 300,000** | 必 | — |
| `rotation` | `double` | 有限；**顺时针**，单位度 | 选 | `0` |
| `flipX` | `bool` | — | 选 | `false` |
| `flipY` | `bool` | — | 选 | `false` |
| `sampling` | `string` | `"Nearest"` \| `"Smooth"` \| `"High quality"` | 选 | `"High quality"` |

> `rotation` 在实现里只对 360 取模后使用（`:26`），但**校验不限制范围**（只要有限）。裁剪/画布变换的语义见 `project-format.md:62`。

### 3.4 `adjustment`（`LayerAdjustment.swift:4-141`）

`kind` 的 12 个合法值（`LayerAdjustment.swift:4-9`）与门槛：

| `kind` | 版本门槛 | 专属字段（其余字段在任何 kind 下都可出现） |
|---|---|---|
| `Hue/Saturation`、`Levels`、`Curves`、`Exposure`、`Gradient Map`、`Grain`、`Invert`、`Black & White`、`Color Balance` | ≥ 7 | — |
| `Gaussian Blur` | **≥ 9** | `blurRadius` |
| `Motion Blur` | **≥ 9** | `motionAngle`、`motionDistance` |
| `Add Noise` | **≥ 9** | `noiseAmount`、`noiseGaussian`、`noiseMonochromatic`、`noiseSeed` |

`Adjustment` 对象字段：

| JSON 字段 | C# 类型 | 取值 / 范围 | 可选性 | 默认值 | 备注 |
|---|---|---|---|---|---|
| `kind` | `string` | 上表 12 值 | 必 | — | 拼错→整包拒绝 |
| `hue` | `double` | 有限，\|hue\| ≤ 360 | **必写**（非可选，保存时总会输出） | `0` | HSV 主档 |
| `saturation` | `double` | 有限，\|sat\| ≤ 100 | 必写 | `0` | |
| `lightness` | `double` | 有限，\|light\| ≤ 100 | 必写 | `0` | |
| `colorize` | `bool` | — | 必写 | `false` | |
| `hsvSettings` | `HsvSettings?` | 见下 | 选 | `null` → 由 `hue/saturation/lightness/colorize` 合成 Master 档 | |
| `levels` | `LevelsSettings` | 见下；**必写** | 必写（非可选） | 4 组恒等区间 | `writing-comp-files.md:102` 亦说明"每种 kind 都带恒等 levels 与 curves" |
| `curves` | `CurvesSettings` | 见下；**必写** | 必写（非可选） | 4 条对角曲线 | 同上 |
| `exposureSettings` | `ExposureSettings?` | 见下 | 选 | 恒等（`exposure 0, offset 0, gamma 1`） | |
| `gradientMapSettings` | `GradientMapSettings?` | 见下 | 选 | 黑→白、`reversed false` | |
| `grainSettings` | `GrainSettings?` | 见下 | 选 | 见下 | |
| `blackWhiteSettings` | `BlackWhiteSettings?` | 见下 | 选 | Photoshop 默认 | |
| `colorBalanceSettings` | `ColorBalanceSettings?` | 见下 | 选 | 全 0 + `preserveLuminosity true` | |
| `blurRadius` | `double?` | 有限，**0.1–250** | 选 | `null` ≡ `10` | |
| `motionAngle` | `double?` | 有限，**−90–90** | 选 | `null` ≡ `0` | |
| `motionDistance` | `double?` | 有限，**1–2000** | 选 | `null` ≡ `10` | |
| `noiseAmount` | `double?` | 有限，**0.1–400** | 选 | `null` ≡ `10` | |
| `noiseGaussian` | `bool?` | — | 选 | `null` ≡ `false` | |
| `noiseMonochromatic` | `bool?` | — | 选 | `null` ≡ `false` | |
| `noiseSeed` | `uint?` | UInt32 | 选 | `null` ≡ `0` | 固定噪声图案，保证跨会话稳定 |

子对象：

| 对象 | JSON 字段 | 类型 | 范围 | 默认值 |
|---|---|---|---|---|
| `LevelsSettings`（`Levels.swift:4-44`） | `channel` | `string` | `"RGB"` \| `"Red"` \| `"Green"` \| `"Blue"` | `"RGB"` |
| | `ranges` | `LevelRange[4]` | **必须正好 4 个**，顺序 RGB, R, G, B；且每项必须**已归一化**（`$0 == $0.normalized`，`LayerAdjustment.swift:135`） | 4 组恒等 |
| `LevelRange`（`Levels.swift:8-31`） | `black` | `double` | 有限，0–254 | `0` |
| | `gamma` | `double` | 有限，0.1–9.99 | `1` |
| | `white` | `double` | 有限，`black+1` – 255 | `255` |
| | `outputBlack` | `double` | 有限，0–255 | `0` |
| | `outputWhite` | `double` | 有限，0–255 | `255` |
| `CurvesSettings`（`Curves.swift:3-16`） | `channel` | `string` | `"RGB"` \| `"Red"` \| `"Green"` \| `"Blue"` | `"RGB"` |
| | `channels` | `CurvePoint[4][]` | **必须 4 条曲线**，每条 **2–32 点**；首点 `x == 0`、末点 `x == 255`；`x,y` ∈ 0–255 有限；**x 严格递增** | 4 条 `[(0,0),(255,255)]` |
| `CurvePoint` | `x`,`y` | `double` | 0–255 有限 | — |
| `HueSaturationSettings`（`HueSaturation.swift:5-213`） | `range` | `string` | `"Master"`,`"Reds"`,`"Yellows"`,`"Greens"`,`"Cyans"`,`"Blues"`,`"Magentas"` | `"Master"` |
| | `colorize` | `bool` | — | `false` |
| | `invertRange` | `bool` | — | `false` |
| | `adjustments` | `map<ColorRange, {hue,saturation,lightness}>` | 每值：\|hue\| ≤ 360、\|sat\| ≤ 100、\|light\| ≤ 100，均有限 | 空 |
| | `bands` | `map<ColorRange, {falloffStart,rangeStart,rangeEnd,falloffEnd}>` | **只校验有限性**（`LayerAdjustment.swift:134`），**不校验范围**（见 §7-4） | 各 range 的 Photoshop 默认带 |
| `ExposureSettings`（`ImageAdjustments.swift:37-47`） | `exposure` | `double` | −20–20 | `0` |
| | `offset` | `double` | −0.5–0.5 | `0` |
| | `gamma` | `double` | 0.01–9.99 | `1` |
| `GradientMapSettings`（`:75-79`） | `shadows`,`highlights` | `{red,green,blue}` | 每通道有限 0–1 | 黑 / 白 |
| | `reversed` | `bool` | — | `false` |
| `GrainSettings`（`:180-191`） | `amount` | `double` | 0–100 | `25` |
| | `size` | `double` | 0.5–20 | `1.5` |
| | `roughness` | `double` | 0–100 | `50` |
| | `seed` | `uint` | UInt32 | `0` |
| `BlackWhiteSettings`（`:114-131`） | `reds`,`yellows`,`greens`,`cyans`,`blues`,`magentas` | `double` | −200–300 | 40/60/40/60/20/80 |
| | `tint` | `bool` | — | `false` |
| | `tintHue` | `double` | 0–360 | `40` |
| | `tintSaturation` | `double` | 0–100 | `20` |
| `ColorBalanceSettings`（`:146-163`） | `shadowCyanRed`,`shadowMagentaGreen`,`shadowYellowBlue`,`midCyanRed`,`midMagentaGreen`,`midYellowBlue`,`highlightCyanRed`,`highlightMagentaGreen`,`highlightYellowBlue` | `double` | 各 −100–100 | `0` |
| | `preserveLuminosity` | `bool` | — | `true` |

> ⚠ **`hsvSettings.adjustments` / `bands` 的 JSON 形态需要实测确认**（Swift 对非 String 键字典的编码形态取决于是 `CodingKeyRepresentable`，可能是对象也可能是"键值交替数组"）。见 §8.1 —— 这是 Windows 侧最容易写错的一处。

### 3.5 `text`（`TypeTool.swift:3-62`、`:190-202`）

| JSON 字段 | C# 类型 | 取值 / 范围 | 可选性 | 默认值 | 版本门槛 |
|---|---|---|---|---|---|
| `content` | `string` | UTF-16 长度 ≤ 100,000 | 必写（非可选） | `"Text"` | — |
| `fontName` | `string` | PostScript 名 | 必写 | `"Helvetica"` | — |
| `fontSize` | `double` | 有限，**1–2000**（像素） | 必写 | `72` | — |
| `red`,`green`,`blue` | `double` | 有限，0–1 | 必写 | `0` | — |
| `alignment` | `string` | `"Left"` \| `"Center"` \| `"Right"` | 必写 | `"Left"` | — |
| `tracking` | `double` | 有限，**−100–1000** | 必写 | `0` | — |
| `leading` | `double` | 有限，**0–5000**；`0` = Auto = `fontSize × 1.2` | 必写 | `0` | — |
| `boxSize` | `[double, double]?` | 每边 **16–30000**，且面积 ≤ 200,000,000 | 选 | `null`（点文本） | — |
| `colorRuns` | `ColorRun[]?` | 见下 | 选 | `null` | **≥ 10** |
| `fontRuns` | `FontRun[]?` | 见下 | 选 | `null` | **≥ 11** |

Run 规则（`TypeTool.swift:43-62`，两种 run 相同，除颜色/字体载荷）：
- 数组非空（`colorRuns: []` 会被判非法）；
- 按 `location` 排序且**不重叠**：`run.location >= 前一个的 location + length`；
- `length > 0`；`location + length ≤ content` 的 UTF-16 长度；

| Run 载荷 | 类型 | 范围 |
|---|---|---|
| `ColorRun`: `location`,`length` | `int` | 见上规则 |
| `ColorRun`: `red`,`green`,`blue` | `double` | 有限，0–1 |
| `FontRun`: `fontName` | `string` | 非空、长度 ≤ 200、**不含换行** |

### 3.6 `shape`（`ShapeTool.swift:3-32`）

| JSON 字段 | C# 类型 | 取值 / 范围 | 可选性 | 默认值 |
|---|---|---|---|---|
| `kind` | `string` | `"Rectangle"` \| `"Ellipse"` \| `"Line"` | 必写 | — |
| `red`,`green`,`blue` | `double` | 0–1（**实现无校验**） | 必写 | — |
| `cornerRadius` | `double` | 文档像素；渲染时被夹到 `min(radius, w/2, h/2)`（`ShapeTool.swift:12`） | 必写 | — |
| `lineWidth` | `double?` | 仅线型；**实现无校验** | 选 | `null` |
| `start`,`end` | `[double,double]?` | 图层框的比例 0–1（`project-format.md:44`） | 选 | `null` |

### 3.7 `effects`（`LayerEffects.swift:6-139`）

六个子对象都可选；每个子对象内 `enabled` 可省略（缺省 = 可见）。**ProjectStore 不校验本对象**（§7-5）。

| 子对象 | 字段 | 类型 | 范围 | 默认值 |
|---|---|---|---|---|
| `stroke` | `enabled` | `bool?` | — | `null` ≡ `true` |
| | `size` | `double` | 0–500（层像素） | `4` |
| | `red`,`green`,`blue` | `double` | 0–1 | `0` |
| | `opacity` | `double` | 0–1 | `1` |
| | `inside` | `bool` | 描边在边缘内侧 | `false` |
| `shadow` | `enabled` | `bool?` | — | `null` ≡ `true` |
| | `angle` | `double` | **−360–360**（度，逆时针，90 = 正上方） | `90` |
| | `distance` | `double` | **0–5000** | `20` |
| | `blur` | `double` | **0–500** | `20` |
| | `red`,`green`,`blue` | `double` | 0–1 | `0` |
| | `opacity` | `double` | 0–1 | `0.5` |
| `colorOverlay` | `enabled` | `bool?` | — | `null` ≡ `true` |
| | `red`,`green`,`blue` | `double` | 0–1 | `0` |
| | `opacity` | `double` | 0–1 | `1` |
| `innerShadow` | 同 `shadow` | | `angle` −360–360、`distance` 0–5000、`blur` 0–500 | `angle 90`、`distance 10`、`blur 10`、`opacity 0.5` |
| `outerGlow` | `enabled`、`size`、`red/green/blue`、`opacity` | — | `size` **0–500**；颜色 0–1；`opacity` 0–1 | `size 20`、颜色 `1,1,1`、`opacity 0.75` |
| `innerGlow` | 同 `outerGlow` | — | `size` 0–500 | `size 10`、颜色 `1,1,1`、`opacity 0.75` |

> 文档（`project-format.md:52`）只说"stroke size 0–500、glow size 0–500"，**没有给 shadow 的 angle/distance/blur 范围**；上表取自实现 `LayerEffects.swift:44-49`、`:83-88`。

### 3.8 `guides[i]`（`Guides.swift:5-11`）

| JSON 字段 | C# 类型 | 取值 / 范围 | 可选性 | 默认值 |
|---|---|---|---|---|
| `id` | `Guid` | 数组内唯一 | 必写 | — |
| `axis` | `string` | `"horizontal"` \| `"vertical"`（**小写**） | 必写 | — |
| `position` | `double` | 有限，\|position\| ≤ 1,000,000（文档像素；横向为 Y，纵向为 X） | 必写 | — |

---

## 4. 校验与安全规则清单（→ Windows 校验器逐条落地）

**A. 版本与格式（4 条）**

| # | 规则 | 依据 | Windows 校验器 |
|---|---|---|---|
| A1 | `manifest.json` 存在、是普通文件、≤ 4 MiB | `ProjectStore.swift:148-149`、`:271-277` | 打开包时先做文件属性检查（大小 + 非符号链接） |
| A2 | 顶层 `format == "com.compositor.project"` | `:155`、`:199` | 拒绝 |
| A3 | `version` ∈ `supported = 1...11`，否则 `ProjectError.version(n)` | `:18`、`:156`、`:200` | 拒绝并回报版本号（错误文案见 `:69`） |
| A4 | `colorSpace == "sRGB"` | `:201` | 拒绝（**不支持其他色彩空间**） |

**B. 结构（6 条）**

| # | 规则 | 依据 |
|---|---|---|
| B1 | JSON 必须可解析（否则 `invalid`）；`Header` 解码失败也 `invalid` | `:152-158` |
| B2 | 1 ≤ `width`,`height` ≤ 30000 | `:205`、`DocumentLimits.swift:18` |
| B3 | `layers.Count ≤ 10000`（超出 → `tooLarge`） | `:206` |
| B4 | 图层 `id` 全局唯一 | `:240`、`LayerGroups.swift:33` |
| B5 | `activeLayerID`（若存在）必须在 `layers` 中 | `:245` |
| B6 | `resolution`（若存在）有限且 1–9600 | `:202-204` |

**C. 图层语义（9 条）**

| # | 规则 | 依据 |
|---|---|---|
| C1 | `name` 去空白后非空；UTF-8 字节 ≤ 16384 | `:241-242` |
| C2 | `transform.IsValid`：有限 + size 1–300000 + \|origin\| ≤ 1e6 | `LayerTransform.swift:27-31` |
| C3 | `imageFile` 必须等于 `"<id 大写>.png"` | `:243` |
| C4 | 层级：父必须存在且为组、无环、祖先 ≤ 64、组不得有 `imageFile` | `LayerGroups.swift:30-46` |
| C5 | `opacity`：有限、0–1；`version < 3` 时必须为 1 且 `blendMode == Normal` | `:226-232` |
| C6 | 组：`blendMode` 必须 `Normal`；`version < 8` 时 `opacity` 必须为 1 | `:232` |
| C7 | `maskFile` 必须等于 `"<id 大写>.mask.png"`，且版本 ≥ 4（组 ≥ 6） | `:222-223` |
| C8 | `maskEnabled` 存在 ⇒ `maskFile` 存在；`maskPlacement` 存在 ⇒ `maskFile` 存在且 `IsValid` | `:224-225` |
| C9 | `maskSourceID`：源存在、非组、非调整层、无自指/环、链 < 256；`version ≥ 5` | `LiveLayerMask.swift:4-19`、`ProjectStore.swift:236` |

**D. 特性门槛（7 条）**

| # | 规则 | 依据 |
|---|---|---|
| D1 | `adjustment` 需 `version ≥ 7`；`imageFile == null`；`isGroup != true`；`IsValid` | `:215-216` |
| D2 | 三种模糊/噪声 `kind` 需 `version ≥ 9` | `:217-219` |
| D3 | `text` 需 `imageFile != null`、`isGroup != true`、`adjustment == null`、`IsValid` | `:208-213` |
| D4 | `text.colorRuns` 需 `version ≥ 10`；`text.fontRuns` 需 `version ≥ 11` | `:211-212` |
| D5 | `version == 1` 不得有 `parentID` 或 `isGroup` | `:237` |
| D6 | `version < 8` 时 `guides` 必须为空；非空时 ≤ 1000 条、`id` 唯一、`position` 有限且 ≤ 1e6 | `:249-262` |
| D7 | 调整层不得是组、不得有图像；组不得有图像、不得是调整层（组与调整层的身份互斥） | `:213`、`:216`、`LayerGroups.swift:34` |

**E. 资源与安全（2 + 4 条，最关键）**

| # | 规则 | 依据 | Windows 实现要点 |
|---|---|---|---|
| E1 | 每个被引用的资源文件必须**存在**，否则 `missingImage` 且**不替换当前文档** | `:167`、`:70` | 加载失败不得破坏已打开文档 |
| E2 | 每个资源 ≤ 512 MiB；`manifest.json` ≤ 4 MiB | `:167`、`:111`、`:149` | 用文件属性而非读入后再判断，避免 OOM |
| E3 | **路径安全**：资源路径必须解析符号链接+标准化后仍位于包内（前缀比较） | `:271-273` | C#：`Path.GetFullPath` + `ResolveLinkTarget`，再判断 `StartsWith(packageRoot + separator)`；`ProjectTests.swift:159-165` 用 `"../../outside.png"` 钉住了该行为 |
| E4 | 资源必须是**普通文件**、**不是符号链接** | `:274-276` | `FileAttributes.ReparsePoint` 必须排除 |
| E5 | 每边 1–30000，且**单资源像素** ≤ 文档像素预算（累加判断） | `:264-269` | 见 §7-1：预算不是固定 1 亿 |
| E6 | 图像/蒙版的像素预算**分别独立累加**（`pixels` 与 `maskPixels` 两个计数器） | `:87`、`:94-95`、`:162`、`:180-181` | 校验器要有两个计数器，不是共用一个 |

**F. 保存侧规则（3 条）**

| # | 规则 | 依据 |
|---|---|---|
| F1 | 保存前先 `validate`；校验失败 → 抛错且**不替换现有文件** | `:85`、`ProjectTests.swift:114-140` |
| F2 | 原子替换：先写完整包到同级临时目录，再原子替换（macOS 走 `NSFileCoordinator(.forReplacing)` + `FileWrapper.write(.atomic)`） | `:122-131` |
| F3 | 保存时对每个资源重新编码为 PNG；蒙版必须通过 `LayerMask.isValid`（8bpc 灰度无 alpha） | `:92-104` |

**G. 超出 ProjectStore 的校验（渲染期才生效，见 §7-5）**：`effects`、`shape`、`maskLinked`、`hue` 色带范围。Windows 校验器可以选择"更严"（推荐：把 `LayerEffects.IsValid` 与色带范围也纳入拒绝），但**必须知道 macOS 不会拒绝它们**，否则会出现"Windows 拒绝、macOS 接受"的文件。

---

## 5. 最小合法 `.comp` fixture

### 5.1 现成 fixture 代码：有，但没有"最小 .comp 清单"

`CompositorTests` 里存在**三类**可引用代码（都已实际读过）：

| 类别 | 文件:行 | 可复用什么 |
|---|---|---|
| 内存构造 `ProjectSnapshot` | `ExportTests.swift:16-21`、`JPEGExportTests.swift:9-10`、`InnerGlowTests.swift:229-232`、`OuterGlowTests.swift:281-284`、`ExternalChangeTests.swift:36-44` | DTO 的构造顺序与必填项 |
| 用 `ProjectStore.save` 落盘 | `GroupTests.swift:91-93`（旧版 v1 manifest）、`LayerMaskTests.swift:141`、`ProjectTests.swift:45` | "先生成快照再存包"的写法 |
| **手写 `manifest.json`** | `GuideTests.swift:71`、`TypeToolTests.swift:430`/`:442`、`ExternalChangeTests.swift:64`/`:114`/`:134`/`:146`、`ProjectTests.swift:151-166` | 直接改 manifest 的字节（旧版本兼容、非法输入） |
| PSD 字节 fixture | `PSDFixture.swift:6-9`、`PSDVectorFixtures.swift:3-17` | 与 `.comp` 无关，供 PSD reader 用 |

**结论**：没有任何测试给出"最小合法 `.comp` 的目录 + manifest 全文"。下面的清单以 `writing-comp-files.md:28-58` 的官方样例为骨架，并用上面 §4 的规则逐条核对——**样例本身来自文档（权威），核对结论来自实现**。标注为"推断"的部分只有 §5.4 的字节级编码顺序。

### 5.2 目录结构（最小合法）

```
Minimal.comp/
├── manifest.json
└── images/
    └── 6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F.png     ← 1920×1080，8-bit，单帧 PNG
```

### 5.3 `manifest.json` 全文（最小合法 v11）

```json
{
  "format": "com.compositor.project",
  "version": 11,
  "colorSpace": "sRGB",
  "documentID": "0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D",
  "width": 1920,
  "height": 1080,
  "resolution": 72,
  "activeLayerID": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F",
  "layers": [
    {
      "id": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F",
      "name": "Background",
      "imageFile": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F.png",
      "isVisible": true,
      "transform": {
        "origin": [0, 0],
        "size": [1920, 1080],
        "rotation": 0,
        "flipX": false,
        "flipY": false,
        "sampling": "High quality"
      }
    }
  ]
}
```

（与 `writing-comp-files.md:28-58` 的样例等价；`isGroup`/`opacity`/`blendMode` 在该样例里显式写出，因为缺省值与它们相同，**省略也合法**——`ProjectStore.swift:40-42` 的默认值就是 `nil → false/1/Normal`。）

### 5.4 该 fixture 满足/不涉及的规则（逐条核对）

| 规则 | 核对结果 |
|---|---|
| A1–A4 | `manifest.json` 远小于 4 MiB；`format`/`version`/`colorSpace` 正确 |
| B1–B6 | 单一图层；`activeLayerID` 命中；`resolution = 72` 在 1–9600 |
| C1–C3 | `name` 非空；`transform.size = (1920,1080)` ∈ 1–300000；`origin` 有限且 ≤ 1e6；`imageFile == "<id>.png"` |
| C4 | 无 `parentID` / `isGroup` → 按根节点处理 |
| C5–C6 | `opacity`/`blendMode` 省略 → 1 / Normal，满足 v≥3 与组规则（此层不是组） |
| C7–C9 | 无蒙版、无剪贴蒙版 |
| D1–D7 | 无 `adjustment`/`text`；`version == 11` 不受 v1 限制；无 `guides` |
| E1–E6 | 只有一个 1920×1080 = 2,073,600 像素的资源，远低于任何预算；文件名不含路径分隔符 |
| F1–F3 | 保存路径见下 |

**写入顺序（必须照做，否则打开中的 macOS 客户端会读到半个包）**（`writing-comp-files.md:75-84`）：
1. 先把 PNG 写进 `images/`；
2. 再把 manifest 写成包内临时文件（如 `.manifest.json.tmp`），然后**原子改名**为 `manifest.json`；
3. 若存在 `QuickLook/` 目录，删除它（否则 Finder 预览过期）；
4. 不再被引用的图片在 manifest 更新后再删除。

**推断项（未逐字节验证）**：manifest 的字节级形态（缩进 2 空格、键按字母序、结尾是否带换行）。见 §8.2——这一点影响 macOS 的"外部变更"判定。

---

## 6. `.comp` 作为跨平台唯一契约的可行性

### 6.1 必须保证的 8 件事

| # | 要求 | 为什么（依据） |
|---|---|---|
| 1 | **版本集合完全一致**：Windows 读 1–11、写 11 | macOS `supported = 1...current`、`current = 11`（`ProjectStore.swift:15-18`），且**每次保存都写 11**；不一致会造成"单向不可读" |
| 2 | 不认识但存在的字段要**原样保留** | 否则 Windows 打开再保存会把 v10/v11 的 `colorRuns`/`fontRuns`、`effects`、`shape` 静默丢掉（`ProjectStore.swift:52-55` 这些字段是一次保存不带就没了） |
| 3 | 文件名与大小写原样保留 | `imageFile` 必须等于 `<UUID 大写>.png`（`:243`）；Windows 文件系统不区分大小写，**不能**做规范化/小写化 |
| 4 | manifest 改写会触发 macOS 的"外部变更"提示 | `ProjectDigest` = manifest **字节** + 每个资源名 + 大小（`ProjectDigest.swift:17-30`）；只要 manifest 字节变了就算变化（这与"同尺寸资源改写"的例外见 `writing-comp-files.md:94`） |
| 5 | PNG 必须是 CGImageSource 能读的形态 | 8-bit、单帧（`:173-179`）；蒙版 8bpc 灰度无 alpha（`LayerMask.swift:23-26`） |
| 6 | 原子写序 | §5.4 的四步；macOS 侧用 `NSFileCoordinator` + 原子包替换（`:125-131`），Windows 侧不得边写边让读者看到半包 |
| 7 | 上限一致 | 30000/边、≤10000 层、manifest ≤4 MiB、资源 ≤512 MiB（`:205-206`、`:111`、`:167`）；**像素预算见 §7-1，不能照抄文档里的 1 亿** |
| 8 | 色彩空间固定 sRGB | 非 `"sRGB"` 直接拒绝（`:201`），没有色彩管理元数据可协商 |

### 6.2 已知的"能互通但会降级"的情形

- **未知键**：Swift 合成解码忽略未知键 → macOS **不会**因为有额外字段而拒绝；但下次 macOS 保存时这些键会消失（推断，§8.3）。
- **`QuickLook/`**：macOS 会写入，加载时忽略（`:116-117`）。Windows 可以不写；但若保留了过期预览，Finder 会显示旧图（`writing-comp-files.md:86`）。
- **`resolution` 缺省**：缺失时 macOS 用 72，Windows 必须同样处理，否则导出元数据不一致（`EditorSession+Projects.swift:29`、`ImageExporter.swift:74`）。
- **`guides` 的坐标随画布变化**：Canvas Size / Crop 会平移 guides（`project-format.md:31`），Windows 侧必须实现同样的偏移，否则两平台的参考线位置会漂移。

### 6.3 结论

**可行**：`.comp` 是自描述的目录包 + JSON manifest，除 PNG 编码与原子写序外没有平台私有内容，具备做"唯一契约"的条件。**前提是** §6.1 的 8 条全部落到 Windows 的读写实现与校验器，并且**版本与校验规则以实现（`ProjectStore.swift`）为准而非以文档为准**——因为文档在像素预算、混合模式集合、effects/shape 校验三处与实现不一致（§7）。

---

## 7. 文档与实现不一致清单（高价值发现）

> 下表每条都已实际读源码确认。**冲突时以实现为准**（与任务约定一致）。

| # | 文档说法 | 实现事实 | 影响 |
|---|---|---|---|
| **1** | `project-format.md:9`："**100 million** total source pixels"；`:21`："Up to **100 million** mask pixels … in addition to the existing **100 million** image pixels" | `DocumentLimits.swift:36-37`：`documentPixelBudget = min(800_000_000, max(maxSurfacePixels, physicalMemory / 16))`，`maxSurfacePixels = 200_000_000`（`:25`）→ 预算**随内存缩放**，区间 **[2 亿, 8 亿]** 像素；且图像与蒙版**各用一个独立计数器**（`ProjectStore.swift:87`、`:94-95`、`:180-181`） | Windows 校验器若硬编码 1 亿，会**比 macOS 严格 2–8 倍**，导致大量合法文件被拒。必须把预算做成配置/策略并与宿主内存挂钩（或至少取 2 亿下限） |
| **2** | `project-format.md:17`：v3 的 `blendMode` 只有 **9** 个值（Normal, Multiply, Screen, Overlay, Darken, Lighten, Difference, Color Dodge, Color Burn） | `LayerAppearance.swift:4-13` 是 **24** 个 case，且 `ProjectStore.validate` **不按版本限制 blendMode 的取值集合**（只限制"v<3 必须为 Normal"） | v≥3 的文件里可以出现 `Linear Burn`/`Divide`/`Hue` 等 15 个"文档未列"的值。Windows 的枚举与校验器必须收 24 个值，否则会拒绝合法文件。文档的 9 个值是"v3 当时实现过的子集"，不是格式约束 |
| **3** | `project-format.md:52`（Layer effects）："A record omitting an effect means that layer does not have it" 与"Out-of-range or non-finite values are rejected"（`:29` 针对 adjustment）给读者的整体印象是格式校验覆盖全部数值 | **`effects` 完全没有进 `ProjectStore.validate`**（`:198-247` 全函数无 `effects` 引用）；越界值只有在渲染/预览路径被 `effects.isValid` 拦下并被当作"无效果"忽略（`EditorCanvas.swift:1216`、`EffectsPreviewCache.swift:83`、`LayerEffects.swift:418`） | "Windows 拒绝、macOS 接受"的差异源。建议 Windows 侧也**不**因 effects 越界而拒绝整包，改为忽略（与 macOS 行为一致），否则互操作性下降 |
| **4** | `project-format.md:29` 说 adjustment "Out-of-range or non-finite values are rejected"，读者会以为每个子字段都有范围校验 | `LayerAdjustment.isValid` 对 `hsvSettings.bands`（色带四个端点）**只检查 `isFinite`，不检查范围**（`LayerAdjustment.swift:128-141`，尤其 `:134`） | 色带可以写到 0–360 之外而仍被接受。Windows 校验器若要"更严"需明确这是**有意加严**（会拒绝 macOS 接受的文件） |
| **5** | `project-format.md:44` 描述 `shape`（kind / rgb / cornerRadius / lineWidth / start / end）像是一等格式字段 | `LayerShapeStyle` **没有 `isValid`**（`ShapeTool.swift:19-32`），`ProjectStore` 也不检查 `shape`；安装时只判非空（`EditorSession+Projects.swift:84` → `LayerShape.loaded`） | 同 #3：不要为 `shape` 写"拒绝式"校验 |
| **6** | `writing-comp-files.md:71`："Layer images are RGBA" | 加载侧**不校验通道**：只校验 PNG/单帧/位深 ≤8（`ProjectStore.swift:173-179`）；RGBA 是**保存侧**的产物 | Windows 写文件时仍应写 RGBA8（保证渲染路径一致），但校验器不要拒绝灰度/带 alpha 的图层 PNG——macOS 不会拒 |
| **7** | 两处文档都没提 `activeLayerID` 的必填性；`project-format.md:11` 只说"starts with clean history" | `activeLayerID` 是**可选**（`ProjectStore.swift:27`，`UUID?`），但一旦出现必须在 `layers` 中（`:245`，否则整包拒绝） | Windows DTO 必须允许 `null`，同时实现"存在即必须命中"的引用完整性检查 |

**另有两处"实现内部不一致"值得记录**（不算文档问题，但影响 Windows 侧的上限策略）：

- `UI/ImageSizeSheet.swift:80` 用 `100_000_000`（1 亿像素）作为 **Image Size 对话框**的面积上限，而 store 的预算是 2–8 亿（`DocumentLimits.swift:36`）——即"对话框比格式更严"，而 `CanvasResizer` 走的是 `documentPixelBudget`（`CanvasResizer.swift:38`）。
- `TypeTool.swift:36` 与 `PSDText.swift:54` 各自硬编码 `content.utf16.count <= 100_000`，与 `InlineTextEditor.swift:247` 的编辑器上限一致；该数字**不在** `DocumentLimits` 里，Windows 侧需单独常量。

---

## 8. 不确定项

1. **`hsvSettings.adjustments` / `bands` 的 JSON 形态（最高优先级）**
   `HueSaturationSettings.adjustments: [ColorRange: RangeAdjustment]`（`HueSaturation.swift:176-177`）。Swift 对"非 String/Int 键的字典"历史上有两种编码形态（键值交替的数组 / JSON 对象），取决于 `CodingKeyRepresentable`（SE-0320）。`ColorRange` 是 `String` 原始值枚举，**我无法在不运行 Swift 的情况下确证**它走哪一条。
   → 落地第一步：用 macOS 版保存一个带 Hue/Saturation 调整层的工程，直接看 `manifest.json` 里 `hsvSettings` 的形态，据此写 C# 转换器。**若按对象处理而实际是数组，v7+ 的调整层会全部解析失败。**

2. **manifest 的字节级形态**：`.prettyPrinted + .sortedKeys`（`ProjectStore.swift:109`）已知，但缩进宽度、末尾换行、以及 `Double` 的最短表示形式（如 `1` vs `1.0`、`1e-05` vs `0.00001`）未逐字节验证。这直接影响 §6.1-4 的"外部变更"判定频率。
   → 落地时用一份 macOS 产出的 manifest 做黄金样本，逐字节比对 Windows 写出结果。**要求：字节相同**（否则每次 Windows 保存都会让 macOS 提示"文件已被外部修改"，虽然只是一次性提示）。

3. **未知键被忽略**：这是 Swift 合成 `Decodable` 的通用行为，但我没有在 `ProjectStore` 或测试里找到显式验证该行为的用例。→ 落地时用"多一个未知键的 manifest"实测一次。

4. **`ProjectStore.save` 是否总会写出全部非可选字段**：`hue/saturation/lightness/colorize/levels/curves` 是非可选属性（`LayerAdjustment.swift:47-57`），按 Swift 合成编码**一定写出**。这与 `writing-comp-files.md:102`（"Every kind carries identity levels and curves blocks"）一致，可以放心据此写 DTO；但 `text` 的 `content/fontName/fontSize/red/green/blue/alignment/tracking/leading` 同理都是非可选（`TypeTool.swift:8-17`），所以**文本层保存时这些字段一定出现**，Windows DTO 应把它们标为必写。

5. **跨平台像素相等性**：本文只定义格式契约。位图内容（PNG 像素）是否要求 Windows 与 macOS 渲染结果一致，属于 `03-test-harness.md` §5.2 的容差策略范围；`.comp` 本身**不要求**这一点（它存的是源像素 + 变换）。

6. **`guides` 与画布操作的偏移语义**：实现里有 `CanvasGuide.offset/scaled/mirrored`（`Guides.swift:12-31`），文档只说"随画布偏移"（`project-format.md:31`）。Canvas Size 的 9 个锚点、Crop、Trim、Image Size 各自如何调用这些函数，本文未逐条核对。
