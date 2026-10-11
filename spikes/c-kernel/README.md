# spikes/c-kernel — 证明 Compositor 的 C 像素内核可以在 Windows 上编译并被 C# 调用

这个 spike 回答一个问题，而且只用**可运行的证据**回答：

> `src/Compositor/Compositor/Rendering/` 下的 9 个 `.c` 像素内核，
> 能不能在 Windows 上编译成本地库，从 C# 通过 P/Invoke 调用，并且**输出与 macOS 原版一致**？

**答案：能。** 9/9 编译成功，42 个入口点导出，10/10 数值校验通过。

---

## 快速开始

```powershell
cd "D:\Compositor For Windows\spikes\c-kernel"
powershell -ExecutionPolicy Bypass -File .\run-spike.ps1
```

期望结尾：

```text
==== 10 passed, 0 failed ====
SPIKE RESULT: ALL CHECKS PASSED
```

### 本机依赖（都已验证存在）

| 工具 | 路径 | 备注 |
|---|---|---|
| gcc | `D:\tdm-gcc\bin\gcc.exe` | tdm64-1 / GCC 10.3.0 / x86_64-w64-mingw32 |
| .NET SDK | `C:\Program Files\dotnet\sdk\7.0.304` | net7.0 |
| python | `D:\Python13\python.exe` | 跑两个变换/校验脚本 |

> **注意**：`KernelProbe/nuget.config` 是**必需的**，见下面"本机的坑"。

---

## 目录

| 文件 | 作用 |
|---|---|
| `csrc/` | `src/.../Rendering/` 下 9 个 `.c` + 9 个 `.h` 的**逐字节副本**（SHA256 已比对） |
| `shim/dispatch/dispatch.h` | `dispatch_apply` + `DISPATCH_APPLY_AUTO` 的最小 Windows shim（**串行**） |
| `port-clang-blocks.py` | 把 2 个文件里的 26 处 Clang blocks 机械改写成纯 C |
| `verify-transform-roundtrip.py` | 逆向还原，证明像素算术一字未改 |
| `build-kernel.ps1` | 7 个文件原样编译 + 2 个用变换产物 → `compositor_kernel.dll` |
| `KernelProbe/` | C# P/Invoke 校验工程（10 项断言） |
| `run-spike.ps1` | 一键串联以上全部 |
| `_net/` | 出网与 TFM 探针的现场证据（git clone / NuGet 包还原 / pip / `NETSDK1045` / WindowsAppSDK 还原）。**不是** spike 运行所需，可整个删除 |

> 注意：`csrc/` 是 `src/` 的副本，**spike 不依赖 `src/` 被修改**，也不依赖 `_net/`。

---

## 为什么要"变换"这 2 个文件

内核里 **7 个文件是纯 C**，零改动即可编译。
只有 2 个文件用了 Apple 的 Clang `blocks` 扩展来喂并行循环：

| 文件 | block 字面量 | block 形参声明 | `__block` | 直接 `dispatch_apply` |
|---|---|---|---|---|
| `AdjustPixels.c` | 16 | 1 | 0 | 3 |
| `DitherPixels.c` | 10 | 1 | 3 | 1 |

`^` 这种闭包字面量 **MSVC 和 GCC 都不支持**，所以必须处理。26 处里有 22 处是喂给同一个
`in_bands()` 辅助函数的（`AdjustPixels.c:9`、`DitherPixels.c:10`）。

`port-clang-blocks.py` 做三件**纯语法**的事：

1. block 字面量 `^(params) { BODY }` → `({ void __blkN(params) { BODY } __blkN; })`
   （GCC 嵌套函数放在语句表达式里，**保留词法捕获语义**）
2. block 形参声明 `void (^name)(A,B)` → `void (*name)(A,B)`
3. 去掉 `__block` 限定符（嵌套函数直接访问外层栈帧，不需要它）

**一句算术都没动。** 这一点由 `verify-transform-roundtrip.py` 证明：它把生成文件逆向还原成
block 写法，再与 `src/` 里的原文**逐字节比较**：

```text
AdjustPixels.c     16 block literals + 1 block declarations inverted -> BYTE-IDENTICAL to original
DitherPixels.c     10 block literals + 1 block declarations inverted -> BYTE-IDENTICAL to original

ROUND TRIP: PASS -- pixel math unchanged
```

---

## 为什么串行 shim 是"正确"的而不只是"能跑"

`in_bands()` 把行区间**划分**给各个 band：band *k* 独占 `[k*size, (k+1)*size)`，
区间互不重叠、合起来完整覆盖 `[0, count)`。每个 band 只写自己那几行，band 之间没有共享可写状态。
所以**串行执行与并行执行的输出逐位相同**。

这不是口头论证，有两项实测断言直接验证它：

* `dither_quantize16` 走 `DitherPixels.c` 改写后的 band 循环 → 与 C# 独立参考实现
  （含逐位复刻的 `hash_noise`）**12600 字节逐字节相同**。
* `camera_raw_statistics` 走 `AdjustPixels.c` 改写后的 `dispatch_apply(32 bands)` →
  与单循环参考实现最大偏差 **3.1e-15**（双精度舍入级）。测试特意用 `h=90`（不能被 32 整除），
  以暴露 band 边界处的遗漏或重叠。

> **性能另说**：串行 shim 不恢复并行吞吐。要性能可换 OpenMP / 线程池 / swift-corelibs-libdispatch，
> 属于后续 Wave 的优化，不影响本次的正确性结论。

---

## 本机的坑（都是真实踩过的）

1. **`dotnet build` 会失败**，除非有 `KernelProbe/nuget.config`。
   机器级配置 `C:\Program Files (x86)\NuGet\Config\Microsoft.VisualStudio.FallbackLocation.config`
   指向不存在的 `D:\Dev C++\Microsoft VS\NuGetPackages`，导致：
   `error MSB4018: ... 无法找到回退包文件夹"D:\Dev C++\Microsoft VS\NuGetPackages"`。
   就近放一个带 `<fallbackPackageFolders><clear /></fallbackPackageFolders>` 的 `nuget.config` 即可，无需管理员权限。
2. **`HealPixels.c:168` 用了 `M_PI`**，严格 `-std=c11` 下会报
   `error: 'M_PI' undeclared`。脚本用 `-std=gnu11`。MSVC 下需要 `_USE_MATH_DEFINES`。
3. gcc 把 **warning 也写到 stderr**；PowerShell 在 `$ErrorActionPreference='Stop'` 下会把它
   当成终止性错误。脚本里已改为 `'Continue'` 并显式检查 `$LASTEXITCODE`。

---

## 独立性

* spike **只读** `src/`（复制一次 + SHA256 比对），之后全部在自己的目录里工作。
* **`src/` 下没有任何文件被修改。** 18 个 `.c`/`.h` 的 SHA256 记录在 `logs/src-rendering-hashes.txt`。
* 删除整个 `build/` 目录后重跑 `run-spike.ps1` 可以从零复现全部结果。
