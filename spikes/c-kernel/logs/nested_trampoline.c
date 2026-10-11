#include <stdio.h>
#include <stddef.h>
/* Emulate Clang blocks with GCC nested functions + statement expressions. */
typedef void (*band_fn)(size_t, size_t);
static void in_bands(size_t count, band_fn body) {
    size_t bands = count < 64 ? 1 : 4, size = (count + bands - 1) / bands;
    for (size_t band = 0; band < bands; ++band) {
        size_t start = band * size, end = start + size < count ? start + size : count;
        if (start < end) body(start, end);
    }
}
int main(void) {
    int total = 0;
    int rows = 100;
    in_bands((size_t)rows, ({ void __blk(size_t first, size_t last) { total += (int)(last - first); } __blk; }));
    printf("nested-fn-pointer total=%d (expected 100)\n", total);
    return total == 100 ? 0 : 1;
}
