# Compositor for Windows

把 [robbietilton/Compositor](https://github.com/robbietilton/Compositor)（macOS 原生图像编辑器，Swift + SwiftUI + AppKit，MIT）移植为 Windows 原生应用。

上游是单作者项目，要求 macOS 26 / Apple silicon，Xcode 26 构建。本仓库是**独立的 Windows 实现**，不是上游分支。

---

## 当前状态

**Wave 0（无 GUI 垂直切片）基本完成。** 除"与 macOS 逐像素比对"一项外，全部退出标准已达成。

| 组件 | 状态 |
|---|---|
| 移植方案（架构、工具链、测试基线、格式契约、路线图） | ✅ 完成，见 `docs/port/` |
| UI 转换系数标定（工期的唯一支柱） | ✅ 已复核（`06-ui-calibration.md`）：原区间得到支持，工期 66.3–102.7 人周 |
| C 像素内核在 Windows 上编译并被 C# 调用 | ✅ 已验证（9/9 编译、42 导出、10/10 数值校验） |
| `.comp` manifest 字节级往返（读 v1–v11，写 v11） | ✅ 已验证 |
| **`.comp` v1–v11 版本门槛校验** | ✅ 已完成（每个门槛双向测试） |
| **撤销/重做栈 + 包摘要（`ProjectDigest`）** | ✅ 已完成（Wave 1 的一部分） |
| **磁盘往返与外部变更检测** | ✅ 已完成（`ProjectPackage` 门面：打开→解码→合成→保存） |
| **`Surface` 抽象（`CGContext` 的替身）** | ✅ 已完成（Wave 2 起点：像素精确路径 + Skia 重采样路径） |
| **降采样器 + 灰度蒙版格式** | ✅ 已完成（精确 2× 减半链、分块对齐不变式、真灰度 PNG） |
| **蒙版贯通**（合成器 / 加载 / 保存均用单通道蒙版） | ✅ 已完成 |
| **分块网格算术（`TileGrid`）** | ✅ 已完成（无接缝保证的三条不变式各有测试） |
| **图层变换**（旋转 / 翻转 / 缩放，经重采样） | ✅ 已完成 |
| **调整层：12 种 kind 的枚举 + 5 种实现**（Invert / Black & White / Exposure / Levels / Curves） | ✅ 已完成 |
| **图层效果：6 种**（描边 / 投影 / 颜色叠加 / 内阴影 / 外发光 / 内发光） | ✅ 已完成 |
| PNG 编解码（RGBA/非预乘，含透明像素） | ✅ 已完成 |
| 24 种混合模式 + 图层合成 + PNG 导出 | ✅ 已完成 |
| 测试套件 | ✅ **407 项全绿**（0 失败 0 跳过） |

> **下一步与缺口**：见 `docs/port/07-handover.md`——现状、三条硬阻塞、已刻意留下的边界、环境笔记与建议顺序。
| 与 macOS 逐像素比对 | 🟡 **待 macOS 黄金样本**（见下） |
| GUI（WinUI 3 / Avalonia） | ⬜ Wave 3+ |

**关键未决项**：**浮点 parity 与渲染一致性都需要一份 macOS 真实产出的黄金样本**——一个 `.comp`
工程 + 它导出的 PNG。没有它，以下三条无法关闭：

1. 11 种走 Core Image 的混合模式（Color Burn/Dodge、Soft Light、Linear/Vivid/Pin Light、
   Hard Mix、Subtract、Divide）在边界情形是否与规范逐位一致。
2. 取整策略：本移植用 `Math.Round`（银行家舍入），Core Graphics 的取整行为未经验证，
   这会在恰好 `.5` 的像素上产生 ±1 的差。
3. 浮点 parity：GCC/clang 与 Apple clang 在 677 处 `float` 上的差异。

详见 `docs/port/wave0-contracts.md` §5。

---

## 快速开始

### 前置条件

```powershell
# 1. 本机必须设置 DOTNET_ROOT，否则任何 dotnet 命令都会报 hostpolicy.dll not found
#    根因：注册表 HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\install_location 为空
[Environment]::SetEnvironmentVariable('DOTNET_ROOT', 'C:\Program Files\dotnet', 'User')

# 2. .NET SDK 8（本机已有运行时不够用：用 SDK 7 构建 net8.0 会报 NETSDK1045）
winget install --exact --id Microsoft.DotNet.SDK.8
```

仓库根的 `nuget.config` **不要删**：本机机器级 NuGet 配置指向不存在的路径，会让任何 `dotnet build` 报 `MSB4018`。详见 `docs/port/02-toolchain.md` §1.1。

### 构建与测试

```powershell
dotnet build Compositor.sln -c Release
dotnet test  Compositor.sln -c Release
```

### 复现 C 内核可行性的原始证据

```powershell
cd spikes\c-kernel
powershell -ExecutionPolicy Bypass -File .\run-spike.ps1
# 期望结尾：SPIKE RESULT: ALL CHECKS PASSED
```

---

## 目录

| 路径 | 内容 |
|---|---|
| `docs/port/01-architecture.md` | 框架映射矩阵、可移植性评级、C# 工程布局、Wave 0–9 计划、风险 R1–R11 |
| `docs/port/02-toolchain.md` | 工具链缺口与安装、C 内核链接论证、出网与编译实测 |
| `docs/port/03-test-harness.md` | 81 个测试文件逐个分类、行为契约清单、CI 分片策略 |
| `docs/port/04-comp-format.md` | `.comp` v1–v11 兼容矩阵、全量 schema、校验规则 |
| `docs/port/05-roadmap.md` | **主路线图**（冲突以它为准）：结论、坑清单、编译器裁决、工期 |
| `docs/port/wave0-contracts.md` | Wave 0 冻结契约：环境事实、格式细节、C# 接口 |
| `src/Compositor.Core/` | 平台无关内核：`.comp` 读写、图层模型、限制。**禁止引用 Windows API** |
| `src/Compositor.Pixels/` | C 内核的托管绑定。**禁止引用 Windows API** |
| `src/Compositor.Codecs/` | PNG 编解码（SkiaSharp） |
| `native/compositor_pixels/` | C 像素内核源码与构建脚本 |
| `tests/Compositor.Tests/` | xUnit：格式契约 + C 内核 parity |
| `spikes/c-kernel/` | 已验证的可行性证据（可一键复现） |
| `src/Compositor/` | **上游 macOS 源码，只读参考，不属于本仓库**（见 `.gitignore`） |

---

## 三条不可协商的纪律

1. **`Compositor.Core` 与 `Compositor.Pixels` 禁止引用任何 Windows API**，且 CI 在 `ubuntu-latest` 上构建并测试它们。这是防止平台耦合扩散的唯一机制。
2. **`.comp` 契约：读 v1–v11，写 v11。** 禁止写入上游不认识的版本号或字段——宁可丢弃新字段，也不要让 macOS 侧拒绝打开。
3. **混合必须在 sRGB 空间做，不能在 linear 空间。** 上游用注释记录过真实数值：40% 灰之上，80% 灰的 Color Dodge 在线性空间得 62%，Photoshop 得 100%。这类错误肉眼难辨但数值全错，只有 golden 比对能抓到。

---

## 许可

上游为 MIT（见 `src/Compositor/LICENSE`）。本移植沿用同一许可。
