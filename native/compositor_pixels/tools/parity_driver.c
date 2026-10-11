// parity_driver.c -- run the pixel kernel and dump every result, so that two builds of
// it can be compared bit for bit.
//
// It includes the kernel's own headers and calls the 42 entry points directly; the
// difference between the two builds is only which import library the executable is
// linked against (compositor_kernel.a from spikes/c-kernel, written by the Clang-blocks
// + GCC-nested-function transform, versus libcompositor_pixels.a from the standard-C
// port). Same source, same calls, so any difference in the dumped bytes is a difference
// the port introduced.
//
// tools/run_parity.ps1 builds and runs both and compares:
//     parity_kernel.exe  -> parity_kernel.bin  + a stdout hash log
//     parity_pixels.exe  -> parity_pixels.bin  + a stdout hash log
//
// Inputs are deterministic (xorshift32 seeded per call) and *valid* premultiplied RGBA
// (a channel never exceeds alpha, with fully clear and fully opaque pixels present),
// because that is the shape the kernels are written for.
//
// Geometry choices deliberately straddle the band partition: heights below 64 (one
// band), exactly 64, not divisible by 32, and tall enough to give 32 bands; widths that
// give one band and 32 bands for the loops that partition columns instead of rows; and
// one padded stride, since the band helpers index rows through `stride`.

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "AdjustPixels.h"
#include "DitherPixels.h"
#include "LensPixels.h"
#include "LevelsPixels.h"

static FILE *Bin;
static int Sections;

static uint64_t fnv1a(const void *data, size_t n) {
    const unsigned char *p = data;
    uint64_t h = 1469598103934665603ULL;
    for (size_t i = 0; i < n; ++i) { h ^= p[i]; h *= 1099511628211ULL; }
    return h;
}

static void emit(const char *tag, const void *data, size_t n) {
    Sections++;
    printf("  %-62s %8zu B  fnv1a=%016llx\n", tag, n, (unsigned long long)fnv1a(data, n));
    uint64_t len = (uint64_t)n;
    fwrite(&len, sizeof len, 1, Bin);
    if (n) fwrite(data, 1, n, Bin);
}

static void emit_int(const char *tag, int value) {
    char line[128];
    snprintf(line, sizeof line, "%s = %d", tag, value);
    int32_t v = value;
    Sections++;
    printf("  %-62s %8s  fnv1a=%016llx\n", line, "4 B", (unsigned long long)fnv1a(&v, sizeof v));
    uint64_t len = sizeof v;
    fwrite(&len, sizeof len, 1, Bin);
    fwrite(&v, sizeof v, 1, Bin);
}

// ---------------------------------------------------------------- deterministic input
static uint32_t Rng;

static uint32_t rnd(void) {
    Rng ^= Rng << 13; Rng ^= Rng >> 17; Rng ^= Rng << 5;
    return Rng;
}

static void seed(uint32_t s) { Rng = s ? s : 1u; }

// A realistic premultiplied RGBA image: alpha varies in soft blotches, the channels
// stay within it, and both fully clear and fully opaque pixels occur.
static uint8_t *make_image(size_t w, size_t h, size_t pad, size_t *stride_out) {
    size_t stride = w * 4 + pad;
    uint8_t *p = malloc((h ? h - 1 : 0) * stride + w * 4);
    if (!p) { fprintf(stderr, "out of memory\n"); exit(2); }
    memset(p, 0, (h ? h - 1 : 0) * stride + w * 4);
    for (size_t y = 0; y < h; ++y) {
        uint8_t *row = p + y * stride;
        for (size_t x = 0; x < w; ++x) {
            unsigned a;
            if ((x % 17 == 0) || (y % 13 == 0)) a = 0;
            else if ((x + y) % 11 == 0) a = 255;
            else a = (unsigned)((x * 9 + y * 5 + rnd() % 23) & 0xFF);
            row[x * 4 + 3] = (uint8_t)a;
            for (int c = 0; c < 3; ++c) row[x * 4 + c] = a ? (uint8_t)(rnd() % (a + 1)) : 0;
        }
    }
    *stride_out = stride;
    return p;
}

static void fill_floats(float *p, size_t n, float lo, float hi) {
    for (size_t i = 0; i < n; ++i) p[i] = lo + (hi - lo) * (float)(rnd() % 1000) / 1000.0f;
}

static void fill_lut(float *p, size_t n) {
    for (size_t i = 0; i < n; ++i) p[i] = (float)i / (float)(n - 1);
}

#define IMAGE_GEOMETRIES 7
static const struct { size_t w, h, pad; } G[IMAGE_GEOMETRIES] = {
    { 61,  90, 0 },   // 32 bands over rows, 90 not divisible by 32
    { 70,  45, 0 },   // fewer rows than 64: a single band
    { 64,  64, 0 },   // exactly 64 rows: 32 bands of 2
    { 129, 65, 8 },   // 32 bands over rows, padded stride
    { 33, 200, 0 },   // 33 columns: a single band for the column loops
    { 37,   7, 0 },   // tiny
    { 200, 300, 0 },  // wide enough that a column-partitioned loop also gets 32 bands
};

static char Tag[160];
#define TAG(...) snprintf(Tag, sizeof Tag, __VA_ARGS__)

int main(int argc, char **argv) {
    const char *path = argc > 1 ? argv[1] : "parity.bin";
    Bin = fopen(path, "wb");
    if (!Bin) { fprintf(stderr, "cannot write %s\n", path); return 2; }
    printf("parity driver -> %s\n", path);

    for (int g = 0; g < IMAGE_GEOMETRIES; ++g) {
        const size_t w = G[g].w, h = G[g].h, pad = G[g].pad;
        printf("\n== %zux%zu (stride pad %zu) ==\n", w, h, pad);
        size_t stride = 0;
        const size_t bytes = (h - 1) * (w * 4 + pad) + w * 4;

        // ---- camera_raw_statistics: the two direct dispatch_apply sites... ----
        seed(0x1000u + (uint32_t)g);
        uint8_t *img = make_image(w, h, pad, &stride);
        double stats[3] = { -1, -1, -1 };
        camera_raw_statistics(img, w, h, stride, stats);
        TAG("camera_raw_statistics %zux%zu", w, h);
        emit(Tag, stats, sizeof stats);
        free(img);

        // ---- ... and camera_raw_scope, which also walks every `step`th pixel ----
        for (int step = 1; step <= 2; ++step) {
            enum { SIDE = 16 };
            double bins[768], scope[SIDE * SIDE];
            memset(bins, 0, sizeof bins);
            memset(scope, 0, sizeof scope);
            seed(0x2000u + (uint32_t)g * 4u + (uint32_t)step);
            img = make_image(w, h, pad, &stride);
            camera_raw_scope(img, w, h, stride, step, bins, scope, SIDE);
            TAG("camera_raw_scope %zux%zu step=%d bins", w, h, step);
            emit(Tag, bins, sizeof bins);
            TAG("camera_raw_scope %zux%zu step=%d scope", w, h, step);
            emit(Tag, scope, sizeof scope);
            free(img);
        }

        // ---- adjust_camera_raw: band loop over rows, null table and a composed one ----
        for (int variant = 0; variant < 4; ++variant) {
            const int grid = 9;
            float table[9 * 9 * 9 * 3];
            seed(0x3000u + (uint32_t)variant);
            fill_floats(table, sizeof table / sizeof table[0], 0.0f, 1.0f);
            seed(0x3100u + (uint32_t)g + (uint32_t)variant);
            img = make_image(w, h, pad, &stride);
            const float *tp = variant == 0 ? NULL : table;
            int clipping = variant == 0 ? 0 : variant - 1;   // 0 grade, 1 highlight clip, 2 shadow clip
            adjust_camera_raw(img, w, h, stride, tp, variant == 0 ? 0 : grid, clipping);
            TAG("adjust_camera_raw %zux%zu table=%d clip=%d", w, h, variant != 0, clipping);
            emit(Tag, img, bytes);
            free(img);
        }

        // ---- adjust_camera_raw_calibration, adjust_camera_raw_sharpen_mask_overlay ----
        seed(0x4000u + (uint32_t)g);
        img = make_image(w, h, pad, &stride);
        adjust_camera_raw_calibration(img, w, h, stride, 20, 10, 30, -5, 20, 5, -10, 5);
        TAG("adjust_camera_raw_calibration %zux%zu", w, h);
        emit(Tag, img, bytes);
        free(img);

        seed(0x4100u + (uint32_t)g);
        img = make_image(w, h, pad, &stride);
        adjust_camera_raw_sharpen_mask_overlay(img, w, h, stride, 50, 25, 20, 1.0);
        TAG("adjust_camera_raw_sharpen_mask_overlay %zux%zu", w, h);
        emit(Tag, img, bytes);
        free(img);

        // ---- adjust_camera_raw_detail: three band loops plus two blurred planes ----
        seed(0x5000u + (uint32_t)g);
        img = make_image(w, h, pad, &stride);
        adjust_camera_raw_detail(img, w, h, stride, 60, 50, 25, 20, 40, 50, 25, 35, 50, 50, 1.0);
        TAG("adjust_camera_raw_detail %zux%zu", w, h);
        emit(Tag, img, bytes);
        free(img);

        // ---- adjust_camera_raw_effects: two band loops; the row loops inside the blur ----
        for (int variant = 0; variant < 2; ++variant) {
            seed(0x6000u + (uint32_t)g * 2u + (uint32_t)variant);
            img = make_image(w, h, pad, &stride);
            adjust_camera_raw_effects(img, w, h, stride,
                                      variant ? 40 : 0, variant ? 30 : 0, variant ? 20 : 0,
                                      variant ? 50 : 0, variant ? 1 : 0, 50, 50, 30,
                                      35, 50, 50, 50, 50, 0, 1.0);
            TAG("adjust_camera_raw_effects %zux%zu variant=%d", w, h, variant);
            emit(Tag, img, bytes);
            free(img);
        }

        // ---- adjust_camera_raw_optics: distortion + defringe + band loop ----
        seed(0x7000u + (uint32_t)g);
        img = make_image(w, h, pad, &stride);
        adjust_camera_raw_optics(img, w, h, stride, 1, 1, 0.2, 50, 0.15, 40, -20, 40, 30, 100, 160, 40, 50, 1.0, NULL, 0);
        TAG("adjust_camera_raw_optics %zux%zu", w, h);
        emit(Tag, img, bytes);
        free(img);

        // ---- adjust_gradient_map (control: no band loop) ----
        uint8_t ramp[256 * 3];
        seed(0x8000u);
        for (int i = 0; i < 256; ++i) { ramp[i * 3] = (uint8_t)rnd(); ramp[i * 3 + 1] = (uint8_t)rnd(); ramp[i * 3 + 2] = (uint8_t)rnd(); }
        seed(0x8100u + (uint32_t)g);
        img = make_image(w, h, pad, &stride);
        adjust_gradient_map(img, w, h, stride, ramp);
        TAG("adjust_gradient_map %zux%zu", w, h);
        emit(Tag, img, bytes);
        free(img);

        seed(0x8200u + (uint32_t)g);
        img = make_image(w, h, pad, &stride);
        rgba_clamp_premultiplied(img, w * h);
        TAG("rgba_clamp_premultiplied %zux%zu", w, h);
        emit(Tag, img, bytes);
        free(img);

        // ---- dither_glow and dither_quantize16: row band loops ----
        seed(0x9000u + (uint32_t)g);
        img = make_image(w, h, pad, &stride);
        size_t glow_stride = 0;
        seed(0x9100u + (uint32_t)g);
        uint8_t *glow = make_image(w, h, pad, &glow_stride);
        dither_glow(img, glow, w, h, stride, 0.8f);
        TAG("dither_glow %zux%zu", w, h);
        emit(Tag, img, bytes);
        free(img); free(glow);

        seed(0xA000u + (uint32_t)g);
        uint16_t *wide = malloc(w * h * 4 * sizeof(uint16_t));
        img = malloc((h - 1) * (w * 4 + pad) + w * 4);
        if (!wide || !img) { fprintf(stderr, "out of memory\n"); return 2; }
        memset(img, 0, (h - 1) * (w * 4 + pad) + w * 4);
        for (size_t i = 0; i < w * h; ++i) {
            unsigned a = rnd() & 0xFFFF;
            wide[i * 4 + 3] = (uint16_t)a;
            for (int c = 0; c < 3; ++c) wide[i * 4 + c] = a ? (uint16_t)(rnd() % (a + 1)) : 0;
        }
        dither_quantize16(wide, img, w, h, stride);
        TAG("dither_quantize16 %zux%zu", w, h);
        emit(Tag, img, bytes);
        free(img); free(wide);

        // ---- dither_dots (control: no band loop) ----
        seed(0xA100u + (uint32_t)g);
        img = make_image(w, h, pad, &stride);
        uint8_t gap[3] = { 20, 30, 40 };
        dither_dots(img, w, h, stride, 4, gap);
        TAG("dither_dots %zux%zu", w, h);
        emit(Tag, img, bytes);
        free(img);

        // ---- dither_apply: every style, through the band loop and the diffuse path ----
        for (int style = 0; style <= DITHER_GLYPHS; ++style) {
            static const uint8_t glyphs[2 * 16] = {
                0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x18, 0x18, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x18, 0x18,
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            };
            static const float coverage[2] = { 0.25f, 1.0f };
            DitherParams p;
            memset(&p, 0, sizeof p);
            p.style = style;
            p.levels = 4;
            p.diffusion = 0.7f;
            p.density = 0.2f;
            p.contrast = 0.3f;
            p.cell = 4;
            p.angle = 0.4f;
            p.lightOnDark = 0;
            p.originalColors = (style & 1);
            p.dark[0] = 20; p.dark[1] = 25; p.dark[2] = 30;
            p.light[0] = 230; p.light[1] = 225; p.light[2] = 220;
            p.glyphWidth = 4; p.glyphHeight = 4;
            p.glyphs = glyphs; p.glyphCoverage = coverage; p.glyphCount = 2;
            seed(0xB000u + (uint32_t)g * 16u + (uint32_t)style);
            img = make_image(w, h, pad, &stride);
            int rc = dither_apply(img, w, h, stride, &p);
            TAG("dither_apply %zux%zu style=%d", w, h, style);
            emit(Tag, img, bytes);
            TAG("dither_apply %zux%zu style=%d rc", w, h, style);
            emit_int(Tag, rc);
            free(img);
        }

        // ---- scanlines_apply: the two band loops inside draw_lines, the column loop,
        // ---- and the split path whose band body returns early on allocation failure ----
        for (int variant = 0; variant < 2; ++variant) {
            ScanlinesParams p;
            memset(&p, 0, sizeof p);
            p.spacing = 4;
            p.thickness = 0.8f;
            p.dots = 0.3f;
            p.wobble = 1.5f;
            p.displace = variant ? 1.2f : 0.0f;   // 0 skips the smoothing band loops
            p.threshold = 0.05f;
            p.split = 2.0f;                        // > 0 runs the early-return band body
            p.density = 0.1f;
            p.contrast = 0.2f;
            p.blackLevel = 0.1f;
            p.smoothness = 0.5f;
            p.originalColors = variant;
            p.dark[0] = 10; p.dark[1] = 12; p.dark[2] = 14;
            p.light[0] = 240; p.light[1] = 235; p.light[2] = 230;
            seed(0xC000u + (uint32_t)g * 2u + (uint32_t)variant);
            img = make_image(w, h, pad, &stride);
            int rc = scanlines_apply(img, w, h, stride, &p);
            TAG("scanlines_apply %zux%zu variant=%d", w, h, variant);
            emit(Tag, img, bytes);
            TAG("scanlines_apply %zux%zu variant=%d rc", w, h, variant);
            emit_int(Tag, rc);
            free(img);
        }

        // ---- camera_raw_compose / camera_raw_compose_onto: the lattice band loops ----
        {
            enum { GRID = 9, SIZE = 3 };
            uint8_t measured[SIZE * SIZE * SIZE * 3];
            seed(0xD000u);
            for (size_t i = 0; i < sizeof measured; ++i) measured[i] = (uint8_t)rnd();
            CameraRawStage stage;
            memset(&stage, 0, sizeof stage);
            stage.table[0] = measured;
            stage.weight[0] = 0.6f;
            stage.weight[1] = 0.4f;   // no table: identity
            float out[GRID * GRID * GRID * 3];
            seed(0xD100u + (uint32_t)g);
            camera_raw_compose(out, GRID, &stage, 1, SIZE);
            TAG("camera_raw_compose %zux%zu grid=%d", w, h, GRID);
            emit(Tag, out, sizeof out);

            float onto[GRID * GRID * GRID * 3];
            seed(0xD200u);
            fill_floats(onto, sizeof onto / sizeof onto[0], 0.0f, 1.0f);
            camera_raw_compose_onto(onto, GRID, &stage, 1, SIZE);
            TAG("camera_raw_compose_onto %zux%zu grid=%d", w, h, GRID);
            emit(Tag, onto, sizeof onto);
        }

        // ---- camera_raw_table_curves (control: no band loop) ----
        {
            enum { GRID = 9 };
            float table[GRID * GRID * GRID * 3], tone[256], red[256], green[256], blue[256];
            seed(0xE000u + (uint32_t)g);
            fill_floats(table, sizeof table / sizeof table[0], 0.0f, 1.0f);
            fill_lut(tone, 256); fill_lut(red, 256); fill_lut(green, 256); fill_lut(blue, 256);
            camera_raw_table_curves(table, GRID, tone, red, green, blue);
            TAG("camera_raw_table_curves %zux%zu grid=%d", w, h, GRID);
            emit(Tag, table, sizeof table);
        }

        // ---- adjust_camera_raw_curve_color: the largest band body (54 captures) ----
        {
            enum { GRID = 9 };
            float tone[256], red[256], green[256], blue[256], mixer[24], points[2 * 9], grade[12];
            float mixerTable[GRID * GRID * GRID * 3];
            seed(0xF000u + (uint32_t)g);
            fill_lut(tone, 256); fill_lut(red, 256); fill_lut(green, 256); fill_lut(blue, 256);
            fill_floats(mixer, 24, -1.0f, 1.0f);
            fill_floats(points, 18, -1.0f, 1.0f);
            for (int i = 0; i < 2; ++i) { points[i * 9 + 6] = 0.2f; points[i * 9 + 7] = 0.2f; points[i * 9 + 8] = 0.2f; }
            fill_floats(grade, 12, -1.0f, 1.0f);
            fill_floats(mixerTable, sizeof mixerTable / sizeof mixerTable[0], 0.0f, 1.0f);
            for (int variant = 0; variant < 2; ++variant) {
                seed(0xF100u + (uint32_t)g * 2u + (uint32_t)variant);
                img = make_image(w, h, pad, &stride);
                adjust_camera_raw_curve_color(img, w, h, stride, tone, red, green, blue,
                                              variant ? 0.4 : -0.4, mixer, 2, points, grade,
                                              0.5, 0.2, variant ? 0 : -1,
                                              variant ? mixerTable : NULL, NULL, variant ? GRID : 0);
                TAG("adjust_camera_raw_curve_color %zux%zu variant=%d", w, h, variant);
                emit(Tag, img, bytes);
                free(img);
            }
        }

        // ---- controls from the seven files that needed no port at all ----
        seed(0x1100u + (uint32_t)g);
        img = make_image(w, h, pad, &stride);
        float tables[3 * 256];
        for (int c = 0; c < 3; ++c) for (int v = 0; v < 256; ++v) tables[c * 256 + v] = (float)v * (0.5f + 0.25f * (float)c);
        levels_apply(img, w * h, tables);
        TAG("levels_apply %zux%zu", w, h);
        emit(Tag, img, bytes);
        free(img);

        seed(0x1200u + (uint32_t)g);
        img = make_image(w, h, pad, &stride);
        uint8_t *dst = malloc(bytes);
        if (!dst) { fprintf(stderr, "out of memory\n"); return 2; }
        memset(dst, 0, bytes);
        lens_distort(img, dst, w, h, stride, 0.3);
        TAG("lens_distort %zux%zu", w, h);
        emit(Tag, dst, bytes);
        free(img); free(dst);
    }

    fclose(Bin);
    printf("\nsections: %d\n", Sections);
    return 0;
}
