using Compositor.Codecs;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// Layer effects: the six a layer can carry, and the order they are drawn in.
/// </summary>
/// <remarks>
/// The order assertions matter more than they look: an effect drawn in the wrong place either
/// disappears behind the pixels or paints over them, and both failures are quiet — the image still
/// looks like an image.
/// </remarks>
public sealed class LayerEffectsTests
{
    /// <summary>A 16×16 opaque red square covering the whole buffer.</summary>
    private static PixelBuffer FullSquare(int size = 16)
    {
        var buffer = new PixelBuffer(size, size);
        buffer.Clear(255, 0, 0, 255);
        return buffer;
    }

    /// <summary>A 16×16 buffer with an opaque square in the middle and transparency round it.</summary>
    private static PixelBuffer SquareInTheMiddle(int size = 16, int inset = 4, byte r = 255, byte g = 0, byte b = 0)
    {
        var buffer = new PixelBuffer(size, size);
        for (var y = inset; y < size - inset; y++)
        {
            for (var x = inset; x < size - inset; x++)
            {
                buffer.SetPixel(x, y, r, g, b, 255);
            }
        }

        return buffer;
    }

    private static (int R, int G, int B, int A) At(PixelBuffer buffer, int x, int y)
    {
        var (r, g, b, a) = buffer.GetPixel(x, y);
        return (r, g, b, a);
    }

    // ---- defaults, straight from the upstream structs ----

    [Fact]
    public void TheDefaults_MatchTheUpstreamStructs()
    {
        // LayerEffects.swift:6-125. A default that drifted would change the look of every project
        // that relies on it.
        Assert.Equal(4, new StrokeEffect().Size);
        Assert.False(new StrokeEffect().Inside);
        Assert.Equal(1, new StrokeEffect().Opacity);

        Assert.Equal(90, new ShadowEffect().Angle);
        Assert.Equal(20, new ShadowEffect().Distance);
        Assert.Equal(20, new ShadowEffect().Blur);
        Assert.Equal(0.5, new ShadowEffect().Opacity);

        Assert.Equal(1, new ColorOverlayEffect().Opacity);

        Assert.Equal(20, new GlowEffect().Size);
        Assert.Equal(0.75, new GlowEffect().Opacity);
    }

    // ---- the shadow's geometry ----

    [Fact]
    public void AShadowAtNinetyDegrees_FallsStraightDown()
    {
        // The light is overhead, so the shadow lands under the object with no sideways offset.
        var (x, y) = new ShadowEffect { Angle = 90, Distance = 20 }.Offset;

        Assert.Equal(0, x, 6);
        Assert.Equal(20, y, 6);
    }

    [Fact]
    public void AShadowFromTheRight_FallsToTheLeft()
    {
        // At 0° the light is on the right, so the shadow falls away from it, to the left.
        var (x, y) = new ShadowEffect { Angle = 0, Distance = 20 }.Offset;

        Assert.Equal(-20, x, 6);
        Assert.Equal(0, y, 6);
    }

    [Fact]
    public void AShadowAtTwoSeventy_DegreesFallsUpward()
    {
        var (x, y) = new ShadowEffect { Angle = 270, Distance = 20 }.Offset;

        Assert.Equal(0, x, 6);
        Assert.Equal(-20, y, 6);
    }

    // ---- validation ranges ----

    [Theory]
    [InlineData(-1)]
    [InlineData(501)]
    [InlineData(double.NaN)]
    public void AStrokeOutsideItsRange_IsNotValid(double size) =>
        Assert.False(new StrokeEffect { Size = size }.IsValid);

    [Fact]
    public void AStrokeAtItsLimits_IsValid()
    {
        Assert.True(new StrokeEffect { Size = 0 }.IsValid);
        Assert.True(new StrokeEffect { Size = StrokeEffect.MaxSize }.IsValid);
    }

    [Theory]
    [InlineData(361)]
    [InlineData(-361)]
    public void AShadowOutsideItsAngleRange_IsNotValid(double angle) =>
        Assert.False(new ShadowEffect { Angle = angle }.IsValid);

    [Fact]
    public void AShadowAtItsAngleLimits_IsValid()
    {
        Assert.True(new ShadowEffect { Angle = 360 }.IsValid);
        Assert.True(new ShadowEffect { Angle = -360 }.IsValid);
    }

    [Fact]
    public void AShadowOutsideItsDistanceAndBlurRanges_IsNotValid()
    {
        Assert.False(new ShadowEffect { Distance = 5001 }.IsValid);
        Assert.False(new ShadowEffect { Blur = 501 }.IsValid);
        Assert.False(new ShadowEffect { Opacity = 1.1 }.IsValid);
    }

    [Fact]
    public void AColourOutsideTheUnitRange_IsNotValid() =>
        Assert.False(new ColorOverlayEffect { Red = 1.5 }.IsValid);

    [Fact]
    public void AGlowOutsideItsSizeRange_IsNotValid() => Assert.False(new GlowEffect { Size = 501 }.IsValid);

    // ---- rendering order ----

    [Fact]
    public void AnEmptyEffectsRecord_DrawsNothing()
    {
        var layer = SquareInTheMiddle();

        var result = LayerEffectRenderer.Render(layer, new LayerEffects());

        Assert.Same(layer, result);
    }

    [Fact]
    public void ANullEffectsRecord_DrawsNothing()
    {
        var layer = SquareInTheMiddle();

        Assert.Same(layer, LayerEffectRenderer.Render(layer, null));
    }

    [Fact]
    public void AnOutOfRangeEffect_IsIgnoredRatherThanRefused()
    {
        // The macOS side ignores an out-of-range effect instead of rejecting the project
        // (EditorCanvas.swift:1216). Being stricter would refuse to open a project macOS opens.
        var layer = SquareInTheMiddle();

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 9999 },
        });

        Assert.Equal(At(layer, 8, 8), At(result, 8, 8));
        Assert.Equal(0, At(result, 1, 1).A);
    }

    [Fact]
    public void TheVisibleKinds_AreInDrawingOrder()
    {
        var effects = new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 2 },
            Shadow = new ShadowEffect(),
            ColorOverlay = new ColorOverlayEffect(),
            InnerShadow = new ShadowEffect(),
            OuterGlow = new GlowEffect(),
            InnerGlow = new GlowEffect(),
        };

        var kinds = effects.VisibleKinds();

        // Shadow, outer glow, outside stroke, the pixels, colour overlay, inner glow, inner shadow.
        Assert.Equal(
            [
                LayerEffectKind.DropShadow,
                LayerEffectKind.OuterGlow,
                LayerEffectKind.Stroke,
                LayerEffectKind.ColorOverlay,
                LayerEffectKind.InnerGlow,
                LayerEffectKind.InnerShadow,
            ],
            kinds);
    }

    [Fact]
    public void AnInsideStroke_IsDrawnAfterThePixelsInsteadOfBefore()
    {
        // An inside stroke painted before the pixels would be covered by them and never seen.
        var effects = new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 2, Inside = true },
        };

        var kinds = effects.VisibleKinds();

        Assert.Equal([LayerEffectKind.Stroke], kinds);

        // And it does show: the square's own edge takes the stroke colour.
        var red = SquareInTheMiddle();
        var result = LayerEffectRenderer.Render(red, effects with
        {
            Stroke = new StrokeEffect { Size = 2, Inside = true, Red = 0, Green = 0, Blue = 1 },
        });

        Assert.Equal(255, At(result, 4, 8).B);
        Assert.Equal(0, At(result, 4, 8).R);
    }

    [Fact]
    public void ADisabledEffect_IsNotInTheVisibleKinds()
    {
        var effects = new LayerEffects
        {
            Shadow = new ShadowEffect { Enabled = false },
            Stroke = new StrokeEffect(),
        };

        Assert.Equal([LayerEffectKind.Stroke], effects.VisibleKinds());
    }

    [Fact]
    public void AZeroSizedStrokeOrGlow_IsNotDrawn()
    {
        var effects = new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 0 },
            OuterGlow = new GlowEffect { Size = 0 },
            InnerGlow = new GlowEffect { Size = 0 },
            ColorOverlay = new ColorOverlayEffect(),
        };

        Assert.Equal([LayerEffectKind.ColorOverlay], effects.VisibleKinds());
    }

    // ---- colour overlay ----

    [Fact]
    public void AColorOverlay_RecoloursThePixelsButNotTheGaps()
    {
        var layer = SquareInTheMiddle();

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            ColorOverlay = new ColorOverlayEffect { Red = 0, Green = 0, Blue = 1 },
        });

        // Inside the square: blue. Outside it: still transparent, not blue.
        Assert.Equal((0, 0, 255, 255), At(result, 8, 8));
        Assert.Equal(0, At(result, 1, 1).A);
    }

    [Fact]
    public void AColorOverlayAtHalfOpacity_MixesWithThePixel()
    {
        var layer = SquareInTheMiddle();

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            ColorOverlay = new ColorOverlayEffect { Red = 0, Green = 0, Blue = 1, Opacity = 0.5 },
        });

        var pixel = At(result, 8, 8);
        Assert.Equal(128, pixel.R);
        Assert.Equal(0, pixel.G);
        Assert.Equal(128, pixel.B);
    }

    [Fact]
    public void AColorOverlay_LeavesATransparentPixelAlone()
    {
        var layer = SquareInTheMiddle();

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            ColorOverlay = new ColorOverlayEffect { Red = 0, Green = 1, Blue = 0 },
        });

        Assert.Equal((0, 0, 0, 0), At(result, 0, 0));
    }

    // ---- stroke ----

    [Fact]
    public void AnOutsideStroke_DrawsBeyondTheEdgeAndLeavesThePixelColourAlone()
    {
        var layer = SquareInTheMiddle();

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 2, Inside = false, Red = 0, Green = 1, Blue = 0 },
        });

        // One pixel outside the square's edge takes the stroke.
        Assert.Equal((0, 255, 0, 255), At(result, 3, 8));

        // The square's own pixel keeps its colour: an outside stroke sits behind the pixels.
        Assert.Equal((255, 0, 0, 255), At(result, 4, 8));
    }

    [Fact]
    public void AnInsideStroke_DrawsWithinTheEdgeAndLeavesTheFillAlone()
    {
        var layer = SquareInTheMiddle();

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 2, Inside = true, Red = 0, Green = 1, Blue = 0 },
        });

        // The square's edge takes the stroke; its middle keeps the fill.
        Assert.Equal((0, 255, 0, 255), At(result, 4, 8));
        Assert.Equal((255, 0, 0, 255), At(result, 8, 8));
    }

    [Fact]
    public void AnInsideStroke_DoesNotExtendPastTheEdge()
    {
        var layer = SquareInTheMiddle();

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 2, Inside = true, Red = 0, Green = 1, Blue = 0 },
        });

        Assert.Equal(0, At(result, 3, 8).A);
    }

    // ---- drop shadow ----

    [Fact]
    public void ADropShadow_LandsBelowTheShapeAtTheDefaultAngle()
    {
        var layer = SquareInTheMiddle(size: 32, inset: 12);

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            Shadow = new ShadowEffect { Angle = 90, Distance = 4, Blur = 0, Opacity = 1 },
        });

        // Below the square, where nothing was.
        Assert.Equal(255, At(result, 16, 21).A);

        // Above the square, where the shadow does not reach.
        Assert.Equal(0, At(result, 16, 10).A);
    }

    [Fact]
    public void ADropShadow_KeepsTheLayersOwnPixelsOnTop()
    {
        var layer = SquareInTheMiddle(size: 32, inset: 12);

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            Shadow = new ShadowEffect { Angle = 90, Distance = 4, Blur = 0, Opacity = 1, Red = 0, Green = 1, Blue = 0 },
        });

        // The square itself is untouched red; only what is beyond it is shadowed.
        Assert.Equal((255, 0, 0, 255), At(result, 16, 16));
    }

    [Fact]
    public void BlurringAShadow_SoftensItsEdge()
    {
        var layer = SquareInTheMiddle(size: 32, inset: 12);
        var effects = new LayerEffects
        {
            Shadow = new ShadowEffect { Angle = 90, Distance = 4, Blur = 6, Opacity = 1 },
        };

        var result = LayerEffectRenderer.Render(layer, effects);

        // A blurred shadow has partial coverage somewhere, rather than a hard edge.
        var hasPartial = false;
        for (var y = 0; y < 32; y++)
        {
            var alpha = At(result, 16, y).A;
            if (alpha is > 0 and < 255)
            {
                hasPartial = true;
                break;
            }
        }

        Assert.True(hasPartial, "A blurred shadow should have a soft edge.");
    }

    // ---- outer glow ----

    [Fact]
    public void AnOuterGlow_ReachesBeyondTheShape()
    {
        var layer = SquareInTheMiddle(size: 32, inset: 12);

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            OuterGlow = new GlowEffect { Size = 4, Opacity = 1, Red = 0, Green = 1, Blue = 0 },
        });

        // Just outside the square the glow shows; the square keeps its own colour.
        Assert.True(At(result, 11, 16).A > 0, "The glow should reach past the edge.");
        Assert.Equal((255, 0, 0, 255), At(result, 16, 16));
    }

    [Fact]
    public void AnOuterGlow_DoesNotReachTheFarCorners()
    {
        var layer = SquareInTheMiddle(size: 32, inset: 12);

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            OuterGlow = new GlowEffect { Size = 2, Opacity = 1 },
        });

        Assert.Equal(0, At(result, 0, 0).A);
    }

    // ---- inner glow and inner shadow ----

    [Fact]
    public void AnInnerGlow_TintsTheEdgeAndKeepsTheAlpha()
    {
        // A 48x48 square inset by 8 spans 8…39, so a glow of size 4 reaches the edge but nowhere
        // near the middle — which is what makes "edge tinted, middle not" a meaningful assertion.
        // On a small square the whole shape falls inside the glow's reach, and the middle *should*
        // be tinted.
        var layer = SquareInTheMiddle(size: 48, inset: 8);

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            InnerGlow = new GlowEffect { Size = 4, Opacity = 1, Red = 0, Green = 1, Blue = 0 },
        });

        var edge = At(result, 8, 24);
        Assert.True(edge.G > 60, $"The edge should be tinted toward green, got green {edge.G}.");
        Assert.True(edge.R < 255, "The edge should have lost some of the fill's red.");

        Assert.Equal((255, 0, 0, 255), At(result, 24, 24));
        Assert.Equal(0, At(result, 2, 2).A);
    }

    [Fact]
    public void AnInnerShadow_FallsOnTheOppositeSideFromADropShadow()
    {
        var layer = SquareInTheMiddle(size: 32, inset: 12);

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            InnerShadow = new ShadowEffect
            {
                Angle = 90,
                Distance = 4,
                Blur = 0,
                Opacity = 1,
                Red = 0,
                Green = 1,
                Blue = 0,
            },
        });

        // A drop shadow at 90° falls below; an inner one shows at the top edge, inside.
        Assert.Equal(255, At(result, 16, 12).G);
        Assert.Equal(0, At(result, 16, 19).G);
    }

    [Fact]
    public void AnInnerEffect_DoesNotChangeAlphaAnywhere()
    {
        var layer = SquareInTheMiddle(size: 32, inset: 12);

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            InnerGlow = new GlowEffect { Size = 5, Opacity = 1 },
            InnerShadow = new ShadowEffect { Blur = 5, Opacity = 1 },
        });

        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                Assert.Equal(At(layer, x, y).A, At(result, x, y).A);
            }
        }
    }

    // ---- combining ----

    [Fact]
    public void SeveralEffects_CombineWithoutCrashingAndKeepTheShapeOpaque()
    {
        var layer = SquareInTheMiddle(size: 32, inset: 12);

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            Shadow = new ShadowEffect { Distance = 4, Blur = 3 },
            OuterGlow = new GlowEffect { Size = 3 },
            Stroke = new StrokeEffect { Size = 2, Red = 0, Green = 0, Blue = 1 },
            ColorOverlay = new ColorOverlayEffect { Red = 0, Green = 1, Blue = 0, Opacity = 0.3 },
            InnerGlow = new GlowEffect { Size = 2 },
            InnerShadow = new ShadowEffect { Blur = 2 },
        });

        // The layer's middle is still opaque, and the buffer is the size it started at.
        Assert.Equal(255, At(result, 16, 16).A);
        Assert.Equal(32, result.Width);
        Assert.Equal(32, result.Height);
    }

    [Fact]
    public void TheResult_IsAlwaysTheSameSizeAsTheLayer()
    {
        // Effects that reach past the rectangle are clipped to it; growing the buffer would move the
        // layer's placement, so callers that need the room pad before and crop after.
        var layer = SquareInTheMiddle(size: 8, inset: 2);

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            Shadow = new ShadowEffect { Distance = 50, Blur = 20 },
            OuterGlow = new GlowEffect { Size = 20 },
        });

        Assert.Equal(layer.Width, result.Width);
        Assert.Equal(layer.Height, result.Height);
    }

    [Fact]
    public void AFullyTransparentLayer_StaysTransparent()
    {
        var layer = new PixelBuffer(8, 8);

        var result = LayerEffectRenderer.Render(layer, new LayerEffects
        {
            Shadow = new ShadowEffect(),
            OuterGlow = new GlowEffect(),
            ColorOverlay = new ColorOverlayEffect { Red = 1 },
            InnerGlow = new GlowEffect(),
            InnerShadow = new ShadowEffect(),
            Stroke = new StrokeEffect(),
        });

        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                Assert.Equal(0, At(result, x, y).A);
            }
        }
    }
}
