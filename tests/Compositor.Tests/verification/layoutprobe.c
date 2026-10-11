/* Struct-layout probe for the managed P/Invoke bindings.
 *
 * This is a build-time verification tool, not part of the test suite: it prints the real
 * sizeof/offsetof of the interop structs straight from the authoritative C headers, so the
 * [StructLayout] structs in src/Compositor.Pixels can be checked against the compiler rather than
 * against someone's reading of the header. Its output is what the constants in
 * tests/Compositor.Tests/AbiSurfaceTests.cs are pinned to.
 *
 * Run it with the gcc on this machine:
 *
 *   gcc -std=c11 -I ../../../spikes/c-kernel/csrc -o layoutprobe.exe layoutprobe.c && ./layoutprobe.exe
 *
 * Verified output (gcc 10.3.0 / tdm64, 2026-10-11):
 *
 *   CameraRawStage size=96 align=8
 *     table  off=0  size=64
 *     weight off=64 size=32
 *   DitherParams size=80 align=8
 *     style=0 levels=4 diffusion=8 density=12 contrast=16 cell=20 angle=24
 *     lightOnDark=28 originalColors=32 dark=36 light=39
 *     glyphWidth=44 glyphHeight=48 glyphs=56 glyphCoverage=64 glyphCount=72
 *   ScanlinesParams size=56 align=4
 *     spacing=0 thickness=4 dots=8 wobble=12 displace=16 threshold=20 split=24
 *     density=28 contrast=32 blackLevel=36 smoothness=40 originalColors=44 dark=48 light=51
 *   long=4 int64_t=8 size_t=8 ptr=8
 *
 * The last line is the important one for this port: `long` is 4 bytes on Windows even in 64-bit
 * code, while size_t/int64_t/pointers are 8. heal_coverage_bounds takes `long bounds[4]`, so its
 * managed binding must marshal a 4-byte int, not a C# long.
 */
#include <stdio.h>
#include <stddef.h>
#include "DitherPixels.h"
#include "AdjustPixels.h"

int main(void) {
    printf("CameraRawStage size=%zu align=%zu\n", sizeof(CameraRawStage), _Alignof(CameraRawStage));
    printf("  table  off=%zu size=%zu\n", offsetof(CameraRawStage, table), sizeof(((CameraRawStage *)0)->table));
    printf("  weight off=%zu size=%zu\n", offsetof(CameraRawStage, weight), sizeof(((CameraRawStage *)0)->weight));

    printf("DitherParams size=%zu align=%zu\n", sizeof(DitherParams), _Alignof(DitherParams));
    printf("  style=%zu levels=%zu diffusion=%zu density=%zu contrast=%zu cell=%zu angle=%zu\n",
           offsetof(DitherParams, style), offsetof(DitherParams, levels), offsetof(DitherParams, diffusion),
           offsetof(DitherParams, density), offsetof(DitherParams, contrast), offsetof(DitherParams, cell),
           offsetof(DitherParams, angle));
    printf("  lightOnDark=%zu originalColors=%zu dark=%zu light=%zu\n",
           offsetof(DitherParams, lightOnDark), offsetof(DitherParams, originalColors),
           offsetof(DitherParams, dark), offsetof(DitherParams, light));
    printf("  glyphWidth=%zu glyphHeight=%zu glyphs=%zu glyphCoverage=%zu glyphCount=%zu\n",
           offsetof(DitherParams, glyphWidth), offsetof(DitherParams, glyphHeight),
           offsetof(DitherParams, glyphs), offsetof(DitherParams, glyphCoverage),
           offsetof(DitherParams, glyphCount));

    printf("ScanlinesParams size=%zu align=%zu\n", sizeof(ScanlinesParams), _Alignof(ScanlinesParams));
    printf("  spacing=%zu thickness=%zu dots=%zu wobble=%zu displace=%zu threshold=%zu split=%zu\n",
           offsetof(ScanlinesParams, spacing), offsetof(ScanlinesParams, thickness),
           offsetof(ScanlinesParams, dots), offsetof(ScanlinesParams, wobble),
           offsetof(ScanlinesParams, displace), offsetof(ScanlinesParams, threshold),
           offsetof(ScanlinesParams, split));
    printf("  density=%zu contrast=%zu blackLevel=%zu smoothness=%zu originalColors=%zu dark=%zu light=%zu\n",
           offsetof(ScanlinesParams, density), offsetof(ScanlinesParams, contrast),
           offsetof(ScanlinesParams, blackLevel), offsetof(ScanlinesParams, smoothness),
           offsetof(ScanlinesParams, originalColors), offsetof(ScanlinesParams, dark),
           offsetof(ScanlinesParams, light));

    printf("long=%zu int64_t=%zu size_t=%zu ptr=%zu\n",
           sizeof(long), sizeof(int64_t), sizeof(size_t), sizeof(void *));
    return 0;
}
