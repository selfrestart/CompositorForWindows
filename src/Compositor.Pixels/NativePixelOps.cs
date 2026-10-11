using System.Runtime.InteropServices;

namespace Compositor.Pixels;

/// <summary>
/// Raw ABI surface of the C pixel kernel, part two: dithering, brush/alpha, healing, lens, levels,
/// noise, wand/color-range and content fill. See <see cref="NativeAdjustPixels"/> for the shared
/// buffer contract, the pointer convention and the missing-library behaviour.
/// </summary>
internal static partial class NativePixelOps
{
    private const string L = NativeKernel.LibraryName;

    // ---------------------------------------------------------------- DitherPixels.h

    /// <summary>Filter / Scanlines: the picture as a CRT draws it. Returns 0 on success.</summary>
    [LibraryImport(L, EntryPoint = "scanlines_apply")]
    internal static partial int ScanlinesApply(IntPtr rgba, nuint width, nuint height, nuint stride,
        ref ScanlinesParams @params);

    /// <summary>Dithers in place with the style chosen by <see cref="DitherParams.Style"/>. Returns 0 on success.</summary>
    [LibraryImport(L, EntryPoint = "dither_apply")]
    internal static partial int DitherApply(IntPtr rgba, nuint width, nuint height, nuint stride,
        ref DitherParams @params);

    /// <summary>Turns each <c>block</c>-squared square of premultiplied RGBA into a round dot on <paramref name="gap"/>.</summary>
    [LibraryImport(L, EntryPoint = "dither_dots")]
    internal static partial void DitherDots(IntPtr rgba, nuint width, nuint height, nuint stride, int block,
        IntPtr gap);

    /// <summary>Adds <paramref name="glow"/> over the pixels at <paramref name="amount"/>, never past their own alpha.</summary>
    [LibraryImport(L, EntryPoint = "dither_glow")]
    internal static partial void DitherGlow(IntPtr rgba, IntPtr glow, nuint width, nuint height, nuint stride,
        float amount);

    /// <summary>Rounds 16-bit premultiplied RGBA (tightly packed) to 8 bits, with fixed per-pixel noise.</summary>
    [LibraryImport(L, EntryPoint = "dither_quantize16")]
    internal static partial void DitherQuantize16(IntPtr wide, IntPtr rgba, nuint width, nuint height,
        nuint stride);

    // ---------------------------------------------------------------- BrushPixels.h

    /// <summary>Half-open bounds [left, top, right, bottom] of nonzero alpha; all zero when empty.</summary>
    [LibraryImport(L, EntryPoint = "brush_alpha_bounds")]
    internal static partial void BrushAlphaBounds(IntPtr bytes, nuint width, nuint height, nuint stride,
        IntPtr bounds);

    /// <summary>Copies the alpha plane into a <c>width x height</c> gray buffer.</summary>
    [LibraryImport(L, EntryPoint = "layer_extract_alpha")]
    internal static partial void LayerExtractAlpha(IntPtr rgba, nuint rgbaStride, IntPtr gray, nuint grayStride,
        nuint width, nuint height);

    /// <summary>Undoes premultiplication on pixels that are fully opaque.</summary>
    [LibraryImport(L, EntryPoint = "layer_unpremultiply_opaque")]
    internal static partial void LayerUnpremultiplyOpaque(IntPtr rgba, nuint stride, nuint width, nuint height);

    /// <summary>Rescales colors by an alpha plane and writes that alpha into the pixels.</summary>
    [LibraryImport(L, EntryPoint = "layer_restore_alpha")]
    internal static partial void LayerRestoreAlpha(IntPtr rgba, nuint stride, IntPtr alpha, nuint alphaStride,
        nuint width, nuint height);

    // ---------------------------------------------------------------- HealPixels.h

    /// <summary>
    /// Half-open bounds [left, top, right, bottom] of nonzero bytes in a gray bitmap; all zero when
    /// empty. Expects a 4-element <see cref="int"/> array.
    /// </summary>
    /// <remarks>
    /// The C parameter is <c>long bounds[4]</c>, and C <c>long</c> is <b>32-bit on Windows</b>
    /// (verified: gcc here reports <c>sizeof(long) == 4</c>) while it is 64-bit on Linux/macOS. This
    /// binding is therefore correct for the Windows build; a <c>libcompositor_pixels.so</c> built on
    /// Linux LP64 would need <c>long</c> here instead. The kernel only ever writes <c>0..width+1</c>
    /// into it, so the values themselves fit either width.
    /// </remarks>
    [LibraryImport(L, EntryPoint = "heal_coverage_bounds")]
    internal static partial void HealCoverageBounds(IntPtr gray, nuint width, nuint height, nuint stride,
        IntPtr bounds);

    /// <summary>Spot healing in place. Returns 0, or -1 when memory runs out.</summary>
    [LibraryImport(L, EntryPoint = "spot_heal")]
    internal static partial int SpotHeal(IntPtr rgba, IntPtr coverage, nuint width, nuint height, nuint stride,
        float opacity, int mode, uint seed);

    // ---------------------------------------------------------------- LensPixels.h

    /// <summary>Radial lens distortion over premultiplied RGBA; <c>k = 0</c> copies the source exactly.</summary>
    [LibraryImport(L, EntryPoint = "lens_distort")]
    internal static partial void LensDistort(IntPtr source, IntPtr destination, nuint width, nuint height,
        nuint stride, double k);

    // ---------------------------------------------------------------- LevelsPixels.h

    /// <summary>Applies three 256-entry channel tables to premultiplied RGBA, in place.</summary>
    [LibraryImport(L, EntryPoint = "levels_apply")]
    internal static partial void LevelsApply(IntPtr pixels, nuint count, IntPtr tables);

    /// <summary>Adds a 3 x 256 histogram of the pixels under the coverage mask into <paramref name="bins"/>.</summary>
    [LibraryImport(L, EntryPoint = "levels_histogram")]
    internal static partial void LevelsHistogram(IntPtr pixels, IntPtr coverage, nuint count, IntPtr bins);

    /// <summary>Applies a <c>dimension</c>-cubed 3D LUT to premultiplied RGBA, in place.</summary>
    [LibraryImport(L, EntryPoint = "cube_apply")]
    internal static partial void CubeApply(IntPtr pixels, nuint count, IntPtr cube, int dimension);

    // ---------------------------------------------------------------- NoisePixels.h

    /// <summary>Adds noise to the color of premultiplied RGBA, leaving alpha and clear pixels alone.</summary>
    [LibraryImport(L, EntryPoint = "noise_add")]
    internal static partial void NoiseAdd(IntPtr rgba, nuint width, nuint height, nuint stride,
        float amount, int gaussian, int monochromatic, uint seed);

    /// <summary>As <see cref="NoiseAdd"/>, but positioned at an explicit document origin.</summary>
    [LibraryImport(L, EntryPoint = "noise_add_at")]
    internal static partial void NoiseAddAt(IntPtr rgba, nuint width, nuint height, nuint stride,
        float amount, int gaussian, int monochromatic, uint seed, long originX, long originY);

    // ---------------------------------------------------------------- WandPixels.h

    /// <summary>Magic Wand match into a <c>width x height</c> mask; returns the count selected or -1.</summary>
    [LibraryImport(L, EntryPoint = "wand_mask")]
    internal static partial int WandMask(IntPtr rgba, nuint width, nuint height, nuint stride,
        nuint seedX, nuint seedY, nuint radius, int tolerance, int contiguous, IntPtr mask);

    /// <summary>Select / Color Range into a mask; returns the count selected.</summary>
    [LibraryImport(L, EntryPoint = "color_range_mask")]
    internal static partial int ColorRangeMask(IntPtr rgba, nuint width, nuint height, nuint stride,
        IntPtr include, int includeCount, IntPtr exclude, int excludeCount,
        int fuzziness, int invert, IntPtr mask);

    /// <summary>
    /// Outline of the nonzero pixels of a mask as closed loops of corner points. Returns 0 on success,
    /// -1 when memory runs out and -2 when the outline is too detailed to be worth drawing. Both
    /// outputs are <c>malloc</c>'d by the kernel and must be released with
    /// <see cref="KernelHeap.Free"/> — <b>not</b> with <see cref="Marshal.FreeHGlobal"/>, which uses
    /// a different heap on Windows and corrupts the process.
    /// </summary>
    [LibraryImport(L, EntryPoint = "wand_trace")]
    internal static partial int WandTrace(IntPtr mask, nuint width, nuint height,
        ref IntPtr points, ref nuint pointCount, ref IntPtr loops, ref nuint loopCount);

    // ---------------------------------------------------------------- ContentFill.h

    /// <summary>Returns 1 on success, 0 when no source patch exists, -1 on allocation failure.</summary>
    [LibraryImport(L, EntryPoint = "content_fill")]
    internal static partial int ContentFill(IntPtr rgba, nuint stride, IntPtr mask, nuint maskStride,
        int width, int height);
}
