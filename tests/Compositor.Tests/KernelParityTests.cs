using Compositor.Pixels;

namespace Compositor.Tests;

/// <summary>
/// Numeric parity of the native C pixel kernel against independent C# reference implementations.
/// </summary>
/// <remarks>
/// <para>
/// The assertion set mirrors <c>spikes/c-kernel/KernelProbe/Program.cs</c>, which was the verified
/// baseline on this machine. Every test either re-derives the kernel's math in C# from the C source,
/// or checks an invariant any correct implementation must satisfy.
/// </para>
/// <para>
/// Each test skips (not fails) when the native library is absent, so a clean checkout with no C
/// toolchain still reports a green suite with a visible skipped count.
/// </para>
/// </remarks>
public class KernelParityTests
{
    // ================================================================ lens_distort

    [Fact]
    public void LensDistort_ZeroK_IsExactIdentity()
    {
        NativeTest.RequireKernel();
        const int w = 64, h = 48;
        var src = NativeTest.Gradient(w, h);
        var dst = new byte[src.Length];

        PinnedBuffers.Invoke(new Array?[] { src, dst }, p =>
            NativePixelOps.LensDistort(p.Ptr<byte>(0), p.Ptr<byte>(1), w, h, (nuint)(w * 4), 0.0));

        int diff = NativeTest.CountDiff(dst, src);
        Assert.True(diff == 0, $"lens_distort k=0 must copy the source exactly; {diff} of {src.Length} bytes differ");
    }

    [Fact]
    public void LensDistort_ResamplesAndPinsCentre()
    {
        NativeTest.RequireKernel();
        const int w = 64, h = 48;
        var src = NativeTest.Gradient(w, h);
        var dst = new byte[src.Length];

        PinnedBuffers.Invoke(new Array?[] { src, dst }, p =>
            NativePixelOps.LensDistort(p.Ptr<byte>(0), p.Ptr<byte>(1), w, h, (nuint)(w * 4), 0.35));

        int moved = 0;
        for (int i = 0; i < src.Length; i += 4)
            if (src[i] != dst[i] || src[i + 1] != dst[i + 1] || src[i + 2] != dst[i + 2]) moved++;

        // The centre pixel's offset from the centre is zero, so k cannot move it.
        int ci = ((h / 2) * w + (w / 2)) * 4;
        bool centreHeld = src[ci] == dst[ci] && src[ci + 1] == dst[ci + 1]
                          && src[ci + 2] == dst[ci + 2] && src[ci + 3] == dst[ci + 3];

        Assert.True(moved > src.Length / 4 / 2, $"k=0.35 should resample most pixels; only {moved} of {src.Length / 4} moved");
        Assert.True(centreHeld, "the exact centre pixel must be unchanged at k=0.35");
    }

    // ================================================================ levels_apply

    [Fact]
    public void LevelsApply_MatchesIndependentReferenceExactly()
    {
        NativeTest.RequireKernel();
        const int w = 32, h = 32;
        const int n = w * h;
        var px = NativeTest.Gradient(w, h);
        var orig = (byte[])px.Clone();

        // Linear ramp tables, one per channel: table[c*256+v] = v * factor.
        var tables = new float[3 * 256];
        float[] factor = { 1.0f, 0.5f, 0.25f };
        for (int c = 0; c < 3; c++)
            for (int v = 0; v < 256; v++) tables[c * 256 + v] = v * factor[c];

        PinnedBuffers.Invoke(new Array?[] { px, tables }, p =>
            NativePixelOps.LevelsApply(p.Ptr<byte>(0), n, p.Ptr<float>(1)));

        // Independent reference, transcribed from LevelsPixels.c.
        int mismatch = 0; string firstBad = "";
        for (int i = 0; i < n; i++)
        {
            float alpha = orig[i * 4 + 3];
            if (alpha == 0) continue;
            for (int c = 0; c < 3; c++)
            {
                float x = MathF.Min(255f, orig[i * 4 + c] * 255.0f / alpha);
                int lo = (int)x, hi = lo < 255 ? lo + 1 : 255;
                float result = tables[c * 256 + lo] + (tables[c * 256 + hi] - tables[c * 256 + lo]) * (x - lo);
                byte expect = (byte)MathF.Min(alpha, MathF.Max(0, MathF.Round(result * alpha, MidpointRounding.AwayFromZero)));
                if (px[i * 4 + c] != expect)
                {
                    mismatch++;
                    if (firstBad.Length == 0) firstBad = $"px{i} ch{c}: got {px[i * 4 + c]} want {expect} (a={alpha})";
                }
            }
        }

        Assert.True(mismatch == 0, $"{n * 3} channel writes, {mismatch} mismatched. {firstBad}");
    }

    // ================================================================ dither_quantize16

    [Fact]
    public void DitherQuantize16_MatchesReferenceByteForByte()
    {
        NativeTest.RequireKernel();
        // Runs its row loop through the band helper rewritten from Clang blocks to plain C, so it is
        // the sharpest available test that the port is faithful.
        const int w = 70, h = 45;
        var wide = new ushort[w * h * 4];
        var rnd = new Random(1234);
        for (int i = 0; i < wide.Length; i++) wide[i] = (ushort)rnd.Next(0, 65536);

        var got = new byte[w * h * 4];
        PinnedBuffers.Invoke(new Array?[] { wide, got }, p =>
            NativePixelOps.DitherQuantize16(p.Ptr<ushort>(0), p.Ptr<byte>(1), w, h, (nuint)(w * 4)));

        // Independent reference, transcribed from DitherPixels.c.
        var want = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                long alpha = CRef.LroundF(wide[i + 3] * (255.0f / 65535.0f));
                for (uint c = 0; c < 3; c++)
                {
                    float value = wide[i + c] * (255.0f / 65535.0f)
                                  + CRef.HashNoise((uint)x, (uint)y, c)
                                  + CRef.HashNoise((uint)x, (uint)y, c + 3) - 1.0f;
                    long rounded = CRef.LroundF(value);
                    want[i + c] = (byte)(rounded < 0 ? 0 : rounded > alpha ? alpha : rounded);
                }
                want[i + 3] = (byte)alpha;
            }

        int idx = NativeTest.FirstDiff(got, want);
        Assert.True(idx < 0,
            $"{got.Length} bytes compared, first difference at byte {idx} " +
            $"(px{idx / 4} c{idx % 4}): got {got[NativeTest.Safe(idx, got.Length)]} " +
            $"want {want[NativeTest.Safe(idx, want.Length)]}");
    }

    // ================================================================ camera_raw_statistics

    [Fact]
    public void CameraRawStatistics_MatchesSingleLoopReference()
    {
        NativeTest.RequireKernel();
        // 90 rows over a 32-row band helper: an independent single loop proves the band partition
        // covers every row exactly once. h is deliberately not a multiple of 32.
        const int w = 61, h = 90;
        var px = NativeTest.Gradient(w, h);
        var got = new double[3];
        PinnedBuffers.Invoke(new Array?[] { px, got }, p =>
            NativeAdjustPixels.CameraRawStatistics(p.Ptr<byte>(0), w, h, (nuint)(w * 4), p.Ptr<double>(1)));

        var decoded = new double[256];
        for (int i = 0; i < 256; i++) decoded[i] = CRef.SrgbDecode(i / 255.0);
        double brightest = 0, luminance = 0, logBrightest = 0, weight = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                double alpha = px[i + 3];
                if (alpha == 0) continue;
                double r, g, b;
                if (px[i + 3] == 255)
                {
                    r = decoded[px[i]]; g = decoded[px[i + 1]]; b = decoded[px[i + 2]];
                }
                else
                {
                    r = CRef.SrgbDecode(Math.Min(1.0, px[i] / alpha));
                    g = CRef.SrgbDecode(Math.Min(1.0, px[i + 1] / alpha));
                    b = CRef.SrgbDecode(Math.Min(1.0, px[i + 2] / alpha));
                }
                double top = Math.Max(r, Math.Max(g, b)), wt = alpha / 255.0;
                brightest += wt * top;
                luminance += wt * CRef.Rec709(r, g, b);
                logBrightest += wt * Math.Log(top + 3e-4);
                weight += wt;
            }

        var want = new double[3];
        if (weight <= 0) { want[0] = want[1] = want[2] = 0.5; }
        else
        {
            want[0] = CRef.SrgbEncode(brightest / weight);
            want[1] = CRef.SrgbEncode(luminance / weight);
            want[2] = CRef.SrgbEncode(Math.Exp(logBrightest / weight) - 3e-4);
        }

        double maxDiff = 0;
        for (int i = 0; i < 3; i++) maxDiff = Math.Max(maxDiff, Math.Abs(got[i] - want[i]));
        Assert.True(maxDiff < 1e-12,
            $"h={h} across the band path; got [{got[0]:F12}, {got[1]:F12}, {got[2]:F12}], max |diff| = {maxDiff:E3}");
    }

    // ================================================================ brush_alpha_bounds

    [Fact]
    public void BrushAlphaBounds_FindsHalfOpenBounds()
    {
        NativeTest.RequireKernel();
        const int w = 20, h = 12;
        var px = new byte[w * h * 4];
        // Mark a solid region: rows 3..7, columns 4..11.
        for (int y = 3; y <= 7; y++)
            for (int x = 4; x <= 11; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = 200; px[i + 1] = 100; px[i + 2] = 50; px[i + 3] = 255;
            }

        var bounds = new nuint[4];
        PinnedBuffers.Invoke(new Array?[] { px, bounds }, p =>
            NativePixelOps.BrushAlphaBounds(p.Ptr<byte>(0), w, h, (nuint)(w * 4), p.Ptr<nuint>(1)));

        Assert.True(bounds[0] == 4 && bounds[1] == 3 && bounds[2] == 12 && bounds[3] == 8,
            $"expected [left,top,right,bottom] = [4,3,12,8] (right/bottom exclusive); got " +
            $"[{bounds[0]},{bounds[1]},{bounds[2]},{bounds[3]}]");

        // Empty input must report all zero rather than an inverted box.
        var empty = new byte[w * h * 4];
        var emptyBounds = new nuint[4];
        PinnedBuffers.Invoke(new Array?[] { empty, emptyBounds }, p =>
            NativePixelOps.BrushAlphaBounds(p.Ptr<byte>(0), w, h, (nuint)(w * 4), p.Ptr<nuint>(1)));
        Assert.True(emptyBounds[0] == 0 && emptyBounds[1] == 0 && emptyBounds[2] == 0 && emptyBounds[3] == 0,
            $"an empty image must give all-zero bounds; got [{emptyBounds[0]},{emptyBounds[1]},{emptyBounds[2]},{emptyBounds[3]}]");
    }

    // ================================================================ alpha planes

    [Fact]
    public void LayerExtractAlpha_CopiesTheAlphaPlane()
    {
        NativeTest.RequireKernel();
        const int w = 20, h = 12;
        var px = NativeTest.Gradient(w, h);
        var gray = new byte[w * h];
        PinnedBuffers.Invoke(new Array?[] { px, gray }, p =>
            NativePixelOps.LayerExtractAlpha(p.Ptr<byte>(0), (nuint)(w * 4), p.Ptr<byte>(1), (nuint)w, w, h));

        var want = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                want[y * w + x] = px[(y * w + x) * 4 + 3];

        int idx = NativeTest.FirstDiff(gray, want);
        Assert.True(idx < 0, $"alpha plane mismatch at byte {idx}");
    }

    [Fact]
    public void LayerRestoreAlpha_RescalesByTheAlphaPlane()
    {
        NativeTest.RequireKernel();
        const int w = 20, h = 12;
        var px = NativeTest.Gradient(w, h);
        var before = (byte[])px.Clone();
        var half = new byte[w * h];
        Array.Fill(half, (byte)128);

        PinnedBuffers.Invoke(new Array?[] { px, half }, p =>
            NativePixelOps.LayerRestoreAlpha(p.Ptr<byte>(0), (nuint)(w * 4), p.Ptr<byte>(1), (nuint)w, w, h));

        int mismatch = 0; string firstBad = "";
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    byte expect = (byte)((before[i + c] * 128u + 127) / 255);
                    if (px[i + c] != expect)
                    {
                        mismatch++;
                        if (firstBad.Length == 0) firstBad = $"px({x},{y}) c{c}: got {px[i + c]} want {expect}";
                    }
                }
                if (px[i + 3] != 128) mismatch++;
            }

        Assert.True(mismatch == 0, $"{w * h * 4} bytes checked, {mismatch} mismatched. {firstBad}");
    }

    // ================================================================ dither_glow

    [Fact]
    public void DitherGlow_IsDeterministicAndNeverExceedsAlpha()
    {
        NativeTest.RequireKernel();
        const int w = 32, h = 32;
        var px = NativeTest.Gradient(w, h);
        var glow = NativeTest.Gradient(w, h);

        var a = (byte[])px.Clone();
        var b = (byte[])px.Clone();
        var aGlow = (byte[])glow.Clone();
        var bGlow = (byte[])glow.Clone();
        PinnedBuffers.Invoke(new Array?[] { a, aGlow }, p =>
            NativePixelOps.DitherGlow(p.Ptr<byte>(0), p.Ptr<byte>(1), w, h, (nuint)(w * 4), 0.8f));
        PinnedBuffers.Invoke(new Array?[] { b, bGlow }, p =>
            NativePixelOps.DitherGlow(p.Ptr<byte>(0), p.Ptr<byte>(1), w, h, (nuint)(w * 4), 0.8f));

        int nondet = 0, overAlpha = 0, changed = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) nondet++;
            for (int c = 0; c < 3; c++)
            {
                if (a[i + c] > a[i + 3]) overAlpha++;
                if (a[i + c] != px[i + c]) changed++;
            }
        }

        Assert.True(nondet == 0, $"{nondet} pixels differed between two identical runs");
        Assert.True(overAlpha == 0, $"{overAlpha} channels ended above their own alpha");
        Assert.True(changed > 0, "dither_glow changed nothing at amount 0.8");
    }

    // ================================================================ content_fill

    /// <summary>
    /// <c>content_fill</c> on an image it can actually solve.
    /// </summary>
    /// <remarks>
    /// The header's return codes are 1 on success, 0 when no source patch exists, -1 on allocation
    /// failure. What "a source patch exists" means was read from <c>ContentFill.c</c>: the image to
    /// copy from is the set of <em>unselected</em> pixels whose alpha is exactly 255 — so a
    /// semi-transparent fixture has no valid donors at all and returns 0 no matter how large the hole
    /// is. This test therefore uses a fully opaque image, which is the realistic case, and separately
    /// pins down the 0 case so neither branch is left to assumption.
    /// </remarks>
    [Fact]
    public void ContentFill_FillsAHoleInAnOpaqueImage()
    {
        NativeTest.RequireKernel();
        const int w = 32, h = 32;
        var fill = new byte[w * h * 4];
        for (int i = 0; i < fill.Length; i += 4)
        {
            fill[i] = 200; fill[i + 1] = 100; fill[i + 2] = 50; fill[i + 3] = 255;
        }
        var mask = new byte[w * h];
        for (int y = 12; y < 16; y++)
            for (int x = 12; x < 16; x++) mask[y * w + x] = 255;

        int rc = 0;
        PinnedBuffers.Invoke(new Array?[] { fill, mask }, p =>
            rc = NativePixelOps.ContentFill(p.Ptr<byte>(0), (nuint)(w * 4), p.Ptr<byte>(1), (nuint)w, w, h));

        Assert.True(rc == 1,
            $"a 4x4 hole in a fully opaque 32x32 image has donors, so content_fill should return 1; got {rc}");
    }

    /// <summary>
    /// The documented 0 case: with no patch to copy from, <c>content_fill</c> reports 0 rather than
    /// faulting. A fully transparent image has no opaque pixels, so it has no valid donor.
    /// </summary>
    [Fact]
    public void ContentFill_ReportsZeroWhenThereIsNoSourcePatch()
    {
        NativeTest.RequireKernel();
        const int w = 32, h = 32;
        var fill = new byte[w * h * 4];   // all zero: every pixel transparent
        var mask = new byte[w * h];
        for (int y = 12; y < 16; y++)
            for (int x = 12; x < 16; x++) mask[y * w + x] = 255;

        int rc = 0;
        PinnedBuffers.Invoke(new Array?[] { fill, mask }, p =>
            rc = NativePixelOps.ContentFill(p.Ptr<byte>(0), (nuint)(w * 4), p.Ptr<byte>(1), (nuint)w, w, h));

        Assert.True(rc == 0, $"a transparent image has no donor patch, so content_fill should return 0; got {rc}");
    }
}
