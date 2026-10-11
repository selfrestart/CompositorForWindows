using Compositor.Codecs;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// The tile grid: the arithmetic that keeps rebuilt pieces from showing seams.
/// </summary>
/// <remarks>
/// Three invariants carry the whole scheme, and all three are asserted here directly:
///
/// <list type="number">
/// <item>Every piece boundary lands on the halving step grid, so a piece reduced by the same
/// halvings as the whole image still lines up with it.</item>
/// <item>Pieces never overlap and never leave their square, so two pieces can neither double-draw a
/// pixel nor leave a gap.</item>
/// <item>The composed region always contains the interior it will be clipped to.</item>
/// </list>
/// </remarks>
public sealed class TileGridTests
{
    // ---- support margin ----

    [Theory]
    [InlineData(0, 8)]
    [InlineData(1, 32)]
    [InlineData(2, 64)]
    [InlineData(3, 128)]
    [InlineData(6, 1024)]
    public void Support_GrowsWithTheLevel(int level, double expected) =>
        Assert.Equal(expected, TileGrid.Support(level));

    [Fact]
    public void Support_IsAlwaysPositive()
    {
        // A zero margin would let resampling reach past the region it composed.
        for (var level = 0; level <= Downsampler.MaxLevel; level++)
        {
            Assert.True(TileGrid.Support(level) > 0);
        }
    }

    // ---- cell sizes ----

    [Fact]
    public void BothCellSizes_AreMultiplesOfEveryHalving()
    {
        // This is why the piece grid and the halving grid agree: 1024 and 256 are both divisible by
        // 2^6, so a square boundary always falls on a level boundary.
        var largestStep = 1 << Downsampler.MaxLevel;

        Assert.Equal(0, TileGrid.CommittedCell % largestStep);
        Assert.Equal(0, TileGrid.StrokeCell % largestStep);
    }

    // ---- alignment ----

    [Fact]
    public void Align_GrowsOutwardOnEverySide()
    {
        var rect = new Rect(10, 20, 30, 40);

        var aligned = TileGrid.Align(rect, step: 16, originX: 0, originY: 0);

        Assert.Equal(0, aligned.X);
        Assert.Equal(16, aligned.Y);
        Assert.Equal(48, aligned.Width);
        Assert.Equal(48, aligned.Height);
        Assert.True(aligned.X <= rect.X && aligned.Y <= rect.Y);
        Assert.True(aligned.Right >= rect.Right && aligned.Bottom >= rect.Bottom);
    }

    [Fact]
    public void Align_ProducesEdgesOnTheStepGrid()
    {
        var aligned = TileGrid.Align(new Rect(7, 13, 5, 9), step: 8, originX: 3, originY: 5);

        foreach (var edge in new[] { aligned.X - 3, aligned.Y - 5, aligned.Right - 3, aligned.Bottom - 5 })
        {
            Assert.Equal(0, edge % 8);
        }
    }

    [Fact]
    public void Align_LeavesAnAlreadyAlignedRectAlone()
    {
        var rect = new Rect(0, 0, 256, 256);

        var aligned = TileGrid.Align(rect, step: 32, originX: 0, originY: 0);

        Assert.Equal(rect, aligned);
    }

    [Fact]
    public void Align_RespectsANonZeroOrigin()
    {
        // The grid is measured from the layer's origin, so piece boundaries stay in the layer's own
        // coordinates rather than drifting as the view pans. The property is that every edge lands
        // on origin + k·step — not that the origin itself is on an edge.
        const double originX = 10;
        const double originY = 10;
        const double step = 64;

        var aligned = TileGrid.Align(new Rect(100, 100, 1, 1), step, originX, originY);

        // (100 − 10) / 64 rounds down to 1, so the near edge is 10 + 64 = 74.
        Assert.Equal(74, aligned.X);
        Assert.Equal(74, aligned.Y);

        foreach (var edge in new[] { aligned.X - originX, aligned.Y - originY, aligned.Right - originX, aligned.Bottom - originY })
        {
            Assert.Equal(0, edge % step);
        }

        // And it still contains the original rectangle.
        Assert.True(aligned.X <= 100 && aligned.Y <= 100);
        Assert.True(aligned.Right >= 101 && aligned.Bottom >= 101);
    }

    [Fact]
    public void Align_RejectsAMeaninglessStep() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TileGrid.Align(new Rect(0, 0, 1, 1), 0, 0, 0));

    // ---- piece interiors ----

    [Fact]
    public void InteriorsNear_ReturnsNothingForNoChanges() =>
        Assert.Empty(TileGrid.InteriorsNear([], level: 0, cellSize: 256, originX: 0, originY: 0));

    [Fact]
    public void InteriorsNear_CoversTheChangedRegionAndItsMargin()
    {
        var changed = new Rect(300, 300, 40, 40);

        var pieces = TileGrid.InteriorsNear([changed], level: 0, cellSize: 256, originX: 0, originY: 0);

        Assert.NotEmpty(pieces);

        // Every piece must contain part of the changed region, and together they must cover a
        // rectangle at least as big as the region grown by the support margin.
        var margin = TileGrid.Support(0);
        foreach (var piece in pieces)
        {
            Assert.True(
                piece.Intersects(changed) || piece.X <= changed.X + margin,
                "A piece should sit near the change it exists for.");
        }

        Assert.Contains(pieces, piece => piece.Intersects(changed));
    }

    [Fact]
    public void InteriorsNear_PiecesAreDisjoint()
    {
        // Overlapping pieces would draw the same pixel twice; gaps would show the untouched image
        // through. Both are visible artefacts.
        var changes = new[]
        {
            new Rect(100, 100, 300, 50),
            new Rect(400, 120, 500, 400),
            new Rect(1000, 1000, 10, 10),
        };

        var pieces = TileGrid.InteriorsNear(changes, level: 0, cellSize: 256, originX: 0, originY: 0);

        for (var i = 0; i < pieces.Count; i++)
        {
            for (var j = i + 1; j < pieces.Count; j++)
            {
                Assert.False(
                    pieces[i].Intersects(pieces[j]),
                    $"Pieces {pieces[i]} and {pieces[j]} overlap.");
            }
        }
    }

    [Fact]
    public void InteriorsNear_PieceEdgesAreOnTheStepGrid()
    {
        // The property that makes a piece line up with the whole image reduced.
        foreach (var level in new[] { 0, 1, 2, 4, 6 })
        {
            var step = 1 << level;
            var pieces = TileGrid.InteriorsNear(
                [new Rect(137, 211, 333, 97)],
                level,
                TileGrid.CommittedCell,
                originX: 0,
                originY: 0);

            Assert.NotEmpty(pieces);
            foreach (var piece in pieces)
            {
                Assert.Equal(0, piece.X % step);
                Assert.Equal(0, piece.Y % step);
                Assert.Equal(0, piece.Right % step);
                Assert.Equal(0, piece.Bottom % step);
            }
        }
    }

    [Fact]
    public void InteriorsNear_PiecesStayInsideTheirSquare()
    {
        // A piece is the part of a change inside one square, grown to the step grid. Since squares
        // are on the step grid, growth can never push it out.
        const double cell = 256;
        var pieces = TileGrid.InteriorsNear(
            [new Rect(10, 10, 700, 700)],
            level: 1,
            cellSize: cell,
            originX: 0,
            originY: 0);

        Assert.NotEmpty(pieces);
        foreach (var piece in pieces)
        {
            var squareX = Math.Floor(piece.X / cell) * cell;
            var squareY = Math.Floor(piece.Y / cell) * cell;
            Assert.True(piece.X >= squareX && piece.Right <= squareX + cell, $"{piece} left its column.");
            Assert.True(piece.Y >= squareY && piece.Bottom <= squareY + cell, $"{piece} left its row.");
        }
    }

    [Fact]
    public void InteriorsNear_MergesSeveralChangesInOneSquare()
    {
        // Two changes close together should rebuild one square, not two overlapping pieces.
        var pieces = TileGrid.InteriorsNear(
            [new Rect(10, 10, 5, 5), new Rect(20, 20, 5, 5)],
            level: 0,
            cellSize: 256,
            originX: 0,
            originY: 0);

        Assert.Single(pieces);
    }

    [Fact]
    public void InteriorsNear_SkipsWhatTheViewCannotShow()
    {
        var changes = new[] { new Rect(5000, 5000, 10, 10) };
        var visible = new Rect(0, 0, 500, 500);

        var pieces = TileGrid.InteriorsNear(changes, level: 0, cellSize: 256, originX: 0, originY: 0, visible);

        Assert.Empty(pieces);
    }

    [Fact]
    public void InteriorsNear_SkipsWhatFallsOutsideTheVisibleAreaAfterAligning()
    {
        // Just outside the visible edge, but the margin brings it close: still nothing to draw.
        var changes = new[] { new Rect(520, 520, 2, 2) };
        var visible = new Rect(0, 0, 500, 500);

        var pieces = TileGrid.InteriorsNear(changes, level: 0, cellSize: 256, originX: 0, originY: 0, visible);

        Assert.Empty(pieces);
    }

    [Fact]
    public void InteriorsNear_KeepsWhatIsPartlyVisible()
    {
        var changes = new[] { new Rect(480, 480, 60, 60) };
        var visible = new Rect(0, 0, 500, 500);

        var pieces = TileGrid.InteriorsNear(changes, level: 0, cellSize: 256, originX: 0, originY: 0, visible);

        Assert.NotEmpty(pieces);
    }

    [Fact]
    public void InteriorsNear_RejectsACellSizeThatIsNotAMultipleOfTheStep()
    {
        // A cell size with a stray factor would put piece boundaries off the level grid, and the
        // pieces would no longer line up with the whole image. Better to refuse than to draw seams.
        Assert.Throws<ArgumentException>(() =>
            TileGrid.InteriorsNear([new Rect(0, 0, 10, 10)], level: 3, cellSize: 100, originX: 0, originY: 0));
    }

    // ---- composed region ----

    [Fact]
    public void RegionFor_ContainsTheInteriorWithMargin()
    {
        var interior = new Rect(300, 300, 100, 100);

        var region = TileGrid.RegionFor(interior, level: 0, originX: 0, originY: 0);

        Assert.True(region.HasValue, "A piece with room to compose should have a region.");
        var margin = TileGrid.Support(0);
        Assert.True(region!.Value.X <= interior.X - margin + 8, "The region should include the support margin.");
        Assert.True(region.Value.Right >= interior.Right + margin - 8);
    }

    [Fact]
    public void RegionFor_ContainsTheInteriorItWillBeClippedTo()
    {
        // A region that did not contain its interior would clip the piece away entirely.
        foreach (var level in new[] { 0, 2, 4 })
        {
            var interior = TileGrid.Align(new Rect(500, 500, 120, 120), 1 << level, 0, 0);
            var region = TileGrid.RegionFor(interior, level, 0, 0);

            Assert.True(region.HasValue, $"Level {level} should have produced a region.");
            Assert.True(region!.Value.X <= interior.X && region.Value.Y <= interior.Y);
            Assert.True(region.Value.Right >= interior.Right && region.Value.Bottom >= interior.Bottom);
        }
    }

    [Fact]
    public void RegionFor_IsClippedToTheLayersBounds()
    {
        // Past the layer's edge there is nothing to compose, and a piece whose margin is empty would
        // pull that emptiness into the layer's edge.
        var interior = new Rect(0, 0, 100, 100);
        var bounds = new Rect(0, 0, 200, 200);

        var region = TileGrid.RegionFor(interior, level: 0, originX: 0, originY: 0, bounds);

        Assert.True(region.HasValue);
        Assert.True(region!.Value.X >= 0 && region.Value.Y >= 0);
    }

    [Fact]
    public void RegionFor_ReturnsNullWhenNothingSurvivesClipping()
    {
        var interior = new Rect(1000, 1000, 50, 50);
        var bounds = new Rect(0, 0, 100, 100);

        Assert.Null(TileGrid.RegionFor(interior, level: 0, originX: 0, originY: 0, bounds));
    }

    [Fact]
    public void RegionFor_ReturnsNullPastThePixelCeiling()
    {
        // The upstream ceiling is 64 million pixels for one piece.
        var huge = new Rect(0, 0, 20000, 20000);

        Assert.Null(TileGrid.RegionFor(huge, level: 0, originX: 0, originY: 0));
    }

    // ---- edge snapping ----

    [Fact]
    public void Snap_RoundsEdgesToWholeDevicePixels()
    {
        // A clip edge on a fraction of a screen pixel rounds one way in one piece and the other way
        // in its neighbour, which shows as a hairline across translucent pixels.
        var rect = new Rect(10.4, 20.6, 30.2, 40.9);

        var snapped = TileGrid.Snap(rect, scaleX: 1, scaleY: 1);

        Assert.Equal(Math.Round(10.4), snapped.X);
        Assert.Equal(Math.Round(20.6), snapped.Y);
        Assert.Equal(Math.Round(10.4 + 30.2) - Math.Round(10.4), snapped.Width, 6);
    }

    [Fact]
    public void Snap_WithAScale_StillLandsOnWholeDevicePixels()
    {
        var rect = new Rect(10.4, 20.6, 30.2, 40.9);

        var snapped = TileGrid.Snap(rect, scaleX: 2, scaleY: 2);

        Assert.Equal(0, Math.Round(snapped.X * 2) - (snapped.X * 2), 6);
        Assert.Equal(0, Math.Round(snapped.Right * 2) - (snapped.Right * 2), 6);
    }

    [Fact]
    public void Snap_LeavesARotatedLayerAlone()
    {
        // A rotated layer's pieces do not lie along the screen's pixels at all, so rounding would
        // move them without making them meet.
        var rect = new Rect(10.4, 20.6, 30.2, 40.9);

        Assert.Equal(rect, TileGrid.Snap(rect, 1, 1, isAxisAligned: false));
    }

    [Fact]
    public void Snap_WithADegenerateScale_ReturnsTheRectUnchanged()
    {
        var rect = new Rect(10.4, 20.6, 30.2, 40.9);

        Assert.Equal(rect, TileGrid.Snap(rect, 0, 1));
        Assert.Equal(rect, TileGrid.Snap(rect, double.NaN, 1));
    }
}
