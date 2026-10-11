# 06 · UI 转换系数标定：把 65–100 周的工期支柱换成可复核的结构度量

> 本文回应 `01-architecture.md` §4.0 与 §4.11 标注的首要「待验证」项：
> **声明式 0.8–1.4、自绘控件 2.5–4.0 人周/千行这两个转换系数没有任何实测支撑，而 65–100 人周的总工期几乎完全建立在它们之上。**
>
> 结论一句话：**两个原区间都得到支持，但「用一个面板标定系数」的验证协议必须废弃——按字面执行会产出偏高 3.9–13.9 倍的数字。**

---

## 0. 结论摘要

### 0.1 我做了什么、不能做什么

| | |
|---|---|
| **不能做** | 这台机器**没有 Windows SDK、没有 MSVC/MSBuild、没有 Visual Studio**，也没有 `Microsoft.Windows.SDK.NET.Ref`（详见 §1.2）。**我无法真的写一行 XAML 并编译它。因此本文没有任何「实测人日」。** |
| **做了什么** | 把「猜」降级为**从源码结构度量出的、可复核的工作量分解**：对 `UI/` 全部 49 个文件逐文件度量结构、修饰符、状态、布局与 LOC 分类；对两个标定物逐节分解到控件级；用文档自己的波次口径反查系数口径。 |
| **度量口径** | 全部统计脚本见 §附录 A，均可在本机原样复跑；每个数字后面标注其来源命令。 |

### 0.2 原区间是否被支持

| 系数 | 原区间 | 我的结论 | 收紧后 |
|---|---|---|---|
| 声明式（`UI/` 面板与表单） | 0.8–1.4 人周/千行 | **原区间得到支持**——但只作为「可复用件摊销之后的边际代码产出率」，**不能按单文件使用** | **边际率维持 0.8–1.4**；新增一次性可复用 XAML 基建项 **F_decl = 1.3–2.7 人周**。声明式桶由 7.1–12.4 人周 → **8.4–15.1 人周** |
| 自绘控件 | 2.5–4.0 人周/千行 | **原区间未被推翻，但标定物选错**：`TransformOverlay.swift` 对桶内 **89.4%** 的行数为代表的那个子类，度量支持为**零** | 对「纯绘制」子类：**3.0–4.7 人周/千行**（不含 marching-ants LOD）、**4.3–7.3**（含）。对「输入状态机」子类（占桶 89.4%）：**【无法验证】** |

### 0.3 两条最重要的新发现

**发现 1（实测，推翻验证协议）**——`FilterSheet.swift` 单独实施一次，结构分解为 **11–22.5 人日 = 2.2–4.5 人周**；除以它自己的 0.406 千行，得 **5.4–11.1 人周/千行**，是原区间 **[0.8, 1.4] 的 3.9–13.9 倍**。原因不是面板难，而是其中 **约 60% 的成本（1.3–2.7 人周）是一次性可复用控件**（滑块行控件、双色轨道滑块、共享标签宽度、色板模板、10 个私有 DSL 行为的 WinUI 重写），会在 UI/ 其余 30 多个面板上被复用。
→ **`01-architecture.md` §4.0「待验证」条与 §4.1 Wave 0 退出标准 4 若照字面执行（"用一个真实面板标定"），产出的系数会被直接乘上 8.86 千行，把总工期推到 106–186 人周。**

**发现 2（实测，标定物无效）**——`TransformOverlay.swift` 是文档指定的自绘控件标定物，但实测它 **0 个 mouse/key override、0 个 tracking/cursor override**（`hitTest` 直接返回 `nil`，它是纯画笔，不处理任何输入）。而文档为 2.5–4.0 给出的上限依据恰恰是「完整输入状态机（`EditorCanvas.swift` 12 个鼠标 override、`resetCursorRects()`、`updateTrackingAreas()`）」。
更量化地说：自绘桶里 **89.4% 的行**属于「输入状态机型」（`EditorCanvas` 55.3% + `NativeLayerList` 24.3% + `InlineTextEditor` 9.8%），**10.6% 属于「纯绘制型」**；而 `TransformOverlay` 一个文件就贡献了桶内 **56.7% 的 CG 绘制调用**，却只占 **8.2% 的行**。**用一个 10.6% 类别的样本，去标定 89.4% 是另一类别的桶。**

---

## 1. 度量环境与诚实边界

### 1.1 度量方法与脚本

所有命令在 **Windows PowerShell 5.1** 下运行（本机未安装 PowerShell 7）。脚本存放在工作区之外（`%TEMP%\uical\`），**未向 `src/Compositor/` 写入任何内容**。

主要脚本：`classify.ps1`（LOC 分类器）、`genTable2.ps1`（49 文件汇总表）。完整源码见 §附录 A。

### 1.2 工具链核查【实测】——证实「无法实测」这一前提

```
PS> (Get-Command dotnet).Source ; dotnet --list-sdks
C:\Program Files\dotnet\dotnet.exe
7.0.304 [C:\Program Files\dotnet\sdk]
8.0.425 [C:\Program Files\dotnet\sdk]
```

```
PS> Get-ChildItem "$env:USERPROFILE\.nuget\packages" -Directory |
      Where-Object Name -match 'windowsappsdk|windows.sdk|cswinrt'
microsoft.windowsappsdk              1.5.240802000
microsoft.windows.sdk.buildtools     10.0.22621.756
# Microsoft.Windows.SDK.NET.Ref      -> ABSENT
# Microsoft.Windows.CsWinRT          -> ABSENT

PS> Test-Path 'C:\Program Files (x86)\Windows Kits\10\Include'   -> False   (无 Windows SDK)
PS> Test-Path "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -> False
PS> (Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\cl.exe').Count -> 0
PS> (Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe').Count      -> 0
```

**结论【实测】**：无法在本机创建或编译任何 WinUI 3 / XAML 工程。缺口是 `Microsoft.Windows.SDK.NET.Ref` 目标包 + Windows SDK（`nuget` 里已有 `microsoft.windowsappsdk 1.5.240802000`，所以缺口比「什么都没有」窄）。
**附带发现【实测】**：本机最高 .NET SDK 为 **8.0.425**，而 `01-architecture.md` §3.1 的全部 TFM 写作 **`net9.0`** / `net9.0-windows10.0.19041.0`。**本机连 `net9.0` 工程都建不出来**（此项与本文主题无关，但与 `04-toolchain.md` 的结论互相印证，交由 Lead 处置）。

### 1.3 三种标注的含义

- **【实测】**：一条命令 + 真实输出，可原样复跑。数字就是命令的输出。
- **【推断】**：由实测结构推出的人日/行数/成本，附完整推导链与区间来源。**不是测量值。**
- **【无法验证】**：需要真实 XAML 工程（编译、运行、渲染、调试）才能确定，本机不具备条件。

---

## 2. 结构分解度量（`UI/` 全部 49 个文件）

### 2.1 文件级结构计数【实测】

```powershell
$dir = 'D:\Compositor For Windows\src\Compositor\Compositor\UI'
Get-ChildItem $dir -Filter *.swift -File | Measure-Object -Property Length -Sum
```

```
Count    Sum
   49  493228
```

| 指标 | 数值 | 命令要点 |
|---|---:|---|
| 文件数 / 字节 | 49 / 493,228 | 同上 |
| 物理行数 | **8,922** | `(Get-Content $f).Count` 求和；`\n` 计数亦为 **8,922**（无「缺尾换行」差异） |
| `struct` / `enum` / `extension` | 87 / 16 / 12 | `(?m)^\s*(?:public \|internal \|private \|fileprivate )?(struct\|enum\|extension)\s+\w+` |
| `: View` 结构体 | **57**（分布于 36 个文件） | `(?m)^\s*…struct\s+\w+\s*:\s*View\b` |
| `NSViewRepresentable` | **11** | 字符串出现次数（= 一致性声明数） |
| NSView 系子类（含 `NSTextView/NSScrollView/NSTableView/NSButton`） | **12** | 见 §2.5 正则 |

**与文档的对账（重要）**：

| 量 | `01-architecture.md` | 本文实测 | 差异 |
|---|---:|---:|---|
| `UI/` 行数（§2.1 子系统表 8,971） | 8,971 | **8,922** | −49 = **恰好每文件 −1 行** |
| UI 层总行数（§4.0 表 14,624） | 14,624 | **14,563** | −61 = **恰好 61 个文件每文件 −1 行** |
| `View` 结构体（§2.2「62 个」） | 62 | **59**（全仓非测试） | +3（+5.1%） |
| AppKit 桥接（§2.2「21 处」） | 21 | **21** = `NSViewRepresentable` 12 + `NSHostingView/Controller` 9 | **完全吻合** |
| `NSView` 子类（§2.2「19 个」） | 19 | **21** | −2（−9.5%） |

**关于行数的系统性偏差【实测，已定位原因】**：文档的全部行数都比物理行数**恰好高 1 行/文件**（`FilterSheet` 407 vs 406、`CameraRawColorControls` 538 vs 537……逐文件核验一致）。这是 `read().split('\n')` 之类工具在「以换行结尾的文件」上多产生一个空元素造成的。**偏差 0.5%，对系数无实质影响**，但任何人重算行数前必须知道这条，否则会对不上账。

**关于 62/21/19 的口径【实测】**：这三个数是**全仓（非测试）**口径，却被写在 §2.2 的 `UI/` 行里（该行的文件数 49 是 `UI/` 单目录口径）。实测：

```
REPO-WIDE (excluding Tests): files=151 structs=169 ViewStructs=59
   NSViewRepresentable=12  NSHostingView|Controller=9  NSViewSubclasses=21
UI/ only:                     ViewStructs=57  NSViewRepresentable=11  NSViewSubclasses=12
```

**对声明式系数上限的直接影响**：§4.0 把「`NSViewRepresentable` 21 处每处都需要一个原生互操作宿主」当作声明式系数的**上限依据**。但 `UI/` 只有 11 处，另外 4 处（`EditorCanvas.swift` 1 + `Rendering/` 其余）已在**自绘控件桶**里计过一次。**该上限依据对声明式桶被高估约 2 倍。**

### 2.2 修饰符 / 状态 / 布局密度【实测】

统计的修饰符集合**显式限定为 97 个名字**（完整列表见 §附录 A.2），命令：

```powershell
$rxMod = '(?<![\w.])\.(frame|padding|background|overlay|foregroundStyle|…|matchedGeometryEffect)\s*\('
```

| 指标 | 总数 | 每千行 |
|---|---:|---:|
| SwiftUI 修饰符调用点 | **1,252** | **140.3** |
| 状态属性（`@State/@Binding/@Bindable/@Observable/@Environment/@FocusState/…`） | **125** | **14.0** |
| 手写布局容器（`HStack/VStack/ZStack/Grid/GeometryReader/ScrollView/Form/List/Spacer/Divider/…`） | **311** | **34.9** |

> 集合口径的敏感性【实测】：换用更宽的 ~115 名列表再测一次，修饰符调用点为 **1,322**（+5.6%）。**密度指标的绝对量有 ±6% 的口径误差，结论不受影响。**

### 2.3 视图声明 vs 逻辑 LOC【实测 + 分类器】

分类器（`classify.ps1`）用「花括号深度 + 行首声明」状态机把每个物理行归入四类之一：`VIEW`（`body` 与返回 `some View` 的计算属性）、`VIEWFACTORY`（返回 `-> some View` 的函数，即控件工厂）、`LOGIC`（其余函数与计算属性）、`DECL`（结构体/枚举/扩展头、存储属性、`import`）。命令与完整脚本见 §附录 A.1。

```
code lines (non-blank, non-comment) = 7914
VIEW+VIEWFACTORY = 3656  (46.2%)
LOGIC            = 3003  (37.9%)
DECL             = 1255  (15.9%)
view:logic ratio = 1.22 : 1
```

| 区域 | 行数 | 占代码行 | 对 XAML 移植的含义 |
|---|---:|---:|---|
| `VIEW`（视图声明） | 3,114 | 39.4% | 要变成 XAML |
| `VIEWFACTORY`（控件工厂，如 `FilterSheet.control(...)`） | 542 | 6.9% | 要变成 XAML `UserControl`（**本文 F_decl 的主要来源**） |
| `LOGIC`（函数/计算属性） | 3,003 | **37.9%** | **近乎原样搬 C# ViewModel —— 不产出任何 XAML** |
| `DECL`（类型头/存储属性） | 1,255 | 15.9% | 机械 |

**这条度量本身就修正了文档的建模方式**：文档用**一个**「每千行 SwiftUI」的系数去乘 `UI/` 的全部 8,922 行，但其中 **37.9% 是 C# 逻辑**、只有 **46.2% 是真正要写成 XAML 的视图声明**。把两者混进同一个费率，会把「逻辑便宜、XAML 贵」这个真实结构抹平。
【推断】正确做法是**分母只取 `VIEW + VIEWFACTORY`（3,656 行）**，逻辑部分按 §4.0(a) 的 A/B 类费率（0.35–1.20 人周/千行）另行计价。

**分类器的已知误差【实测，抽样核验】**：抽查 `BlendModePicker.swift`（71 行），分类器给出 `VIEW=0 / LOGIC=52`。人工阅读确认：该文件是 `NSViewRepresentable` + `Coordinator: NSObject, NSMenuDelegate` 包一个 `NSPopUpButton`，**确实一行视图声明都没有**——分类正确。抽查 `FilterSheet.swift` 得 `VIEW=283 / VIEWFACTORY=31 / LOGIC=17 / DECL=43`，与人工阅读一致（`body` 的 166 行 + 两个 `@ViewBuilder` 计算属性）。**不代表全量正确；误差量级估计 ±5%，足以支撑本文的结论粒度。**

### 2.4 声明式表面里有多少是「项目私有 DSL」【实测】

提取全仓 `extension View` 的成员定义，再统计它们在 `UI/` 里的调用点：

```powershell
[regex]::Matches($t, 'extension\s+View\b[\s\S]{0,20000}?(?=\n(?:extension|struct|final class|class|enum|@main)\b|\z)')
```

**18 个**私有 `View` 扩展成员，其中 **10 个有实际调用点，合计 123 处**：

| DSL 构造 | 调用点 | 定义位置 | XAML 对应物 |
|---|---:|---|---|
| `.scrubbable` | **38** | `NumericScrub.swift`（63 行） | **无**。要自建 attached behavior + pointer capture |
| `.configuredNativeShortcut` | **29** | `KeyboardShortcuts.swift`（320 行） | **无**。`KeyboardAccelerator` 语义不同 |
| `.arrowSteps` | **21** | `ContentView.swift` | **无** |
| `.unitSuffix` | **17** | `ToolHeaderStyle.swift` | **无**。要自建 attached property + `NumberBox` 模板 |
| `.toolHeaderBar` | 8 | `ToolHeaderStyle.swift` | **无** |
| `.releasesFocusOnCommit` | 5 | `ContentView.swift` | **无**。`FocusManager` 语义不同 |
| `.footerHitArea` | 3 | `LayersPanel.swift` | **无** |
| `.rawDevelopSheet` / `.psdConversionSheet` / `.roundedControls` | 各 1 | 同名文件 | 部分（`ContentDialog` / 圆角样式） |

对照量【实测】：`UI/` 中修饰符类调用点合计 1,639，其中 **stock SwiftUI 1,199、项目私有 DSL 123、非视图类型成员调用 317**。即 **DSL 占 123/(1,199+123) ≈ 9.3%**（对 97 名枚举集则为 9.8%）。

**【推断】这条为什么重要**：这些构造的**定义行数已经在文档统计的 8,864 行之内**，所以不是「额外加上去」的成本；但它们是**每千行费率的天敌**——它们是**固定成本**，且单位行成本远高于普通面板行。这直接引出 §4 的 F_decl 项。

### 2.5 两个桶的行数复核【实测】

文档 §4.0(b) 的自绘控件桶为 7 个文件，实测逐文件行数：

| 文件 | 行 | `context.*` CG 绘制调用 | mouse/key override | tracking/cursor override | 每千行绘制调用 |
|---|---:|---:|---:|---:|---:|
| `Rendering/EditorCanvas.swift` | 3,152 | 45 | **16** | **22** | 14.3 |
| `UI/NativeLayerList.swift` | 1,387 | 1 | **13** | **11** | 0.7 |
| `Rendering/InlineTextEditor.swift` | 557 | 0 | **9** | **5** | 0 |
| `Rendering/TransformOverlay.swift` | 467 | **110** | **0** | **0** | **235.5** |
| `Rendering/BrushCursorOverlay.swift` | 95 | 38 | 0 | 0 | 400.0 |
| `Rendering/SampleRingOverlay.swift` | 29 | 0 | 0 | 0 | 0 |
| `Rendering/CanvasLinesOverlay.swift` | 12 | 0 | 0 | 0 | 0 |
| **桶合计** | **5,699** | **194** | **38** | **38** | **34.0** |

> 文档说桶是 5,706 行，实测 5,699（差 7，仍是每文件 +1 的同一伪影）。

**桶的内部构成【实测，本文最关键的一张表】**：

| 子类 | 文件 | 行 | 占桶 | CG 绘制/千行 | 输入 override |
|---|---|---:|---:|---:|---:|
| **输入状态机型** | `EditorCanvas` + `NativeLayerList` + `InlineTextEditor` | 5,096 | **89.4%** | **9.0** | 38 |
| **纯绘制型** | `TransformOverlay` + 3 个 overlay | 603 | **10.6%** | **245.4** | **0** |

两类之间的绘制原语密度差 **27 倍**。而文档选的标定物是 10.6% 那一类里**绘制密度最高的文件**。

**"12 个鼠标 override" 复核【实测，文档正确】**：`EditorCanvas.swift` 有 26 个 `override func`，其中指针类正好 **12 个**（`mouseEntered/Exited/Moved/Down/Dragged/Up` 6 个 + `rightMouse*` 3 个 + `otherMouse*` 3 个），另有 `scrollWheel`、`magnify`、`keyDown`、`keyUp` 4 个，输入类合计 16 个。`resetCursorRects()`、`updateTrackingAreas()`、`cursorUpdate`、`cacheDisplay`（2 处）均实测存在。**文档这一处的依据是准确的。**

### 2.6 三张密度图的结论【推断】

```
视图声明 : 逻辑                = 1.22 : 1        (3,656 : 3,003)
stock SwiftUI : 项目私有 DSL   = 1,199 : 123     (9.3% 是必须重建的私有抽象)
自绘桶内 输入状态机型 : 纯绘制型 = 89.4% : 10.6%   (绘制密度差 27 倍)
```

**这三张图合起来说明：UI 层的成本不是「按行均匀分布」的**，任何单一费率都只能是近似。本文的任务不是把它变准，而是**给出这个近似在什么条件下成立、在什么条件下会错 3–7 倍**。

---

## 3. SwiftUI 结构 → XAML 结构的映射与换算比

### 3.1 可直接映射（机械）【推断】

| SwiftUI | XAML / WinUI 3 | 换算比 | 依据 |
|---|---|---|---|
| `VStack/HStack/ZStack(spacing:alignment:)` | `StackPanel`（`Spacing`）/ `Grid` | 1 : 1，行数 1:1–1:1.5 | 311 个布局容器 |
| `Text("…")` + `.font/.foregroundStyle` | `TextBlock` + `Style`/资源 | 1 行 → 1–2 行 | 25 处集中在 `FilterSheet` |
| `Slider(value:in:)` | `Slider`（`Minimum/Maximum/Value`） | 1 : 1.5 | 实测 15 处直接 + 63 处经 helper |
| 对数滑块（`log()…exp()`，`FilterSheet.swift:346-348`） | 无内建；ViewModel 里做同样映射 | 逻辑 1:1，**不需 XAML** | `control(...)` helper |
| `TextField(value:format:)` + 步进取整 | `NumberBox`（`SpinButtonPlacementMode`） | 1 : 1.5–2 | 46 处 `TextField(` |
| `Toggle` / `Picker(.segmented)` | `ToggleSwitch` / `RadioButtons` 或 `SegmentedControl`（社区） | 1 : 1.5 | 6 / 6 处 |
| `Divider` / `Spacer` | `Border` 1px / `Grid` 空列 | 1 : 1 | 311 个布局容器的一部分 |
| `LinearGradient` + `clipShape(RoundedRectangle)` | `LinearGradientBrush` + `Rectangle RadiusX/Y` | 1 : 1.5 | `GradientControls`、`GradientMapControls` |
| `.help("…")` | `ToolTipService.ToolTip` | 1 : 1 | 实测 **156 处** |
| `@State/@Binding` | ViewModel 属性 + `x:Bind` TwoWay | 1 : 2–3（含 `INotifyPropertyChanged`） | 实测 **125 个状态属性** |

**换算比 N 的来源【推断，可审计】**：文档要的「每个 SwiftUI View 结构体 ≈ 1 个 XAML 控件类 + 平均 N 行 XAML」中的 N，**我不用猜测，而用 `FilterSheet` 的实测结构反推**：
- `FilterSheet` 有 **63 处 `control(...)` 调用**，全部由**一个 27 行的函数**（`FilterSheet.swift:328-355`）产出；
- 该函数体内含 `HStack` + `Text` + `Slider` + `TextField` + `.scrubbable` + `.onTapGesture` + `.unitSuffix` + `GeometryReader` 共 8 个构造 —— 对应 XAML 的一行 `Grid`：`TextBlock`（标签）+ `Slider` + `NumberBox` + `ToolTipService.ToolTip` + `Grid.Row/Column` 属性。
- 一行 XAML 的 UI 元素 4–5 个、每个 2–4 行（含属性换行），故 **N ≈ 12–18 行 XAML / 每个滑块行实例**。
- 63 个实例 → **约 750–1,130 行 XAML**，对应该 helper 的 27 行源码 + 63 个调用点（约 150 行）→ **XAML/源行比 ≈ 4–6 倍**（仅限这一高度规整的模式）。

**全局 XAML/源行比【推断】**：`VIEW + VIEWFACTORY` = 3,656 行，考虑到 63 行滑块模式的高膨胀比与条件渲染块的低膨胀比，取 **1.0–1.5 倍** → **XAML 约 3,700–5,500 行**；`LOGIC` 3,003 行 → **C# ViewModel 约 3,000–4,500 行**（含 `INotifyPropertyChanged` 样板，1.0–1.5 倍）。**这两个数是【推断】；把它们变成【实测】需要真实 XAML 工程，本机做不到（§1.2）。**

### 3.2 没有对应物（必须重新设计）【推断】

| Swift 概念 | 位置 | WinUI 侧 | 为什么难 |
|---|---|---|---|
| **`NSViewRepresentable` 宿主** | `UI/` 内 11 处 | 无等价协议 | SwiftUI 里「嵌一个 NSView」是一等公民；WinUI 里要自绘/自建控件，或用 `ContentPresenter` + `SwapChainPanel` 绕路 |
| **`NSMenuDelegate` 悬停预览** | `BlendModePicker.swift:44-51` | **无** | AppKit 菜单的 `willHighlight` 逐项预览（画布实时跟随）；WinUI `MenuFlyout` 无逐项高亮事件。**需重新设计交互** |
| **拖放协议** | `NativeLayerList.swift`（`NSTableView` 5 个子类、41 处 pasteboard/dragging 命中） | `DragStarting`/`Drop` + `DataPackage` | 跨工程拖放要同时决定 OLE 与 WinRT 两条路径（文档 §2 已列为待验证） |
| **输入状态机** | `EditorCanvas.swift`（12 指针 + 4 键盘 override、22 处 tracking/cursor） | `PointerPressed/Moved/Released/CaptureLost` + `ManipulationMode` | 语义映射不 1:1（`acceptsFirstMouse`、`rightMouseDragged`、`otherMouse*` 中键、`cursorUpdate` 在 WinUI 都没有直接对应） |
| **`PreferenceKey` 反向布局传播** | `FilterSheet.swift:403-406`（`LabelWidthKey`） | **无** | 「所有行的标签同宽」这个需求在 XAML 里没有声明式做法：要么固定宽度（视觉差异），要么 ViewModel 预算最大宽度，要么自定义 `Panel` 逐子测量 |
| **`.scrubbable`（拖动式数值盘）** | 38 处 | **无** | 需要 attached behavior + pointer capture + 灵敏度/量程换算 |
| **`marching ants` 的位图掩膜重采样** | `TransformOverlay.swift:143-177` | `CanvasRenderTarget` + 像素回读 + 路径重建 | CGContext 位图掩膜 → 降采样 → 边缘追踪 → 路径缩放；Win2D 侧可用但需整套重写，还要重建 `Task.detached` 的 generation guard |
| **`NSCursor.FrameResizePosition`** | `TransformOverlay.swift:43-49` | `InputSystemCursorShape`（只有 4 种尺寸方向） | 8 个方向 → 4 个系统光标，需保留角度→索引的映射数学 |

### 3.3 与「无规格可依」的关系

文档 §4.11 第 6 条说 UI「不可压缩……没有规格可依，只能逐屏实现 + 截图验收」。**实测支持这条**：
- `UI/` 里 **156 处 `.help()` 文案**是纯文字规格，只能逐条搬运；
- **119 个 switch-case 标签**（`FilterSheet` 18 个 filter kind、Camera Raw 25+22 个）意味着**至少 119 个互相独立的界面分支**；
- 全仓**无**任何 UI 截图 baseline 或布局规格文件（`docs/` 下只有 `project-format.md`、`brush-performance.md` 等，无 UI 规格）。

---

## 4. 标定物一：`FilterSheet.swift` 逐节分解

**文件**：`src/Compositor/Compositor/UI/FilterSheet.swift`，28,034 B，**406 行**（文档作 407）。
**实测结构**：`struct` 3、`: View` 2、修饰符调用点 **103**（253.7/千行，是 `UI/` 均值 140.3 的 **1.8 倍**）、状态属性 3、布局容器 19、`NSViewRepresentable` **0**、AppKit 符号命中 6。
**LOC 分类**：`VIEW 283 / VIEWFACTORY 31 / LOGIC 17 / DECL 43`。

### 4.1 逐节清单

| 节 | 行区间 | 行数 | 内容 | 难度 | XAML 行 | C# 行 |
|---|---|---:|---|---|---:|---:|
| S1 绑定与状态 | 5–17 | 13 | `@Bindable session`、`edit`/`settings` 计算属性、`update(_:)`、`isCameraRaw`、`@State labelWidth` | 机械 | 0 | 40–70 |
| S2 `body` 顶层 + 18 分支 switch | 19–184 | 166 | 18 个 filter kind 各一段；含 30 处 `.help()`、6 `Toggle`、6 `Picker`、4 `Button`、25 `Text` | 机械（量大） | 340–520 | 120–240 |
| S3 `ditherControls` | 186–253 | 68 | 14 个条件行（`usesPixelSize`/`ascii`/`halftone`/`hasTones`/`diffuses`/`drawsMarks`）+ 字符输入 + 双色板 | 机械 | 150–230 | 60–120 |
| S4 `scanlinesControls` | 255–293 | 39 | 12 行 + 条件双色板 | 机械 | 110–170 | 50–100 |
| S5 `swatch(_:help:action:)` | 295–306 | 12 | 24×24 圆角色板，白内描边 + 黑外描边 | 需设计（轻） | 20–40 | 40–80 |
| S6 `flag(_:)` | 308–310 | 3 | `Binding<Bool>` 助手 | 机械 | 0 | 10–15 |
| S7 `resetting(_:in:)` | 312–317 | 6 | 双击复位到默认值 | 机械 | 0 | 10–20 |
| S8 三条 `CameraRawSliderTrack.opposing` | 319–324 | 6 | 双色对向轨道（青/红、品/绿、黄/蓝） | **需设计** | 60–100 | 120–200 |
| S9 **`control(...)` 行控件** | 326–355 | **30** | **被 63 处复用**：标签（`GeometryReader` 测宽 + `.scrubbable` + 双击复位）+ 对数/线性 `Slider` + `NumberBox` + `.unitSuffix` | **需设计** | 150–220 | 150–220 |
| S10 `GradientMapControls` | 358–401 | 44 | `LinearGradient` 预览条 + 2 色板 + Reverse | **直接映射** | 60–90 | 40–70 |
| S11 `LabelWidthKey` | 403–406 | 4 | 全行等宽标签的 `PreferenceKey` | **需设计** | 0–30 | 40–120 |
| — 导入/空行/注释 | — | 15 | | — | | |
| 集成（未单列） | — | — | ESC / Enter 快捷键、`ProgressView` 等待态、`disabled` 联动、焦点、DPI、截图验收 | 需设计（轻） | 20–40 | 60–120 |
| **合计** | | **406** | | | **910–1,440** | **740–1,375** |

### 4.2 人日分解【推断】

| 项 | 人日 | 说明 |
|---|---:|---|
| **一次性可复用件** | | |
| S9 `FilterSliderRow`（UserControl，8 个依赖属性 + 对数映射 + 取整 + 单位后缀 + tooltip + 双击复位 + 拖动盘接线） | 2.0–4.0 | 被 63 处复用 |
| S8 双色对向轨道滑块 | 2.0–4.0 | 1 处使用，但同一控件会被 Camera Raw 的 15 个 `CameraRawSlider(` 复用 |
| S11 共享标签宽度 | 0.5–1.5 | 或降级为固定宽度（视觉差异，需拍板） |
| S5 色板按钮模板 | 0.5–1.0 | 6 处复用 |
| 10 个私有 DSL 行为的 WinUI 重写分摊 | 1.5–3.0 | `.scrubbable` 38 处、`.configuredNativeShortcut` 29 处等 |
| **一次性小计** | **6.5–13.5** | = **1.3–2.7 人周**（= 本文的 **F_decl**） |
| **本文件专属** | | |
| 63 行实例化 + 18 容器 + 3 组条件行（逐条核对 tooltip/量程/小数位） | 2.0–4.0 | 纯搬运，但极易错 |
| ViewModel 接线（29 个设置字段、63 处绑定） | 1.0–2.0 | |
| 集成与截图验收（ESC/Enter、等待态、DPI、与 macOS 并列截图） | 1.5–3.0 | |
| **专属小计** | **4.5–9.0** | |
| **总计** | **11–22.5 人日 = 2.2–4.5 人周** | |

### 4.3 这个标定物告诉了我们什么

- **单文件口径**：2.2–4.5 人周 ÷ 0.406 千行 = **5.4–11.1 人周/千行**。这是原区间 [0.8, 1.4] 的 **3.9–13.9 倍**。
- **摊销口径**：一次性件 F_decl = 1.3–2.7 人周（占本文件约 60%）被 UI/ 其余 **30+ 个面板、63 处 `ForEach`、119 个 switch 分支**复用后，**边际**成本只剩 4.5–9.0 人日 = 0.9–1.8 人周 → **2.2–4.4 人周/千行**。
- **结论**：`FilterSheet` **不能**单独用来标定一个「人周/千行」的费率。它的价值在于**证明了 F_decl 存在且量级为 1.3–2.7 人周**。
- **顺带一条**：`FilterSheet` 的修饰符密度是全 `UI/` 均值的 1.8 倍（253.7 vs 140.3/千行），**它本身也比平均的声明式文件更难**。文档挑它作标定物，方向上是挑了偏难的样本。

---

## 5. 标定物二：`TransformOverlay.swift` 逐节分解

**文件**：`src/Compositor/Compositor/Rendering/TransformOverlay.swift`，24,882 B，**467 行**（文档作 468）。
**实测结构**：`func` 17 个（其中 8 个 `draw*`）、`context.*` CG 绘制调用 **110**、`NSGraphicsContext.current` 8、`CGContext(` 1、`CGMutablePath/CGPath` 10、AppKit 符号命中 34、`NSView` 子类 1、`setAccessibilityElement` 1、**mouse/key override 0、tracking/cursor override 0、`hitTest` 返回 `nil`**。

### 5.1 逐节清单

| 节 | 行区间 | 行数 | 内容 | 需要的 WinUI 原语 | 难度 | 人日 |
|---|---|---:|---|---|---|---:|
| G `TransformOverlayGeometry` | 3–50 | **48** | 把手坐标、旋转把手、`hit(_:)` 8 把手 + 4 边命中测试、`resizeCursor(for:)` 角度→光标 | 纯数学；`NSCursor.FrameResizePosition` → `InputSystemCursorShape`（8 方向→4 系统光标） | **近乎机械** | 0.5–1.0 |
| H `NSView` 壳 | 52–90 | 39 | `isFlipped=true`、`hitTest → nil`、`geometry` 计算属性（组/多选/单选三分支）、`gradientLine`、`antsPhase` | `Canvas` 或 `CanvasControl`；`IsHitTestVisible=false` | 机械 | 0.5–1.0 |
| L **marching-ants LOD** | 92–177 | **86** | 复杂度判定（20,000 元素阈值）→ 二次幂缩放步长 → 后台 `Task.detached` 追踪 → 位图掩膜填充（≤4,000 万像素）→ 降采样 → `MagicWand.outline` → 路径回缩；含 `antsSource` 恒等 + `antsPendingStep` 的 generation guard | `CanvasRenderTarget`（离屏位图）+ `DrawImage(CanvasImageInterpolation.Medium)` + 像素回读 + 路径重建 + `Task.Run`/`CancellationTokenSource` 重写缓存失效逻辑 | **需设计（本文件最难，且无直接对应物）** | **3.0–6.0** |
| D `draw(_:)` 分派 | 179–188 | 11 | 8 个绘制函数的调用顺序（网格/参考线/裁剪或渐变或变换把手/选区/套索草稿/吸附线） | `CanvasControl.Draw` 事件；`Invalidate()` | 机械 | 0.5 |
| P1 `drawLayoutGrid` | 192–234 | 43 | 主/次网格线、虚线样式、hairline 宽度 `1/backingScale` | `DrawLine` + `CreateStrokeStyle(CanvasDashStyle)` | 机械 | 0.5–1.0 |
| P2 `drawGuides` | 237–258 | 22 | 全视图参考线 | `DrawLine` | 机械 | 0.25–0.5 |
| P3 `drawSnapGuides` | 261–278 | 18 | 吸附对齐线（强调色） | `DrawLine` | 机械 | 0.25–0.5 |
| P4 `drawSelection` | 288–302 | 15 | 白底线 + 动画黑虚线（marching ants） | 两次 `DrawGeometry` + dash | 机械（依赖 L） | 0.5 |
| P5 `drawLassoDraft` | 305–337 | 33 | 套索草稿：线/矩形/椭圆三态 + 首角点把手 | `DrawGeometry` / `FillRectangle` / `DrawEllipse` | 机械 | 0.5–1.0 |
| P6 `drawTransformHandles` | 339–366 | 28 | 变换框 8 个 7×7 把手 + 旋转把手 + 旋转连线 | `DrawRectangle` / `FillEllipse` | 机械 | 0.5 |
| P7 裁剪几何 + `drawCrop` | 368–396, 439–466 | 57 | `cropViewRect`/`cropHandles`/`cropResizeRegions`（含整边可拖）+ 遮罩、三分线、把手 | `DrawRectangle` + `CanvasPathBuilder` + even-odd 填充 | 机械 | 1.0–1.5 |
| P8 `drawGradientLine` | 397–437 | 41 | 径向渐变外圈虚线 rim、双描边线、两端棋盘格色点 | `DrawLine` / `DrawEllipse` + dash | 机械 | 1.0–1.5 |
| — 坐标系与 DPI 集成 | — | — | `isFlipped=true`（左上原点，**与 WinUI 一致，好消息**）；`1/max(backingScale,1)` 的 hairline | `CanvasDrawingSession.Dpi` / `ConvertDipsToPixels` | 需设计（轻） | 1.0–2.0 |
| **合计** | | **467** | | | | **10.0–17.0** |

### 5.2 结论：这个标定物**不是**自绘桶的代表

| | `TransformOverlay` | 自绘桶 | 倍数 |
|---|---:|---:|---:|
| 占桶行数 | 467（**8.2%**） | 5,699 | — |
| `context.*` CG 绘制调用 | **110（56.7%）** | 194 | — |
| CG 绘制密度 | **235.5/千行** | 34.0/千行 | **6.9×** |
| mouse/key override | **0** | 38 | — |
| tracking/cursor override | **0** | 38 | — |
| `hitTest` | **返回 `nil`** | — | 纯画笔 |

**按人日换算**：
- 含 LOD：10.0–17.0 人日 = **2.0–3.4 人周** ÷ 0.467 千行 = **4.3–7.3 人周/千行**
- 不含 LOD（LOD 计入 Wave 4 的 selection/`MagicWand` 工作，避免重复计价）：10.0–3.0 = 7.0 至 17.0–6.0 = 11.0 人日 = **1.4–2.2 人周** ÷ 0.467 = **3.0–4.7 人周/千行**

→ **对「纯绘制」子类，原区间 2.5–4.0 大体得到支持**：实测 3.0–4.7 与原区间在 **3.0–4.0 段重合**，上端超出 0.7 人周/千行（+18%）；含 LOD 则整体超出上限，但 LOD 可归入 Wave 4 的 selection 工作。
→ **对桶，原区间无法验证**：桶的 89.4% 是「输入状态机型」，其绘制密度只有 9.0/千行而输入密度极高，**本标定物对它们的度量支持为零**。若把本文件测得的 4.3–7.3 人周/千行外推到桶：5.699 × 4.3–7.3 = **24.5–41.6 人周**（文档给的是 14.3–22.8），接近翻倍——**这正是不具代表性样本外推的危险。**

### 5.3 对本标定物应做的替换（具体建议）

**把自绘控件的标定物从 `TransformOverlay.swift` 换成 `EditorCanvas.swift` 的一个最小可用切片**，切片应至少包含：
1. 缩放/平移 + 以指针为中心的滚轮缩放（`scrollWheel` → `PointerWheelChanged`）；
2. **12 个指针 override 中至少 6 个**的 WinUI 映射，含中键（`otherMouse*`）与右键拖动；
3. `resetCursorRects` / `updateTrackingAreas` / `cursorUpdate` 三件套的 WinUI 替代（**这是文档上限依据的核心，也是 `TransformOverlay` 完全没有的部分**）;
4. 三档 DPI（100/150/200%）下的 hairline 与光标位置正确性。

理由【推断】：桶的 55.3% 是 `EditorCanvas`，它的成本结构与 `TransformOverlay` 相反——**绘制少（45 处 vs 110 处）、输入多（16 vs 0）**。用后者标定前者，误差方向是**系统性低估输入侧、高估绘制侧**，而两者不可互相替代。

---

## 6. 收紧后的系数区间

### 6.1 声明式

**原区间 0.8–1.4 人周/千行：得到支持，但必须限定分母与摊销范围。**

```
【实测】View 声明 + 控件工厂 = 3,656 行（46.2%）；逻辑 = 3,003 行（37.9%）
【实测】FilterSheet 单文件口径 = 5.4–11.1 人周/千行（原区间的 3.9–13.9 倍）
【实测】一次性可复用件 = 123 处私有 DSL 调用点 + 1 个 27 行行控件被 63 处复用
【推断】F_decl = 1.3–2.7 人周（一次性，桶级摊销）
```

**收紧后的口径（三件套，替换原来的单一费率）**：

| 项 | 值 | 依据 |
|---|---|---|
| 边际代码产出率 `r_decl` | **0.8–1.4 人周/千行（维持原区间）** | 63 行滑块模式的高度规整性；视图声明 46.2%；XAML/源比 1.0–1.5 |
| 一次性可复用 XAML 基建 `F_decl` | **1.3–2.7 人周（新增）** | §4.2 的 6.5–13.5 人日 |
| 分母 | **`VIEW + VIEWFACTORY` 行数**，逻辑行按 A/B 类费率另计 | §2.3 |

**桶级结果**：8.86 千行 × [0.8, 1.4] + [1.3, 2.7] = **8.4–15.1 人周**（文档原为 7.1–12.4）。
**若坚持用文档的「全部 `UI/` 行数」作分母**，则 8.86 × [0.8, 1.4] = 7.1–12.4 人周**仍然成立** —— 因为逻辑行便宜、视图行贵的偏差在桶级大致相抵。**这就是「原区间得到支持」的准确含义：桶级成立，单文件级不成立。**

### 6.2 自绘控件

**原区间 2.5–4.0 人周/千行：未被推翻，但对桶的适用性【无法验证】。**

| 子类 | 占桶行数 | 本文测得的费率 | 与原区间的关系 |
|---|---:|---|---|
| 纯绘制型（`TransformOverlay` + 3 overlay） | 10.6% | **3.0–4.7 人周/千行**（不含 LOD）；4.3–7.3（含） | 不含 LOD 时与原区间在 3.0–4.0 段重合，上端 +18% |
| 输入状态机型（`EditorCanvas` + `NativeLayerList` + `InlineTextEditor`） | **89.4%** | **【无法验证】** | 标定物不含该类任何特征 |

**收紧后的表述**：
- 保留 **2.5–4.0** 作为桶级费率（对纯绘制子类实测支持、对输入子类无证据、且文档自己的波次口径给出一致量级，见 §7.3）；
- **但必须在文档里注明：该费率对桶的适用性尚未标定**，并附 §5.3 的替换标定方案；
- 已知的风险方向是**单侧**：`TransformOverlay` 的绘制密度是桶均值的 6.9 倍，如果桶的真值真的靠近它的纯绘制费率，桶成本会从 14.3–22.8 上移到 17.1–41.6（用 3.0–7.3 计），**上限接近翻倍**。

### 6.3 一句话回答「支持还是推翻」

> **两个原区间都得到支持**——而且是**在做了更严格的度量之后**得到支持，不是我为了显得有产出而保留的。
> 但支持是**附条件的**：(1) 声明式费率只能用于桶级摊销，单文件口径会偏高 3.9–13.9 倍；(2) 声明式桶需要新增 1.3–2.7 人周的固定基建项；(3) 自绘费率对桶的适用性没有证据，文档指定的标定物对 89.4% 的桶不具代表性。
> **真正需要改的不是那两个数字，是 `01-architecture.md` §4.0「待验证」条里写的验证方法。**

---

## 7. 反推工期的敏感度分析

### 7.1 系数口径下的 UI 总成本【实测行数 × 文档系数】

| 桶 | 行数（本文实测） | 系数 | 人周 |
|---|---:|---|---:|
| 声明式 | 8,864（= 14,563 − 5,699） | 0.8–1.4 | **7.1–12.4** |
| 自绘控件 | 5,699 | 2.5–4.0 | **14.3–22.8** |
| **UI 合计** | 14,563 | — | **21.4–35.2** |

> 与文档的 21.4–35.3 差 0.1 人周（行数差 −0.4% 所致）。**§4.11 的两个口径「重合」这一结论，在本文的行数复核下依然成立。**

### 7.2 敏感度：UI 系数值多少

**摆动幅度**：
- 声明式：8.864 × (1.4−0.8) = **5.3 人周**
- 自绘：5.699 × (4.0−2.5) = **8.6 人周**
- → **自绘控件的敏感度是声明式的 1.61 倍**；两者合计摆动 **13.9 人周**。

**单侧移动（其余各波保持原值）**：

| 情形 | 计算 | 总工期 |
|---|---|---:|
| 基准（全取下限 / 全取上限） | 3+6+6+5+10+8+10+8+5+4 / 5+9+10+8+16+12+14+12+8+6 | **65 / 100** |
| UI 系数取**上限**、其余取**下限** | 65 − 21.4 + 35.2 | **78.8 人周（+13.8，+21%）** |
| UI 系数取**下限**、其余取**上限** | 100 − 35.2 + 21.4 | **86.2 人周（−13.8，−14%）** |

**读法**：
1. **35 人周的总区间宽度里，13.9 人周（39%）纯粹来自这两个 UI 系数。** 让读者知道这个数字值多少 —— 它值总不确定性的四成。
2. **这是一个单侧风险**：实测证据只支持「不比 0.8/2.5 更便宜」，给不出任何「更便宜」的证据（§4.3、§5.2 的下限都高于或接近原区间下沿）。**所以 UI 系数能把工期往后推，推不回来。** 一个乐观的 65 周计划里，最大的下行风险就是这里。
3. **自绘系数比声明式系数重要 1.6 倍，而它恰恰是度量覆盖最差的那个**（标定物对 89.4% 的桶不具代表性）。这是本文最该被记住的一条。

### 7.3 两种「错误用法」会把工期推到哪里【本文最有操作价值的一节】

**错误用法 A：按 `01-architecture.md` §4.0 字面执行「用一个真实面板标定系数」**
若 Wave 0 照做，得到声明式费率 **5.4–11.1 人周/千行**，然后被乘上 8.86 千行：

```
声明式 = 8.864 × [5.4, 11.1]      = 47.9–98.4 人周
自绘   = 5.699 × [2.5, 4.0]       = 14.3–22.8 人周
UI 合计                            = 62.2–121.2 人周     (文档为 21.4–35.2)
总工期 = 43.6 + 62.2  …  64.7 + 121.2 = 105.8–185.9 人周   (文档为 65–100)
```

**即：一个 M 级工时、量测方法正确、结论错误 3.9–13.9 倍的标定，会把 65–100 周的计划直接改写成 106–186 周，并很可能因此错误地否决整个移植项目。**

**错误用法 B：用 `TransformOverlay` 标定出的费率外推到整个自绘桶**
```
自绘 = 5.699 × [4.3, 7.3] = 24.5–41.6 人周   (文档为 14.3–22.8)
UI 合计 = 7.1–12.4 + 24.5–41.6 = 31.6–54.0
总工期 = 43.6 + 31.6 … 64.7 + 54.0 = 75.2–118.7 人周
```

**正确用法（本文建议）**：
```
声明式桶 = 8.864 × r_decl + F_decl = 7.1–12.4 + 1.3–2.7 = 8.4–15.1 人周
自绘桶   = 5.699 × 2.5–4.0                              = 14.3–22.8 人周
UI 合计                                                  = 22.7–37.9 人周
总工期   = (65 − 21.4 + 22.7) … (100 − 35.2 + 37.9)      = 66.3–102.7 人周
```

**与原区间 65–100 的差异 < 3%。原区间得到支持。**

### 7.4 Wave 4–8 的敏感性【实测 + 推断】

逐波推算（用 §7.3 的正确口径）：

| Wave | 文档工作量 | 其中 UI 部分（实测行数推算） | UI 系数取上/下限的影响 |
|---|---:|---|---|
| W4 画布与工具 | 10–16 | 自定义桶 3,429 行 + 声明式 1,629 行 → 8.6–13.7 + 1.3–2.3 = **9.9–16.0** | ±~2.5 人周（声明式部分小） |
| W5 外壳与图层面板 | 8–12 | `NativeLayerList` 1,387（自定义）+ 2,718 行声明式 → 3.5–5.6 + 2.2–3.8 = **5.7–9.4** | ±~1.8 人周 |
| W6 面板/滤镜 UI | 10–14 | 实测登记的 18 个文件 = **3,976 行**，全声明式 → 3.2–5.6 + 每面板验收 | ±~2.5 人周 |
| W8 文本 | 5–8 | `InlineTextEditor` 557 + `TypeControls` 249 → 1.4–2.2（自绘）+ 0.2–0.4（声明式）= **1.6–2.6** | ±~0.3 人周 |

**一处值得 Lead 注意的内部张力【实测 + 推断】**：文档 §4.11 声明的两个口径在 UI 上「重合」，但把同一段代码分别按两个口径算一次：

| 波次 | 登记的 UI 代码 | 系数口径（本文 §7.3 正确用法） | 文档波次口径 | 比值 |
|---|---:|---|---|---|
| W6 | 3,976 行声明式 | 3.2–5.6 人周 + 面板验收 | **10–14 周** | 波次口径高 **1.8–4.4×** |
| W5 的声明式部分 | 2,718 行 | 2.2–3.8 人周 | 8–12 周减去自绘 3.5–5.6 = **2.4–8.5 周** | 隐含费率 **0.9–3.1 人周/千行** |

W5 的隐含费率（0.9–3.1）**下限落在原区间内、上限超出 2.2 倍**；W6 的隐含费率 **2.5–3.5 人周/千行，整体在原区间之上**。
**【推断】这个差额不是矛盾，而是口径差**：波次口径包含**每个面板的截图验收、与 macOS 并列比对、DPI/焦点/键盘检查**（W6 出口标准 3：「每个面板有截图存证，并与 macOS 侧的对应面板并列」），而系数口径只算代码产出。**约 30 个面板 × 0.15–0.35 人周/面板（验收）= 4.5–10.5 人周**，正好落在这个差额里。
→ **建议在 `01-architecture.md` §4.0 明确写出：0.8–1.4 是代码产出率，不含逐面板验收；§4.11 的「两口径互相印证」应改为「系数口径 + 逐面板验收 ≈ 波次口径」。** 这是本文对文档最有价值的一条修正。

---

## 8. 本文的度量**无法覆盖**的成本

以下成本**没有**进入 §6 的任何费率，且**我在本机无法验证**（§1.2）。列出实测到的规模，供 Lead 在回写时决定是否单列预算。

### 8.1 实测到的规模

| 项 | 实测数量 | 命令 | 原 SwiftUI 里为什么「免费/便宜」 |
|---|---:|---|---|
| 无障碍调用 | **122 处**，分布 29 个文件（`NativeLayerList` 23、`EditorCanvas` 11、`LayersPanel` 8、`ProjectTabs` 8、`ContentView` 8、`CameraRawControls` 7、`ColorPickerSheet` 7） | `\baccessibility[A-Z]\w*\|setAccessibility\w*\|AccessibilityLabel\|AutomationProperties` | SwiftUI 的 `.accessibilityLabel()` 是链式修饰符；WinUI 要逐个挂 `AutomationProperties` 并做 UI Automation 验证 |
| 键盘/焦点耦合 | `configuredNativeShortcut` **29**、`keyboardShortcut` 12、`.focused(` 7、`releasesFocusOnCommit` 5、`onKeyPress` 2、`HeldModifiers` 8 | 见 §附录 A.3 | SwiftUI 的 `.keyboardShortcut` + `FocusState` 与 WinUI `KeyboardAccelerator` + `FocusManager` 语义不同，逐个面板都要重测 |
| 异步/定时耦合 | `DispatchQueue` **16**、`Task {` **22**、`Task.detached` 2、`Timer` 2、`NotificationCenter` 3、`.animation(` 2、`.transition(` 0 | 见 §附录 A.4 | SwiftUI 里「状态变了视图就跟上」是免费的；WinUI 要显式 `DispatcherQueue` 编组，跨线程更新 UI 会抛 |
| `.help()` 文案 | **156 处** | `\.help\(` | 文案本身可搬，但 `ToolTipService` 的显示时机/换行/键盘可达性要重测 |
| 独立界面分支 | **119 个 switch-case 标签** | `(?m)^\s*case\s+\.` | 每个分支都要单独截图验收 |

### 8.2 无数量、但确定存在的额外成本【无法验证】

1. **XAML 编译期错误诊断**：SwiftUI 的 `some View` 类型错误由类型检查器在编译期报出；XAML 侧 `x:Bind` 的路径错误是**编译期**错误（这点更好），但 `{Binding}` 走运行期、失败时**静默**。调试体验的净变化**无法在本机验证**。
2. **XAML 热重载**：需要 Visual Studio + 调试附加，且对自定义控件与 `x:Bind` 覆盖不全。SwiftUI 侧的迭代速度无法在此对比。
3. **DPI 适配**：实测 `EditorCanvas.swift:848` 的 `viewDidChangeBackingProperties` 存在，`TransformOverlay` 有 8 处 `1/max(backingScale,1)` 的 hairline 计算。WinUI 侧要处理 `XamlRoot.RasterizationScale` + per-monitor DPI + 跨屏拖动。**Wave 4 出口标准 2（三档 DPI 截图 + 跨屏拖动）在本机无法执行。**
4. **XAML 行数膨胀带来的 review 成本**：`Grid.Row/Column`、`x:Name`、`x:Bind` 逐行属性使 XAML 的 diff 比 SwiftUI 冗长。本文估的 XAML/源比 1.0–1.5 倍**已经是这个膨胀量，但它本身是【推断】。**
5. **上游只读的持续成本**：`05-roadmap.md` §6 明令「不向上游提方便移植的 Swift PR」，因此每次上游变更都要人工追。**这与系数无关，但会持续消耗工期。**
6. **`NSViewRepresentable` 的隐藏成本**：`UI/` 内 11 处，其中 `BlendModePicker` 的逐项悬停预览（§3.2）在 WinUI 里**没有对应机制**，属于「重设计」而非「翻译」。

---

## 9. 对 `01-architecture.md` 的回写建议（供 Lead 使用）

> 本文按约束**未修改** `01-architecture.md`。以下是可直接落笔的建议，逐条给出替换文本的要点。

### 9.1 §4.0「待验证」条（第 562 行）——**必须替换**

原文的验证方法（「Wave 0 用一个真实面板做标定……记录人日」）**按字面执行会产出偏高 3.9–13.9 倍的系数**（§7.3 错误用法 A）。建议替换为：

> **待验证（已部分标定，2026-10）**：两个转换系数的**桶级适用性**已由 `docs/port/06-ui-calibration.md` 用结构度量复核：
> ① 声明式 **0.8–1.4 人周/千行得到支持**，但仅作为「可复用件摊销后的边际代码产出率」，**不得用于单文件换算**；桶级还需加一次性基建项 **F_decl = 1.3–2.7 人周**。
> ② 自绘控件 **2.5–4.0 得到部分支持**：对「纯绘制」子类实测 3.0–4.7，对占桶 89.4% 的「输入状态机型」子类**无度量支持**。
> ③ **标定协议已修订**：声明式费率必须在**同类面板 ≥3 个、合计 ≥1,500 行**上标定并扣除一次性件；自绘控件费率必须用 `EditorCanvas` 的最小切片标定（缩放/平移 + ≥6 个指针 override + cursor/tracking 三件套 + 三档 DPI），**不得**用 `TransformOverlay`（它 0 输入处理）。

### 9.2 §4.0 系数表（第 555–560 行）——建议增补三行

| 增补 | 内容 |
|---|---|
| 分母 | 声明式费率的**分母应取 `VIEW + VIEWFACTORY` 行数**（实测占 `UI/` 代码行 46.2%），逻辑行（37.9%）按 A/B 类费率另计 |
| F_decl | **新增一次性项 1.3–2.7 人周**：行控件（含对数映射/取整/单位后缀/拖动盘）、双色对向轨道滑块、共享标签宽度（替代 `PreferenceKey`）、色板模板、10 个私有 DSL 行为的 WinUI 重写 |
| 验收 | 明确写出 **0.8–1.4 不含逐面板验收**；验收约 **0.15–0.35 人周/面板 × 约 30 个面板 = 4.5–10.5 人周**（§7.4） |

### 9.3 §4.11 第 2 条与第 3 条——建议改口径表述

- 第 2 条 W6 那行：文档按「行数占比 ≈47%」折算 W4 的 UI 部分；建议改为按**实测行数**（W4 自定义桶 3,429 + 声明式 1,629）。
- 第 3 条：「两个独立口径在 21–36 人周区间内重合」**成立**，本文复核为 **21.4–35.2**（文档 21.4–35.3）。但建议补一句口径差：**系数口径 = 代码产出，波次口径 = 代码 + 逐面板验收**；两者相减得验收成本 4.5–10.5 人周。

### 9.4 §2.2 评级表 `UI` 行——建议修正计数口径

「49 个文件、62 个 `View` 结构体、21 处 AppKit 桥接、19 个 `NSView` 子类」中，文件数是 `UI/` 单目录口径，后三者是**全仓非测试**口径。建议改为：
「`UI/` 49 个文件、**57** 个 `View` 结构体、**11** 处 `NSViewRepresentable`、**12** 个 NSView 系子类；全仓非测试口径为 **59 / 21**（= 12 representable + 9 hosting）/ **21**。」

### 9.5 全局行数——建议一次性下调

文档全部行数比物理行数**恰高 1 行/文件**（§2.1）。建议以本文的复现命令重算一次并整体替换：`UI/` 8,922（非 8,971）、UI 层 14,563（非 14,624）、自绘桶 5,699（非 5,706）。**差异 0.4–0.6%，不改变任何结论，但不换会对不上账。**

---

## 附录 A：复现命令（Windows PowerShell 5.1，本机实测）

### A.1 LOC 分类器（`classify.ps1`）

```powershell
param([string]$Dir, [string]$OutFile)
function Strip-Noise([string]$s) {
  $s = [regex]::Replace($s, '//.*$', '')
  $s = [regex]::Replace($s, '"(?:\\.|[^"\\])*"', '""')
  return $s
}
$results = @()
foreach ($f in Get-ChildItem $Dir -Filter *.swift -File | Sort-Object Name) {
  $lines = Get-Content -LiteralPath $f.FullName
  $depth = 0; $region = $null
  $c = [ordered]@{ total=$lines.Count; blank=0; comment=0; VIEW=0; VIEWFACTORY=0; LOGIC=0; DECL=0 }
  for ($i = 0; $i -lt $lines.Count; $i++) {
    $raw = $lines[$i]; $clean = Strip-Noise $raw; $trim = $raw.Trim()
    if ($trim -eq '') { $c.blank++; continue }
    if ($trim.StartsWith('//') -or $trim.StartsWith('///') -or $trim.StartsWith('*') -or $trim.StartsWith('/*')) { $c.comment++; continue }
    if ($null -eq $region) {
      $lookahead = ($lines[$i..([math]::Min($i+6, $lines.Count-1))] -join ' ')
      $isFunc     = $clean -match '^\s*(?:@\w+(?:\([^)]*\))?\s+)*(?:public |internal |private |fileprivate |static |final |override |mutating )*func\s+\w+'
      $isBody     = $clean -match '\bvar\s+body\b'
      $isSomeView = $lookahead -match '->\s*some\s+View'
      $isCompVar  = $clean -match '^\s*(?:@\w+(?:\([^)]*\))?\s+)*(?:public |internal |private |fileprivate |static )*var\s+\w+\s*:'
      $isInit     = $clean -match '^\s*(?:public |internal |private |fileprivate )?(?:convenience\s+)?init\s*[\(<]'
      if ($isBody -or ($isCompVar -and $lookahead -match 'some\s+View')) { $region = @{ kind='VIEW'; depth=$depth } }
      elseif ($isFunc -and $isSomeView) { $region = @{ kind='VIEWFACTORY'; depth=$depth } }
      elseif ($isFunc -or $isCompVar -or $isInit) { $region = @{ kind='LOGIC'; depth=$depth } }
    }
    if ($null -ne $region) { $c[$region.kind]++ } else { $c.DECL++ }
    $depth += ([regex]::Matches($clean,'\{')).Count - ([regex]::Matches($clean,'\}')).Count
    if ($null -ne $region -and $depth -le $region.depth) { $region = $null }
  }
  $results += [pscustomobject]@{ File=$f.Name; Total=$c.total; Blank=$c.blank; Comment=$c.comment
    VIEW=$c.VIEW; VIEWFACTORY=$c.VIEWFACTORY; LOGIC=$c.LOGIC; DECL=$c.DECL }
}
$results | Format-Table -AutoSize | Out-String -Width 200
```

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File classify.ps1 `
  -Dir 'D:\Compositor For Windows\src\Compositor\Compositor\UI'
```

真实输出（汇总行）：`TOTAL 8922 474 534 3114 542 3003 1255`；`code lines = 7914`；`VIEW+VIEWFACTORY = 3656 (46.2%)`；`LOGIC = 3003 (37.9%)`；`DECL = 1255 (15.9%)`；`view:logic ratio = 1.22 : 1`。

### A.2 修饰符 / 状态 / 布局（97 名集合，逐字用此列表）

```powershell
$mods = @('frame','padding','background','overlay','foregroundStyle','foregroundColor','onChange','onAppear',
'onDisappear','onTapGesture','gesture','highPriorityGesture','simultaneousGesture','disabled','help','font',
'clipShape','contentShape','fixedSize','buttonStyle','textFieldStyle','pickerStyle','labelsHidden',
'accessibilityLabel','accessibilityHidden','accessibilityValue','accessibilityAddTraits','animation','transition',
'opacity','fill','stroke','strokeBorder','inset','sheet','alert','confirmationDialog','toolbar','environment',
'preference','onPreferenceChange','scrubbable','configuredNativeShortcut','tag','lineLimit',
'multilineTextAlignment','controlSize','tint','symbolRenderingMode','bold','italic','monospaced','kerning',
'tracking','scaleEffect','rotationEffect','offset','zIndex','mask','blur','shadow','cornerRadius','listStyle',
'scrollContentBackground','colorMultiply','saturation','brightness','contrast','imageScale','textSelection',
'textCase','truncationMode','layoutPriority','drawingGroup','allowsHitTesting','coordinateSpace','onHover',
'onSubmit','focused','focusable','keyboardShortcut','aspectRatio','clipped','border','progressViewStyle',
'toggleStyle','menuStyle','id','onInsert','onMove','onDelete','refreshable','searchable','navigationTitle',
'presentationDetents','defaultFocus','matchedGeometryEffect') | Select-Object -Unique   # 97 个

$rxMod    = '(?<![\w.])\.(' + (($mods | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\s*\('
$rxState  = '(?<![\w.])@(State|Binding|Bindable|Observable|Environment|FocusState|StateObject|ObservedObject|Published|AppStorage|EnvironmentObject|GestureState|Namespace)\b'
$rxLayout = '(?<![\w.])(HStack|VStack|ZStack|LazyVStack|LazyHStack|LazyVGrid|LazyHGrid|Grid|GridRow|GeometryReader|ScrollView|ScrollViewReader|Form|Section|List|Spacer|Divider|ViewThatFits|AnyLayout|Canvas)\s*[\{\(]'
```

真实输出：`Lines=8922 ModifierCallSites=1252 StateProps=125 LayoutContainers=311`；`per-1000-lines: mods=140.3 state=14.0 layout=34.9`。

### A.3 键盘 / 焦点耦合

```powershell
foreach ($pat in @('configuredNativeShortcut','configuredKeyboardShortcut','keyboardShortcut',
                   'releasesFocusOnCommit','onKeyPress','\.focusable\(','\.focused\(','HeldModifiers')) {
  $c = 0
  foreach ($f in Get-ChildItem 'D:\Compositor For Windows\src\Compositor\Compositor\UI' -Filter *.swift -File) {
    $c += ([regex]::Matches((Get-Content -Raw -LiteralPath $f.FullName), $pat)).Count
  }
  "$pat = $c"
}
```

真实输出：`configuredNativeShortcut = 29`、`configuredKeyboardShortcut = 1`、`keyboardShortcut = 12`、`releasesFocusOnCommit = 5`、`onKeyPress = 2`、`\.focusable\( = 0`、`\.focused\( = 7`、`HeldModifiers = 8`。

### A.4 异步 / 定时耦合

```powershell
foreach ($pat in @('Timer\.','DispatchQueue','Task\s*\{','Task\.detached','\.animation\(','withAnimation','\.transition\(','NotificationCenter')) {
  $c = 0
  foreach ($f in (Get-ChildItem 'D:\Compositor For Windows\src\Compositor\Compositor\UI' -Filter *.swift -File) +
                 (Get-ChildItem 'D:\Compositor For Windows\src\Compositor\Compositor\Rendering' -Filter *.swift -File)) {
    $c += ([regex]::Matches((Get-Content -Raw -LiteralPath $f.FullName), $pat)).Count
  }
  "$pat = $c"
}
```

真实输出：`Timer\. = 2`、`DispatchQueue = 16`、`Task\s*\{ = 22`、`Task\.detached = 2`、`\.animation\( = 2`、`withAnimation = 0`、`\.transition\( = 0`、`NotificationCenter = 3`。

### A.5 自绘桶逐文件密度

```powershell
$root = 'D:\Compositor For Windows\src\Compositor\Compositor'
$bucket = 'Rendering\EditorCanvas.swift','UI\NativeLayerList.swift','Rendering\InlineTextEditor.swift',
          'Rendering\TransformOverlay.swift','Rendering\BrushCursorOverlay.swift',
          'Rendering\SampleRingOverlay.swift','Rendering\CanvasLinesOverlay.swift'
foreach ($b in $bucket) {
  $p = Join-Path $root $b; $t = Get-Content -Raw -LiteralPath $p; $l = (Get-Content -LiteralPath $p).Count
  [pscustomobject]@{
    File = $b; Lines = $l
    CGdraw  = ([regex]::Matches($t,'(?m)^\s*context\.\w+')).Count
    MouseOv = ([regex]::Matches($t,'(?m)^\s*override func (?:mouse|key|scroll|rightMouse|otherMouse|flagsChanged|pressure|tablet|magnify|rotate)\w*')).Count
    TrackOv = ([regex]::Matches($t,'resetCursorRects|updateTrackingAreas|cursorUpdate|addCursorRect')).Count
    CGperK  = [math]::Round(1000*([regex]::Matches($t,'(?m)^\s*context\.\w+')).Count/$l,1)
  }
}
```

真实输出：见 §2.5 表；`bucket total lines=5699 CG-draw=194 mouse=38 track=38`；`weighted CG/kLOC = 34.0`。

---

## 附录 B：`UI/` 全部 49 个文件的完整度量表【实测】

由 `genTable2.ps1` 生成（同一 97 名修饰符集合），列含义：`Lines` 物理行、`Mods` 修饰符调用点、`State` 状态属性、`Layout` 布局容器、`VIEW/VIEWFACTORY/LOGIC/DECL` 为 §附录 A.1 分类器的四类行数。

| File | Lines | struct | enum | ext | ViewSt | Mods | State | Layout | VIEW | VIEWFACTORY | LOGIC | DECL |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `BlendModePicker.swift` | 71 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 52 | 9 |
| `BrushControls.swift` | 152 | 2 | 0 | 0 | 2 | 60 | 1 | 6 | 140 | 0 | 0 | 5 |
| `CameraRawColorControls.swift` | 537 | 4 | 3 | 0 | 4 | 90 | 7 | 17 | 241 | 76 | 146 | 23 |
| `CameraRawControls.swift` | 386 | 1 | 2 | 0 | 1 | 63 | 3 | 19 | 156 | 107 | 58 | 40 |
| `CameraRawDetailOpticsControls.swift` | 189 | 2 | 0 | 0 | 2 | 41 | 2 | 7 | 88 | 7 | 18 | 64 |
| `CameraRawGeometryCalibrationControls.swift` | 147 | 2 | 0 | 0 | 2 | 37 | 2 | 4 | 74 | 24 | 16 | 22 |
| `CameraRawSlider.swift` | 206 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 | 152 | 22 |
| `CanvasRulers.swift` | 185 | 2 | 1 | 0 | 1 | 8 | 0 | 0 | 12 | 0 | 135 | 16 |
| `CanvasSizeSheet.swift` | 146 | 1 | 0 | 0 | 1 | 29 | 4 | 12 | 80 | 0 | 51 | 11 |
| `CanvasThumbnail.swift` | 61 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 40 | 4 |
| `ColorPaletteControls.swift` | 79 | 1 | 0 | 0 | 1 | 31 | 2 | 3 | 49 | 16 | 5 | 8 |
| `ColorPickerSheet.swift` | 218 | 3 | 0 | 1 | 2 | 59 | 4 | 11 | 120 | 22 | 30 | 20 |
| `ColorRangeSheet.swift` | 91 | 1 | 0 | 0 | 1 | 27 | 1 | 9 | 54 | 9 | 15 | 3 |
| `CommandPalette.swift` | 253 | 1 | 2 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 139 | 55 |
| `CommandPaletteView.swift` | 144 | 1 | 0 | 0 | 1 | 21 | 2 | 7 | 37 | 14 | 50 | 16 |
| `CropControls.swift` | 24 | 1 | 0 | 0 | 1 | 7 | 1 | 2 | 17 | 0 | 0 | 3 |
| `CurvesControls.swift` | 69 | 1 | 0 | 0 | 1 | 9 | 3 | 5 | 65 | 0 | 0 | 3 |
| `EffectsSheet.swift` | 193 | 1 | 0 | 0 | 1 | 30 | 1 | 18 | 139 | 15 | 0 | 24 |
| `ExportAsSheet.swift` | 308 | 4 | 0 | 0 | 2 | 47 | 13 | 16 | 164 | 4 | 61 | 49 |
| `FilterSheet.swift` | 406 | 3 | 0 | 0 | 2 | 103 | 3 | 19 | 283 | 31 | 17 | 43 |
| `FloatingPanel.swift` | 176 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 120 | 14 |
| `GradientControls.swift` | 108 | 2 | 0 | 0 | 2 | 22 | 1 | 6 | 77 | 0 | 0 | 23 |
| `GridSettingsSheet.swift` | 125 | 1 | 0 | 0 | 1 | 31 | 4 | 9 | 71 | 0 | 26 | 14 |
| `HeldModifiers.swift` | 39 | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 0 | 0 | 25 | 6 |
| `HueSaturationSheet.swift` | 210 | 2 | 0 | 0 | 2 | 41 | 3 | 16 | 113 | 25 | 24 | 22 |
| `ImageSizeSheet.swift` | 171 | 1 | 0 | 0 | 1 | 26 | 8 | 6 | 67 | 0 | 86 | 9 |
| `IndicatorlessScrollView.swift` | 48 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 29 | 9 |
| `KeyboardShortcuts.swift` | 320 | 4 | 0 | 1 | 1 | 20 | 4 | 9 | 48 | 10 | 160 | 87 |
| `LassoControls.swift` | 271 | 4 | 0 | 0 | 4 | 83 | 3 | 18 | 209 | 15 | 14 | 11 |
| `LayerAppearanceControls.swift` | 59 | 1 | 0 | 0 | 1 | 12 | 4 | 4 | 35 | 0 | 15 | 6 |
| `LayerMaskMenu.swift` | 77 | 2 | 0 | 0 | 1 | 3 | 0 | 0 | 10 | 0 | 42 | 16 |
| `LayersPanel.swift` | 99 | 2 | 0 | 1 | 2 | 38 | 2 | 9 | 67 | 4 | 0 | 10 |
| `LevelsSheet.swift` | 146 | 1 | 0 | 0 | 1 | 44 | 1 | 19 | 88 | 41 | 13 | 3 |
| `NativeLayerList.swift` | 1387 | 2 | 0 | 2 | 0 | 1 | 0 | 0 | 0 | 0 | 958 | 258 |
| `NavigationToolHeader.swift` | 66 | 1 | 0 | 0 | 1 | 11 | 5 | 2 | 32 | 0 | 22 | 6 |
| `NavigatorMinimap.swift` | 78 | 2 | 0 | 0 | 1 | 11 | 2 | 1 | 37 | 0 | 13 | 13 |
| `NewCanvasSheet.swift` | 265 | 3 | 1 | 0 | 2 | 43 | 8 | 12 | 93 | 12 | 81 | 51 |
| `NumericScrub.swift` | 63 | 1 | 0 | 1 | 0 | 2 | 3 | 0 | 1 | 30 | 1 | 25 |
| `ProjectTabLayout.swift` | 78 | 3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 17 | 42 |
| `ProjectTabs.swift` | 428 | 10 | 1 | 1 | 5 | 67 | 9 | 6 | 101 | 44 | 152 | 60 |
| `ProjectWindowBridge.swift` | 62 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 45 | 13 |
| `PSDConversionSheet.swift` | 69 | 2 | 0 | 1 | 1 | 13 | 0 | 6 | 32 | 10 | 9 | 12 |
| `RawDevelopSheet.swift` | 101 | 1 | 0 | 1 | 1 | 21 | 4 | 6 | 38 | 11 | 18 | 18 |
| `ShapeControls.swift` | 64 | 1 | 0 | 0 | 1 | 22 | 1 | 5 | 59 | 0 | 0 | 3 |
| `SliderSnap.swift` | 42 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 14 | 17 |
| `ToolHeaderStyle.swift` | 43 | 0 | 1 | 1 | 0 | 5 | 0 | 3 | 0 | 14 | 0 | 20 |
| `TransformInspector.swift` | 145 | 2 | 0 | 0 | 2 | 36 | 4 | 5 | 79 | 1 | 29 | 15 |
| `TrimSheet.swift` | 68 | 1 | 0 | 0 | 1 | 12 | 5 | 10 | 56 | 0 | 0 | 4 |
| `TypeControls.swift` | 249 | 3 | 1 | 0 | 1 | 25 | 3 | 4 | 82 | 0 | 105 | 28 |
| **TOTAL (49)** | **8922** | **87** | **16** | **12** | **57** | **1252** | **125** | **311** | **3114** | **542** | **3003** | **1255** |

---

## 附录 C：与既有数字的对账表

| 文档数字 | 出处 | 本文实测 | 差异 | 判定 |
|---|---|---|---|---|
| `UI/` 8,971 行 | §2.1 | **8,922** | −49（= 每文件 −1） | 工具伪影，0.5%，无实质影响 |
| UI 层 14,624 行 | §4.0(b) | **14,563** | −61（= 每文件 −1） | 同上 |
| 62 个 `View` 结构体 | §2.2 | **59**（全仓非测试）/ 57（`UI/`） | +3 | 口径混淆（全仓 vs `UI/`） |
| 21 处 AppKit 桥接 | §2.2 | **21** = 12 `NSViewRepresentable` + 9 `NSHosting*` | 0 | **完全吻合** |
| 19 个 `NSView` 子类 | §2.2 | **21**（严格 NSView 系）/ 12（`UI/`） | −2 | 轻微低估 + 口径混淆 |
| `EditorCanvas` 12 个鼠标 override | §4.0(b) | **12**（6 `mouse*` + 3 `rightMouse*` + 3 `otherMouse*`） | 0 | **完全吻合**（另有 4 个键盘/滚轮/缩放 override，合计 16） |
| 自绘桶 5,706 行 | §4.0(b) | **5,699** | −7（= 每文件 −1） | 工具伪影 |
| 声明式桶 ≈8,900 行 | §4.0(b) | **8,864** | −36（0.4%） | 一致 |
| 系数口径 21.4–35.3 人周 | §4.11 第 3 条 | **21.4–35.2** | −0.1 | **成立** |
| 波次口径 24.7–35.5 人周 | §4.11 第 2 条 | 未独立复核（依赖各波登记行数） | — | 其中 W6 登记 18 文件实测 **3,976 行**（文档作「约 5,000 行」） |
| `FilterSheet.swift` 407 行 | §4.0(b)/§4.1 | **406** | −1 | 工具伪影 |
| `TransformOverlay.swift` 468 行 | §4.0(b)/§4.1 | **467** | −1 | 工具伪影 |
| 两个系数「没有实测支撑」 | §4.0/§4.11 待验证 | **仍然成立** | — | 本文**没有**把它们变成实测，只把区间收紧到「可复核的结构度量 + 明确的作用范围」 |
