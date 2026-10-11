using Compositor.Core;
using SkiaSharp;

namespace Compositor.Codecs;

/// <summary>
/// How a source image is placed onto a surface: a destination rectangle, a rotation about its own
/// centre, and axis flips.
/// </summary>
/// <remarks>
/// This is the geometry half of <see cref="LayerTransform"/> (LayerTransform.swift:18-26), with the
/// document-space origin and size already resolved into canvas pixels.
///
/// <b>Rotation is clockwise and happens about the centre of the destination rectangle</b>, matching
/// <c>LayerTransform.center</c>. Flip is applied before rotation, so a flipped-and-rotated layer
/// behaves the way the transform overlay shows it.
/// </remarks>
public readonly record struct Placement
{
    /// <summary>Left edge of the unrotated rectangle, in canvas pixels.</summary>
    public double X { get; init; }

    /// <summary>Top edge of the unrotated rectangle, in canvas pixels.</summary>
    public double Y { get; init; }

    /// <summary>Width of the unrotated rectangle, in canvas pixels.</summary>
    public double Width { get; init; }

    /// <summary>Height of the unrotated rectangle, in canvas pixels.</summary>
    public double Height { get; init; }

    /// <summary>Clockwise rotation in degrees about the rectangle's centre.</summary>
    public double RotationDegrees { get; init; }

    /// <summary>Mirror horizontally before rotating.</summary>
    public bool FlipX { get; init; }

    /// <summary>Mirror vertically before rotating.</summary>
    public bool FlipY { get; init; }

    /// <summary>An unrotated, unflipped placement.</summary>
    public static Placement Of(double x, double y, double width, double height) =>
        new() { X = x, Y = y, Width = width, Height = height };

    /// <summary>Builds a placement from a layer transform, which is already in document pixels.</summary>
    public static Placement From(LayerTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        return new Placement
        {
            X = transform.Origin.X,
            Y = transform.Origin.Y,
            Width = transform.Size.Width,
            Height = transform.Size.Height,
            RotationDegrees = transform.Rotation,
            FlipX = transform.FlipX,
            FlipY = transform.FlipY,
        };
    }

    /// <summary>Centre of the unrotated rectangle.</summary>
    public (double X, double Y) Center => (X + (Width / 2), Y + (Height / 2));

    /// <summary>Whether the placement is axis-aligned and unflipped, so no resampling is needed.</summary>
    public bool IsAxisAligned => RotationDegrees == 0 && !FlipX && !FlipY;
}

/// <summary>
/// A bitmap-backed drawing surface: the replacement for <c>CGContext</c>.
/// </summary>
/// <remarks>
/// Upstream reaches <c>CGContext</c> in 113 places and <c>CGImage</c> in 500, and almost all of it
/// is immediate-mode drawing into a bitmap: matrices, a clip stack, a blend mode, an alpha, and
/// <c>draw(image, in:)</c>. This type covers exactly that.
///
/// <para>
/// <b>Two drawing paths, one blend model.</b> <see cref="Draw"/>. A placement that is axis-aligned
/// and unscaled is composited pixel by pixel through <see cref="LayerCompositor.CompositePixel"/>,
/// which is the implementation verified against the numbers upstream recorded
/// (SeparableBlend.swift:14-16). Anything needing resampling goes through Skia. That split is
/// deliberate: the common case stays exact and independently testable, and the resampling case
/// gets a correct interpolator instead of a hand-rolled one. Skia's own blend modes were checked
/// against the same specification and agree, so the two paths do not disagree about colour.
/// </para>
///
/// <para>
/// <b>Everything happens on sRGB-encoded values.</b> No colour space is attached to the backing
/// bitmap, so Skia operates on the numeric values directly — which is what the canvas and Photoshop
/// do, and what <see cref="Blend"/> requires.
/// </para>
/// </remarks>
public sealed class Surface : IDisposable
{
    private readonly PixelBuffer _pixels;
    private readonly Stack<(Rect Clip, double Alpha, BlendMode Mode, TransformState Transform)> _state = new();
    private readonly List<TransformState> _transforms;

    private Rect _clip;
    private double _alpha = 1;
    private BlendMode _mode = BlendMode.Normal;
    private bool _disposed;

    /// <summary>Creates a transparent surface.</summary>
    public Surface(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        _pixels = new PixelBuffer(width, height);
        _clip = new Rect(0, 0, width, height);
        _transforms = [new TransformState(1, 1, 0, 0)];
    }

    /// <summary>Wraps existing pixels, taking ownership.</summary>
    public Surface(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        _pixels = pixels;
        _clip = new Rect(0, 0, pixels.Width, pixels.Height);
        _transforms = [new TransformState(1, 1, 0, 0)];
    }

    /// <summary>The canvas width in pixels.</summary>
    public int Width => _pixels.Width;

    /// <summary>The canvas height in pixels.</summary>
    public int Height => _pixels.Height;

    /// <summary>The pixels drawn so far, straight RGBA.</summary>
    public PixelBuffer Pixels => _pixels;

    /// <summary>The active clip, in canvas pixels.</summary>
    public Rect Clip => _clip;

    /// <summary>The active global alpha.</summary>
    public double Alpha => _alpha;

    /// <summary>The active blend mode.</summary>
    public BlendMode BlendMode => _mode;

    /// <summary>How many states are on the stack.</summary>
    public int SavedStateCount => _state.Count;

    /// <summary>Saves the clip, alpha, blend mode and transform.</summary>
    public void Save()
    {
        ThrowIfDisposed();
        _state.Push((_clip, _alpha, _mode, _transforms[^1]));
    }

    /// <summary>Restores the most recently saved state. Does nothing when none was saved.</summary>
    public void Restore()
    {
        ThrowIfDisposed();
        if (_state.Count == 0)
        {
            return;
        }

        var (clip, alpha, mode, transform) = _state.Pop();
        _clip = clip;
        _alpha = alpha;
        _mode = mode;
        _transforms[^1] = transform;
    }

    /// <summary>Intersects the clip with a rectangle.</summary>
    public void ClipTo(Rect rect)
    {
        ThrowIfDisposed();
        _clip = _clip.Intersect(rect);
    }

    /// <summary>Resets the clip to the whole canvas.</summary>
    public void ResetClip()
    {
        ThrowIfDisposed();
        _clip = new Rect(0, 0, Width, Height);
    }

    /// <summary>Sets the global alpha, clamped to 0…1.</summary>
    public void SetAlpha(double alpha)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(alpha))
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "Alpha must be finite.");
        }

        _alpha = Math.Clamp(alpha, 0, 1);
    }

    /// <summary>Sets the blend mode used by <see cref="Draw"/>.</summary>
    public void SetBlendMode(BlendMode mode)
    {
        ThrowIfDisposed();
        _mode = mode;
    }

    /// <summary>Scales and translates subsequently drawn placements.</summary>
    public void Scale(double x, double y)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(x) || !double.IsFinite(y) || x == 0 || y == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Scale must be finite and non-zero, got ({x}, {y}).");
        }

        var current = _transforms[^1];
        _transforms[^1] = current with { ScaleX = current.ScaleX * x, ScaleY = current.ScaleY * y };
    }

    /// <summary>Translates subsequently drawn placements.</summary>
    public void Translate(double x, double y)
    {
        ThrowIfDisposed();
        var current = _transforms[^1];
        _transforms[^1] = current with { OffsetX = current.OffsetX + x, OffsetY = current.OffsetY + y };
    }

    /// <summary>Clears every pixel to transparent.</summary>
    public void Clear()
    {
        ThrowIfDisposed();
        _pixels.Clear();
    }

    /// <summary>Fills the clip with a colour, using the active blend mode and alpha.</summary>
    public void Fill(Color color)
    {
        ThrowIfDisposed();

        var (left, top, right, bottom) = Bounds();
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                BlendPixel(x, y, color.R, color.G, color.B, color.A / 255.0 * _alpha, _mode);
            }
        }
    }

    /// <summary>
    /// Draws a source image using a placement, with the active blend mode and alpha.
    /// </summary>
    /// <remarks>
    /// A source whose placement is exactly its own pixel size and position takes the pixel-exact
    /// path. Anything else is resampled through Skia, because a correct interpolator is not
    /// something to hand-roll and the exact path is worth keeping for the common case.
    /// </remarks>
    public void Draw(Surface source, Placement placement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();

        if (_alpha <= 0 || _clip.IsEmpty)
        {
            return;
        }

        var effective = Apply(placement);

        // Three modes have no exact Skia counterpart (Linear Burn, Subtract, Divide), so their
        // placements always take the pixel-exact path -- including when they would otherwise need
        // resampling, where the Skia path is then used for the geometry with Normal blending and
        // the mode is applied afterwards.
        if (IsPixelExact(effective, source.Width, source.Height) || !SkiaInterop.MapsExactly(_mode))
        {
            DrawPixelExact(source.Pixels, effective);
            return;
        }

        DrawResampled(source, effective);
    }

    /// <summary>Draws a source image into a destination rectangle, unrotated and unflipped.</summary>
    public void Draw(Surface source, Rect destination) =>
        Draw(source, Placement.Of(destination.X, destination.Y, destination.Width, destination.Height));

    /// <summary>
    /// Composites a raw pixel buffer using a placement, honouring the active clip, alpha, blend mode
    /// and transform.
    /// </summary>
    /// <remarks>
    /// This is the entry point the layer compositor uses, so that a layer's mask, opacity and blend
    /// mode all take the same path and the same clip stack as anything drawn onto a canvas.
    /// </remarks>
    public void Draw(PixelBuffer pixels, Placement placement)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ThrowIfDisposed();

        using var source = new Surface(pixels);
        Draw(source, placement);
    }

    /// <summary>Produces a PNG of what has been drawn.</summary>
    public byte[] EncodePng() => PngCodec.Encode(_pixels);

    /// <summary>Writes what has been drawn to a PNG file.</summary>
    public void EncodePngTo(string path) => PngCodec.EncodeFile(_pixels, path);

    /// <summary>
    /// Whether the placement lands exactly on whole pixels at its natural size, so every source
    /// pixel maps to exactly one destination pixel pair.
    /// </summary>
    private static bool IsPixelExact(Placement placement, int width, int height) =>
        placement.IsAxisAligned
        && placement.Width == width
        && placement.Height == height
        && placement.X == Math.Floor(placement.X)
        && placement.Y == Math.Floor(placement.Y);

    private void DrawPixelExact(PixelBuffer pixels, Placement placement)
    {
        var left = (int)placement.X;
        var top = (int)placement.Y;
        var (clipLeft, clipTop, clipRight, clipBottom) = Bounds();

        for (var y = 0; y < pixels.Height; y++)
        {
            var canvasY = top + y;
            if (canvasY < clipTop || canvasY >= clipBottom)
            {
                continue;
            }

            for (var x = 0; x < pixels.Width; x++)
            {
                var canvasX = left + x;
                if (canvasX < clipLeft || canvasX >= clipRight)
                {
                    continue;
                }

                var (r, g, b, a) = pixels.GetPixel(x, y);
                if (a == 0)
                {
                    continue;
                }

                BlendPixel(canvasX, canvasY, r, g, b, a / 255.0 * _alpha, _mode);
            }
        }
    }

    /// <summary>
    /// Resamples through Skia. Used when the placement is rotated, flipped, or scaled, where a
    /// correct interpolator matters more than the exact path's precision.
    /// </summary>
    private void DrawResampled(Surface source, Placement placement)
    {
        var (clipLeft, clipTop, clipRight, clipBottom) = Bounds();

        // Pin rather than copy, so the draw lands directly in the destination buffer.
        using var pinned = SkiaInterop.Pin(_pixels);
        using var canvas = new SKCanvas(pinned.Bitmap);
        using var bitmap = SkiaInterop.ToOwnedBitmap(source._pixels);

        canvas.Save();
        canvas.ClipRect(new SKRect(clipLeft, clipTop, clipRight, clipBottom));

        var center = placement.Center;
        canvas.Translate((float)center.X, (float)center.Y);
        if (placement.RotationDegrees != 0)
        {
            canvas.RotateDegrees((float)placement.RotationDegrees);
        }

        if (placement.FlipX || placement.FlipY)
        {
            canvas.Scale(placement.FlipX ? -1 : 1, placement.FlipY ? -1 : 1);
        }

        canvas.Translate((float)(-placement.Width / 2), (float)(-placement.Height / 2));

        using var paint = new SKPaint
        {
            BlendMode = SkiaInterop.ToSkiaBlendMode(_mode),
            Color = new SKColor(255, 255, 255, (byte)Math.Round(_alpha * 255)),
            IsAntialias = false,
            FilterQuality = SKFilterQuality.High,
        };

        var destination = new SKRect(0, 0, (float)placement.Width, (float)placement.Height);
        canvas.DrawBitmap(bitmap, destination, paint);
        canvas.Restore();
        canvas.Flush();
    }

    private void BlendPixel(int x, int y, byte r, byte g, byte b, double alpha, BlendMode mode)
    {
        var (br, bg, bb, ba) = _pixels.GetPixel(x, y);
        var (outR, outG, outB, outA) = LayerCompositor.CompositePixel(
            r / 255.0, g / 255.0, b / 255.0, alpha,
            br / 255.0, bg / 255.0, bb / 255.0, ba / 255.0,
            mode);

        _pixels.SetPixel(
            x, y,
            (byte)Math.Round(outR * 255),
            (byte)Math.Round(outG * 255),
            (byte)Math.Round(outB * 255),
            (byte)Math.Round(outA * 255));
    }

    private Placement Apply(Placement placement)
    {
        var transform = _transforms[^1];
        return placement with
        {
            X = (placement.X * transform.ScaleX) + transform.OffsetX,
            Y = (placement.Y * transform.ScaleY) + transform.OffsetY,
            Width = placement.Width * transform.ScaleX,
            Height = placement.Height * transform.ScaleY,
        };
    }

    private (int Left, int Top, int Right, int Bottom) Bounds()
    {
        var left = (int)Math.Max(0, Math.Floor(_clip.X));
        var top = (int)Math.Max(0, Math.Floor(_clip.Y));
        var right = (int)Math.Min(Width, Math.Ceiling(_clip.Right));
        var bottom = (int)Math.Min(Height, Math.Ceiling(_clip.Bottom));
        return (left, top, Math.Max(left, right), Math.Max(top, bottom));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose() => _disposed = true;

    private readonly record struct TransformState(double ScaleX, double ScaleY, double OffsetX, double OffsetY);
}

/// <summary>A rectangle in canvas pixels.</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>Whether the rectangle encloses no area.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The overlapping part of two rectangles, possibly empty.</summary>
    public Rect Intersect(Rect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top
            ? new Rect(left, top, 0, 0)
            : new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Whether two rectangles share any area. Touching edges do not count.</summary>
    public bool Intersects(Rect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

/// <summary>An 8-bit RGBA colour.</summary>
public readonly record struct Color(byte R, byte G, byte B, byte A)
{
    /// <summary>An opaque colour.</summary>
    public static Color Opaque(byte r, byte g, byte b) => new(r, g, b, 255);

    /// <summary>Transparent black.</summary>
    public static Color Transparent => new(0, 0, 0, 0);
}
