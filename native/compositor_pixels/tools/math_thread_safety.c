// math_thread_safety.c -- do the C runtime's math functions return the same bits when
// called from OpenMP worker threads as they do on the main thread?
//
// This is the diagnostic for a real finding: with OMP_NUM_THREADS > 1, the parity dump of
// `camera_raw_statistics` differs from the serial build by one byte (about 1 ULP in one of
// the three doubles), and the difference depends on the thread count and varies run to run,
// even though the band ranges are disjoint and every thread has the same MXCSR.
//
// Each "band" here computes a fixed, order-preserving sum over its own inputs, exactly the
// shape the kernel uses; if the math functions are thread-safe and the FP environment is
// uniform, every band must reproduce the serial value bit for bit.
//
// Build/run:  gcc -std=gnu11 -O2 -fopenmp math_thread_safety.c -o math_thread_safety.exe
//             OMP_NUM_THREADS=4 ./math_thread_safety.exe

#include <math.h>
#include <stdio.h>
#include <stdlib.h>

enum { BANDS = 32, N = 400 };

static double inputs[BANDS * N];

static void fill(void) {
    for (int i = 0; i < BANDS * N; ++i) inputs[i] = 1.0 + (i % 997) * 1e-3 + (i % 31) * 0.25;
}

int main(void) {
    fill();
    double par[BANDS], ser[BANDS];

#pragma omp parallel for
    for (int b = 0; b < BANDS; ++b) {
        double slog = 0, sexp = 0, spow = 0, smix = 0;
        for (int k = 0; k < N; ++k) {
            double x = inputs[b * N + k];
            slog += 0.5 * log(x + 3e-4);
            sexp += exp(-x * 0.01);
            spow += pow(x * 0.001, 2.4);
            smix += fmax(x, 1.0) * log(x) + sqrt(x);
        }
        par[b] = slog + sexp + spow + smix;
    }

    for (int b = 0; b < BANDS; ++b) {
        double slog = 0, sexp = 0, spow = 0, smix = 0;
        for (int k = 0; k < N; ++k) {
            double x = inputs[b * N + k];
            slog += 0.5 * log(x + 3e-4);
            sexp += exp(-x * 0.01);
            spow += pow(x * 0.001, 2.4);
            smix += fmax(x, 1.0) * log(x) + sqrt(x);
        }
        ser[b] = slog + sexp + spow + smix;
    }

    int bad = 0;
    for (int b = 0; b < BANDS; ++b) {
        if (par[b] != ser[b]) {
            if (bad < 5) printf("band %2d differs: parallel=%.20g serial=%.20g\n", b, par[b], ser[b]);
            bad++;
        }
    }
    printf("bands computed in a parallel region: %d, differing from the serial value: %d\n", BANDS, bad);
    printf("%s\n", bad ? "MATH NOT THREAD-REPRODUCIBLE" : "math is bit-identical across threads");
    return bad ? 1 : 0;
}
