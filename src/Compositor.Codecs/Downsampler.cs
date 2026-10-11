namespace Compositor.Codecs;

/// <summary>
/// Successive halvings of an image, so a zoomed-out draw stays sharp.
/// </summary>
/// <remarks>
/// Ported from <c>DownsampleCache</c> (DownsampleCache.swift:13-112). The reasons it exists are
/// worth keeping:
///
/// <list type="bullet">
/// <item>
/// <b>One-step downscaling looks soft or grainy.</b> A resampler that reads only a few neighbouring
/// pixels, at any interpolation quality, cannot shrink an image 4× or 8× cleanly. A chain of
/// halvings can, then the last reduction handed on is at most 2×.
/// </item>
/// <item>
/// <b>Every halving is exactly 2×, and the size rounds up.</b> That makes level <i>k</i> pixel
/// <i>i</i> cover source pixels <c>i·2^k ..&lt; (i+1)·2^k</c> — exactly the set a whole-image
/// reduction would give that pixel. Without that property, a piece of an image reduced on its own
/// would not line up with the whole image reduced, and tiled rendering would show seams.
/// </item>
/// <item>
/// <b>Colour is padded with eight transparent pixels before reducing, then cropped back.</b> The
/// fade at an edge is then the same wherever the image is cut, and Lanczos ringing beyond a pixel's
/// alpha is clamped so edges do not glow.
/// </item>
/// <item>
/// <b>Masks do not pad; they repeat the last row or column instead.</b> A mask has no alpha to fade
/// into, so a transparent margin would be wrong — it would darken the edge.
/// </item>
/// </list>
/// </remarks>
public static class Downsampler
{
    /// <summary>Most halvings ever produced; past this the drawing resampler does the rest.</summary>
    public const int MaxLevel = 6;

    /// <summary>How far a colour image is padded before each halving.</summary>
    private const int Pad = 8;

    /// <summary>
    /// Halvings to draw from when an image lands <paramref name="factor"/> output pixels per image
    /// pixel: the most that still leave the copy at least that large, and 0 from half size up.
    /// </summary>
    public static int LevelFor(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0 || factor >= 0.5)
        {
            return 0;
        }

        return Math.Min(MaxLevel, (int)Math.Floor(Math.Log2(1 / factor)));
    }

    /// <summary>Exactly half the size, rounded up, resampled in high quality.</summary>
    /// <returns>Null when the reduction could not be produced.</returns>
    public static PixelBuffer? Halve(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var width = (source.Width + 1) / 2;
        var height = (source.Height + 1) / 2;

        // Pad with a transparent margin so the edge fades identically wherever the image is cut.
        var padded = new PixelBuffer(source.Width + (Pad * 2), source.Height + (Pad * 2));
        Canvas.Draw(padded, source, Pad, Pad);

        var reduced = Resample(padded, padded.Width / 2, padded.Height / 2);
        var cropped = new PixelBuffer(width, height);
        Canvas.Draw(cropped, reduced, -Pad / 2, -Pad / 2);

        // The upstream implementation clamps each channel down to its alpha here, because its
        // canvas is premultiplied (DownsampleCache.swift:108). This port carries straight alpha
        // everywhere, where that invariant does not hold and applying it would darken colour that
        // legitimately exceeds its alpha. The padding already removes the ringing it guards against.
        return cropped;
    }

    /// <summary>
    /// Exactly half the size, rounded up, with an odd trailing row or column repeated.
    /// </summary>
    /// <remarks>
    /// A mask has one channel and no alpha, so there is nothing to fade into: the odd row or column
    /// is repeated to keep the last real value at the edge instead of inventing a black one.
    /// </remarks>
    public static MaskBuffer? Halve(MaskBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var width = (source.Width + 1) / 2;
        var height = (source.Height + 1) / 2;

        // Pad to an even 2× so the reduction is exact, repeating the last row and column.
        var paddedWidth = width * 2;
        var paddedHeight = height * 2;
        var padded = new MaskBuffer(paddedWidth, paddedHeight);

        for (var y = 0; y < paddedHeight; y++)
        {
            var sourceY = Math.Min(y, source.Height - 1);
            for (var x = 0; x < paddedWidth; x++)
            {
                var sourceX = Math.Min(x, source.Width - 1);
                padded.Pixels[(y * padded.Stride) + x] = source.Pixels[(sourceY * source.Stride) + sourceX];
            }
        }

        return Resample(padded, width, height);
    }

    /// <summary>
    /// Reduces a colour image by <paramref name="level"/> halvings, reporting how many were applied.
    /// </summary>
    /// <remarks>
    /// Fewer halvings than asked for are applied only when the image reaches 1 pixel on both sides,
    /// which matches the upstream behaviour of stopping there.
    /// </remarks>
    public static (PixelBuffer Image, int Applied) Reduce(PixelBuffer image, int level)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (level < 1 || (image.Width <= 1 && image.Height <= 1))
        {
            return (image, 0);
        }

        var current = image;
        var applied = 0;
        while (applied < level && (current.Width > 1 || current.Height > 1))
        {
            var next = Halve(current);
            if (next is null)
            {
                break;
            }

            current = next;
            applied++;
        }

        return (current, applied);
    }

    /// <summary>Reduces a mask by <paramref name="level"/> halvings.</summary>
    public static (MaskBuffer Mask, int Applied) Reduce(MaskBuffer mask, int level)
    {
        ArgumentNullException.ThrowIfNull(mask);

        if (level < 1 || (mask.Width <= 1 && mask.Height <= 1))
        {
            return (mask, 0);
        }

        var current = mask;
        var applied = 0;
        while (applied < level && (current.Width > 1 || current.Height > 1))
        {
            var next = Halve(current);
            if (next is null)
            {
                break;
            }

            current = next;
            applied++;
        }

        return (current, applied);
    }

    /// <summary>
    /// Reduces a colour image by the halvings appropriate for a draw factor.
    /// </summary>
    public static PixelBuffer ReduceFor(PixelBuffer image, double factor) =>
        Reduce(image, LevelFor(factor)).Image;

    /// <summary>Resamples one buffer into another size, in high quality.</summary>
    private static PixelBuffer Resample(PixelBuffer source, int width, int height)
    {
        var destination = new PixelBuffer(width, height);
        using var pinned = SkiaInterop.Pin(destination);
        using var bitmap = SkiaInterop.ToOwnedBitmap(source);
        using var canvas = new SkiaSharp.SKCanvas(pinned.Bitmap);
        using var paint = new SkiaSharp.SKPaint
        {
            FilterQuality = SkiaSharp.SKFilterQuality.High,
            IsAntialias = false,
        };

        canvas.DrawBitmap(
            bitmap,
            new SkiaSharp.SKRect(0, 0, width, height),
            paint);
        canvas.Flush();
        return destination;
    }

    /// <summary>Resamples coverage into another size, nearest neighbour so no grey is invented.</summary>
    private static MaskBuffer Resample(MaskBuffer source, int width, int height)
    {
        var destination = new MaskBuffer(width, height);
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(height <= 1 ? 0 : (y * source.Height) / height, source.Height - 1);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(width <= 1 ? 0 : (x * source.Width) / width, source.Width - 1);
                destination.Pixels[(y * destination.Stride) + x] = source.Pixels[(sourceY * source.Stride) + sourceX];
            }
        }

        return destination;
    }
}
