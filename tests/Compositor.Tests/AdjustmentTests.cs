using Compositor.Codecs;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// Adjustment layers: the kinds that are expressible as per-channel lookup tables, plus Black &amp;
/// White.
/// </summary>
/// <remarks>
/// Four of the twelve kinds are implemented so far (see <see cref="Adjustment"/>). The tests here pin
/// the arithmetic that is easy to get subtly wrong: which direction a weight runs, that alpha is
/// never touched, and that a curve does not overshoot between its handles.
/// </remarks>
public sealed class AdjustmentTests
{
    private static PixelBuffer Solid(byte r, byte g, byte b, byte a = 255, int size = 1)
    {
        var buffer = new PixelBuffer(size, size);
        buffer.Clear(r, g, b, a);
        return buffer;
    }

    private static (int R, int G, int B, int A) At(PixelBuffer buffer, int x = 0, int y = 0)
    {
        var (r, g, b, a) = buffer.GetPixel(x, y);
        return (r, g, b, a);
    }

    // ---- invert ----

    [Theory]
    [InlineData(0, 255)]
    [InlineData(255, 0)]
    [InlineData(64, 191)]
    [InlineData(128, 127)]
    public void Invert_ComplementsEachChannel(byte input, byte expected)
    {
        var buffer = Solid(input, input, input);

        Adjustment.Invert().ApplyTo(buffer);

        Assert.Equal(expected, buffer.GetPixel(0, 0).R);
    }

    [Fact]
    public void Invert_LeavesAlphaAlone()
    {
        // The upstream works in premultiplied space where inverting is α − c, which preserves
        // transparency; in straight alpha that means alpha is untouched.
        var buffer = Solid(10, 20, 30, a: 128);

        Adjustment.Invert().ApplyTo(buffer);

        Assert.Equal((245, 235, 225, 128), At(buffer));
    }

    [Fact]
    public void Invert_DoesNotColourAFullyTransparentPixel()
    {
        // A transparent pixel carries no colour. Writing a complement into it would be invisible
        // until the pixel later became opaque, and then it would be wrong.
        var buffer = Solid(10, 20, 30, a: 0);

        Adjustment.Invert().ApplyTo(buffer);

        Assert.Equal((10, 20, 30, 0), At(buffer));
    }

    [Fact]
    public void Invert_IsItsOwnInverse()
    {
        var buffer = Solid(37, 200, 99, a: 200);

        Adjustment.Invert().ApplyTo(buffer);
        Adjustment.Invert().ApplyTo(buffer);

        Assert.Equal((37, 200, 99, 200), At(buffer));
    }

    [Fact]
    public void Invert_ReportsItselfAsTheKind() =>
        Assert.Equal(AdjustmentKind.Invert, Adjustment.Invert().Kind);

    // ---- black & white ----

    [Fact]
    public void BlackWhite_TakesAWeightedGrey_NotAPlainAverage()
    {
        // Photoshop's default weights are 0.4 / 0.4 / 0.2, which is not Rec. 601's
        // 0.299 / 0.587 / 0.114. A plain average of pure red would be 85; these weights give 102,
        // and Rec. 601 would give 76. The three are far enough apart to tell apart.
        var buffer = Solid(255, 0, 0);

        Adjustment.BlackWhite().ApplyTo(buffer);

        Assert.Equal(102, buffer.GetPixel(0, 0).R);
        Assert.Equal(102, buffer.GetPixel(0, 0).G);
        Assert.Equal(102, buffer.GetPixel(0, 0).B);
    }

    [Fact]
    public void BlackWhite_WeightsAreFullySettable()
    {
        var buffer = Solid(255, 0, 0);

        Adjustment.BlackWhite([0f, 1f, 0f]).ApplyTo(buffer);

        // Pure red with all the weight on green is black.
        Assert.Equal(0, buffer.GetPixel(0, 0).R);
    }

    [Fact]
    public void BlackWhite_LeavesGreyUnchanged()
    {
        // A neutral grey has the same value in every channel, so any weights summing to 1 keep it.
        foreach (var value in new byte[] { 0, 1, 64, 128, 255 })
        {
            var buffer = Solid(value, value, value);

            Adjustment.BlackWhite().ApplyTo(buffer);

            Assert.Equal(value, buffer.GetPixel(0, 0).R);
        }
    }

    [Fact]
    public void BlackWhite_LeavesAlphaAlone()
    {
        var buffer = Solid(255, 0, 0, a: 77);

        Adjustment.BlackWhite().ApplyTo(buffer);

        Assert.Equal(77, buffer.GetPixel(0, 0).A);
    }

    [Fact]
    public void BlackWhite_WithATint_ShiftsTheGreyTowardTheHue()
    {
        // Hue 0 is red, so at full saturation green and blue are suppressed and red is kept.
        var buffer = Solid(200, 200, 200);

        Adjustment.BlackWhite(tint: true, tintHue: 0, tintSaturation: 1).ApplyTo(buffer);

        var pixel = At(buffer);
        Assert.Equal(200, pixel.R);
        Assert.Equal(0, pixel.G);
        Assert.Equal(0, pixel.B);
    }

    [Fact]
    public void BlackWhite_WithZeroSaturationTint_IsJustTheGrey()
    {
        var untinted = Solid(200, 100, 50);
        var tintedAtZero = Solid(200, 100, 50);

        Adjustment.BlackWhite().ApplyTo(untinted);
        Adjustment.BlackWhite(tint: true, tintHue: 210, tintSaturation: 0).ApplyTo(tintedAtZero);

        Assert.Equal(At(untinted), At(tintedAtZero));
    }

    [Fact]
    public void BlackWhite_RejectsTheWrongNumberOfWeights() =>
        Assert.Throws<ArgumentException>(() => Adjustment.BlackWhite([0.5f, 0.5f]));

    [Fact]
    public void BlackWhite_IsNeverTheIdentity()
    {
        // Even balanced weights turn a coloured pixel grey, so it must not be skipped as a no-op.
        Assert.False(Adjustment.BlackWhite().IsIdentity);
    }

    // ---- exposure ----

    [Fact]
    public void Exposure_OneStopDoublesTheValue()
    {
        var buffer = Solid(50, 50, 50);

        Adjustment.Exposure(stops: 1).ApplyTo(buffer);

        Assert.Equal(100, buffer.GetPixel(0, 0).R);
    }

    [Fact]
    public void Exposure_MinusOneStopHalvesTheValue()
    {
        var buffer = Solid(100, 100, 100);

        Adjustment.Exposure(stops: -1).ApplyTo(buffer);

        Assert.Equal(50, buffer.GetPixel(0, 0).R);
    }

    [Fact]
    public void Exposure_Clamps()
    {
        var buffer = Solid(200, 200, 200);

        Adjustment.Exposure(stops: 4).ApplyTo(buffer);

        Assert.Equal(255, buffer.GetPixel(0, 0).R);
    }

    [Fact]
    public void Exposure_ZeroStopsIsTheIdentity() => Assert.True(Adjustment.Exposure().IsIdentity);

    [Fact]
    public void Exposure_GammaDarkensTheMidtones()
    {
        // A gamma above 1 with the 1/gamma convention lightens; below 1 darkens. Either way the
        // endpoints stay put, which is what makes it a gamma rather than a gain.
        var dark = Solid(128, 128, 128);
        var light = Solid(128, 128, 128);
        var black = Solid(0, 0, 0);

        Adjustment.Exposure(gamma: 2.2).ApplyTo(dark);
        Adjustment.Exposure(gamma: 1 / 2.2).ApplyTo(light);
        Adjustment.Exposure(gamma: 2.2).ApplyTo(black);

        Assert.True(dark.GetPixel(0, 0).R > 128, $"A gamma of 2.2 should lighten mid grey, got {dark.GetPixel(0, 0).R}.");
        Assert.True(light.GetPixel(0, 0).R < 128, $"A gamma of 1/2.2 should darken mid grey, got {light.GetPixel(0, 0).R}.");
        Assert.Equal(0, black.GetPixel(0, 0).R);
    }

    [Fact]
    public void Exposure_RejectsANonPositiveGamma() =>
        Assert.Throws<ArgumentException>(() => Adjustment.Exposure(gamma: 0));

    // ---- levels ----

    [Fact]
    public void Levels_DefaultRangesAreTheIdentity()
    {
        var adjustment = Adjustment.Levels([(0, 255), (0, 255), (0, 255), (0, 255)]);

        Assert.True(adjustment.IsIdentity);
    }

    [Fact]
    public void Levels_AWhitePointRemapsTheTop()
    {
        // Black 0, white 128: everything at or above 128 becomes white.
        var buffer = Solid(128, 128, 128);

        Adjustment.Levels([(0, 128), (0, 255), (0, 255), (0, 255)]).ApplyTo(buffer);

        Assert.Equal(255, buffer.GetPixel(0, 0).R);
    }

    [Fact]
    public void Levels_ABlackPointRemapsTheBottom()
    {
        // Black 64, white 255: everything at or below 64 becomes black.
        var buffer = Solid(64, 64, 64);

        Adjustment.Levels([(64, 255), (0, 255), (0, 255), (0, 255)]).ApplyTo(buffer);

        Assert.Equal(0, buffer.GetPixel(0, 0).R);
    }

    [Fact]
    public void Levels_AChannelRangeOnlyAffectsThatChannel()
    {
        var buffer = Solid(128, 128, 128);

        Adjustment.Levels([(0, 255), (0, 128), (0, 255), (0, 255)]).ApplyTo(buffer);

        var pixel = At(buffer);
        Assert.Equal(255, pixel.R);
        Assert.Equal(128, pixel.G);
        Assert.Equal(128, pixel.B);
    }

    [Fact]
    public void Levels_CompositeAndChannelRangesChain()
    {
        // The composite range runs first, then the channel's own. Black 0/white 128 takes 64 to 128;
        // the red channel's own 0/255 is then the identity, so red ends at 128.
        var buffer = Solid(64, 64, 64);

        Adjustment.Levels([(0, 128), (0, 255), (0, 255), (0, 255)]).ApplyTo(buffer);

        Assert.Equal(128, buffer.GetPixel(0, 0).R);
    }

    [Fact]
    public void Levels_RejectsAWhitePointBelowTheBlackPoint() =>
        Assert.Throws<ArgumentException>(() => Adjustment.Levels([(200, 100), (0, 255), (0, 255), (0, 255)]));

    [Fact]
    public void Levels_RejectsTheWrongNumberOfRanges() =>
        Assert.Throws<ArgumentException>(() => Adjustment.Levels([(0, 255), (0, 255)]));

    // ---- curves ----

    private static CurvePoint[] Identity() => [new(0, 0), new(255, 255)];

    [Fact]
    public void Curves_IdentityIsTheIdentity()
    {
        var adjustment = Adjustment.Curves([Identity(), Identity(), Identity(), Identity()]);

        Assert.True(adjustment.IsIdentity);
    }

    [Fact]
    public void Curves_APointPullsTheMidtonesUp()
    {
        // Handles 0,0 → 128,200 → 255,255: a value of 128 comes out at 200.
        CurvePoint[] lifted = [new(0, 0), new(128, 200), new(255, 255)];
        var buffer = Solid(128, 128, 128);

        Adjustment.Curves([lifted, Identity(), Identity(), Identity()]).ApplyTo(buffer);

        Assert.Equal(200, buffer.GetPixel(0, 0).R);
    }

    [Fact]
    public void Curves_DoesNotOvershootBetweenHandles()
    {
        // This is the point of the monotone interpolation. A plain cubic through these handles
        // bulges; the shape-preserving version stays within the values its neighbours bracket, so a
        // pixel meant to be left alone is not lit up.
        CurvePoint[] flat = [new(0, 0), new(100, 0), new(120, 0), new(255, 255)];

        var adjustment = Adjustment.Curves([flat, Identity(), Identity(), Identity()]);
        var buffer = Solid(0, 0, 0, size: 256);

        // Paint a gradient so every input value under 120 is exercised.
        for (var x = 0; x < 256; x++)
        {
            buffer.SetPixel(x, 0, (byte)x, (byte)x, (byte)x, 255);
        }

        adjustment.ApplyTo(buffer);

        for (var x = 0; x <= 120; x++)
        {
            Assert.True(
                buffer.GetPixel(x, 0).R <= 2,
                $"Input {x} should map to about 0 but mapped to {buffer.GetPixel(x, 0).R}; the curve overshot.");
        }
    }

    [Fact]
    public void Curves_PinsBothEnds()
    {
        // Handles 0,255 → 255,0 is a full inversion. The point is that the two endpoints land
        // exactly on 255 and 0 rather than near them.
        CurvePoint[] inverted = [new(0, 255), new(255, 0)];
        var buffer = new PixelBuffer(2, 1);
        buffer.SetPixel(0, 0, 0, 0, 0, 255);
        buffer.SetPixel(1, 0, 255, 255, 255, 255);

        Adjustment.Curves([inverted, Identity(), Identity(), Identity()]).ApplyTo(buffer);

        Assert.Equal(255, buffer.GetPixel(0, 0).R);
        Assert.Equal(0, buffer.GetPixel(1, 0).R);
    }

    [Fact]
    public void Curves_AChannelCurveOnlyAffectsThatChannel()
    {
        CurvePoint[] lifted = [new(0, 0), new(128, 200), new(255, 255)];
        var buffer = Solid(128, 128, 128);

        Adjustment.Curves([Identity(), lifted, Identity(), Identity()]).ApplyTo(buffer);

        var pixel = At(buffer);
        Assert.Equal(200, pixel.R);
        Assert.Equal(128, pixel.G);
        Assert.Equal(128, pixel.B);
    }

    [Fact]
    public void Curves_CompositeAndChannelCurvesBothApply()
    {
        // Composite lifts 128 to 200, then the red channel lifts 200 further. Neither is the
        // identity, so this also documents that the composite is not applied twice.
        CurvePoint[] composite = [new(0, 0), new(128, 200), new(255, 255)];
        CurvePoint[] red = [new(0, 0), new(200, 255), new(255, 255)];
        var buffer = Solid(128, 128, 128);

        Adjustment.Curves([composite, red, Identity(), Identity()]).ApplyTo(buffer);

        var pixel = At(buffer);
        Assert.True(pixel.R > 200, $"The red curve should lift 200 further, got {pixel.R}.");
        Assert.Equal(200, pixel.G);
    }

    [Fact]
    public void Curves_RejectsAPointListThatDoesNotSpanTheRange()
    {
        Assert.Throws<ArgumentException>(() =>
            Adjustment.Curves([[new(0, 0), new(200, 200)], Identity(), Identity(), Identity()]));
    }

    [Fact]
    public void Curves_RejectsNonIncreasingPoints()
    {
        Assert.Throws<ArgumentException>(() =>
            Adjustment.Curves([[new(0, 0), new(100, 10), new(100, 20), new(255, 255)], Identity(), Identity(), Identity()]));
    }

    [Fact]
    public void Curves_RejectsASinglePoint() =>
        Assert.Throws<ArgumentException>(() =>
            Adjustment.Curves([[new(0, 0)], Identity(), Identity(), Identity()]));

    [Fact]
    public void Curves_RejectsTheWrongNumberOfChannels() =>
        Assert.Throws<ArgumentException>(() => Adjustment.Curves([Identity(), Identity()]));

    // ---- kind names ----

    [Theory]
    [InlineData(AdjustmentKind.HueSaturation, "Hue/Saturation")]
    [InlineData(AdjustmentKind.BlackWhite, "Black & White")]
    [InlineData(AdjustmentKind.GradientMap, "Gradient Map")]
    [InlineData(AdjustmentKind.AddNoise, "Add Noise")]
    [InlineData(AdjustmentKind.GaussianBlur, "Gaussian Blur")]
    public void AdjustmentKindNames_RoundTrip(AdjustmentKind kind, string name)
    {
        Assert.Equal(name, AdjustmentKindJson.Name(kind));
        Assert.Equal(kind, AdjustmentKindJson.Parse(name));
    }

    [Fact]
    public void AdjustmentKindNames_CoverAllTwelve() => Assert.Equal(12, AdjustmentKindJson.All.Count);

    [Fact]
    public void AdjustmentKindNames_RejectAnUnknownName() =>
        Assert.Throws<FormatException>(() => AdjustmentKindJson.Parse("Sepia"));

    // ---- shared behaviour ----

    [Fact]
    public void AnIdentityAdjustment_LeavesEveryPixelAlone()
    {
        var buffer = Solid(37, 200, 99, a: 200);

        Adjustment.Levels([(0, 255), (0, 255), (0, 255), (0, 255)]).ApplyTo(buffer);

        Assert.Equal((37, 200, 99, 200), At(buffer));
    }

    [Fact]
    public void EveryAdjustment_LeavesAlphaAlone()
    {
        foreach (var adjustment in new[]
        {
            Adjustment.Invert(),
            Adjustment.BlackWhite(),
            Adjustment.Exposure(stops: 1),
            Adjustment.Levels([(0, 128), (0, 255), (0, 255), (0, 255)]),
            Adjustment.Curves([[new(0, 0), new(128, 200), new(255, 255)], Identity(), Identity(), Identity()]),
        })
        {
            var buffer = Solid(120, 130, 140, a: 77);

            adjustment.ApplyTo(buffer);

            Assert.Equal(77, buffer.GetPixel(0, 0).A);
        }
    }
}
