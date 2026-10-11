using Compositor.Pixels;

namespace Compositor.Tests;

/// <summary>
/// <c>adjust_black_white</c>, including the documented assertion at <c>AdjustPixels.h:20</c>:
/// "Pure red at the default 40% comes out 40% gray, as it does there."
/// </summary>
/// <remarks>
/// <para>
/// This is also the first floating-point-parity probe between the macOS (Apple clang) build and the
/// Windows build. The chain for pure red is unusually clean, which is what makes an exact assertion
/// defensible rather than fragile:
/// </para>
/// <list type="number">
/// <item><c>md == mn == 0</c> for a pure primary, so <c>gray = mn + (md-mn)*w[secondary] + (mx-md)*w[primary]</c>
/// collapses to <c>mx * w[red]</c> — the secondary weights drop out exactly. Only <c>weights[0]</c>
/// can influence the result, so any transposition of the six-float array shows up as a hard failure.</item>
/// <item>At full coverage the unpremultiply is <c>255.0f * 255.0f / 255.0f</c>, exactly 1.0f, so
/// <c>gray</c> is exactly <c>0.4f</c>.</item>
/// <item><c>0.4f</c> is <c>0.4000000059604645</c>, so <c>0.4f * 255.0f</c> is <c>102.0000019f</c>.
/// <c>roundf</c> of that is unambiguously 102 — it is not near a .5 boundary, so it cannot flip
/// between compilers that both round to nearest.</item>
/// </list>
/// <para>
/// The expected value is therefore the exact integer 102 with no tolerance. If MSVC/clang and Apple
/// clang ever disagree about <c>float</c> arithmetic, or about <c>roundf</c> at a half step, this test
/// is where it surfaces first.
/// </para>
/// </remarks>
public class BlackWhiteTests
{
    /// <summary>Photoshop's Black &amp; White defaults, in the order named by <c>AdjustPixels.h:19</c>: red, yellow, green, cyan, blue, magenta.</summary>
    private static readonly float[] Defaults = { 0.4f, 0.6f, 0.6f, 0.6f, 0.2f, 0.8f };

    private const int Gray40 = 102; // roundf(0.4f * 255)

    [Fact]
    public void PureRedAtDefault40Percent_ComesOut40PercentGray()
    {
        NativeTest.RequireKernel();
        const int w = 8, h = 4;
        // Premultiplied pure red at full coverage, which is the storage the kernel documents.
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 255; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = 255; }

        PinnedBuffers.Invoke(new Array?[] { px, Defaults }, p =>
            NativeAdjustPixels.BlackWhite(p.Ptr<byte>(0), w, h, (nuint)(w * 4), p.Ptr<float>(1),
                tint: 0, tintHue: 0, tintSaturation: 0));

        for (int i = 0; i < px.Length; i += 4)
        {
            int n = i / 4;
            Assert.True(px[i] == Gray40 && px[i + 1] == Gray40 && px[i + 2] == Gray40,
                $"px{n}: pure red at the 40% red weight should be {Gray40},{Gray40},{Gray40}; " +
                $"got {px[i]},{px[i + 1]},{px[i + 2]}");
            Assert.True(px[i + 3] == 255, $"px{n}: alpha must be preserved");
        }
    }

    /// <summary>
    /// The secondary weights must not leak into a pure primary. Cyan and magenta are set to extremes
    /// here: if the binding passed the six floats in a rotated order, or the kernel picked the wrong
    /// secondary range, this diverges from <see cref="Gray40"/>.
    /// </summary>
    [Fact]
    public void PureRed_IgnoresTheSecondaryWeights()
    {
        NativeTest.RequireKernel();
        const int w = 4, h = 4;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 255; px[i + 3] = 255; }

        float[] otherWeights = { 0.4f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f };

        PinnedBuffers.Invoke(new Array?[] { px, otherWeights }, p =>
            NativeAdjustPixels.BlackWhite(p.Ptr<byte>(0), w, h, (nuint)(w * 4), p.Ptr<float>(1), 0, 0, 0));

        Assert.True(px[0] == Gray40 && px[1] == Gray40 && px[2] == Gray40,
            $"secondary weights (cyan=1.0, magenta=1.0) must not affect a pure primary; " +
            $"got {px[0]},{px[1]},{px[2]}");
    }

    /// <summary>
    /// The same relation at half coverage, which exercises the premultiplied path: the kernel
    /// unpremultiplies by <c>255/alpha</c>, computes the gray, then re-premultiplies with
    /// <c>roundf(outR * alpha)</c>. At alpha = 128 the unpremultiplied red is exactly 1.0f, so
    /// <c>gray</c> is exactly 0.4f and the stored value is <c>roundf(0.4f * 128) = roundf(51.2) = 51</c>.
    /// </summary>
    [Fact]
    public void PureRedAtHalfAlpha_ScalesTheSameGray()
    {
        NativeTest.RequireKernel();
        const int w = 4, h = 4;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 128;      // premultiplied full red at alpha 128
            px[i + 3] = 128;
        }

        PinnedBuffers.Invoke(new Array?[] { px, Defaults }, p =>
            NativeAdjustPixels.BlackWhite(p.Ptr<byte>(0), w, h, (nuint)(w * 4), p.Ptr<float>(1), 0, 0, 0));

        for (int i = 0; i < px.Length; i += 4)
        {
            int n = i / 4;
            Assert.True(px[i] == 51 && px[i + 1] == 51 && px[i + 2] == 51,
                $"px{n}: at alpha 128 the premultiplied 40% gray should be 51,51,51; got {px[i]},{px[i + 1]},{px[i + 2]}");
            Assert.True(px[i + 3] == 128, $"px{n}: alpha must be preserved");
        }
    }

    /// <summary>
    /// The full six-range split against a C# transcription of <c>AdjustPixels.c:120-162</c> with tint
    /// disabled, over a gradient that covers every primary/secondary branch. This is what proves the
    /// binding's six-float array carries the documented order for general colors, not just pure red.
    /// </summary>
    [Fact]
    public void MatchesSixRangeReferenceOverAGradient()
    {
        NativeTest.RequireKernel();
        const int w = 33, h = 17;
        var px = NativeTest.Gradient(w, h);
        var orig = (byte[])px.Clone();

        PinnedBuffers.Invoke(new Array?[] { px, Defaults }, p =>
            NativeAdjustPixels.BlackWhite(p.Ptr<byte>(0), w, h, (nuint)(w * 4), p.Ptr<float>(1), 0, 0, 0));

        // Independent reference, transcribed from AdjustPixels.c with tint == 0.
        var want = (byte[])orig.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float alpha = want[i + 3];
                if (alpha == 0) continue;
                float r = want[i] * 255.0f / alpha, g = want[i + 1] * 255.0f / alpha, b = want[i + 2] * 255.0f / alpha;
                r = MathF.Min(255.0f, r) / 255.0f;
                g = MathF.Min(255.0f, g) / 255.0f;
                b = MathF.Min(255.0f, b) / 255.0f;
                float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b));
                float md = r + g + b - mx - mn;
                int primary, secondary;
                if (mx == r) { primary = 0; secondary = (g >= b) ? 1 : 5; }
                else if (mx == g) { primary = 2; secondary = (r >= b) ? 1 : 3; }
                else { primary = 4; secondary = (g >= r) ? 3 : 5; }
                float gray = mn + (md - mn) * Defaults[secondary] + (mx - md) * Defaults[primary];
                gray = MathF.Min(1.0f, MathF.Max(0.0f, gray));
                byte v = (byte)MathF.Min(alpha, MathF.Max(0.0f, MathF.Round(gray * alpha, MidpointRounding.AwayFromZero)));
                want[i] = v; want[i + 1] = v; want[i + 2] = v;
            }

        int idx = NativeTest.FirstDiff(px, want);
        Assert.True(idx < 0,
            $"first difference at byte {idx} (px{idx / 4} c{idx % 4}): " +
            $"got {px[NativeTest.Safe(idx, px.Length)]} want {want[NativeTest.Safe(idx, want.Length)]}");
    }

    /// <summary>
    /// The tint path: the gray becomes the lightness of a color at the chosen hue, so the three
    /// channels must diverge while staying premultiplied (never above alpha).
    /// </summary>
    [Fact]
    public void TintColorsTheGrayWithoutExceedingAlpha()
    {
        NativeTest.RequireKernel();
        const int w = 8, h = 4;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 255; px[i + 3] = 255; }

        // Hue 120 degrees is pure green in the standard HSL sector mapping the kernel uses.
        PinnedBuffers.Invoke(new Array?[] { px, Defaults }, p =>
            NativeAdjustPixels.BlackWhite(p.Ptr<byte>(0), w, h, (nuint)(w * 4), p.Ptr<float>(1),
                tint: 1, tintHue: 120.0, tintSaturation: 1.0));

        int over = 0;
        for (int i = 0; i < px.Length; i += 4)
            for (int c = 0; c < 3; c++) if (px[i + c] > px[i + 3]) over++;

        Assert.True(over == 0, $"{over} channels ended above alpha in the tint path");
        Assert.True(px[1] > px[0] && px[1] > px[2] && px[0] < 255,
            $"a green tint should leave green dominant and the other channels darker; got {px[0]},{px[1]},{px[2]}");
    }
}
