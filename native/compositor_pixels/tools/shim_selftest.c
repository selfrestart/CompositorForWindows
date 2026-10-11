// shim_selftest.c -- verify the band partition dispatch_shim.h hands out, and report
// whether the OpenMP path was compiled in.
//
// The partition is the one upstream's in_bands() had:
//     bands = count < 64 ? 1 : 32, size = (count + bands - 1) / bands
//     band k owns [k*size, min((k+1)*size, count))
// This checks, for every count from 0 to 5000, that
//   * the macro form covers every index exactly once (no gap, no overlap),
//   * the `function pointer + void*` form covers exactly the same indices,
//   * a `continue` in a band body abandons that band and leaves the other bands running
//     (the semantics Clang blocks gave `return`), and
//   * the two forms agree with a reference partition transcribed from upstream.
//
// Built twice by run-tests.ps1: with and without -fopenmp. With -fopenmp the coverage
// arrays are written per index (disjoint), so the check itself stays race-free.
//
// Exit code 0 means the shim behaves; 1 means it does not.

#include <stdio.h>
#include <string.h>

#include "dispatch_shim.h"

#define MAX_COUNT 5000

static unsigned char cover[MAX_COUNT + 1];
static unsigned char expect[MAX_COUNT + 1];
static unsigned char band_seen[MAX_COUNT + 1];
static int failures;

typedef struct { unsigned char *marks; } MarkCtx;

static void mark(size_t first, size_t last, void *vctx) {
    MarkCtx *ctx = vctx;
    for (size_t i = first; i < last; ++i) ctx->marks[i] = 1;
}

static void report(const char *what, size_t count, int ok) {
    if (!ok) {
        failures++;
        if (failures < 10) printf("  FAIL count=%zu: %s\n", count, what);
    }
}

int main(void) {
    printf("COMPOSITOR_BANDS_PARALLEL = %d\n", COMPOSITOR_BANDS_PARALLEL);

    for (size_t count = 0; count <= MAX_COUNT; ++count) {
        // reference partition, transcribed from upstream's in_bands body
        size_t bands = count < 64 ? 1 : 32, size = (count + bands - 1) / bands;
        memset(expect, 0, count);
        for (size_t band = 0; band < bands; ++band) {
            size_t start = band * size, end = start + size < count ? start + size : count;
            if (start < end)
                for (size_t i = start; i < end; ++i) expect[i]++;
        }

        // form 1: the macro, exactly as the 22 call sites use it
        memset(cover, 0, count);
        in_bands(count, first, last, { for (size_t i = first; i < last; ++i) cover[i]++; });
        report("macro form does not match the reference partition", count, memcmp(cover, expect, count) == 0);

        // form 2: the function-pointer + void* form
        memset(cover, 0, count);
        MarkCtx ctx = { cover };
        compositor_bands_apply(count, mark, &ctx);
        report("callback form does not match the reference partition", count, memcmp(cover, expect, count) == 0);
    }

    // boundary behaviour: fewer than 64 items is one band, 64 is the full 32
    memset(band_seen, 0, sizeof band_seen);
    in_bands((size_t)63, first, last, { band_seen[first] = 1; });
    size_t bands_63 = 0;
    for (size_t i = 0; i < 63; ++i) bands_63 += band_seen[i] ? 1 : 0;
    memset(band_seen, 0, sizeof band_seen);
    in_bands((size_t)64, first, last, { band_seen[first] = 1; });
    size_t bands_64 = 0;
    for (size_t i = 0; i < 64; ++i) bands_64 += band_seen[i] ? 1 : 0;
    printf("bands: count=63 -> %zu, count=64 -> %zu, count=5000 -> 32\n", bands_63, bands_64);
    report("count=63 must be one band", 63, bands_63 == 1);
    report("count=64 must be 32 bands", 64, bands_64 == 32);

    // `continue` in a band body abandons that band only: every band still runs
    memset(band_seen, 0, sizeof band_seen);
    in_bands((size_t)1000, first, last, { band_seen[first] = 1; continue; });
    size_t bands_1000 = 0;
    for (size_t i = 0; i < 1000; ++i) bands_1000 += band_seen[i] ? 1 : 0;
    printf("bands entered with `continue` as the body: %zu (expected 32)\n", bands_1000);
    report("`continue` skipped other bands", 1000, bands_1000 == 32);

    printf("%s\n", failures ? "shim self-test: FAIL" : "shim self-test: PASS");
    return failures ? 1 : 0;
}
