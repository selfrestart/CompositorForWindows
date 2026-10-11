namespace Compositor.Codecs;

/// <summary>
/// The six effects a layer can carry, and the order they are drawn in.
/// </summary>
/// <remarks>
/// The order is not a detail. Upstream spells it out at LayerEffects.swift:469-503, and getting it
/// wrong changes what is visible: an outside stroke sits <i>behind</i> the layer's own pixels while
/// an inside one is drawn <i>over</i> them, and an inner shadow drawn before the pixels instead of
/// after would be covered by them.
///
/// <list type="number">
/// <item>Drop shadow — behind everything, including the glows.</item>
/// <item>Outer glow — behind the layer and the stroke.</item>
/// <item>Stroke — outside only; an inside stroke is drawn over the pixels at step 5.</item>
/// <item>The layer's own pixels.</item>
/// <item>Color overlay — recolours the pixels, over the stroke and the shadow, but only where the
/// pixels already are.</item>
/// <item>Inner glow, then inner shadow — over the pixels, keeping the pixels' own alpha.</item>
/// </list>
/// </remarks>
public enum LayerEffectKind
{
    /// <summary>A line around what the layer shows, outside its edge or inside it.</summary>
    Stroke,

    /// <summary>The layer's shape repeated behind it, offset and softened.</summary>
    DropShadow,

    /// <summary>A flat colour over everything the layer shows.</summary>
    ColorOverlay,

    /// <summary>A shadow cast inside the layer's own edges, as though it were cut out of what is behind it.</summary>
    InnerShadow,

    /// <summary>A soft glow drawn around the outside of what the layer shows.</summary>
    OuterGlow,

    /// <summary>A soft glow drawn inside the layer's own edges.</summary>
    InnerGlow,
}

/// <summary>One stroke around a layer (LayerEffects.swift:6-22).</summary>
public sealed record StrokeEffect
{
    /// <summary>Largest stroke width the format allows.</summary>
    public const double MaxSize = 500;

    /// <summary>Missing in older projects means visible.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Width in layer pixels, 0…500.</summary>
    public double Size { get; init; } = 4;

    public double Red { get; init; }

    public double Green { get; init; }

    public double Blue { get; init; }

    /// <summary>0…1.</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>Whether the stroke sits inside the layer's edge rather than outside it.</summary>
    public bool Inside { get; init; }

    /// <summary>Whether the effect is drawn. Missing means visible.</summary>
    public bool IsEnabled => Enabled ?? true;

    /// <summary>Whether the settings are inside the ranges the format accepts.</summary>
    public bool IsValid =>
        double.IsFinite(Size) && Size is >= 0 and <= MaxSize
        && double.IsFinite(Opacity) && Opacity is >= 0 and <= 1
        && InUnitRange(Red) && InUnitRange(Green) && InUnitRange(Blue);

    internal static bool InUnitRange(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}

/// <summary>A drop shadow or an inner shadow (LayerEffects.swift:25-50, 67-90).</summary>
public sealed record ShadowEffect
{
    /// <summary>Largest blur radius the format allows.</summary>
    public const double MaxBlur = 500;

    /// <summary>Largest distance the format allows.</summary>
    public const double MaxDistance = 5000;

    /// <summary>Missing in older projects means visible.</summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// Where the light comes from, in degrees counterclockwise from the right, as Photoshop's dial
    /// is: 90 is from straight above, which drops the shadow straight down. −360…360.
    /// </summary>
    public double Angle { get; init; } = 90;

    /// <summary>How far the shadow is cast, in layer pixels, 0…5000.</summary>
    public double Distance { get; init; } = 20;

    /// <summary>Softening applied to the shadow's edge, 0…500.</summary>
    public double Blur { get; init; } = 20;

    public double Red { get; init; }

    public double Green { get; init; }

    public double Blue { get; init; }

    /// <summary>0…1.</summary>
    public double Opacity { get; init; } = 0.5;

    /// <summary>Whether the effect is drawn. Missing means visible.</summary>
    public bool IsEnabled => Enabled ?? true;

    /// <summary>
    /// Where the shadow sits, in layer pixels (y grows downward, as the layer's own pixels do).
    /// </summary>
    /// <remarks>
    /// The shadow falls <i>away</i> from the light: at 90° the light is overhead, so the offset is
    /// zero across and positive down, which is what a shadow under an object looks like.
    /// </remarks>
    public (double X, double Y) Offset
    {
        get
        {
            var radians = Angle * Math.PI / 180;
            return (-Math.Cos(radians) * Distance, Math.Sin(radians) * Distance);
        }
    }

    /// <summary>Whether the settings are inside the ranges the format accepts.</summary>
    public bool IsValid =>
        double.IsFinite(Angle) && Angle is >= -360 and <= 360
        && double.IsFinite(Distance) && Distance is >= 0 and <= MaxDistance
        && double.IsFinite(Blur) && Blur is >= 0 and <= MaxBlur
        && double.IsFinite(Opacity) && Opacity is >= 0 and <= 1
        && StrokeEffect.InUnitRange(Red)
        && StrokeEffect.InUnitRange(Green)
        && StrokeEffect.InUnitRange(Blue);
}

/// <summary>A flat colour over everything the layer shows (LayerEffects.swift:53-64).</summary>
public sealed record ColorOverlayEffect
{
    public bool? Enabled { get; init; }

    public double Red { get; init; }

    public double Green { get; init; }

    public double Blue { get; init; }

    /// <summary>0…1.</summary>
    public double Opacity { get; init; } = 1;

    public bool IsEnabled => Enabled ?? true;

    public bool IsValid =>
        double.IsFinite(Opacity) && Opacity is >= 0 and <= 1
        && StrokeEffect.InUnitRange(Red)
        && StrokeEffect.InUnitRange(Green)
        && StrokeEffect.InUnitRange(Blue);
}

/// <summary>A glow around, or inside, what the layer shows (LayerEffects.swift:92-125).</summary>
public sealed record GlowEffect
{
    /// <summary>Largest glow size the format allows.</summary>
    public const double MaxSize = 500;

    public bool? Enabled { get; init; }

    /// <summary>How far the glow reaches, in layer pixels, 0…500.</summary>
    public double Size { get; init; } = 20;

    public double Red { get; init; } = 1;

    public double Green { get; init; } = 1;

    public double Blue { get; init; } = 1;

    /// <summary>0…1.</summary>
    public double Opacity { get; init; } = 0.75;

    public bool IsEnabled => Enabled ?? true;

    public bool IsValid =>
        double.IsFinite(Size) && Size is >= 0 and <= MaxSize
        && double.IsFinite(Opacity) && Opacity is >= 0 and <= 1
        && StrokeEffect.InUnitRange(Red)
        && StrokeEffect.InUnitRange(Green)
        && StrokeEffect.InUnitRange(Blue);
}

/// <summary>Everything a layer carries (LayerEffects.swift:127-138).</summary>
public sealed record LayerEffects
{
    public StrokeEffect? Stroke { get; init; }

    public ShadowEffect? Shadow { get; init; }

    public ColorOverlayEffect? ColorOverlay { get; init; }

    public ShadowEffect? InnerShadow { get; init; }

    public GlowEffect? OuterGlow { get; init; }

    public GlowEffect? InnerGlow { get; init; }

    /// <summary>Whether no effect is configured at all.</summary>
    public bool IsEmpty =>
        Stroke is null && Shadow is null && ColorOverlay is null
        && InnerShadow is null && OuterGlow is null && InnerGlow is null;

    /// <summary>Whether every configured effect is inside its accepted ranges.</summary>
    /// <remarks>
    /// An out-of-range effect is ignored rather than rejected, which is what the macOS side does
    /// (EditorCanvas.swift:1216, EffectsPreviewCache.swift:83). Being stricter here would refuse to
    /// open a project macOS opens.
    /// </remarks>
    public bool IsValid =>
        (Stroke?.IsValid ?? true)
        && (Shadow?.IsValid ?? true)
        && (ColorOverlay?.IsValid ?? true)
        && (InnerShadow?.IsValid ?? true)
        && (OuterGlow?.IsValid ?? true)
        && (InnerGlow?.IsValid ?? true);

    /// <summary>The effects that will actually be drawn, in the order they are layered.</summary>
    public IReadOnlyList<LayerEffectKind> VisibleKinds()
    {
        var kinds = new List<LayerEffectKind>(6);
        if (Shadow is { IsEnabled: true, Opacity: > 0 }) kinds.Add(LayerEffectKind.DropShadow);
        if (OuterGlow is { IsEnabled: true, Opacity: > 0, Size: > 0 }) kinds.Add(LayerEffectKind.OuterGlow);
        if (Stroke is { IsEnabled: true, Opacity: > 0, Size: > 0 } stroke && !stroke.Inside) kinds.Add(LayerEffectKind.Stroke);
        if (ColorOverlay is { IsEnabled: true, Opacity: > 0 }) kinds.Add(LayerEffectKind.ColorOverlay);
        if (InnerGlow is { IsEnabled: true, Opacity: > 0, Size: > 0 }) kinds.Add(LayerEffectKind.InnerGlow);
        if (InnerShadow is { IsEnabled: true, Opacity: > 0 }) kinds.Add(LayerEffectKind.InnerShadow);
        if (Stroke is { IsEnabled: true, Opacity: > 0, Size: > 0, Inside: true }) kinds.Add(LayerEffectKind.Stroke);
        return kinds;
    }
}

/// <summary>
/// A layer's alpha as a single-channel coverage map, plus the morphology and blurring the effects
/// need.
/// </summary>
/// <remarks>
/// Every effect is built from the layer's coverage: a shadow is that coverage offset and blurred, a
/// stroke is its outer or inner band, a glow is one of those softened. Doing them all from one
/// coverage pass keeps a single definition of "where the layer is".
/// </remarks>
internal static class EffectCoverage
{
    /// <summary>The layer's alpha as coverage in 0…1.</summary>
    public static MaskBuffer FromAlpha(PixelBuffer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);

        var coverage = new MaskBuffer(layer.Width, layer.Height);
        for (var y = 0; y < layer.Height; y++)
        {
            for (var x = 0; x < layer.Width; x++)
            {
                coverage.Pixels[(y * coverage.Stride) + x] = layer.GetPixel(x, y).A;
            }
        }

        return coverage;
    }

    /// <summary>Covers everything outside the given coverage by the given distance.</summary>
    /// <remarks>
    /// Grows outward one pixel at a time, so the result contains the original: a stroke or glow that
    /// did not contain the layer would leave a gap between them.
    /// </remarks>
    public static MaskBuffer Grow(MaskBuffer source, int distance)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (distance <= 0)
        {
            return Copy(source);
        }

        var current = source;
        for (var step = 0; step < distance; step++)
        {
            current = DilateOnce(current);
        }

        return current;
    }

    /// <summary>Shrinks the coverage by the given distance, so it covers only what is inside.</summary>
    public static MaskBuffer Shrink(MaskBuffer source, int distance)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (distance <= 0)
        {
            return Copy(source);
        }

        var current = source;
        for (var step = 0; step < distance; step++)
        {
            current = ErodeOnce(current);
        }

        return current;
    }

    /// <summary>
    /// A separable Gaussian blur of the coverage, with sigma derived from the radius.
    /// </summary>
    /// <remarks>
    /// Mirrors the upstream kernel's relationship between the two: a shadow's blur becomes
    /// <c>sigma = blur / 2</c> and the taps reach <c>round(sigma * 3)</c> either side
    /// (MetalLayerEffects.swift:105-108). Separable so the cost is linear in the radius rather than
    /// quadratic.
    /// </remarks>
    public static MaskBuffer Blur(MaskBuffer source, double blurRadius)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!double.IsFinite(blurRadius) || blurRadius <= 0)
        {
            return Copy(source);
        }

        var sigma = blurRadius / 2;
        var reach = (int)Math.Round(sigma * 3);
        if (reach < 1)
        {
            return Copy(source);
        }

        var kernel = new double[(reach * 2) + 1];
        var total = 0.0;
        for (var index = -reach; index <= reach; index++)
        {
            var weight = Math.Exp(-(index * index) / (2 * sigma * sigma));
            kernel[index + reach] = weight;
            total += weight;
        }

        for (var index = 0; index < kernel.Length; index++)
        {
            kernel[index] /= total;
        }

        var horizontal = new MaskBuffer(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var sum = 0.0;
                for (var tap = -reach; tap <= reach; tap++)
                {
                    var sx = Math.Clamp(x + tap, 0, source.Width - 1);
                    sum += source.Pixels[(y * source.Stride) + sx] * kernel[tap + reach];
                }

                horizontal.Pixels[(y * horizontal.Stride) + x] = ToByte(sum);
            }
        }

        var vertical = new MaskBuffer(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var sum = 0.0;
                for (var tap = -reach; tap <= reach; tap++)
                {
                    var sy = Math.Clamp(y + tap, 0, source.Height - 1);
                    sum += horizontal.Pixels[(sy * horizontal.Stride) + x] * kernel[tap + reach];
                }

                vertical.Pixels[(y * vertical.Stride) + x] = ToByte(sum);
            }
        }

        return vertical;
    }

    /// <summary>The part of <paramref name="region"/> that is outside <paramref name="shape"/>.</summary>
    public static MaskBuffer Outside(MaskBuffer region, MaskBuffer shape)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(shape);

        var result = new MaskBuffer(region.Width, region.Height);
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                var outer = region.Pixels[(y * region.Stride) + x];
                var inner = CoverageAt(shape, x, y);
                result.Pixels[(y * result.Stride) + x] = (byte)Math.Max(0, outer - inner);
            }
        }

        return result;
    }

    /// <summary>The part of <paramref name="shape"/> that is also inside <paramref name="region"/>.</summary>
    public static MaskBuffer Inside(MaskBuffer shape, MaskBuffer region)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(region);

        var result = new MaskBuffer(shape.Width, shape.Height);
        for (var y = 0; y < shape.Height; y++)
        {
            for (var x = 0; x < shape.Width; x++)
            {
                var outer = CoverageAt(region, x, y) / 255.0;
                var value = shape.Pixels[(y * shape.Stride) + x] * outer;
                result.Pixels[(y * result.Stride) + x] = ToByte(value);
            }
        }

        return result;
    }

    /// <summary>Moves coverage by an offset, filling what comes in from outside with nothing.</summary>
    public static MaskBuffer Offset(MaskBuffer source, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new MaskBuffer(source.Width, source.Height);
        var shiftX = (int)Math.Round(dx);
        var shiftY = (int)Math.Round(dy);

        for (var y = 0; y < source.Height; y++)
        {
            var sourceY = y - shiftY;
            if ((uint)sourceY >= (uint)source.Height)
            {
                continue;
            }

            for (var x = 0; x < source.Width; x++)
            {
                var sourceX = x - shiftX;
                if ((uint)sourceX >= (uint)source.Width)
                {
                    continue;
                }

                result.Pixels[(y * result.Stride) + x] = source.Pixels[(sourceY * source.Stride) + sourceX];
            }
        }

        return result;
    }

    /// <summary>Coverage at a pixel, with edge replication outside the bounds.</summary>
    private static byte CoverageAt(MaskBuffer buffer, int x, int y) =>
        buffer.Pixels[
            (Math.Clamp(y, 0, buffer.Height - 1) * buffer.Stride)
            + Math.Clamp(x, 0, buffer.Width - 1)];

    private static MaskBuffer DilateOnce(MaskBuffer source)
    {
        var result = new MaskBuffer(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var best = 0;
                for (var oy = -1; oy <= 1; oy++)
                {
                    var sy = y + oy;
                    if ((uint)sy >= (uint)source.Height)
                    {
                        continue;
                    }

                    for (var ox = -1; ox <= 1; ox++)
                    {
                        var sx = x + ox;
                        if ((uint)sx >= (uint)source.Width)
                        {
                            continue;
                        }

                        var value = source.Pixels[(sy * source.Stride) + sx];
                        if (value > best)
                        {
                            best = value;
                        }
                    }
                }

                result.Pixels[(y * result.Stride) + x] = (byte)best;
            }
        }

        return result;
    }

    private static MaskBuffer ErodeOnce(MaskBuffer source)
    {
        var result = new MaskBuffer(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var best = 255;
                for (var oy = -1; oy <= 1; oy++)
                {
                    var sy = y + oy;
                    if ((uint)sy >= (uint)source.Height)
                    {
                        continue;
                    }

                    for (var ox = -1; ox <= 1; ox++)
                    {
                        var sx = x + ox;
                        if ((uint)sx >= (uint)source.Width)
                        {
                            continue;
                        }

                        var value = source.Pixels[(sy * source.Stride) + sx];
                        if (value < best)
                        {
                            best = value;
                        }
                    }
                }

                result.Pixels[(y * result.Stride) + x] = (byte)best;
            }
        }

        return result;
    }

    private static MaskBuffer Copy(MaskBuffer source)
    {
        var result = new MaskBuffer(source.Width, source.Height);
        Array.Copy(source.Pixels, result.Pixels, source.Pixels.Length);
        return result;
    }

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 255));
}
