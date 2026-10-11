namespace Compositor.Codecs;

/// <summary>
/// Plain pixel copies between buffers, with an offset, clipped to the destination.
/// </summary>
/// <remarks>
/// This is the buffer-to-buffer move that a drawing context gives for free when a whole image lands
/// at an integer position. It exists separately from <see cref="Surface"/> because the downsampler
/// needs it while building intermediate copies, before there is anything to draw with.
///
/// The offset may be negative, which crops the source's top-left; that is how a padded intermediate
/// is trimmed back to the size its caller expects.
/// </remarks>
internal static class Canvas
{
    /// <summary>Copies <paramref name="source"/> into <paramref name="destination"/> at an offset.</summary>
    public static void Draw(PixelBuffer destination, PixelBuffer source, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        var startX = Math.Max(0, -x);
        var startY = Math.Max(0, -y);
        var endX = Math.Min(source.Width, destination.Width - x);
        var endY = Math.Min(source.Height, destination.Height - y);

        if (startX >= endX || startY >= endY)
        {
            return;
        }

        var width = endX - startX;
        for (var row = startY; row < endY; row++)
        {
            var sourceOffset = (row * source.Stride) + (startX * PixelBuffer.BytesPerPixel);
            var destinationOffset = ((row + y) * destination.Stride) + ((startX + x) * PixelBuffer.BytesPerPixel);
            Array.Copy(
                source.Pixels,
                sourceOffset,
                destination.Pixels,
                destinationOffset,
                width * PixelBuffer.BytesPerPixel);
        }
    }
}
