using Compositor.Codecs;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// The downsampler: halving levels, exact 2× reductions, and the tiling-alignment property.
/// </summary>
/// <remarks>
/// The property that matters most is the last one. A halving is exactly 2× with the size rounded up,
/// so level <i>k</i> pixel <i>i</i> covers source pixels <c>i·2^k ..&lt; (i+1)·2^k</c>. Everything
/// else here is about not inventing colours at edges.
/// </remarks>
public sealed class DownsamplerTests
{
    // ---- level selection ----

    [Theory]
    [InlineData(1.0, 0)]      // drawn at its own size
    [InlineData(0.5, 0)]      // exactly half: the resampler handles it
    [InlineData(0.49, 1)]
    [InlineData(0.25, 2)]
    [InlineData(0.125, 3)]
    [InlineData(0.0625, 4)]
    [InlineData(0.03125, 5)]
    [InlineData(0.015625, 6)]
    [InlineData(0.0078125, 6)] // past the cap
    public void LevelFor_PicksTheHalvingsThatStillLeaveTheCopyBigEnough(double factor, int expected) =>
        Assert.Equal(expected, Downsampler.LevelFor(factor));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void LevelFor_RejectsMeaninglessFactors(double factor) =>
        Assert.Equal(0, Downsampler.LevelFor(factor));

    // ---- halving dimensions ----

    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(2, 2, 1, 1)]
    [InlineData(3, 3, 2, 2)]   // rounds up
    [InlineData(4, 4, 2, 2)]
    [InlineData(5, 3, 3, 2)]
    [InlineData(8, 1, 4, 1)]
    public void HalvingRoundsUp(int width, int height, int expectedWidth, int expectedHeight)
    {
        var source = new PixelBuffer(width, height);
        source.Clear(120, 130, 140, 255);

        var halved = Downsampler.Halve(source);

        Assert.NotNull(halved);
        Assert.Equal(expectedWidth, halved!.Width);
        Assert.Equal(expectedHeight, halved.Height);
    }

    [Fact]
    public void AUniformImage_StaysUniform()
    {
        // A flat colour has nothing to resample, so every reduction must be that same colour.
        var source = new PixelBuffer(8, 8);
        source.Clear(200, 100, 50, 255);

        var halved = Downsampler.Halve(source)!;

        for (var y = 0; y < halved.Height; y++)
        {
            for (var x = 0; x < halved.Width; x++)
            {
                var (r, g, b, a) = halved.GetPixel(x, y);
                Assert.Equal((200, 100, 50, 255), ((int)r, (int)g, (int)b, (int)a));
            }
        }
    }

    [Fact]
    public void ThePaddedRegion_IsNotInTheResult()
    {
        // The eight transparent pixels the colour path pads with must be cropped away again: a
        // residual margin would put a transparent border round every zoomed-out layer.
        var source = new PixelBuffer(4, 4);
        source.Clear(255, 0, 0, 255);

        var halved = Downsampler.Halve(source)!;

        Assert.Equal(2, halved.Width);
        Assert.Equal(2, halved.Height);
        Assert.Equal(255, halved.GetPixel(0, 0).A);
        Assert.Equal(255, halved.GetPixel(1, 1).A);
    }

    // ---- reduction chains ----

    [Fact]
    public void Reduce_StopsAtOnePixel()
    {
        var source = new PixelBuffer(4, 4);
        source.Clear(10, 20, 30, 255);

        var (reduced, applied) = Downsampler.Reduce(source, level: 6);

        // 4 → 2 → 1, so only two halvings were possible.
        Assert.Equal(2, applied);
        Assert.Equal(1, reduced.Width);
        Assert.Equal(1, reduced.Height);
    }

    [Fact]
    public void Reduce_WithLevelZero_ReturnsTheOriginal()
    {
        var source = new PixelBuffer(4, 4);

        var (reduced, applied) = Downsampler.Reduce(source, level: 0);

        Assert.Equal(0, applied);
        Assert.Same(source, reduced);
    }

    [Fact]
    public void EachLevel_IsExactlyHalfThePrevious()
    {
        var source = new PixelBuffer(64, 64);
        source.Clear(1, 2, 3, 255);

        var sizes = new List<(int Width, int Height)>();
        var current = source;
        for (var level = 0; level < 6; level++)
        {
            current = Downsampler.Halve(current)!;
            sizes.Add((current.Width, current.Height));
        }

        Assert.Equal([(32, 32), (16, 16), (8, 8), (4, 4), (2, 2), (1, 1)], sizes);
    }

    // ---- the alignment property ----

    /// <summary>
    /// Level k pixel i covers source pixels <c>i·2^k ..&lt; (i+1)·2^k</c>. This is the property that
    /// lets a tile be reduced on its own and still line up with the whole image reduced, so a seam
    /// here would show up as a visible join in a tiled render.
    /// </summary>
    [Fact]
    public void LevelK_PixelI_CoversExactlyItsPowerOfTwoRange()
    {
        // A mask is used because its reduction is a nearest-neighbour pick, so which source pixel
        // ended up in which destination pixel is observable; a colour reduction blends them.
        const int width = 32;
        const int height = 1;
        var source = new MaskBuffer(width, height);
        for (var x = 0; x < width; x++)
        {
            source.Pixels[x] = (byte)x; // a unique value per source pixel
        }

        var level1 = Downsampler.Halve(source)!;
        Assert.Equal(16, level1.Width);
        Assert.Equal(0, level1.Pixels[0]);   // covers source 0..1, takes the first
        Assert.Equal(2, level1.Pixels[1]);   // covers source 2..3
        Assert.Equal(30, level1.Pixels[15]); // covers source 30..31

        var level2 = Downsampler.Halve(level1)!;
        Assert.Equal(8, level2.Width);
        Assert.Equal(0, level2.Pixels[0]);   // covers source 0..3
        Assert.Equal(4, level2.Pixels[1]);   // covers source 4..7
        Assert.Equal(28, level2.Pixels[7]);  // covers source 28..31

        var level3 = Downsampler.Halve(level2)!;
        Assert.Equal(4, level3.Width);
        Assert.Equal(0, level3.Pixels[0]);   // covers source 0..7
        Assert.Equal(8, level3.Pixels[1]);   // covers source 8..15
        Assert.Equal(24, level3.Pixels[3]);  // covers source 24..31
    }

    [Fact]
    public void HalvingATileAndHalvingTheWholeImage_AgreeOnTheSharedPixels()
    {
        // The tiled-rendering guarantee, stated directly: reduce the whole image, then reduce a
        // sub-rectangle of it, and the overlapping pixels must match.
        const int size = 32;
        var whole = new MaskBuffer(size, size);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                whole.Pixels[(y * whole.Stride) + x] = (byte)((x + (y * size)) % 256);
            }
        }

        var wholeReduced = Downsampler.Halve(whole)!;

        // A 16x16 tile at level 1 must line up with a quarter of the reduced image.
        var tile = new MaskBuffer(16, 16);
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                tile.Pixels[(y * tile.Stride) + x] = whole.Pixels[(y * whole.Stride) + x];
            }
        }

        var tileReduced = Downsampler.Halve(tile)!;

        Assert.Equal(8, tileReduced.Width);
        for (var y = 0; y < tileReduced.Height; y++)
        {
            for (var x = 0; x < tileReduced.Width; x++)
            {
                Assert.Equal(
                    wholeReduced.Pixels[(y * wholeReduced.Stride) + x],
                    tileReduced.Pixels[(y * tileReduced.Stride) + x]);
            }
        }
    }

    // ---- masks ----

    [Fact]
    public void AMaskHalvesWithoutPadding()
    {
        // A mask has no alpha to fade into, so the padded path would darken its edge.
        var mask = new MaskBuffer(4, 4, Enumerable.Repeat((byte)200, 16).ToArray());

        var halved = Downsampler.Halve(mask)!;

        Assert.Equal(2, halved.Width);
        Assert.Equal(2, halved.Height);
        Assert.Equal(200, halved.Pixels[0]);
        Assert.Equal(200, halved.Pixels[3]);
    }

    [Fact]
    public void AnOddMask_RepeatsTheLastRowAndColumn()
    {
        // 3x3 reduces to 2x2: the repeated row and column keep the real edge value rather than
        // inventing a black one.
        var mask = new MaskBuffer(3, 3, [
            10, 20, 30,
            40, 50, 60,
            70, 80, 90,
        ]);

        var halved = Downsampler.Halve(mask)!;

        Assert.Equal(2, halved.Width);
        Assert.Equal(2, halved.Height);
        Assert.Equal(10, halved.Pixels[0]); // top-left
        Assert.Equal(30, halved.Pixels[1]); // the repeated last column, not a black pixel
        Assert.Equal(70, halved.Pixels[2]); // the repeated last row
    }

    [Fact]
    public void ASolidMask_StaysSolidThroughEveryLevel()
    {
        var mask = MaskBuffer.Solid(revealing: true);

        var (reduced, applied) = Downsampler.Reduce(mask, level: 6);

        // A 1x1 mask is already as small as it goes.
        Assert.Equal(0, applied);
        Assert.Equal(1, reduced.Width);
        Assert.Equal(255, reduced.Pixels[0]);
    }

    [Fact]
    public void ReduceFor_UsesTheFactorToPickALevel()
    {
        var source = new PixelBuffer(64, 64);
        source.Clear(50, 60, 70, 255);

        // A factor of 0.25 asks for two halvings: 64 → 32 → 16.
        var reduced = Downsampler.ReduceFor(source, 0.25);

        Assert.Equal(16, reduced.Width);
        Assert.Equal(16, reduced.Height);
    }

    // ---- mask PNG format ----

    [Fact]
    public void AMaskEncodesAsTrueGrayscale()
    {
        // The format requires a monochrome image with no alpha (LayerMask.swift:24-25). An RGBA
        // encode would be colour type 6 and the macOS side would reject it.
        var mask = new MaskBuffer(2, 2, [0, 64, 128, 255]);

        var png = PngCodec.Encode(mask);

        Assert.Equal(0, png[25]); // IHDR colour type 0 is greyscale
    }

    [Fact]
    public void MaskPngRoundTrips()
    {
        var mask = new MaskBuffer(3, 2, [0, 1, 2, 100, 200, 255]);

        var decoded = PngCodec.DecodeMask(PngCodec.Encode(mask));

        Assert.Equal(3, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(mask.Pixels, decoded.Pixels);
    }

    [Fact]
    public void MaskCoverage_ReadsAndWritesInTheExpectedRange()
    {
        var mask = new MaskBuffer(2, 1);

        mask.SetCoverage(0, 0, 0.5);
        mask.SetCoverage(1, 0, 1.0);

        Assert.Equal(128, mask.Pixels[0]);
        Assert.Equal(255, mask.Pixels[1]);
        Assert.Equal(128 / 255.0, mask.Coverage(0, 0), 6);
        Assert.Equal(0.0, mask.Coverage(5, 5)); // out of range hides
    }
}
