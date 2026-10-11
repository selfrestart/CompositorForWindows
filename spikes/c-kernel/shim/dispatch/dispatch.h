/*
 * dispatch/dispatch.h -- minimal Windows shim for the only GCD entry point the
 * Compositor C pixel kernels use: `dispatch_apply`.
 *
 * Scope (measured, not assumed):
 *   The 9 .c files under Compositor/Rendering contain exactly 4 direct
 *   `dispatch_apply` calls, in 2 files:
 *       AdjustPixels.c : lines 11, 348, 386
 *       DitherPixels.c : line  12
 *   `DISPATCH_APPLY_AUTO` is the only GCD symbol they reference. Nothing else
 *   from libdispatch (no queues, no groups, no semaphores, no dispatch_once,
 *   no dispatch_async) appears anywhere in the kernel. So this shim is complete
 *   for the kernel -- it is NOT a libdispatch replacement.
 *
 * Semantics: this shim runs the iteration function SERIALLY and in order.
 *   That is a correctness port, not a performance port. The callers are
 *   band-partitioned loops whose bands are disjoint by construction
 *   (see in_bands(): band k owns rows/columns [k*size, (k+1)*size)), so serial
 *   execution produces bit-identical output to the parallel original while
 *   removing the data race question entirely. See README.md for how to restore
 *   real parallelism (OpenMP, std::thread pool, or swift-corelibs-libdispatch).
 *
 * The signature takes a plain function pointer, not a Clang block, because the
 * block literal syntax is transformed away by port-clang-blocks.py before this
 * header is ever included.
 */
#ifndef COMPOSITOR_DISPATCH_SHIM_H
#define COMPOSITOR_DISPATCH_SHIM_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef void *dispatch_queue_t;

/* GCD's real value is a sentinel meaning "let the system pick the pool". */
#define DISPATCH_APPLY_AUTO ((dispatch_queue_t)0)

typedef void (*compositor_dispatch_apply_fn)(size_t);

static inline void dispatch_apply(size_t iterations,
                                  dispatch_queue_t queue,
                                  compositor_dispatch_apply_fn body) {
    (void)queue;
    for (size_t i = 0; i < iterations; ++i) {
        body(i);
    }
}

#ifdef __cplusplus
}
#endif

#endif /* COMPOSITOR_DISPATCH_SHIM_H */
