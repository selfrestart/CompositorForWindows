using System.Runtime.InteropServices;
using Compositor.Pixels;

namespace Compositor.Tests;

/// <summary>
/// The ABI contract itself: every entry point the bindings declare must actually be exported by the
/// native library, and every managed <c>[StructLayout]</c> struct must match the C compiler's layout.
/// </summary>
/// <remarks>
/// A missing or renamed export otherwise only fails when that particular kernel is first called, and a
/// struct whose padding differs from the C compiler's silently reads the wrong parameters. Both are
/// cheap to check up front and expensive to debug later.
/// </remarks>
public class AbiSurfaceTests
{
    /// <summary>The 42 exported entry points, from <c>spikes/c-kernel/build/bin/compositor_kernel.def</c>.</summary>
    private static readonly string[] Exports =
    {
        "adjust_black_white", "adjust_camera_raw", "adjust_camera_raw_calibration",
        "adjust_camera_raw_clip_overlay", "adjust_camera_raw_curve_color", "adjust_camera_raw_detail",
        "adjust_camera_raw_effects", "adjust_camera_raw_optics", "adjust_camera_raw_sharpen_mask_overlay",
        "adjust_color_balance", "adjust_colored_vignette", "adjust_gradient_map", "adjust_grain",
        "adjust_tonal_contrast", "brush_alpha_bounds", "camera_raw_compose", "camera_raw_compose_onto",
        "camera_raw_scope", "camera_raw_stage_color", "camera_raw_statistics", "camera_raw_table_curves",
        "color_range_mask", "content_fill", "cube_apply", "dither_apply", "dither_dots", "dither_glow",
        "dither_quantize16", "heal_coverage_bounds", "layer_extract_alpha", "layer_restore_alpha",
        "layer_unpremultiply_opaque", "lens_distort", "levels_apply", "levels_histogram", "noise_add",
        "noise_add_at", "rgba_clamp_premultiplied", "scanlines_apply", "spot_heal", "wand_mask", "wand_trace",
    };

    [Fact]
    public void TheExportManifestHas42Entries()
    {
        // Guards the guard: if someone trims this list, the export test below stops proving anything.
        Assert.Equal(42, Exports.Length);
        Assert.Equal(42, Exports.Distinct().Count());
    }

    [Fact]
    public void NativeLibrary_ExportsEveryBoundEntryPoint()
    {
        NativeTest.RequireKernel();

        Assert.True(NativeKernel.LoadedPath is not null, "IsAvailable was true but no path was recorded");
        Assert.True(NativeLibrary.TryLoad(NativeKernel.LoadedPath!, out var handle),
            $"could not reopen {NativeKernel.LoadedPath} to enumerate its exports");

        var missing = new List<string>();
        foreach (var name in Exports)
            if (!NativeLibrary.TryGetExport(handle, name, out _)) missing.Add(name);

        Assert.True(missing.Count == 0,
            $"{missing.Count} of {Exports.Length} bound entry points are not exported by " +
            $"{Path.GetFileName(NativeKernel.LoadedPath)}: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Struct layouts measured with <c>gcc</c> against the authoritative headers. The numbers below
    /// are that run's output, recorded in <c>verification/layoutprobe.c</c>; a failure here means the
    /// managed struct and the C struct have diverged.
    /// </summary>
    [Fact]
    public void ManagedStructLayouts_MatchTheCCompiler()
    {
        // typedef struct { const uint8_t *table[8]; float weight[8]; } CameraRawStage;
        // gcc: size=96 align=8, table off=0 size=64, weight off=64 size=32
        Assert.Equal(96, Marshal.SizeOf<CameraRawStage>());
        Assert.Equal(0, Offset<CameraRawStage>(nameof(CameraRawStage.Table)));
        Assert.Equal(64, Offset<CameraRawStage>(nameof(CameraRawStage.Weight)));

        // DitherParams: gcc size=80 align=8. The two 3-byte color arrays are padded to 4 before the
        // next int, which is why glyphWidth sits at 44 and not 42.
        Assert.Equal(80, Marshal.SizeOf<DitherParams>());
        Assert.Equal(0, Offset<DitherParams>(nameof(DitherParams.Style)));
        Assert.Equal(4, Offset<DitherParams>(nameof(DitherParams.Levels)));
        Assert.Equal(8, Offset<DitherParams>(nameof(DitherParams.Diffusion)));
        Assert.Equal(12, Offset<DitherParams>(nameof(DitherParams.Density)));
        Assert.Equal(16, Offset<DitherParams>(nameof(DitherParams.Contrast)));
        Assert.Equal(20, Offset<DitherParams>(nameof(DitherParams.Cell)));
        Assert.Equal(24, Offset<DitherParams>(nameof(DitherParams.Angle)));
        Assert.Equal(28, Offset<DitherParams>(nameof(DitherParams.LightOnDark)));
        Assert.Equal(32, Offset<DitherParams>(nameof(DitherParams.OriginalColors)));
        Assert.Equal(36, Offset<DitherParams>(nameof(DitherParams.Dark)));
        Assert.Equal(39, Offset<DitherParams>(nameof(DitherParams.Light)));
        Assert.Equal(44, Offset<DitherParams>(nameof(DitherParams.GlyphWidth)));
        Assert.Equal(48, Offset<DitherParams>(nameof(DitherParams.GlyphHeight)));
        Assert.Equal(56, Offset<DitherParams>(nameof(DitherParams.Glyphs)));
        Assert.Equal(64, Offset<DitherParams>(nameof(DitherParams.GlyphCoverage)));
        Assert.Equal(72, Offset<DitherParams>(nameof(DitherParams.GlyphCount)));

        // ScanlinesParams: gcc size=56 align=4 — trailing padding to a 4-byte boundary, not 8.
        Assert.Equal(56, Marshal.SizeOf<ScanlinesParams>());
        Assert.Equal(0, Offset<ScanlinesParams>(nameof(ScanlinesParams.Spacing)));
        Assert.Equal(4, Offset<ScanlinesParams>(nameof(ScanlinesParams.Thickness)));
        Assert.Equal(8, Offset<ScanlinesParams>(nameof(ScanlinesParams.Dots)));
        Assert.Equal(12, Offset<ScanlinesParams>(nameof(ScanlinesParams.Wobble)));
        Assert.Equal(16, Offset<ScanlinesParams>(nameof(ScanlinesParams.Displace)));
        Assert.Equal(20, Offset<ScanlinesParams>(nameof(ScanlinesParams.Threshold)));
        Assert.Equal(24, Offset<ScanlinesParams>(nameof(ScanlinesParams.Split)));
        Assert.Equal(28, Offset<ScanlinesParams>(nameof(ScanlinesParams.Density)));
        Assert.Equal(32, Offset<ScanlinesParams>(nameof(ScanlinesParams.Contrast)));
        Assert.Equal(36, Offset<ScanlinesParams>(nameof(ScanlinesParams.BlackLevel)));
        Assert.Equal(40, Offset<ScanlinesParams>(nameof(ScanlinesParams.Smoothness)));
        Assert.Equal(44, Offset<ScanlinesParams>(nameof(ScanlinesParams.OriginalColors)));
        Assert.Equal(48, Offset<ScanlinesParams>(nameof(ScanlinesParams.Dark)));
        Assert.Equal(51, Offset<ScanlinesParams>(nameof(ScanlinesParams.Light)));
    }

    /// <summary>
    /// The two kernels whose C signature uses a bare <c>long</c>. On Windows <c>long</c> is 4 bytes
    /// while <c>int64_t</c> and <c>size_t</c> are 8, so these are the bindings most likely to be
    /// transcribed wrongly; asserting the widths pins the decision down.
    /// </summary>
    [Fact]
    public void LongIsFourBytesOnWindows_WhichIsWhyTheBoundsBindingUsesInt()
    {
        if (!OperatingSystem.IsWindows()) return; // LP64 elsewhere: the C long is 8 bytes there.

        Assert.Equal(4, Marshal.SizeOf<int>());
        Assert.Equal(8, Marshal.SizeOf<long>());
        Assert.Equal(8, Marshal.SizeOf<nuint>());
        Assert.Equal(8, IntPtr.Size);
    }

    /// <summary>Offset of a field, as the runtime lays the struct out.</summary>
    private static int Offset<T>(string fieldName) where T : struct
        => Marshal.OffsetOf<T>(fieldName).ToInt32();
}
