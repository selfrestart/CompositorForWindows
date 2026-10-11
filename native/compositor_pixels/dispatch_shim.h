// dispatch_shim.h -- portable, standard-C stand-in for Apple's <dispatch/dispatch.h>.
//
// WHAT THIS REPLACES, AND WHY
// --------------------------
// Upstream `AdjustPixels.c` and `DitherPixels.c` are plain C apart from one Apple
// dependency each:
//
//     #include <dispatch/dispatch.h>          // AdjustPixels.c:3, DitherPixels.c:5
//     dispatch_apply(bands, DISPATCH_APPLY_AUTO, ^(size_t band) { ... });
//
// The bodies passed to `dispatch_apply` are Clang *blocks* (`^`). Blocks are a
// compiler extension: MSVC cannot compile them, and neither can GCC. This header
// removes that dependency with no compiler-specific extension anywhere.
//
// The four `dispatch_apply` call sites split work as disjoint index ranges, and the
// 23 `in_bands(...)` call sites split it as disjoint *row* ranges: band k owns
// [k*size, (k+1)*size) alone, the ranges do not overlap and together cover
// [0, count). No band reads what another band writes, and nothing is reduced across
// bands inside the loop (the reductions happen afterwards, serially). Therefore
// serial and parallel execution produce bit-identical pixel output; serial is the
// default because that is the ordering the reference implementation produces.
//
// HOW THE BODY IS PASSED WITHOUT BLOCKS
// ------------------------------------
// Standard C has no closures, so a body cannot be handed to a helper function while
// still referring to its surrounding locals. The two ways out are:
//
//   (a) hoist every body into a named function and pass `function pointer + void*`
//       context. That is what `compositor_bands_apply` below offers. It costs a
//       context struct per call site: the 23 `in_bands` sites capture between 5 and
//       54 surrounding names each (about 400 in total), so applying it everywhere
//       would rewrite every arithmetic line in the two files.
//   (b) expand the band loop at the call site, so the body stays exactly where it
//       is, in its original scope, byte for byte.
//
// The macros below take route (b) for the 23 `in_bands` sites and for the two
// direct `dispatch_apply` sites, and route (a) stays available as
// `compositor_bands_apply` for callers that prefer an outlined body (for example a
// future thread-pool backend, which cannot use a macro expansion).
//
// CONTROL FLOW INSIDE A BAND BODY
// ------------------------------
// A Clang block's `return` returns from the *block*, i.e. it abandons that band and
// leaves the other bands running. The macros reproduce that with a single-iteration
// wrapper loop, so `continue` inside the body abandons the current band exactly as
// the original `return` did -- including under OpenMP, where a `continue` that binds
// to the worksharing loop itself is an OpenMP 2.0 violation (MSVC's /openmp).
// The three bodies that used this (DitherPixels.c `draw_lines` twice, `dither_dots`)
// are the only such sites; they were changed from `return` to `continue`, nothing else.
//
// OPENMP
// ------
// _OPENMP is defined by GCC/Clang with -fopenmp and by MSVC with /openmp. The loop
// index is `long long` under OpenMP because OpenMP 2.0 (MSVC) requires a signed
// index. Build scripts default to serial; build.ps1 -OpenMp / build.sh --openmp opt
// in, and the test suite checks that the two builds agree bit for bit.
//
// No other dependency: only <stddef.h>.

#ifndef COMPOSITOR_DISPATCH_SHIM_H
#define COMPOSITOR_DISPATCH_SHIM_H

#include <stddef.h>

#if defined(_OPENMP) && !defined(COMPOSITOR_NO_OPENMP)
// MSVC's __pragma has always been available; _Pragma is the standard spelling (C99)
// and is what GCC/Clang need. Both expand to `#pragma omp parallel for`.
#  if defined(_MSC_VER)
#    define COMPOSITOR_BANDS_PRAGMA __pragma(omp parallel for)
#  else
#    define COMPOSITOR_BANDS_PRAGMA _Pragma("omp parallel for")
#  endif
typedef long long compositor_bands_index;
// 1 when the band loops are compiled for parallel execution, 0 when serial. The test
// suite prints this so a build's mode is never inferred from the flags alone.
#define COMPOSITOR_BANDS_PARALLEL 1
#else
#  define COMPOSITOR_BANDS_PRAGMA
typedef size_t compositor_bands_index;
#define COMPOSITOR_BANDS_PARALLEL 0
#endif

// Upstream passes DISPATCH_APPLY_AUTO as the queue argument. There is one queue
// here (the current thread, or the OpenMP team), and it is ignored.
#define DISPATCH_APPLY_AUTO ((void *)0)

// ---------------------------------------------------------------------------
// in_bands(count, first, last, { body })
//
// Replaces upstream's block-taking helper, which was byte-for-byte identical in
// both files:
//
//     static void in_bands(size_t count, void (^body)(size_t start, size_t end)) {
//         size_t bands = count < 64 ? 1 : 32, size = (count + bands - 1) / bands;
//         dispatch_apply(bands, DISPATCH_APPLY_AUTO, ^(size_t band) {
//             size_t start = band * size, end = start + size < count ? start + size : count;
//             if (start < end) body(start, end);
//         });
//     }
//
// The partition arithmetic below is the same expression, character for character.
// `first`/`last` are declared in the innermost scope, so they shadow exactly what
// the block parameters shadowed.
// ---------------------------------------------------------------------------
#define in_bands(count, first, last, ...)                                                  \
    do {                                                                                   \
        size_t compositor_band_count_ = (count);                                           \
        size_t compositor_band_total_ = compositor_band_count_ < 64 ? 1 : 32;              \
        size_t compositor_band_size_ =                                                     \
            (compositor_band_count_ + compositor_band_total_ - 1) / compositor_band_total_; \
        COMPOSITOR_BANDS_PRAGMA                                                            \
        for (compositor_bands_index compositor_band_index_ = 0;                            \
             compositor_band_index_ < (compositor_bands_index)compositor_band_total_;      \
             ++compositor_band_index_) {                                                   \
            size_t first = (size_t)compositor_band_index_ * compositor_band_size_;         \
            size_t last = first + compositor_band_size_ < compositor_band_count_           \
                              ? first + compositor_band_size_                              \
                              : compositor_band_count_;                                    \
            if (first < last)                                                              \
                for (int compositor_band_once_ = 1; compositor_band_once_;                 \
                     compositor_band_once_ = 0) {                                          \
                    __VA_ARGS__                                                            \
                }                                                                          \
        }                                                                                  \
    } while (0)

// ---------------------------------------------------------------------------
// dispatch_apply(iterations, queue, index, { body })
//
// Keeps upstream's call shape for the two sites that drive their own loop
// (AdjustPixels.c `camera_raw_statistics`, `camera_raw_scope`). The index variable
// carries the caller's own name, exactly as the block parameter did.
// ---------------------------------------------------------------------------
#define dispatch_apply(iterations, queue, index_name, ...)                                 \
    do {                                                                                   \
        size_t compositor_apply_count_ = (size_t)(iterations);                             \
        (void)(queue);                                                                     \
        COMPOSITOR_BANDS_PRAGMA                                                            \
        for (compositor_bands_index compositor_apply_index_ = 0;                           \
             compositor_apply_index_ < (compositor_bands_index)compositor_apply_count_;    \
             ++compositor_apply_index_) {                                                  \
            size_t index_name = (size_t)compositor_apply_index_;                           \
            __VA_ARGS__                                                                    \
        }                                                                                  \
    } while (0)

// ---------------------------------------------------------------------------
// compositor_bands_apply(count, body, context) -- the `function pointer + void*`
// form of the same partition, for callers that can outline their body.
//
// A band body receives its band's half-open range [first, last) and the caller's
// context pointer. It may be called for several bands concurrently, in any order.
// ---------------------------------------------------------------------------
typedef void (*compositor_band_body)(size_t first, size_t last, void *context);

static inline void compositor_bands_apply(size_t count, compositor_band_body body, void *context) {
    size_t bands = count < 64 ? 1 : 32, size = (count + bands - 1) / bands;
    COMPOSITOR_BANDS_PRAGMA
    for (compositor_bands_index band = 0; band < (compositor_bands_index)bands; ++band) {
        size_t first = (size_t)band * size, last = first + size < count ? first + size : count;
        if (first < last) body(first, last, context);
    }
}

#endif // COMPOSITOR_DISPATCH_SHIM_H
