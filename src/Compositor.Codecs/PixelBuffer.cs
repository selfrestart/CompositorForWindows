namespace Compositor.Codecs;

/// <summary>
/// A straight (non-premultiplied) 8-bit RGBA pixel buffer, top row first.
/// </summary>
/// <remarks>
/// This is the one pixel container every layer of the port shares, and its invariants are load
/// bearing:
///
/// <list type="bullet">
/// <item><b>RGBA order, not BGRA.</b> Skia decodes PNGs to <c>Bgra8888</c> by default; every
/// decode in this assembly converts.</item>
/// <item><b>Straight alpha, not premultiplied.</b> Skia also defaults to <c>Premul</c>. A
/// premultiplied buffer fed to the blend maths produces wrong colours wherever alpha is below 255,
/// and the error is invisible on fully opaque test images.</item>
/// <item><b>Row-major, tightly packed, four bytes per pixel.</b> The native kernels take a stride
/// separate from the width, so the stride is carried explicitly even though it always equals
/// <c>Width * 4</c> here.</item>
/// </list>
/// </remarks>
public sealed class PixelBuffer
{
    /// <summary>Bytes per pixel.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>Allocates a transparent buffer.</summary>
    public PixelBuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Width = width;
        Height = height;
        Stride = width * BytesPerPixel;
        Pixels = new byte[Stride * height];
    }

    /// <summary>Wraps an existing tightly packed buffer, taking ownership of it.</summary>
    public PixelBuffer(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(pixels);

        Width = width;
        Height = height;
        Stride = width * BytesPerPixel;
        if (pixels.Length != Stride * height)
        {
            throw new ArgumentException(
                $"Expected {Stride * height} bytes for {width}x{height}, got {pixels.Length}.",
                nameof(pixels));
        }

        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row. Always <c>Width * 4</c> here.</summary>
    public int Stride { get; }

    /// <summary>The raw bytes, RGBA straight.</summary>
    public byte[] Pixels { get; }

    public Span<byte> Span => Pixels;

    /// <summary>A buffer of the same size, cleared to transparent.</summary>
    public PixelBuffer CreateCompatible() => new(Width, Height);

    /// <summary>Reads one pixel as (r, g, b, a). Out of range reads yield transparent black.</summary>
    public (byte R, byte G, byte B, byte A) GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return (0, 0, 0, 0);
        }

        var offset = (y * Stride) + (x * BytesPerPixel);
        return (Pixels[offset], Pixels[offset + 1], Pixels[offset + 2], Pixels[offset + 3]);
    }

    /// <summary>Writes one pixel. Out of range writes are ignored.</summary>
    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return;
        }

        var offset = (y * Stride) + (x * BytesPerPixel);
        Pixels[offset] = r;
        Pixels[offset + 1] = g;
        Pixels[offset + 2] = b;
        Pixels[offset + 3] = a;
    }

    /// <summary>Fills every pixel with the same value.</summary>
    public void Clear(byte r = 0, byte g = 0, byte b = 0, byte a = 0)
    {
        for (var offset = 0; offset < Pixels.Length; offset += BytesPerPixel)
        {
            Pixels[offset] = r;
            Pixels[offset + 1] = g;
            Pixels[offset + 2] = b;
            Pixels[offset + 3] = a;
        }
    }
}
