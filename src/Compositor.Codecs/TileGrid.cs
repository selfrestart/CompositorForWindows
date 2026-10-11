namespace Compositor.Codecs;

/// <summary>
/// The grid arithmetic that lets a layer be drawn as rebuilt pieces rather than one image, without
/// seams.
/// </summary>
/// <remarks>
/// Ported from the geometry half of <c>TiledLayerRenderer</c> (TiledLayerRenderer.swift:26-49,
/// 288-354). The problem it solves is stated in that file's header: drawing each tile on its own
/// resamples it without its neighbours, which shows as a seam, and it cannot use the sharp halvings
/// — so a layer's edited areas are rebuilt as squares of the layer's own grid, each carried with a
/// margin of surrounding pixels so its resampled result matches what the whole image would give.
///
/// <list type="bullet">
/// <item>
/// <b>Every square is a multiple of every halving used.</b> <see cref="CommittedCell"/> is 1024 and
/// <see cref="StrokeCell"/> is 256, both divisible by 2^6, so a square boundary always falls on a
/// level boundary. That is what lets a piece be reduced with the same halvings as the whole image
/// and still line up with it.
/// </item>
/// <item>
/// <b>The margin is grown to the level's step, then clipped to the layer's own bounds.</b> Past that
/// edge there is nothing to compose, and resampling a piece whose margin is empty would pull that
/// emptiness into the layer's edge — which the whole image's own draw never does.
/// </item>
/// <item>
/// <b>Pieces stay disjoint.</b> A piece is the part of a changed region that falls inside one
/// square, grown to the step grid. Squares are on the step grid too, so growing never leaves its
/// square and two pieces can never overlap.
/// </item>
/// </list>
///
/// <para>
/// <b>What is not here yet, and why.</b> Upstream this lives inside a renderer that also composes
/// the pieces: it draws the layer's full-resolution pixels into a context translated to the grid
/// origin, reduces the result, and clips each piece to its interior with hard edges so neighbours
/// meet exactly (TiledLayerRenderer.swift:295-320, 259-280). That composition step is tangled up
/// with brush strokes and a bottom-left coordinate flip, and the painting system it serves is not
/// ported yet — so it is deferred rather than guessed at. The grid arithmetic is the part the rest
/// of the renderer depends on, and it is testable on its own.
/// </para>
/// </remarks>
public static class TileGrid
{
    /// <summary>Committed snapshots use large squares: few to build, and built once.</summary>
    /// <remarks>1024 is a whole multiple of every halving up to <see cref="Downsampler.MaxLevel"/>.</remarks>
    public const double CommittedCell = 1024;

    /// <summary>Live strokes use small squares: little to rebuild per pointer move.</summary>
    public const double StrokeCell = 256;

    /// <summary>Largest piece area allowed, matching the upstream ceiling of 64 million pixels.</summary>
    public const long MaxPiecePixels = 64_000_000;

    /// <summary>
    /// Grid pixels beyond a change that its reduced, resampled pixels can reach, with room to spare.
    /// </summary>
    public static double Support(int level) => level == 0 ? 8 : 16 << level;

    /// <summary>Grows a rectangle outward to whole multiples of <paramref name="step"/> from an origin.</summary>
    /// <remarks>
    /// Growth is outward on every side, so the result always contains the input. Measuring from the
    /// origin rather than from zero is what keeps the grid stable as a layer is panned: the piece
    /// boundaries stay in the layer's own coordinates instead of drifting with the view.
    /// </remarks>
    public static Rect Align(Rect rect, double step, double originX, double originY)
    {
        if (!double.IsFinite(step) || step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "Step must be finite and positive.");
        }

        var minX = originX + (Math.Floor((rect.X - originX) / step) * step);
        var minY = originY + (Math.Floor((rect.Y - originY) / step) * step);
        var maxX = originX + (Math.Ceiling((rect.Right - originX) / step) * step);
        var maxY = originY + (Math.Ceiling((rect.Bottom - originY) / step) * step);

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// The piece interiors to rebuild near <paramref name="regions"/>: in each square, the part
    /// within the margin of a changed region, grown to the step grid.
    /// </summary>
    /// <param name="regions">The changed areas, in layer grid pixels.</param>
    /// <param name="level">Halvings in use, which sets the margin and the step.</param>
    /// <param name="cellSize">Square size: <see cref="CommittedCell"/> or <see cref="StrokeCell"/>.</param>
    /// <param name="originX">Grid origin the squares are measured from.</param>
    /// <param name="originY">Grid origin the squares are measured from.</param>
    /// <param name="visible">Grid pixels the view can show, or null to consider all of them.</param>
    public static List<Rect> InteriorsNear(
        IEnumerable<Rect> regions,
        int level,
        double cellSize,
        double originX,
        double originY,
        Rect? visible = null)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        if (!double.IsFinite(cellSize) || cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be finite and positive.");
        }

        if (cellSize % (1 << Math.Min(level, Downsampler.MaxLevel)) != 0)
        {
            throw new ArgumentException(
                $"A cell size of {cellSize} is not a whole multiple of the level {level} step "
                + $"({1 << Math.Min(level, Downsampler.MaxLevel)}); piece boundaries would not fall on "
                + "level boundaries and the pieces would not line up with the whole image.",
                nameof(cellSize));
        }

        var margin = Support(level);
        var step = (double)(1 << level);
        var parts = new Dictionary<(int X, int Y), Rect>();

        foreach (var region in regions)
        {
            var grown = Grow(region, margin);

            // Skip what the view cannot show: rebuilding an invisible piece is wasted work.
            if (visible is { } visibleRect && !grown.Intersects(visibleRect))
            {
                continue;
            }

            var x0 = (int)Math.Floor((grown.X - originX) / cellSize);
            var x1 = (int)Math.Ceiling((grown.Right - originX) / cellSize);
            var y0 = (int)Math.Floor((grown.Y - originY) / cellSize);
            var y1 = (int)Math.Ceiling((grown.Bottom - originY) / cellSize);
            if (x1 <= x0 || y1 <= y0)
            {
                continue;
            }

            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var square = new Rect(
                        originX + (x * cellSize),
                        originY + (y * cellSize),
                        cellSize,
                        cellSize);
                    var part = grown.Intersect(square);
                    if (part.IsEmpty)
                    {
                        continue;
                    }

                    var key = (x, y);

                    // Several changed regions can land in the same square; union them so the square
                    // is rebuilt once.
                    parts[key] = parts.TryGetValue(key, out var existing) ? Union(existing, part) : part;
                }
            }
        }

        var result = new List<Rect>(parts.Count);
        foreach (var part in parts.Values)
        {
            var aligned = Align(part, step, originX, originY);

            // Aligning outward cannot leave the square, because squares sit on the step grid too;
            // the filter is the upstream guard against a changed region reaching past `visible`.
            if (visible is { } limit && !aligned.Intersects(limit))
            {
                continue;
            }

            result.Add(aligned);
        }

        return result;
    }

    /// <summary>
    /// The region a piece must compose: its interior grown by the support, aligned, and clipped to
    /// the layer's own bounds.
    /// </summary>
    /// <param name="interior">The grid pixels the piece draws.</param>
    /// <param name="level">Halvings in use.</param>
    /// <param name="originX">Grid origin.</param>
    /// <param name="originY">Grid origin.</param>
    /// <param name="bounds">
    /// Everything that holds pixels — the layer's grid, plus anything painted past it. The margin is
    /// kept inside it, so an empty margin never pulls emptiness into the layer's edge.
    /// </param>
    /// <returns>The region to compose, or null when there is nothing left after clipping.</returns>
    public static Rect? RegionFor(
        Rect interior,
        int level,
        double originX,
        double originY,
        Rect? bounds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        var margin = Support(level);
        var step = (double)(1 << level);
        var region = Align(Grow(interior, margin), step, originX, originY);

        if (bounds is { } limit)
        {
            var alignedLimit = Align(limit, step, originX, originY);
            region = region.Intersect(alignedLimit);
            if (region.IsEmpty)
            {
                return null;
            }
        }

        var width = (long)Math.Floor(region.Width);
        var height = (long)Math.Floor(region.Height);
        if (width <= 0 || height <= 0 || width * height > MaxPiecePixels)
        {
            return null;
        }

        return region;
    }

    /// <summary>
    /// Rounds a rectangle's edges to whole screen pixels before clipping.
    /// </summary>
    /// <remarks>
    /// A clip edge landing on a fraction of a screen pixel gets rounded one way in one piece and the
    /// other way in its neighbour, which shows as a hairline across translucent pixels. Rounding
    /// both pieces' shared edge to the same whole pixels makes them meet exactly.
    ///
    /// <para>
    /// The upstream version skips this for a rotated layer, whose pieces do not lie along the
    /// screen's pixels at all; that case is expressed here by passing
    /// <paramref name="isAxisAligned"/> false.
    /// </para>
    /// </remarks>
    public static Rect Snap(Rect rect, double scaleX, double scaleY, bool isAxisAligned = true)
    {
        if (!isAxisAligned)
        {
            return rect;
        }

        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || scaleX == 0 || scaleY == 0)
        {
            return rect;
        }

        var minX = Math.Round(rect.X * scaleX);
        var minY = Math.Round(rect.Y * scaleY);
        var maxX = Math.Round(rect.Right * scaleX);
        var maxY = Math.Round(rect.Bottom * scaleY);

        return new Rect(
            minX / scaleX,
            minY / scaleY,
            Math.Max(0, maxX - minX) / scaleX,
            Math.Max(0, maxY - minY) / scaleY);
    }

    private static Rect Grow(Rect rect, double margin) =>
        new(rect.X - margin, rect.Y - margin, rect.Width + (margin * 2), rect.Height + (margin * 2));

    private static Rect Union(Rect left, Rect right)
    {
        var x = Math.Min(left.X, right.X);
        var y = Math.Min(left.Y, right.Y);
        var right2 = Math.Max(left.Right, right.Right);
        var bottom = Math.Max(left.Bottom, right.Bottom);
        return new Rect(x, y, right2 - x, bottom - y);
    }
}
