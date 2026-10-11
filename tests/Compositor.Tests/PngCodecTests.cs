using Compositor.Codecs;
using Compositor.Core;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// The PNG codec, and the channel/alpha conversions it has to get right.
/// </summary>
/// <remarks>
/// Skia decodes to <c>Bgra8888</c> with <c>Premultiplied</c> alpha; this port's buffers are
/// <c>Rgba8888</c> straight. Both mistakes are invisible on a fully opaque fixture — red and blue
/// simply swap, and a premultiplied pixel only differs where alpha is below 255 — so the fixtures
/// here deliberately include fully transparent, alpha-1, and half-transparent pixels.
/// </remarks>
public sealed class PngCodecTests
{
    private static PixelBuffer Fixture()
    {
        var buffer = new PixelBuffer(4, 2);
        buffer.SetPixel(0, 0, 255, 0, 0, 255);     // pure red: catches a BGR swap
        buffer.SetPixel(1, 0, 0, 255, 0, 255);     // pure green
        buffer.SetPixel(2, 0, 0, 0, 255, 255);     // pure blue: catches a BGR swap
        buffer.SetPixel(3, 0, 0, 0, 0, 0);         // fully transparent
        buffer.SetPixel(0, 1, 128, 64, 32, 128);   // half transparent: catches premultiplication
        buffer.SetPixel(1, 1, 255, 255, 255, 1);   // alpha 1: catches premultiplication badly
        buffer.SetPixel(2, 1, 17, 200, 99, 200);
        buffer.SetPixel(3, 1, 255, 255, 255, 255);
        return buffer;
    }

    [Fact]
    public void RoundTrip_PreservesEveryPixelExactly()
    {
        var original = Fixture();
        var decoded = PngCodec.Decode(PngCodec.Encode(original));

        Assert.Equal(original.Width, decoded.Width);
        Assert.Equal(original.Height, decoded.Height);

        for (var y = 0; y < original.Height; y++)
        {
            for (var x = 0; x < original.Width; x++)
            {
                Assert.Equal(original.GetPixel(x, y), decoded.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void Encode_ProducesAPng()
    {
        var bytes = PngCodec.Encode(Fixture());

        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    [Fact]
    public void RedAndBlue_AreNotSwapped()
    {
        // Stated separately from the round trip so a BGR regression names itself.
        var decoded = PngCodec.Decode(PngCodec.Encode(Fixture()));

        Assert.Equal((byte)255, decoded.GetPixel(0, 0).R);
        Assert.Equal((byte)0, decoded.GetPixel(0, 0).B);
        Assert.Equal((byte)0, decoded.GetPixel(2, 0).R);
        Assert.Equal((byte)255, decoded.GetPixel(2, 0).B);
    }

    [Fact]
    public void TransparentPixels_StayFullyTransparent()
    {
        var decoded = PngCodec.Decode(PngCodec.Encode(Fixture()));

        Assert.Equal((byte)0, decoded.GetPixel(3, 0).A);
    }

    [Fact]
    public void HalfTransparentPixels_AreNotPremultiplied()
    {
        var decoded = PngCodec.Decode(PngCodec.Encode(Fixture()));

        var pixel = decoded.GetPixel(0, 1);
        Assert.Equal(128, pixel.R);
        Assert.Equal(64, pixel.G);
        Assert.Equal(32, pixel.B);
        Assert.Equal(128, pixel.A);
    }

    [Fact]
    public void Inspect_ReadsEightBitDepth()
    {
        var (width, height, bits) = PngCodec.Inspect(PngCodec.Encode(Fixture()));

        Assert.Equal(4, width);
        Assert.Equal(2, height);
        Assert.Equal(8, bits);
    }

    [Fact]
    public void Decode_RejectsGarbage()
    {
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode([1, 2, 3, 4, 5]));
    }
}
