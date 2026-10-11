using System.Runtime.InteropServices;

namespace Compositor.Pixels;

/// <summary>
/// Raw ABI surface of the C pixel kernel. One method per exported C function, signatures
/// transcribed from the headers under <c>native/compositor_pixels/csrc</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every method here is a thin P/Invoke.</b> They do not check argument sizes, they do not clamp,
/// and they do not fault-in the native library: calling one when
/// <see cref="NativeKernel.IsAvailable"/> is false throws <see cref="DllNotFoundException"/>, which
/// is what a missing <c>compositor_pixels.dll</c> does by design. Gate every call on
/// <see cref="NativeKernel.IsAvailable"/>, or use <see cref="NativeKernel.ThrowIfUnavailable"/>.
/// </para>
/// <para>
/// <b>Buffer contract</b> (<c>wave0-contracts.md</c> section 3.1): pixel buffers are 4 bytes per
/// pixel, RGBA, and <c>stride</c> is in bytes, so it may exceed <c>width * 4</c>. <c>size_t</c> is
/// <see cref="nuint"/>, <c>int64_t</c> is <see cref="long"/>, and every C pointer is
/// <see cref="IntPtr"/> so that "null means none" stays expressible: several entry points document a
/// null pointer as meaningful (<c>table</c>, <c>mixerTable</c>, <c>gradeTable</c>, <c>exclude</c>,
/// <c>exposureTables</c>, a null <c>CameraRawStage::table[i]</c>), and a <c>ref</c> parameter cannot
/// carry null.
/// </para>
/// <para>
/// <b>Premultiplication.</b> The headers describe most kernels as operating on <em>premultiplied</em>
/// RGBA while <c>wave0-contracts.md</c> section 3.1 describes the cross-module pixel buffer as
/// non-premultiplied. The bindings do not paper over that: it is the caller's job to premultiply
/// before and unpremultiply after. See <c>docs/port/wave0-contracts.md</c>.
/// </para>
/// <para>
/// The calling convention is not stated explicitly. <c>LibraryImport</c> defaults to
/// <c>CallingConvention.Cdecl</c>, which is the only convention these symbols carry, and passing it
/// explicitly makes the source generator emit an unqualified <c>CallingConvention</c> reference that
/// fails to compile (CS1069) because it does not import <c>System.Runtime.InteropServices</c>.
/// </para>
/// </remarks>
internal static partial class NativeAdjustPixels
{
    private const string L = NativeKernel.LibraryName;

    // ---------------------------------------------------------------- AdjustPixels.h

    /// <summary>Gradient Map: each pixel's luminance picks a color from a 256 x 3 straight-sRGB table.</summary>
    [LibraryImport(L, EntryPoint = "adjust_gradient_map")]
    internal static partial void GradientMap(IntPtr rgba, nuint width, nuint height, nuint stride, IntPtr table);

    /// <summary>Film grain: the same brightness change on all three channels, strongest in the midtones.</summary>
    [LibraryImport(L, EntryPoint = "adjust_grain")]
    internal static partial void Grain(IntPtr rgba, nuint width, nuint height, nuint stride, double amount,
        double size, double roughness, uint seed, double originX, double originY, double unitsPerPixel);

    /// <summary>Black &amp; White: six range weights, in the order red, yellow, green, cyan, blue, magenta.</summary>
    [LibraryImport(L, EntryPoint = "adjust_black_white")]
    internal static partial void BlackWhite(IntPtr rgba, nuint width, nuint height, nuint stride, IntPtr weights,
        int tint, double tintHue, double tintSaturation);

    /// <summary>Color Balance: three floats per tonal range (cyan/red, magenta/green, yellow/blue), -1..1.</summary>
    [LibraryImport(L, EntryPoint = "adjust_color_balance")]
    internal static partial void ColorBalance(IntPtr rgba, nuint width, nuint height, nuint stride, IntPtr shadows,
        IntPtr midtones, IntPtr highlights, int preserveLuminosity);

    /// <summary>Clamps each color channel back to its pixel's alpha after a ringing resample.</summary>
    [LibraryImport(L, EntryPoint = "rgba_clamp_premultiplied")]
    internal static partial void RgbaClampPremultiplied(IntPtr rgba, nuint count);

    /// <summary>Runs <paramref name="count"/> stages over a <c>grid</c>-cubed lattice of sRGB colors into <paramref name="out"/>.</summary>
    [LibraryImport(L, EntryPoint = "camera_raw_compose")]
    internal static partial void CameraRawCompose(IntPtr @out, int grid, ref CameraRawStage stages, int count, int size);

    /// <summary>Runs <paramref name="count"/> stages over every entry of an already-composed table, in place.</summary>
    [LibraryImport(L, EntryPoint = "camera_raw_compose_onto")]
    internal static partial void CameraRawComposeOnto(IntPtr table, int grid, ref CameraRawStage stages, int count, int size);

    /// <summary>Runs a composed table's colors through the tone curve and each channel's 256-entry curve.</summary>
    [LibraryImport(L, EntryPoint = "camera_raw_table_curves")]
    internal static partial void CameraRawTableCurves(IntPtr table, int grid, IntPtr toneLut, IntPtr redLut,
        IntPtr greenLut, IntPtr blueLut);

    /// <summary>Camera Raw histogram (256 bins each of red, green and blue) and vectorscope, both added into.</summary>
    [LibraryImport(L, EntryPoint = "camera_raw_scope")]
    internal static partial void CameraRawScope(IntPtr rgba, nuint width, nuint height, nuint stride, int step,
        IntPtr bins, IntPtr scope, int side);

    /// <summary>One stage on one sRGB color (0..1), in place.</summary>
    [LibraryImport(L, EntryPoint = "camera_raw_stage_color")]
    internal static partial void CameraRawStageColor(ref CameraRawStage stage, int size, IntPtr rgb);

    /// <summary>Contrast / Shadows / Highlights statistics as sRGB levels, written to three doubles.</summary>
    [LibraryImport(L, EntryPoint = "camera_raw_statistics")]
    internal static partial void CameraRawStatistics(IntPtr rgba, nuint width, nuint height, nuint stride, IntPtr @out);

    /// <summary>Camera Raw Light and Color groups through one composed table; <c>clipping</c> 0/1/2 selects the view.</summary>
    [LibraryImport(L, EntryPoint = "adjust_camera_raw")]
    internal static partial void CameraRaw(IntPtr rgba, nuint width, nuint height, nuint stride, IntPtr table,
        int grid, int clipping);

    /// <summary>Preview only: blue over clipped shadows and red over clipped highlights.</summary>
    [LibraryImport(L, EntryPoint = "adjust_camera_raw_clip_overlay")]
    internal static partial void CameraRawClipOverlay(IntPtr rgba, nuint width, nuint height, nuint stride,
        int shadows, int highlights);

    /// <summary>Curve, Color Mixer and Color Grading after the basic grade.</summary>
    [LibraryImport(L, EntryPoint = "adjust_camera_raw_curve_color")]
    internal static partial void CameraRawCurveColor(IntPtr rgba, nuint width, nuint height, nuint stride,
        IntPtr toneLut, IntPtr redLut, IntPtr greenLut, IntPtr blueLut,
        double refineSaturation, IntPtr mixer, int pointCount, IntPtr points,
        IntPtr grade, double blending, double balance, int visualize,
        IntPtr mixerTable, IntPtr gradeTable, int grid);

    /// <summary>Camera Raw Effects: Texture, Clarity, Dehaze, Glow and Vignette.</summary>
    [LibraryImport(L, EntryPoint = "adjust_camera_raw_effects")]
    internal static partial void CameraRawEffects(IntPtr rgba, nuint width, nuint height, nuint stride,
        double texture, double clarity, double dehaze,
        double glow, int glowStyle, double glowRange, double glowSpread, double glowWarmth,
        double vignetteAmount, double vignetteMidpoint, double vignetteRoundness,
        double vignetteFeather, double vignetteHighlights, int vignetteStyle, double scale);

    /// <summary>Standalone Vignette, shaped to a frame in the image's own pixels.</summary>
    [LibraryImport(L, EntryPoint = "adjust_colored_vignette")]
    internal static partial void ColoredVignette(IntPtr rgba, nuint width, nuint height, nuint stride,
        double frameX, double frameY, double frameWidth, double frameHeight, int fillsClear,
        double amount, double midpoint, double roundness, double feather,
        double highlights, double red, double green, double blue);

    /// <summary>Local luminance contrast against a pre-blurred copy of the same image.</summary>
    [LibraryImport(L, EntryPoint = "adjust_tonal_contrast")]
    internal static partial void TonalContrast(IntPtr rgba, IntPtr blurred, nuint width, nuint height,
        nuint stride, nuint blurredStride, double amount,
        double shadows, double midtones, double highlights);

    /// <summary>Manual noise reduction, then sharpening. Applied after the creative grade.</summary>
    [LibraryImport(L, EntryPoint = "adjust_camera_raw_detail")]
    internal static partial void CameraRawDetail(IntPtr rgba, nuint width, nuint height, nuint stride,
        double sharpenAmount, double sharpenRadius, double sharpenDetail, double sharpenMasking,
        double noiseLuminance, double noiseLuminanceDetail, double noiseLuminanceContrast,
        double noiseColor, double noiseColorDetail, double noiseColorSmoothness, double scale);

    /// <summary>Preview only: white where sharpening would land, black where masking protects.</summary>
    [LibraryImport(L, EntryPoint = "adjust_camera_raw_sharpen_mask_overlay")]
    internal static partial void CameraRawSharpenMaskOverlay(IntPtr rgba, nuint width, nuint height, nuint stride,
        double sharpenRadius, double sharpenDetail, double sharpenMasking, double scale);

    /// <summary>Chromatic aberration, lens distortion, defringe and lens-vignetting correction.</summary>
    [LibraryImport(L, EntryPoint = "adjust_camera_raw_optics")]
    internal static partial void CameraRawOptics(IntPtr rgba, nuint width, nuint height, nuint stride,
        int removeChromatic, int lensProfile, double profileDistortion, double profileVignetting,
        double distortionK, double purpleAmount, double purpleHueLow, double purpleHueHigh,
        double greenAmount, double greenHueLow, double greenHueHigh,
        double vignetteAmount, double vignetteMidpoint, double scale,
        IntPtr exposureTables, int tableSize);

    /// <summary>Camera calibration before the main grade. Hue and saturation shifts are -100..100.</summary>
    [LibraryImport(L, EntryPoint = "adjust_camera_raw_calibration")]
    internal static partial void CameraRawCalibration(IntPtr rgba, nuint width, nuint height, nuint stride,
        double shadowTint, double redHue, double redSaturation,
        double greenHue, double greenSaturation, double blueHue, double blueSaturation,
        int processVersion);
}
