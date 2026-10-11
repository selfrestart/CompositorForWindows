namespace Compositor.Codecs;

/// <summary>
/// An 8-bit grayscale coverage map: white reveals, black hides, intermediate grey is partial.
/// </summary>
/// <remarks>
/// <b>Masks are not images, and the format says so.</b> <c>LayerMask.isValid</c> requires a
/// monochrome image with <c>bitsPerComponent == 8</c> and <c>alphaInfo == .none</c>
/// (LayerMask.swift:23-26) — a single channel, no alpha. Writing them as RGBA would produce a PNG
/// the macOS side rejects, so they get their own buffer rather than reusing
/// <see cref="PixelBuffer"/>.
///
/// <para>
/// The size of this type is not an oversight either: a mask is one byte per pixel, so a 30,000-pixel
/// square mask is 900 MB as RGBA and 225 MB as it is stored here.
/// </para>
/// </remarks>
public sealed class MaskBuffer
{
    /// <summary>Allocates a mask, black by default (hiding everything).</summary>
    public MaskBuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Width = width;
        Height = height;
        Stride = width;
        Pixels = new byte[Stride * height];
    }

    /// <summary>Wraps existing coverage bytes, taking ownership.</summary>
    public MaskBuffer(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(pixels);

        Width = width;
        Height = height;
        Stride = width;
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

    /// <summary>Bytes per row. Always <c>Width</c> here.</summary>
    public int Stride { get; }

    /// <summary>One byte per pixel; 0 hides, 255 reveals.</summary>
    public byte[] Pixels { get; }

    public Span<byte> Span => Pixels;

    /// <summary>Coverage at a pixel in 0…1. Out of range reads report 0 (fully hiding).</summary>
    public double Coverage(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height
            ? Pixels[(y * Stride) + x] / 255.0
            : 0;

    /// <summary>Sets coverage at a pixel from a 0…1 value.</summary>
    public void SetCoverage(int x, int y, double coverage)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return;
        }

        Pixels[(y * Stride) + x] = (byte)Math.Round(Math.Clamp(coverage, 0, 1) * 255);
    }

    /// <summary>Fills the whole mask with one coverage value.</summary>
    public void Clear(byte coverage = 0) => Pixels.AsSpan().Fill(coverage);

    /// <summary>
    /// The 1×1 mask that reveals or hides everything.
    /// </summary>
    /// <remarks>
    /// The format explicitly allows this: "A uniform 1×1 mask is valid and avoids allocating
    /// full-resolution pixels before painting" (docs/project-format.md:21).
    /// </remarks>
    public static MaskBuffer Solid(bool revealing) => new(1, 1, [revealing ? (byte)255 : (byte)0]);
}
