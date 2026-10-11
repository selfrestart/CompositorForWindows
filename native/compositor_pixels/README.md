# native/compositor_pixels — the Compositor C pixel kernels, portable

The nine C pixel kernels of `src/Compositor/Compositor/Rendering/` built into a dynamic
library that C# can P/Invoke. Upstream's kernels are plain C except for Apple's
`<dispatch/dispatch.h>` and Clang *blocks*; this directory removes that dependency with
**standard C only** (no Clang blocks, no GCC nested functions, no statement expressions),
so MSVC and GCC both compile it.

* Artifact (Windows): `build/bin/compositor_pixels.dll`
* Artifact (Linux):   `build/bin/libcompositor_pixels.so`
* Exported entry points: exactly **42**, the public surface of the nine headers, unchanged
  from `AdjustPixels.h:8-119` and friends (`docs/port/wave0-contracts.md` §3.1).

```powershell
# build + verify everything (Windows PowerShell 5.1 or pwsh)
powershell -NoProfile -ExecutionPolicy Bypass -File native/compositor_pixels/run-tests.ps1

# just the DLL
pwsh -File native/compositor_pixels/build.ps1            # -Toolchain msvc, -OpenMp, -Clean
```

```bash
# CI (ubuntu-latest)
native/compositor_pixels/build.sh                        # --openmp, --clean
```

---

## The port in one screen

Upstream has 26 block literals in two files. **22 of them merely feed `in_bands()`, a 7-line
helper that is byte-for-byte identical in both files**; the other four are
`dispatch_apply` (two of those being the helper's own body). Nothing else in the two files is
platform-specific, and the other seven files compile verbatim.

`tools/port_blocks.py` does the port mechanically from the read-only upstream sources, with an
expected occurrence count for every replacement, and writes `csrc/`:

| # | Change | Sites | Why |
|---|---|---|---|
| 1 | `#include <dispatch/dispatch.h>` → `#include "dispatch_shim.h"` | 2 | Apple header |
| 2 | `in_bands` definition → a macro in `dispatch_shim.h` | 2 | C has no closures; the macro expands the same loop at each call site, so the call sites' bodies stay put |
| 3 | `in_bands(count, ^(size_t first, size_t last) {` → `in_bands(count, first, last, {` | 22 | block literal → macro argument |
| 4 | `dispatch_apply(bands, DISPATCH_APPLY_AUTO, ^(size_t band) {` → `…, band, {` | 2 | ditto |
| 5 | `__block int failed = 0;` → `int failed = 0;` | 3 | Clang `__block` qualifier |
| 6 | `if (!copy) { failed = 1; return; }` → `… continue; }` | 3 | a block's `return` abandoned **one band**; `continue` does exactly that in the expanded loop |

Diff against upstream, enforced by `tools/diff_vs_upstream.py` (it fails on any changed line
that does not decompose into the table above):

```
AdjustPixels.c  (1380 lines upstream)   -24 +20 = 44 changed lines (3.2%), 1356 untouched (98.3%)
DitherPixels.c  ( 547 lines upstream)   -24 +20 = 44 changed lines (8.0%),  523 untouched (95.6%)
16/16 other files SHA256-identical to upstream
```

**No arithmetic line changed.** The band partition in the macro
(`bands = count < 64 ? 1 : 32`, `size = (count + bands - 1) / bands`) is upstream's expression
character for character, and `first`/`last` are declared in the innermost scope, so they shadow
what the block parameters shadowed. `tools/shim_selftest.c` checks the partition over every count
from 0 to 5000 for both forms (`in_bands` and `compositor_bands_apply`), plus the
`continue`-abandons-one-band semantics.

---

## Layout

| Path | What it is |
|---|---|
| `csrc/` | the nine `.c` + nine `.h` kernels; 16 files byte-identical to upstream, 2 ported (see above) |
| `dispatch_shim.h` | standard-C stand-in for `<dispatch/dispatch.h>`: `DISPATCH_APPLY_AUTO`, the `dispatch_apply` and `in_bands` macros, and the `compositor_bands_apply(count, body, ctx)` callback form |
| `build.ps1` | Windows build: `gcc` (verified) or `msvc` (written, unverifiable here — no MSVC on this machine); `-OpenMp`; verifies the linked PE exports 42 symbols |
| `build.sh` | Linux build for CI: `libcompositor_pixels.so`, verifies the 42 exported `T` symbols with `nm` |
| `run-tests.ps1` | every acceptance check, in order, one command |
| `probe/` | the ten numeric checks, ported from `spikes/c-kernel/KernelProbe` (only the library name and the DLL locator changed) |
| `tools/port_blocks.py` | the port itself, re-runnable against upstream, with count assertions |
| `tools/scan_blocks.py` | the analysis that decided the port: per call site, its captures, block `return`s and `__block` uses |
| `tools/diff_vs_upstream.py` | proves the diff is confined to the seven change kinds above |
| `tools/shim_selftest.c` | band partition + control-flow self-test (compiled with and without `-fopenmp`) |
| `tools/parity_driver.c` | calls every entry point that uses a band loop and dumps all outputs |
| `tools/run_parity.ps1` | builds that driver against both kernels and compares byte for byte |
| `tools/compare_parity.py` | names the sections that differ, with a tolerance |
| `tools/run_openmp_parity.ps1` | the parallel build: compile, link, and how it compares |
| `tools/run_mutation.ps1` | control: a deliberately wrong kernel must make the comparison fail |
| `tools/math_thread_safety.c` | diagnostic used while investigating the parallel finding |
| `logs/` | build logs, export tables, SHA256 records, test output |

---

## Verification (what is actually proven)

`run-tests.ps1` runs all of this and reports `RESULT: ALL STEPS PASSED`.

1. **`csrc/` is reproducible**: `port_blocks.py --check` re-derives it from upstream.
2. **The diff is band plumbing only** (numbers above).
3. **`build.ps1` → exit 0, 42 exports.** Verified against the *linked* PE export table
   (`objdump -p`, logged to `logs/exports.txt`), not against the `.def` we asked for:
   ```
   === [3/3] verify the DLL export table ===
     DLL exports 42 symbols
     export set matches the 42 public entry points exactly
   ```
   The objects also carry exactly 42 non-static symbols, so the Linux `.so` exports the same set
   (`build.sh` asserts that with `nm -D`).
   The Windows DLL is **bit-reproducible**: the link passes `-Wl,--no-insert-timestamp`, so two
   builds of the same sources produce the same SHA256
   (`B9D78FA6F22D438B15A03161006AD537C68F7C88947D28BB62A871BEDB44A2C3` at the time of writing).
   Without it the PE header's Time/Date field was the only difference between builds, which made
   the hash useless as a fingerprint. Windows links only 42 symbols into the PE export table, and
   the DLL depends on nothing but `KERNEL32.dll` and `msvcrt.dll` (`-static-libgcc`).
4. **10/10 numeric checks** (`probe/`), identical names, references and thresholds as the spike:
   `==== 10 passed, 0 failed ====`, including `camera_raw_statistics` at `max |diff| = 3.109E-015`
   and `dither_quantize16` matching an independent C# reference byte for byte (12600 bytes).
5. **Bit-for-bit parity with the spike build** over the whole band-loop surface:
   **357 sections, 11,227,148 bytes, one SHA256** —
   `47BB8E7CD29124A69ED8525673D18FB35F0A2A7F1EC06707563B03D229171258` for both
   `compositor_kernel.dll` (spike: Clang-blocks → GCC nested functions) and
   `compositor_pixels.dll` (this port). The driver covers all 15 entry points that reach a band
   loop, across seven geometries that straddle the partition (fewer than 64 rows, exactly 64,
   90 rows not divisible by 32, a padded stride, a 33-column image, 200×300), plus controls from
   the seven files that needed no port. The kernel's own 16-bit/8-bit paths, the dither styles,
   scanlines' early-return band bodies and the composed-table lattice loops are all included.
6. **A cross-compiler check**: the same sources built with GCC 8.1.0 (MinGW-W64) also match the
   spike bit for bit, so the result is not an artefact of one compiler.
7. **The mutation control**: changing one constant (`h += tint * 0.06` → `0.07`) in a scratch copy
   makes step 5 fail, and names the mutated function —
   `RESULT: PASS -- a one-constant change was detected, at the mutated function`. A green parity
   run therefore means something.

### The parallel build (`-OpenMp`)

The default is **serial**, and serial is what every result above uses. `-OpenMp` adds
`#pragma omp parallel for` to the band loops instead of the plain loop (and widens the loop index
to `long long`, which OpenMP 2.0 requires). What is verified:

* `-fopenmp` compiles all nine sources with the default GCC 10.3.0 (`9/9`).
* On a compiler that ships libgomp (GCC 8.1.0, `D:\GNU-gcc\mingw64`), the OpenMP build links and
  runs, and the partition self-test passes **under OpenMP** (`COMPOSITOR_BANDS_PARALLEL = 1`).
* With `OMP_NUM_THREADS=1` the parallel build is **byte-identical** to the serial build and to the
  spike — so the OpenMP lowering itself is faithful.
* With several threads it is **not** byte-identical, and this is not a port artefact:

  | threads | result |
  |---|---|
  | 1 | identical to serial and to the spike |
  | 2, 4, 8 | 1–2 of the 357 sections differ, by **one byte each**, and the value depends on the thread count *and* varies from run to run |

  Two contributing causes were isolated:

  * **Confirmed and actionable:** `DitherPixels.c`'s `draw_lines` reads uninitialized memory.
    `cover` is `malloc(height * sizeof(float) * 4)` but only the first `height` floats are
    zeroed; `color[y * 3]` (the plane after it) is read in
    `screen[0] + (color[y*3] * 1.35f - screen[0]) * shown` even when `shown == 0`. The product is
    zero for an ordinary finite garbage value, but not for a NaN/Inf bit pattern. Zeroing the
    whole buffer in a scratch build removes every `scanlines_apply` difference — the serial build
    is deterministic only because its heap history is. **This is an upstream bug, worth reporting
    regardless of parallelism.**
  * **Not explained:** `camera_raw_statistics` still differs by about 1 ULP in one of its three
    doubles. Its bands write disjoint memory, the reduction afterwards is serial and in band
    order, and the following were tested and **falsified** as causes: the FP environment
    (`MXCSR = 0x1f80`, no FTZ/DAZ, identical on master and workers) and C-runtime math
    thread-reproducibility (`tools/math_thread_safety.c`: `log`, `exp`, `pow`, `sqrt` return
    identical bits in worker threads). Until that is understood, do not treat `-OpenMp` as
    pixel-exact.

  The claim in `spikes/c-kernel/README.md` that serial and parallel are bit-identical holds for
  the *partition* (disjoint ranges, no cross-band reduction, which the self-test verifies) but not
  for these kernels as compiled here.

---

## This machine's traps (all of them cost time, none are about the port)

1. `DOTNET_ROOT` must be `C:\Program Files\dotnet` (it is; `run-tests.ps1` sets it if missing) —
   `docs/port/wave0-contracts.md` §0.
2. The repository-root `nuget.config` is required for `dotnet build`; the probe inherits it, so it
   needs no copy of its own (`The compiler`/NuGet failure is `MSB4018` without it).
3. `HealPixels.c:168` uses `M_PI`, so the GCC build is `-std=gnu11`; MSVC needs
   `/D_USE_MATH_DEFINES`.
4. `pwsh` is **not installed** on this machine (only Windows PowerShell 5.1). The scripts use
   only PS 5.1-compatible constructs and are launched through whichever host exists, so
   `pwsh -File …` works wherever PowerShell 7 is present; here they were run with
   `powershell -NoProfile -File …`.
5. **TDM-GCC 10.3.0 ships no libgomp at all** (no `libgomp.spec`, no `libgomp.a` anywhere under
   `D:\tdm-gcc`), so it can compile `-fopenmp` but cannot link it. That is why the parallel
   verification uses `D:\GNU-gcc\mingw64\bin\gcc.exe` (GCC 8.1.0).
6. Windows PowerShell's `>` redirection writes UTF-16, so the parity logs are written with
   `Out-File -Encoding utf8` and the Python helpers sniff the encoding anyway.

---

## Syncing with upstream

Upstream stays read-only. To re-port after an upstream change:

```powershell
python native/compositor_pixels/tools/port_blocks.py           # regenerates csrc/ (counts asserted)
python native/compositor_pixels/tools/port_blocks.py --check    # or verify without writing
python native/compositor_pixels/tools/diff_vs_upstream.py       # re-check the diff is still plumbing only
```

If an upstream edit moves or rewords one of the seven patterns, the port script fails loudly with
the text it could not find, rather than producing a half-ported file. The provenance of all 18
files (SHA256, upstream vs this copy vs the spike's copy) is in `logs/csrc-hashes.txt`.
