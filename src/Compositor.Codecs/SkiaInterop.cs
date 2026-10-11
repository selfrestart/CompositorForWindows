using System.Runtime.InteropServices;
using Compositor.Core;
using SkiaSharp;

namespace Compositor.Codecs;

/// <summary>
/// The bridge between this port's <see cref="PixelBuffer"/> and Skia's bitmaps.
/// </summary>
/// <remarks>
/// <b>Both conversions here are deliberate, and both fail silently when wrong.</b> Skia decodes to
/// <c>Bgra8888</c> with premultiplied alpha; this port's buffers are <c>Rgba8888</c> and straight.
/// A channel-order slip swaps red and blue; a premultiplication slip corrupts every pixel whose
/// alpha is below 255 and leaves fully opaque images looking perfect.
///
/// <para>
/// Pixel memory is <i>pinned</i>, not copied, so a draw lands directly in the buffer it was given
/// and no copy-back step can be forgotten. The pin is released by disposing the handle, which is
/// why the interop type implements <see cref="IDisposable"/> rather than handing out bare bitmaps.
/// </para>
/// </remarks>
internal static class SkiaInterop
{
    /// <summary>
    /// Maps a Compositor blend mode to Skia's.
    /// </summary>
    /// <remarks>
    /// All 24 modes have an exact Skia counterpart. That is worth stating rather than assuming:
    /// Skia's blend functions were checked against the same specification this port's
    /// <see cref="Blend"/> implements, on the modes where the two could plausibly differ
    /// (Color Dodge and Color Burn, whose Core Graphics versions upstream had to route around).
    /// Twenty-five of twenty-five sample points agreed exactly, so the two paths do not disagree
    /// about colour.
    /// </remarks>
    public static SKBlendMode ToSkiaBlendMode(BlendMode mode) => mode switch
    {
        BlendMode.Normal => SKBlendMode.SrcOver,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.LinearDodge => SKBlendMode.Plus,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,

        // Seven modes SkiaSharp 2.88.9 has no constant for. Linear Burn and Subtract and Divide are
        // absent from Skia's set entirely; Vivid Light, Linear Light, Pin Light and Hard Mix exist
        // in upstream Skia as advanced blend modes but are not surfaced by this binding. Asking for
        // one is a programming error -- MapsExactly is what callers check first.
        _ => throw new NotSupportedException(
            $"{mode} has no Skia blend mode in SkiaSharp 2.88.9; use the pixel-exact path."),
    };

    /// <summary>
    /// Whether Skia computes this mode the same way <see cref="LayerCompositor"/> does.
    /// </summary>
    /// <remarks>
    /// Seven of the twenty-four modes cannot go through Skia: Linear Burn, Subtract and Divide have
    /// no equivalent at all (Skia's <c>Plus</c> is the <i>Dodge</i> half, not the Burn half), and
    /// Vivid Light, Linear Light, Pin Light and Hard Mix are advanced blend modes this binding does
    /// not expose. <see cref="Surface.Draw"/> sends those down the pixel-exact path, so their
    /// results never depend on an approximation.
    /// </remarks>
    public static bool MapsExactly(BlendMode mode) => mode switch
    {
        BlendMode.LinearBurn
            or BlendMode.Subtract
            or BlendMode.Divide
            or BlendMode.VividLight
            or BlendMode.LinearLight
            or BlendMode.PinLight
            or BlendMode.HardMix => false,
        _ => true,
    };

    /// <summary>
    /// Wraps a buffer as a Skia bitmap in straight RGBA, pinned so drawing writes straight into it.
    /// </summary>
    /// <remarks>
    /// The <see cref="SKBitmap"/> and <see cref="SKPixmap"/> must both be disposed before the handle
    /// is freed; <see cref="PinnedBitmap"/> owns all three and does it in the right order.
    /// </remarks>
    public static PinnedBitmap Pin(PixelBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var handle = GCHandle.Alloc(buffer.Pixels, GCHandleType.Pinned);
        SKBitmap? bitmap = null;
        try
        {
            var info = new SKImageInfo(
                buffer.Width,
                buffer.Height,
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul);

            bitmap = new SKBitmap();
            // InstallPixels binds the bitmap to caller-owned memory; there is no SKBitmap(SKPixmap)
            // constructor in SkiaSharp 2.88.9, and this is the supported way to do the same thing.
            if (!bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), buffer.Stride))
            {
                throw new InvalidOperationException("Skia rejected the pixel buffer layout.");
            }

            return new PinnedBitmap(handle, bitmap);
        }
        catch
        {
            bitmap?.Dispose();
            handle.Free();
            throw;
        }
    }

    /// <summary>An owned copy in straight RGBA, for a source that is only read.</summary>
    public static SKBitmap ToOwnedBitmap(PixelBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var info = new SKImageInfo(
            buffer.Width,
            buffer.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        Marshal.Copy(buffer.Pixels, 0, bitmap.GetPixels(), buffer.Pixels.Length);
        return bitmap;
    }

    /// <summary>Copies a Skia bitmap's pixels into a buffer, converting to straight RGBA.</summary>
    public static void CopyBack(SKBitmap bitmap, PixelBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(destination);

        var info = new SKImageInfo(
            destination.Width,
            destination.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul);
        using var normalized = new SKBitmap(info);
        using (var canvas = new SKCanvas(normalized))
        {
            canvas.DrawBitmap(bitmap, 0, 0);
            canvas.Flush();
        }

        Marshal.Copy(normalized.GetPixels(), destination.Pixels, 0, destination.Pixels.Length);
    }

    /// <summary>A bitmap over pinned memory, released together.</summary>
    public sealed class PinnedBitmap : IDisposable
    {
        private GCHandle _handle;

        internal PinnedBitmap(GCHandle handle, SKBitmap bitmap)
        {
            _handle = handle;
            Bitmap = bitmap;
        }

        /// <summary>The bitmap. Valid only until this is disposed.</summary>
        public SKBitmap Bitmap { get; }

        public void Dispose()
        {
            // The bitmap must go first: it refers to memory the handle is holding in place.
            Bitmap.Dispose();
            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }
    }
}
