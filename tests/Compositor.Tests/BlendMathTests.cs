using Compositor.Codecs;
using Compositor.Core;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// The blend maths, checked against the concrete numbers the upstream project recorded.
/// </summary>
/// <remarks>
/// <c>SeparableBlend.swift:14-16</c> documents a real bug it hit: compute these two modes in a
/// linear space instead of sRGB and "over 40% grey, an 80% grey layer dodges to 62% instead of
/// Photoshop's 100%, and burns to 0% instead of 25%". Those two numbers are the sharpest available
/// test of the whole formulation, so they are asserted exactly.
/// </remarks>
public sealed class BlendMathTests
{
    [Fact]
    public void ColorDodge_80Percent_Over_40Percent_Reaches_100()
    {
        // Not close to 1: exactly 1. In linear space this yields 0.62, which is the failure the
        // upstream comment records.
        Assert.Equal(1.0, Blend.ColorDodge(0.4, 0.8));
    }

    [Fact]
    public void ColorBurn_80Percent_Over_40Percent_Reaches_25Percent()
    {
        Assert.Equal(0.25, Blend.ColorBurn(0.4, 0.8), 12);
    }

    [Fact]
    public void Multiply_IsIdentityForWhite_AndZeroForBlack()
    {
        Assert.Equal(0.37, Blend.Multiply(0.37, 1.0), 12);
        Assert.Equal(0.0, Blend.Multiply(0.37, 0.0), 12);
    }

    [Fact]
    public void Screen_IsIdentityForBlack_AndOneForWhite()
    {
        Assert.Equal(0.37, Blend.Screen(0.37, 0.0), 12);
        Assert.Equal(1.0, Blend.Screen(0.37, 1.0), 12);
    }

    [Fact]
    public void Overlay_IsHardLightWithTheOperandsSwapped()
    {
        // The definition, not an incidental identity: this is what makes Overlay keyed on the
        // backdrop while Hard Light is keyed on the source.
        Assert.Equal(Blend.HardLight(0.7, 0.3), Blend.Overlay(0.3, 0.7), 12);
    }

    [Fact]
    public void Difference_OfAColourWithItself_IsZero() =>
        Assert.Equal(0.0, Blend.Difference(0.42, 0.42), 12);

    [Fact]
    public void LinearDodge_ClampsAtOne() => Assert.Equal(1.0, Blend.LinearDodge(0.8, 0.8));

    [Fact]
    public void LinearBurn_FloorsAtZero() => Assert.Equal(0.0, Blend.LinearBurn(0.2, 0.2));

    [Fact]
    public void Subtract_FloorsAtZero() => Assert.Equal(0.0, Blend.Subtract(0.2, 0.7));

    [Fact]
    public void Divide_WithABlackDivisor_ForcesWhite() => Assert.Equal(1.0, Blend.Divide(0.3, 0.0));

    [Fact]
    public void HardMix_OnlyEverYieldsBlackOrWhite()
    {
        foreach (var backdrop in new[] { 0.0, 0.1, 0.25, 0.4, 0.5, 0.75, 1.0 })
        {
            var value = Blend.HardMix(backdrop, 0.6);
            Assert.True(value is 0.0 or 1.0, $"HardMix({backdrop}, 0.6) was {value}.");
        }
    }

    [Fact]
    public void SoftLight_WithAMidGreySource_LeavesTheBackdropAlone() =>
        Assert.Equal(0.3, Blend.SoftLight(0.3, 0.5), 12);

    [Fact]
    public void PinLight_PicksOneOperandOrTheOther()
    {
        // Below mid source it is a Darken against the doubled source; above it, a Lighten.
        Assert.Equal(Blend.Darken(0.4, 0.4), Blend.PinLight(0.4, 0.2), 12);
        Assert.Equal(Blend.Lighten(0.4, 0.6), Blend.PinLight(0.4, 0.8), 12);
    }
}
