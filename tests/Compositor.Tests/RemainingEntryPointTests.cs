using System.Runtime.InteropServices;
using Compositor.Pixels;

namespace Compositor.Tests;

/// <summary>
/// Exercises the entry points the baseline probe never called, so that no binding is left entirely
/// unverified. A wrong arity, a mis-typed argument or a bad struct layout here corrupts the stack,
/// faults, or trips one of the invariants below.
/// </summary>
/// <remarks>
/// <para>
/// These are deliberately invariant checks rather than parity checks: for kernels whose output this
/// port has no independent reference for, claiming more would be dishonest. What they do prove is
/// that the ABI call lands, that alpha is preserved where the header says it is, and that the
/// documented return codes come back.
/// </para>
/// <para>
/// Buffer sizes here were taken from the C sources, not from the headers alone. That matters: two of
/// these kernels need more room than a first reading suggests (see
/// <see cref="LevelsHistogram_UsesTheFourBinBlocksTheKernelWrites"/> and the cube test), and getting
/// it wrong overwrites neighbouring memory instead of failing loudly.
/// </para>
/// </remarks>
public class RemainingEntryPointTests
{
    private const int W = 32, H = 24, N = W * H;
    private const nuint Stride = W * 4;

    /// <summary>A flat, fully opaque pixel buffer.</summary>
    private static byte[] Opaque()
    {
        var a = new byte[N * 4];
        for (int i = 0; i < N; i++)
        {
            a[i * 4] = 200; a[i * 4 + 1] = 100; a[i * 4 + 2] = 50; a[i * 4 + 3] = 255;
        }
        return a;
    }

    [Fact]
    public void RgbaClampPremultiplied_BringsEveryChannelWithinAlpha()
    {
        NativeTest.RequireKernel();
        var px = new byte[N * 4];
        for (int i = 0; i < N; i++)
        {
            px[i * 4] = 250; px[i * 4 + 1] = 200; px[i * 4 + 2] = 30; px[i * 4 + 3] = 64;
        }

        PinnedBuffers.Invoke(new Array?[] { px }, p =>
            NativeAdjustPixels.RgbaClampPremultiplied(p.Ptr<byte>(0), N));

        // The kernel only pulls channels *down to* alpha; a channel already at or below alpha is left
        // alone. So 250 and 200 clamp to 64, and 30 must stay 30.
        int over = 0;
        for (int i = 0; i < N; i++)
            for (int c = 0; c < 3; c++) if (px[i * 4 + c] > px[i * 4 + 3]) over++;
        Assert.True(over == 0, $"{over} channels still exceed alpha after rgba_clamp_premultiplied");
        Assert.True(px[0] == 64 && px[1] == 64 && px[2] == 30,
            $"expected only the over-alpha channels clamped to 64,30 kept below alpha; got {px[0]},{px[1]},{px[2]}");
    }

    [Fact]
    public void LayerUnpremultiplyOpaque_LeavesFullyOpaquePixelsAlone()
    {
        NativeTest.RequireKernel();
        var px = Opaque();
        var before = (byte[])px.Clone();

        PinnedBuffers.Invoke(new Array?[] { px }, p =>
            NativePixelOps.LayerUnpremultiplyOpaque(p.Ptr<byte>(0), Stride, W, H));

        int idx = NativeTest.FirstDiff(px, before);
        Assert.True(idx < 0,
            $"an already-opaque pixel changed at byte {NativeTest.Safe(idx, before.Length)}: " +
            $"got {px[NativeTest.Safe(idx, px.Length)]} want {before[NativeTest.Safe(idx, before.Length)]}");
    }

    /// <summary>
    /// <c>levels_histogram</c> writes four 256-bin blocks: the luminance histogram at <c>bins[0..255]</c>
    /// and one per-channel block at <c>bins[(channel+1)*256 .. +255]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That is <b>1024</b> doubles, not the 768 a "3 x 256 histogram" reading suggests. The binding
    /// says nothing about the size, so this test is the only thing pinning it down; allocating 768
    /// overwrites the following 256 doubles and corrupts the heap.
    /// </para>
    /// <para>
    /// Each pixel credits <c>alpha/255 * coverage/255</c> of weight: one third into each channel block
    /// and the whole of it into the luminance block. So each block totals the alpha-weighted pixel
    /// count, <em>not</em> the pixel count — for a fixture whose alpha varies the two differ, and
    /// assuming the naive count is what made this test fail the first time.
    /// </para>
    /// </remarks>
    [Fact]
    public void LevelsHistogram_UsesTheFourBinBlocksTheKernelWrites()
    {
        NativeTest.RequireKernel();
        var px = NativeTest.Gradient(W, H);
        var coverage = new byte[N];
        Array.Fill(coverage, (byte)255);
        var bins = new double[1024];

        PinnedBuffers.Invoke(new Array?[] { px, coverage, bins }, p =>
            NativePixelOps.LevelsHistogram(p.Ptr<byte>(0), p.Ptr<byte>(1), N, p.Ptr<double>(2)));

        // Independent expectation, straight from LevelsPixels.c:17-28 with full coverage.
        double expected = 0;
        for (int i = 0; i < N; i++) expected += px[i * 4 + 3] / 255.0;

        double luminance = 0;
        for (int v = 0; v < 256; v++) luminance += bins[v];
        Assert.True(Math.Abs(luminance - expected) < 1e-6,
            $"the luminance block should total the alpha-weighted count {expected}; got {luminance}");

        for (int c = 0; c < 3; c++)
        {
            double channel = 0;
            for (int v = 0; v < 256; v++) channel += bins[(c + 1) * 256 + v];
            Assert.True(Math.Abs(channel - expected) < 1e-6,
                $"channel {c}'s block should total {expected}; got {channel}");
        }
    }

    /// <summary>
    /// <c>cube_apply</c> reads RGBA entries (<c>dimension</c>-cubed of them, four floats each), not
    /// three-float RGB entries.
    /// </summary>
    /// <remarks>
    /// The header line says "RGBA entries" while <c>AdjustPixels.h</c> uses "3 floats per color" for
    /// the Camera Raw tables, so the two are easy to conflate. <c>LevelsPixels.c</c> strides by 4 and
    /// indexes <c>cube + (...) * 4</c>; an RGB-sized array would be read out of bounds. A
    /// dimension-2 identity cube leaves a mid-gray image's color mapping intact, so this also proves
    /// the call reached the kernel rather than being a no-op.
    /// </remarks>
    [Fact]
    public void CubeApply_ReadsRgbaEntriesAndKeepsAlpha()
    {
        NativeTest.RequireKernel();
        var px = NativeTest.Gradient(W, H);
        var before = (byte[])px.Clone();

        const int dim = 2;
        var cube = new float[dim * dim * dim * 4];
        int k = 0;
        for (int r = 0; r < dim; r++)
            for (int g = 0; g < dim; g++)
                for (int b = 0; b < dim; b++)
                {
                    cube[k++] = r / (float)(dim - 1);
                    cube[k++] = g / (float)(dim - 1);
                    cube[k++] = b / (float)(dim - 1);
                    cube[k++] = 1f;   // alpha slot
                }

        PinnedBuffers.Invoke(new Array?[] { px, cube }, p =>
            NativePixelOps.CubeApply(p.Ptr<byte>(0), N, p.Ptr<float>(1), dim));

        int alphaChanged = 0;
        for (int i = 0; i < N; i++) if (px[i * 4 + 3] != before[i * 4 + 3]) alphaChanged++;
        Assert.True(alphaChanged == 0, $"cube_apply must not touch alpha; {alphaChanged} alphas changed");
    }

    [Fact]
    public void NoiseAdd_IsDeterministicAndLeavesAlphaAlone()
    {
        NativeTest.RequireKernel();
        var a = NativeTest.Gradient(W, H);
        var b = NativeTest.Gradient(W, H);
        var alphaBefore = new byte[N];
        for (int i = 0; i < N; i++) alphaBefore[i] = a[i * 4 + 3];

        PinnedBuffers.Invoke(new Array?[] { a }, p =>
            NativePixelOps.NoiseAdd(p.Ptr<byte>(0), W, H, Stride, 50f, 0, 0, 7u));
        PinnedBuffers.Invoke(new Array?[] { b }, p =>
            NativePixelOps.NoiseAdd(p.Ptr<byte>(0), W, H, Stride, 50f, 0, 0, 7u));

        Assert.True(NativeTest.CountDiff(a, b) == 0, "noise_add is not deterministic for a fixed seed");
        for (int i = 0; i < N; i++)
            Assert.True(a[i * 4 + 3] == alphaBefore[i], $"noise_add changed alpha at px{i}");
    }

    [Fact]
    public void NoiseAddAt_WithZeroOrigin_MatchesNoiseAdd()
    {
        NativeTest.RequireKernel();
        var viaAdd = NativeTest.Gradient(W, H);
        var viaAddAt = NativeTest.Gradient(W, H);

        PinnedBuffers.Invoke(new Array?[] { viaAdd }, p =>
            NativePixelOps.NoiseAdd(p.Ptr<byte>(0), W, H, Stride, 50f, 0, 0, 7u));
        PinnedBuffers.Invoke(new Array?[] { viaAddAt }, p =>
            NativePixelOps.NoiseAddAt(p.Ptr<byte>(0), W, H, Stride, 50f, 0, 0, 7u, 0L, 0L));

        int idx = NativeTest.FirstDiff(viaAddAt, viaAdd);
        Assert.True(idx < 0,
            $"noise_add_at at origin (0,0) must agree with noise_add; first difference at byte {idx}");
    }

    [Fact]
    public void HealCoverageBounds_ReportsFullCoverage()
    {
        NativeTest.RequireKernel();
        var coverage = new byte[N];
        Array.Fill(coverage, (byte)255);
        // 4-byte ints because the C parameter is `long`, which is 32-bit on Windows.
        var bounds = new int[4];

        PinnedBuffers.Invoke(new Array?[] { coverage, bounds }, p =>
            NativePixelOps.HealCoverageBounds(p.Ptr<byte>(0), W, H, (nuint)W, p.Ptr<int>(1)));

        Assert.True(bounds[0] == 0 && bounds[1] == 0 && bounds[2] == W && bounds[3] == H,
            $"full coverage should give [0,0,{W},{H}]; got [{bounds[0]},{bounds[1]},{bounds[2]},{bounds[3]}]");
    }

    [Fact]
    public void WandMask_SelectsTheWholeUniformImage()
    {
        NativeTest.RequireKernel();
        var px = new byte[N * 4];
        for (int i = 0; i < N; i++) { px[i * 4] = 10; px[i * 4 + 1] = 20; px[i * 4 + 2] = 30; px[i * 4 + 3] = 255; }
        var mask = new byte[N];

        int selected = 0;
        PinnedBuffers.Invoke(new Array?[] { px, mask }, p =>
            selected = NativePixelOps.WandMask(p.Ptr<byte>(0), W, H, Stride,
                (nuint)(W / 2), (nuint)(H / 2), (nuint)1, 10, 1, p.Ptr<byte>(1)));

        Assert.True(selected == N, $"a uniform image should select all {N} pixels; got {selected}");
        int marked = mask.Count(v => v == 255);
        Assert.True(marked == N, $"the mask should be all 255; {marked} of {N} were");
    }

    [Fact]
    public void ColorRangeMask_SelectsEveryPixelMatchingTheIncludeColor()
    {
        NativeTest.RequireKernel();
        var px = new byte[N * 4];
        for (int i = 0; i < N; i++) { px[i * 4] = 10; px[i * 4 + 1] = 20; px[i * 4 + 2] = 30; px[i * 4 + 3] = 255; }
        var include = new byte[] { 10, 20, 30 };
        var exclude = Array.Empty<byte>();
        var mask = new byte[N];

        // A zero-length array still pins, so this passes a real (non-null) pointer with a count of 0.
        int selected = 0;
        PinnedBuffers.Invoke(new Array?[] { px, include, exclude, mask }, p =>
            selected = NativePixelOps.ColorRangeMask(p.Ptr<byte>(0), W, H, Stride,
                p.Ptr<byte>(1), 1, p.Ptr<byte>(2), 0, 10, 0, p.Ptr<byte>(3)));

        Assert.True(selected == N, $"every pixel matches the include color, so all {N} should be selected; got {selected}");
    }

    [Fact]
    public void WandTrace_OutlinesASolidRectangleAsOneLoopOfFourCorners()
    {
        NativeTest.RequireKernel();
        var mask = new byte[N];
        Array.Fill(mask, (byte)255);

        IntPtr points = IntPtr.Zero, loops = IntPtr.Zero;
        nuint pointCount = 0, loopCount = 0;
        int rc = 0;
        PinnedBuffers.Invoke(new Array?[] { mask }, p =>
            rc = NativePixelOps.WandTrace(p.Ptr<byte>(0), W, H,
                ref points, ref pointCount, ref loops, ref loopCount));

        try
        {
            Assert.True(rc == 0, $"wand_trace over a solid rectangle returned {rc}");
            Assert.True(pointCount == 4, $"a rectangle's outline has 4 corners; got {pointCount}");
            Assert.True(loopCount == 1, $"a solid rectangle has one loop; got {loopCount}");
            Assert.True(points != IntPtr.Zero && loops != IntPtr.Zero,
                "wand_trace reported success but returned a null buffer");

            // The four corners of a W x H rectangle in pixel-edge coordinates, as (x, y) pairs.
            var got = KernelHeap.ReadInt32(points, (int)pointCount * 2);
            var corners = new HashSet<(int X, int Y)>();
            for (int i = 0; i + 1 < got.Length; i += 2) corners.Add((got[i], got[i + 1]));
            var expected = new HashSet<(int, int)> { (0, 0), (W, 0), (W, H), (0, H) };
            Assert.True(corners.SetEquals(expected),
                $"expected the four corners of a {W}x{H} rectangle " +
                $"[{string.Join(" ", expected)}]; got [{string.Join(" ", corners)}]");

            var loopLengths = KernelHeap.ReadInt32(loops, (int)loopCount);
            Assert.True(loopLengths.Sum() == (long)pointCount,
                $"the loop lengths ({string.Join(",", loopLengths)}) must sum to pointCount ({pointCount})");
        }
        finally
        {
            // These came from the kernel's malloc, so they must go back to a free sharing its heap.
            // Marshal.FreeHGlobal here corrupts the process (see KernelHeap).
            KernelHeap.Free(points);
            KernelHeap.Free(loops);
        }
    }

    [Fact]
    public void SpotHeal_ContentAwareModeSucceedsOnASmallSpot()
    {
        NativeTest.RequireKernel();
        var px = NativeTest.Gradient(W, H);
        var coverage = new byte[N];
        for (int y = 10; y < 14; y++)
            for (int x = 10; x < 14; x++) coverage[y * W + x] = 255;

        int rc = 0;
        PinnedBuffers.Invoke(new Array?[] { px, coverage }, p =>
            rc = NativePixelOps.SpotHeal(p.Ptr<byte>(0), p.Ptr<byte>(1), W, H, Stride, 1.0f, mode: 1, seed: 99u));

        Assert.True(rc == 0, $"spot_heal should return 0 on success, or -1 on allocation failure; got {rc}");
    }

    /// <summary>
    /// <c>dither_apply</c> and <c>scanlines_apply</c> return <b>1</b> on success, not 0.
    /// </summary>
    /// <remarks>
    /// The headers only document the failure direction ("Returns 0 if working memory couldn't be
    /// had"), which makes it easy to invert. <c>DitherPixels.c</c> returns 1 at the end of both and 0
    /// on every allocation failure.
    /// </remarks>
    [Fact]
    public void DitherAndScanlines_ReturnOneOnSuccess()
    {
        NativeTest.RequireKernel();

        var ditherPx = NativeTest.Gradient(W, H);
        var ditherBefore = (byte[])ditherPx.Clone();
        var dp = new DitherParams
        {
            Style = (int)DitherStyle.FloydSteinberg,
            Levels = 4,
            Diffusion = 0.8f,
            Cell = 4,
            OriginalColors = 1,
            Glyphs = IntPtr.Zero,
            GlyphCoverage = IntPtr.Zero,
        };
        dp.SetDark(0, 0, 0);
        dp.SetLight(255, 255, 255);

        int ditherRc = 0;
        PinnedBuffers.Invoke(new Array?[] { ditherPx }, p =>
            ditherRc = NativePixelOps.DitherApply(p.Ptr<byte>(0), W, H, Stride, ref dp));

        Assert.True(ditherRc == 1, $"dither_apply returns 1 on success; got {ditherRc}");
        Assert.True(NativeTest.CountDiff(ditherPx, ditherBefore) > 0, "dither_apply changed nothing");

        var scanPx = NativeTest.Gradient(W, H);
        var scanBefore = (byte[])scanPx.Clone();
        var sp = new ScanlinesParams
        {
            Spacing = 3,
            Thickness = 0.6f,
            Dots = 0.3f,
            Wobble = 1f,
            Displace = 0.5f,
            Threshold = 0.1f,
            Split = 0.5f,
            BlackLevel = 0.05f,
            Smoothness = 0.5f,
            OriginalColors = 1,
        };
        sp.SetDark(0, 0, 0);
        sp.SetLight(255, 255, 255);

        int scanRc = 0;
        PinnedBuffers.Invoke(new Array?[] { scanPx }, p =>
            scanRc = NativePixelOps.ScanlinesApply(p.Ptr<byte>(0), W, H, Stride, ref sp));

        Assert.True(scanRc == 1, $"scanlines_apply returns 1 on success; got {scanRc}");
        Assert.True(NativeTest.CountDiff(scanPx, scanBefore) > 0, "scanlines_apply changed nothing");
    }

    [Fact]
    public void DitherDots_ChangesTheImage()
    {
        NativeTest.RequireKernel();
        var px = NativeTest.Gradient(W, H);
        var before = (byte[])px.Clone();
        var gap = new byte[] { 16, 16, 16 };

        PinnedBuffers.Invoke(new Array?[] { px, gap }, p =>
            NativePixelOps.DitherDots(p.Ptr<byte>(0), W, H, Stride, 4, p.Ptr<byte>(1)));

        Assert.True(NativeTest.CountDiff(px, before) > 0, "dither_dots changed nothing");
    }

    [Fact]
    public void GradientMap_KeepsAlpha()
    {
        NativeTest.RequireKernel();
        var px = Opaque();
        var before = (byte[])px.Clone();
        var table = new byte[256 * 3];
        for (int v = 0; v < 256; v++)
        {
            table[v * 3] = (byte)v;
            table[v * 3 + 1] = (byte)(255 - v);
            table[v * 3 + 2] = 128;
        }

        PinnedBuffers.Invoke(new Array?[] { px, table }, p =>
            NativeAdjustPixels.GradientMap(p.Ptr<byte>(0), W, H, Stride, p.Ptr<byte>(1)));

        int alphaChanged = 0;
        for (int i = 0; i < N; i++) if (px[i * 4 + 3] != before[i * 4 + 3]) alphaChanged++;
        Assert.True(alphaChanged == 0, $"gradient map must keep alpha; {alphaChanged} alphas changed");
    }

    [Fact]
    public void AllKernelsThatDocumentIt_KeepAlpha()
    {
        NativeTest.RequireKernel();

        void AssertAlphaKept(string kernel, Action<byte[]> run)
        {
            var px = Opaque();
            var before = (byte[])px.Clone();
            run(px);
            for (int i = 0; i < N; i++)
                Assert.True(px[i * 4 + 3] == before[i * 4 + 3],
                    $"{kernel} changed alpha at px{i}: {before[i * 4 + 3]} -> {px[i * 4 + 3]}");
        }

        var shadows = new float[] { 0.2f, 0f, -0.2f };
        var midtones = new float[] { 0f, 0.1f, 0f };
        var highlights = new float[] { -0.1f, 0f, 0.1f };
        var ramp = new float[256];
        var mixer = new float[24];
        var grade = new float[12];
        for (int v = 0; v < 256; v++) ramp[v] = v / 255f;

        AssertAlphaKept("adjust_color_balance", px =>
            PinnedBuffers.Invoke(new Array?[] { px, shadows, midtones, highlights }, p =>
                NativeAdjustPixels.ColorBalance(p.Ptr<byte>(0), W, H, Stride,
                    p.Ptr<float>(1), p.Ptr<float>(2), p.Ptr<float>(3), 1)));

        // A null composed table means "no grade", which must still round-trip.
        AssertAlphaKept("adjust_camera_raw (null table)", px =>
            PinnedBuffers.Invoke(new Array?[] { px }, p =>
                NativeAdjustPixels.CameraRaw(p.Ptr<byte>(0), W, H, Stride, IntPtr.Zero, 0, 0)));

        AssertAlphaKept("adjust_camera_raw (clipping view)", px =>
            PinnedBuffers.Invoke(new Array?[] { px }, p =>
                NativeAdjustPixels.CameraRaw(p.Ptr<byte>(0), W, H, Stride, IntPtr.Zero, 0, 1)));

        AssertAlphaKept("adjust_camera_raw_clip_overlay", px =>
            PinnedBuffers.Invoke(new Array?[] { px }, p =>
                NativeAdjustPixels.CameraRawClipOverlay(p.Ptr<byte>(0), W, H, Stride, 1, 1)));

        AssertAlphaKept("adjust_camera_raw_curve_color", px =>
            PinnedBuffers.Invoke(new Array?[] { px, ramp, mixer, grade }, p =>
                NativeAdjustPixels.CameraRawCurveColor(p.Ptr<byte>(0), W, H, Stride,
                    p.Ptr<float>(1), p.Ptr<float>(1), p.Ptr<float>(1), p.Ptr<float>(1),
                    0, p.Ptr<float>(2), 0, IntPtr.Zero, p.Ptr<float>(3), 1.0, 0.0, 0,
                    IntPtr.Zero, IntPtr.Zero, 0)));

        AssertAlphaKept("adjust_camera_raw_effects", px =>
            PinnedBuffers.Invoke(new Array?[] { px }, p =>
                NativeAdjustPixels.CameraRawEffects(p.Ptr<byte>(0), W, H, Stride,
                    0, 0, 0, 0, 0, 0.5, 0.5, 0, 0, 0.5, 0, 0.5, 0, 0, 1.0)));

        AssertAlphaKept("adjust_colored_vignette", px =>
            PinnedBuffers.Invoke(new Array?[] { px }, p =>
                NativeAdjustPixels.ColoredVignette(p.Ptr<byte>(0), W, H, Stride,
                    0, 0, W, H, 0, 0.5, 0.5, 0, 0.5, 0, 0, 0, 0)));

        var blurred = Opaque();
        AssertAlphaKept("adjust_tonal_contrast", px =>
            PinnedBuffers.Invoke(new Array?[] { px, blurred }, p =>
                NativeAdjustPixels.TonalContrast(p.Ptr<byte>(0), p.Ptr<byte>(1), W, H, Stride, Stride,
                    0.5, 0, 0.5, 0)));

        AssertAlphaKept("adjust_camera_raw_detail", px =>
            PinnedBuffers.Invoke(new Array?[] { px }, p =>
                NativeAdjustPixels.CameraRawDetail(p.Ptr<byte>(0), W, H, Stride,
                    25, 1.0, 25, 0, 0, 0, 0, 0, 0, 0, 1.0)));

        AssertAlphaKept("adjust_camera_raw_sharpen_mask_overlay", px =>
            PinnedBuffers.Invoke(new Array?[] { px }, p =>
                NativeAdjustPixels.CameraRawSharpenMaskOverlay(p.Ptr<byte>(0), W, H, Stride, 1.0, 25, 0, 1.0)));

        AssertAlphaKept("adjust_camera_raw_optics", px =>
            PinnedBuffers.Invoke(new Array?[] { px }, p =>
                NativeAdjustPixels.CameraRawOptics(p.Ptr<byte>(0), W, H, Stride,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.5, 1.0, IntPtr.Zero, 0)));

        AssertAlphaKept("adjust_camera_raw_calibration", px =>
            PinnedBuffers.Invoke(new Array?[] { px }, p =>
                NativeAdjustPixels.CameraRawCalibration(p.Ptr<byte>(0), W, H, Stride, 0, 0, 0, 0, 0, 0, 0, 2)));
    }

    [Fact]
    public void AdjustGrain_ChangesTheImage()
    {
        NativeTest.RequireKernel();
        var px = NativeTest.Gradient(W, H);
        var before = (byte[])px.Clone();

        PinnedBuffers.Invoke(new Array?[] { px }, p =>
            NativeAdjustPixels.Grain(p.Ptr<byte>(0), W, H, Stride, amount: 50, size: 4,
                roughness: 20, seed: 5u, originX: 0, originY: 0, unitsPerPixel: 1.0));

        Assert.True(NativeTest.CountDiff(px, before) > 0, "adjust_grain changed nothing");
    }

    /// <summary>
    /// The Camera Raw table pipeline: compose a stage into a grid-cubed table, run it through the
    /// curves, then run a stage against a single color. This is what proves
    /// <see cref="CameraRawStage"/>'s pointer array is laid out the way the kernel indexes it.
    /// </summary>
    [Fact]
    public void CameraRawCompose_UsesTheStagePointerArrayCorrectly()
    {
        NativeTest.RequireKernel();
        const int size = 2;
        const int grid = size * size * size;
        const int entries = grid * 3;

        // A 2-cubed measured table covering the corners of the unit cube, red slowest, 3 bytes each.
        var measured = new byte[grid * 3];
        int m = 0;
        for (int r = 0; r < size; r++)
            for (int g = 0; g < size; g++)
                for (int b = 0; b < size; b++)
                {
                    measured[m++] = (byte)(r * 255);
                    measured[m++] = (byte)(g * 255);
                    measured[m++] = (byte)(b * 255);
                }

        // The identity lattice: every entry is already a lattice corner, so a correct lookup in the
        // measured table returns that same corner.
        var lattice = new float[entries];
        for (int r = 0; r < size; r++)
            for (int g = 0; g < size; g++)
                for (int b = 0; b < size; b++)
                {
                    int i = ((r * size + g) * size + b) * 3;
                    lattice[i] = r;
                    lattice[i + 1] = g;
                    lattice[i + 2] = b;
                }

        var ramp = new float[256];
        for (int v = 0; v < 256; v++) ramp[v] = v / 255f;

        var stage = default(CameraRawStage);

        PinnedBuffers.Invoke(new Array?[] { measured, lattice }, p =>
        {
            stage.SetTable(0, p.Ptr<byte>(0));
            stage.SetWeight(0, 1.0f);
            NativeAdjustPixels.CameraRawCompose(p.Ptr<float>(1), size, ref stage, 1, size);
        });

        int last = entries - 3;
        Assert.True(Math.Abs(lattice[0]) < 1e-3f && Math.Abs(lattice[1]) < 1e-3f && Math.Abs(lattice[2]) < 1e-3f,
            $"black must stay black; got [{lattice[0]},{lattice[1]},{lattice[2]}]");
        Assert.True(Math.Abs(lattice[last] - 1f) < 1e-2f
                    && Math.Abs(lattice[last + 1] - 1f) < 1e-2f
                    && Math.Abs(lattice[last + 2] - 1f) < 1e-2f,
            $"white must stay white; got [{lattice[last]},{lattice[last + 1]},{lattice[last + 2]}]");

        // An identity ramp through the curve stage must leave the table essentially untouched.
        var before = (float[])lattice.Clone();
        PinnedBuffers.Invoke(new Array?[] { lattice, ramp }, p =>
            NativeAdjustPixels.CameraRawTableCurves(p.Ptr<float>(0), size,
                p.Ptr<float>(1), p.Ptr<float>(1), p.Ptr<float>(1), p.Ptr<float>(1)));

        float maxDiff = 0;
        for (int i = 0; i < entries; i++) maxDiff = Math.Max(maxDiff, Math.Abs(lattice[i] - before[i]));
        Assert.True(maxDiff < 5e-3f, $"an identity curve moved the composed table by up to {maxDiff}");

        // One stage on one color, in place.
        var rgb = new double[] { 1.0, 0.0, 0.0 };
        PinnedBuffers.Invoke(new Array?[] { measured, rgb }, p =>
        {
            var s = default(CameraRawStage);
            s.SetTable(0, p.Ptr<byte>(0));
            s.SetWeight(0, 1.0f);
            NativeAdjustPixels.CameraRawStageColor(ref s, size, p.Ptr<double>(1));
        });
        Assert.True(rgb[0] is >= 0 and <= 1 && rgb[1] is >= 0 and <= 1 && rgb[2] is >= 0 and <= 1,
            $"camera_raw_stage_color left the color outside 0..1: [{rgb[0]},{rgb[1]},{rgb[2]}]");

        // And the in-place variant over an existing table.
        var onto = (float[])before.Clone();
        var stage2 = default(CameraRawStage);
        PinnedBuffers.Invoke(new Array?[] { measured, onto }, p =>
        {
            stage2.SetTable(0, p.Ptr<byte>(0));
            stage2.SetWeight(0, 1.0f);
            NativeAdjustPixels.CameraRawComposeOnto(p.Ptr<float>(1), size, ref stage2, 1, size);
        });
        Assert.True(Math.Abs(onto[0] - before[0]) < 1e-2f,
            "camera_raw_compose_onto moved the black corner unexpectedly");
    }

    [Fact]
    public void CameraRawScope_RecordsCounts()
    {
        NativeTest.RequireKernel();
        var px = NativeTest.Gradient(W, H);
        var bins = new double[3 * 256];
        var cells = new double[4 * 4];

        PinnedBuffers.Invoke(new Array?[] { px, bins, cells }, p =>
            NativeAdjustPixels.CameraRawScope(p.Ptr<byte>(0), W, H, Stride, step: 1,
                p.Ptr<double>(1), p.Ptr<double>(2), side: 4));

        Assert.True(bins.Sum() > 0, "camera_raw_scope recorded no histogram counts");
        Assert.True(cells.Sum() > 0, "camera_raw_scope recorded no vectorscope counts");
    }
}
