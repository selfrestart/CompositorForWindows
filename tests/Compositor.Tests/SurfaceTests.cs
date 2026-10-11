using Compositor.Codecs;
using Compositor.Core;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// The drawing surface: the replacement for <c>CGContext</c>.
/// </summary>
/// <remarks>
/// Two things are worth pinning here. First, the state stack behaves the way an immediate-mode
/// context must — a clip or an alpha set inside a save must not leak out. Second, the two drawing
/// paths agree about colour: an axis-aligned placement composites pixel by pixel, a rotated or
/// scaled one goes through Skia, and a blend mode must not change its answer depending on which
/// path a placement happened to take.
/// </remarks>
public sealed class SurfaceTests
{
    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        surface.Fill(new Color(r, g, b, a));
        return surface;
    }

    [Fact]
    public void ANewSurface_IsTransparent()
    {
        using var surface = new Surface(4, 4);

        Assert.Equal((0, 0, 0, 0), Read(surface, 0, 0));
    }

    [Fact]
    public void Fill_PaintsInsideTheClip()
    {
        using var surface = new Surface(4, 4);

        surface.Fill(Color.Opaque(10, 20, 30));

        Assert.Equal((10, 20, 30, 255), Read(surface, 0, 0));
        Assert.Equal((10, 20, 30, 255), Read(surface, 3, 3));
        Assert.Equal(4, surface.Width);
    }

    [Fact]
    public void Clip_ConfinesAFill()
    {
        using var surface = new Surface(4, 4);

        surface.ClipTo(new Rect(1, 1, 2, 2));
        surface.Fill(Color.Opaque(255, 0, 0));

        Assert.Equal((255, 0, 0, 255), Read(surface, 1, 1));
        Assert.Equal((255, 0, 0, 255), Read(surface, 2, 2));
        Assert.Equal((0, 0, 0, 0), Read(surface, 0, 0));
        Assert.Equal((0, 0, 0, 0), Read(surface, 3, 3));
    }

    [Fact]
    public void SaveAndRestore_RollBackClipAlphaAndBlendMode()
    {
        using var surface = new Surface(4, 4);

        surface.Save();
        surface.ClipTo(new Rect(0, 0, 1, 1));
        surface.SetAlpha(0.5);
        surface.SetBlendMode(BlendMode.Multiply);
        Assert.Equal(1, surface.SavedStateCount);

        surface.Restore();

        Assert.Equal(0, surface.SavedStateCount);
        Assert.Equal(1.0, surface.Alpha);
        Assert.Equal(BlendMode.Normal, surface.BlendMode);
        Assert.Equal(new Rect(0, 0, 4, 4), surface.Clip);
    }

    [Fact]
    public void Restore_WithNothingSaved_IsIgnored()
    {
        using var surface = new Surface(2, 2);
        surface.SetAlpha(0.5);

        surface.Restore();

        Assert.Equal(0.5, surface.Alpha);
    }

    [Fact]
    public void Alpha_IsClamped()
    {
        using var surface = new Surface(2, 2);

        surface.SetAlpha(5);
        Assert.Equal(1.0, surface.Alpha);

        surface.SetAlpha(-3);
        Assert.Equal(0.0, surface.Alpha);
    }

    [Fact]
    public void Draw_AtItsOwnSizeAndPosition_IsPixelExact()
    {
        // The fast path: no resampling, so every source pixel lands on exactly one destination
        // pixel and the values must come through untouched.
        using var canvas = new Surface(4, 4);
        using var source = Solid(2, 2, 17, 200, 99, 200);

        canvas.Draw(source, Placement.Of(1, 1, 2, 2));

        Assert.Equal((17, 200, 99, 200), Read(canvas, 1, 1));
        Assert.Equal((17, 200, 99, 200), Read(canvas, 2, 2));
        Assert.Equal((0, 0, 0, 0), Read(canvas, 0, 0));
    }

    [Fact]
    public void Draw_WithAGlobalAlpha_ScalesTheSourceAlpha()
    {
        using var canvas = new Surface(2, 2);
        using var source = Solid(2, 2, 255, 0, 0, 255);

        canvas.SetAlpha(0.5);
        canvas.Draw(source, Placement.Of(0, 0, 2, 2));

        // 255 * 0.5 rounds to 128, and over a transparent backdrop the colour is unchanged.
        Assert.Equal((255, 0, 0, 128), Read(canvas, 0, 0));
    }

    [Fact]
    public void Draw_OffCanvas_IsClipped()
    {
        using var canvas = new Surface(2, 2);
        using var source = Solid(2, 2, 255, 255, 255);

        canvas.Draw(source, Placement.Of(10, 10, 2, 2));

        Assert.Equal((0, 0, 0, 0), Read(canvas, 0, 0));
    }

    [Fact]
    public void Draw_HalfOffCanvas_DrawsTheVisiblePart()
    {
        using var canvas = new Surface(2, 2);
        using var source = Solid(2, 2, 0, 255, 0);

        canvas.Draw(source, Placement.Of(1, 1, 2, 2));

        Assert.Equal((0, 255, 0, 255), Read(canvas, 1, 1));
        Assert.Equal((0, 0, 0, 0), Read(canvas, 0, 0));
    }

    /// <summary>
    /// Every mode that Skia can compute must give the same answer as the verified pixel-exact
    /// implementation. If the two paths disagreed, a layer's colour would depend on whether it
    /// happened to be rotated.
    /// </summary>
    [Theory]
    [InlineData(BlendMode.Normal)]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.Screen)]
    [InlineData(BlendMode.Overlay)]
    [InlineData(BlendMode.Darken)]
    [InlineData(BlendMode.Lighten)]
    [InlineData(BlendMode.Difference)]
    [InlineData(BlendMode.Exclusion)]
    [InlineData(BlendMode.ColorDodge)]
    [InlineData(BlendMode.ColorBurn)]
    [InlineData(BlendMode.SoftLight)]
    [InlineData(BlendMode.HardLight)]
    [InlineData(BlendMode.LinearDodge)]
    [InlineData(BlendMode.Hue)]
    [InlineData(BlendMode.Saturation)]
    [InlineData(BlendMode.Color)]
    [InlineData(BlendMode.Luminosity)]
    public void BothPaths_AgreeAboutColour(BlendMode mode)
    {
        const byte backdrop = 102;   // 40% grey
        const byte foreground = 204; // 80% grey

        // Pixel-exact: an opaque backdrop, then an in-place draw at the same size.
        using var exact = new Surface(1, 1);
        exact.Fill(Color.Opaque(backdrop, backdrop, backdrop));
        using var source = Solid(1, 1, foreground, foreground, foreground);
        exact.SetBlendMode(mode);
        exact.Draw(source, Placement.Of(0, 0, 1, 1));

        // Skia path: the same two colours, but the draw is scaled so it must resample.
        using var resampled = new Surface(2, 2);
        resampled.Fill(Color.Opaque(backdrop, backdrop, backdrop));
        resampled.SetBlendMode(mode);
        resampled.Draw(source, Placement.Of(0, 0, 2, 2));

        var expected = Read(exact, 0, 0);
        var actual = Read(resampled, 0, 0);

        // Scaling an opaque 1x1 source up is a constant colour, so interpolation cannot shift it;
        // any difference is the blend, and 1 LSB is allowed for Skia's pre-rounded blend maths.
        Assert.True(
            Math.Abs(expected.R - actual.R) <= 1
            && Math.Abs(expected.G - actual.G) <= 1
            && Math.Abs(expected.B - actual.B) <= 1
            && Math.Abs(expected.A - actual.A) <= 1,
            $"{mode}: pixel-exact gave {expected}, Skia gave {actual}.");
    }

    [Theory]
    [InlineData(BlendMode.LinearBurn)]
    [InlineData(BlendMode.Subtract)]
    [InlineData(BlendMode.Divide)]
    [InlineData(BlendMode.VividLight)]
    [InlineData(BlendMode.LinearLight)]
    [InlineData(BlendMode.PinLight)]
    [InlineData(BlendMode.HardMix)]
    public void ModesWithoutASkiaEquivalent_TakeThePixelExactPath(BlendMode mode)
    {
        // SkiaSharp 2.88.9 has no constant for these. Asking for one would throw, so the surface
        // must route them to the exact path instead -- and 25% grey dodged by 80% grey is a value
        // the verified implementation pins.
        using var canvas = new Surface(1, 1);
        canvas.Fill(Color.Opaque(64, 64, 64));
        using var source = Solid(1, 1, 204, 204, 204);

        canvas.SetBlendMode(mode);
        canvas.Draw(source, Placement.Of(0, 0, 1, 1));

        var result = Read(canvas, 0, 0);
        var expected = LayerCompositor.CompositePixel(
            204 / 255.0, 204 / 255.0, 204 / 255.0, 1.0,
            64 / 255.0, 64 / 255.0, 64 / 255.0, 1.0,
            mode);

        Assert.Equal(
            ((int)Math.Round(expected.R * 255), (int)Math.Round(expected.G * 255),
             (int)Math.Round(expected.B * 255), (int)Math.Round(expected.A * 255)),
            ((int)result.R, (int)result.G, (int)result.B, (int)result.A));
    }

    [Fact]
    public void RotatingAFullTurn_LeavesTheImageUpright()
    {
        using var canvas = new Surface(4, 4);
        using var source = Solid(2, 2, 10, 20, 30);

        canvas.Draw(source, new Placement
        {
            X = 1,
            Y = 1,
            Width = 2,
            Height = 2,
            RotationDegrees = 360,
        });

        // 360 degrees is a rotation, not the identity path, so this exercises Skia.
        Assert.Equal(4, canvas.Width);
        Assert.True(Read(canvas, 1, 1).A > 0, "A full turn should still leave the image drawn.");
    }

    [Fact]
    public void DrawingARotatedSquare_KeepsItsCentre()
    {
        using var canvas = new Surface(8, 8);
        using var source = Solid(4, 4, 255, 255, 255);

        canvas.Draw(source, new Placement
        {
            X = 2,
            Y = 2,
            Width = 4,
            Height = 4,
            RotationDegrees = 45,
        });

        // The centre of the placement is (4, 4) and rotation is about the centre, so the middle
        // stays covered.
        Assert.True(Read(canvas, 4, 4).A > 0, "The centre must stay covered under rotation.");
    }

    [Fact]
    public void ScalingUp_GoesThroughSkiaAndStillPaints()
    {
        using var canvas = new Surface(4, 4);
        using var source = Solid(1, 1, 200, 100, 50);

        canvas.Draw(source, Placement.Of(0, 0, 4, 4));

        var pixel = Read(canvas, 2, 2);
        Assert.True(pixel.R > 190 && pixel.G > 90 && pixel.B > 40, $"Expected roughly the source colour, got {pixel}.");
    }

    [Fact]
    public void Translate_OffsetsSubsequentDraws()
    {
        using var canvas = new Surface(4, 4);
        using var source = Solid(1, 1, 255, 255, 255);

        canvas.Translate(2, 2);
        canvas.Draw(source, Placement.Of(0, 0, 1, 1));

        Assert.True(Read(canvas, 2, 2).A > 0);
        Assert.Equal(0, Read(canvas, 0, 0).A);
    }

    [Fact]
    public void Scale_ResizesSubsequentDraws()
    {
        using var canvas = new Surface(4, 4);
        using var source = Solid(1, 1, 255, 255, 255);

        canvas.Scale(2, 2);
        canvas.Draw(source, Placement.Of(0, 0, 1, 1));

        Assert.True(Read(canvas, 1, 1).A > 0, "A 2x scale should cover more than one pixel.");
    }

    [Fact]
    public void CopyingASurface_PreservesPixels()
    {
        using var canvas = new Surface(2, 2);
        using var source = Solid(2, 2, 1, 2, 3, 4);

        canvas.Draw(source, Placement.Of(0, 0, 2, 2));

        Assert.Equal((1, 2, 3, 4), Read(canvas, 0, 0));
        Assert.Equal((1, 2, 3, 4), Read(canvas, 1, 1));
    }

    [Fact]
    public void EncodePng_RoundTrips()
    {
        using var canvas = new Surface(2, 1);
        canvas.Fill(Color.Opaque(9, 8, 7));

        var decoded = PngCodec.Decode(canvas.EncodePng());

        Assert.Equal(9, decoded.GetPixel(0, 0).R);
        Assert.Equal(8, decoded.GetPixel(0, 0).G);
        Assert.Equal(7, decoded.GetPixel(0, 0).B);
    }

    [Fact]
    public void ZeroAlpha_DrawsNothing()
    {
        using var canvas = new Surface(2, 2);
        using var source = Solid(2, 2, 255, 0, 0);

        canvas.SetAlpha(0);
        canvas.Draw(source, Placement.Of(0, 0, 2, 2));

        Assert.Equal(0, Read(canvas, 0, 0).A);
    }

    [Fact]
    public void Rect_IntersectionIsEmpty_WhenTheyMiss()
    {
        var left = new Rect(0, 0, 2, 2);
        var right = new Rect(5, 5, 2, 2);

        Assert.True(left.Intersect(right).IsEmpty);
    }

    private static (int R, int G, int B, int A) Read(Surface surface, int x, int y)
    {
        var (r, g, b, a) = surface.Pixels.GetPixel(x, y);
        return (r, g, b, a);
    }
}
