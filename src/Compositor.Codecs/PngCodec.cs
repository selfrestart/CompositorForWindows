using SkiaSharp;

namespace Compositor.Codecs;

/// <summary>PNG (and general raster) decoding and encoding for project assets.</summary>
/// <remarks>
/// <b>Every conversion here is deliberate.</b> Skia decodes to <c>Bgra8888</c> with
/// <c>Premultiplied</c> alpha by default; this port's buffers are <c>Rgba8888</c> and straight
/// (non-premultiplied). Getting that wrong swaps the red and blue channels and corrupts every
/// partially transparent pixel, and the mistake is invisible on a fully opaque fixture. It was
/// confirmed experimentally before this file was written.
///
/// A <c>.comp</c>'s embedded assets are PNGs of layer pixels with transparency preserved
/// (docs/project-format.md:7), and the loader rejects anything deeper than 8 bits per channel
/// (ProjectStore.swift:179).
/// </remarks>
public static class PngCodec
{
    /// <summary>Decodes image bytes to a straight RGBA buffer.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a decodable image.</exception>
    public static PixelBuffer Decode(ReadOnlySpan<byte> bytes)
    {
        using var data = SKData.CreateCopy(bytes.ToArray());
        using var decoded = SKBitmap.Decode(data)
            ?? throw new InvalidDataException("The image could not be decoded.");

        if (decoded.Width < 1 || decoded.Height < 1)
        {
            throw new InvalidDataException($"The image reports {decoded.Width}x{decoded.Height}.");
        }

        return FromSkia(decoded);
    }

    /// <summary>Decodes a file to a straight RGBA buffer.</summary>
    public static PixelBuffer DecodeFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Decode(File.ReadAllBytes(path));
    }

    /// <summary>Encodes a buffer as PNG bytes.</summary>
    public static byte[] Encode(PixelBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        using var bitmap = ToSkia(buffer);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>Encodes a buffer to a file.</summary>
    public static void EncodeFile(PixelBuffer buffer, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, Encode(buffer));
    }

    /// <summary>
    /// Reads just the size and per-channel depth of PNG bytes, without decoding pixels.
    /// Used by the project validator, which rejects assets deeper than 8 bits per channel.
    /// </summary>
    public static (int Width, int Height, int BitsPerChannel) Inspect(ReadOnlySpan<byte> bytes)
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(bytes.ToArray()))
            ?? throw new InvalidDataException("The image could not be inspected.");

        var info = codec.Info;
        var bits = info.ColorType switch
        {
            SKColorType.Alpha8 or SKColorType.Gray8 => 8,
            SKColorType.Rgb565 => 5,
            SKColorType.RgbaF16 or SKColorType.RgbaF16Clamped or SKColorType.RgbaF32 or SKColorType.Rgba1010102 => 16,
            _ => 8,
        };

        return (info.Width, info.Height, bits);
    }

    /// <summary>
    /// Decodes a grayscale PNG — a layer mask — to single-channel coverage.
    /// </summary>
    /// <remarks>
    /// Masks are stored as monochrome images with no alpha (LayerMask.swift:23-26). Skia's decoder
    /// may still hand back a wider format for one, so the first channel of a normalized RGBA read is
    /// what carries the coverage; when the file really is Gray8 the read is one byte per pixel.
    /// </remarks>
    public static MaskBuffer DecodeMask(ReadOnlySpan<byte> bytes)
    {
        using var data = SKData.CreateCopy(bytes.ToArray());
        using var decoded = SKBitmap.Decode(data)
            ?? throw new InvalidDataException("The mask could not be decoded.");

        if (decoded.Width < 1 || decoded.Height < 1)
        {
            throw new InvalidDataException($"The mask reports {decoded.Width}x{decoded.Height}.");
        }

        var width = decoded.Width;
        var height = decoded.Height;
        var source = decoded.ColorType;

        if (source == SKColorType.Gray8)
        {
            var gray = new byte[width * height];
            System.Runtime.InteropServices.Marshal.Copy(decoded.GetPixels(), gray, 0, gray.Length);
            return new MaskBuffer(width, height, gray);
        }

        using var normalized = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(normalized))
        {
            canvas.DrawBitmap(decoded, 0, 0);
            canvas.Flush();
        }

        var pixels = new byte[width * height];
        System.Runtime.InteropServices.Marshal.Copy(normalized.GetPixels(), pixels, 0, pixels.Length);
        return new MaskBuffer(width, height, pixels);
    }

    /// <summary>
    /// Encodes coverage as a true grayscale PNG.
    /// </summary>
    /// <remarks>
    /// <b>The colour type is the point.</b> Encoding through an RGBA bitmap produces a colour-type-6
    /// PNG that the macOS loader rejects, because a mask must be monochrome with no alpha
    /// (LayerMask.swift:24-25). Verified: <c>SKColorType.Gray8</c> with <c>SKAlphaType.Opaque</c>
    /// writes colour type 0.
    /// </remarks>
    public static byte[] Encode(MaskBuffer mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        var info = new SKImageInfo(mask.Width, mask.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(mask.Pixels, 0, bitmap.GetPixels(), mask.Pixels.Length);

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>Encodes coverage to a PNG file.</summary>
    public static void EncodeFile(MaskBuffer mask, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, Encode(mask));
    }

    /// <summary>Converts a Skia bitmap to a straight RGBA buffer.</summary>
    private static PixelBuffer FromSkia(SKBitmap decoded)
    {
        var width = decoded.Width;
        var height = decoded.Height;

        // Ask for exactly the layout this port uses. Skia's decoders hand back Bgra8888/Premul,
        // and this is where that is corrected -- a straight copy into a new bitmap would carry the
        // wrong channel order and premultiplied values straight through.
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var normalized = new SKBitmap(info);
        using (var canvas = new SKCanvas(normalized))
        {
            canvas.DrawBitmap(decoded, 0, 0);
            canvas.Flush();
        }

        var pixels = new byte[width * height * PixelBuffer.BytesPerPixel];
        System.Runtime.InteropServices.Marshal.Copy(normalized.GetPixels(), pixels, 0, pixels.Length);
        return new PixelBuffer(width, height, pixels);
    }

    /// <summary>Converts a straight RGBA buffer to a Skia bitmap for encoding.</summary>
    private static SKBitmap ToSkia(PixelBuffer buffer)
    {
        var info = new SKImageInfo(
            buffer.Width,
            buffer.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(buffer.Pixels, 0, bitmap.GetPixels(), buffer.Pixels.Length);
        return bitmap;
    }
}
