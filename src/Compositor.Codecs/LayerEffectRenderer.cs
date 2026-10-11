namespace Compositor.Codecs;

/// <summary>
/// Draws a layer's effects around and over its pixels.
/// </summary>
/// <remarks>
/// The order is the whole point, and upstream states it at LayerEffects.swift:469-503: shadow, then
/// outer glow, then an outside stroke, then the layer's pixels with a colour overlay, then an inner
/// glow and an inner shadow. Reverse two of them and something disappears — an inner shadow drawn
/// before the pixels would simply be covered by them, and an outside stroke drawn after them would
/// paint over the layer's own edge.
///
/// <para>
/// <b>Every shaded colour is composited with source-over, not copy.</b> Upstream calls this out
/// (LayerEffects.swift:486-487): drawing with copy "would erase the stroke and shadow everywhere
/// inside the source's rectangular bounds", which is to say outside the layer's actual shape but
/// inside the rectangle that holds it. Source-over leaves the transparent parts transparent.
/// </para>
///
/// <para>
/// <b>Inner effects keep the pixels' own alpha.</b> A translucent pixel takes an inner glow fully,
/// as Photoshop's do, rather than only in proportion to its transparency.
/// </para>
/// </remarks>
public static class LayerEffectRenderer
{
    /// <summary>
    /// Returns <paramref name="layer"/> with its effects drawn, or the same buffer when there is
    /// nothing to draw.
    /// </summary>
    /// <remarks>
    /// The result is the same size as the input. Effects that reach past the layer's rectangle — a
    /// shadow, an outer glow, an outside stroke — are clipped to it, which is why the upstream grows
    /// the area first (LayerEffects.swift:449-465). Growing it here would change the layer's
    /// placement, so callers that need the room should pad the layer before calling and crop after.
    /// </remarks>
    public static PixelBuffer Render(PixelBuffer layer, LayerEffects? effects)
    {
        ArgumentNullException.ThrowIfNull(layer);

        // An out-of-range effect is ignored rather than refused, matching the macOS side.
        if (effects is null || effects.IsEmpty || !effects.IsValid)
        {
            return layer;
        }

        var kinds = effects.VisibleKinds();
        if (kinds.Count == 0)
        {
            return layer;
        }

        var result = new PixelBuffer(layer.Width, layer.Height);
        Array.Copy(layer.Pixels, result.Pixels, layer.Pixels.Length);

        var coverage = EffectCoverage.FromAlpha(layer);

        foreach (var kind in kinds)
        {
            switch (kind)
            {
                case LayerEffectKind.DropShadow when effects.Shadow is { } shadow:
                    DrawShadow(result, coverage, shadow);
                    break;

                case LayerEffectKind.OuterGlow when effects.OuterGlow is { } glow:
                    DrawOuterGlow(result, coverage, glow);
                    break;

                case LayerEffectKind.Stroke when effects.Stroke is { } stroke:
                    DrawStroke(result, coverage, stroke);
                    break;

                case LayerEffectKind.ColorOverlay when effects.ColorOverlay is { } overlay:
                    DrawColorOverlay(result, overlay);
                    break;

                case LayerEffectKind.InnerGlow when effects.InnerGlow is { } glow:
                    DrawInnerGlow(result, coverage, glow);
                    break;

                case LayerEffectKind.InnerShadow when effects.InnerShadow is { } shadow:
                    DrawInnerShadow(result, coverage, shadow);
                    break;
            }
        }

        return result;
    }

    private static void DrawShadow(PixelBuffer target, MaskBuffer coverage, ShadowEffect shadow)
    {
        var (dx, dy) = shadow.Offset;

        // The shadow's own footprint is the layer's, moved. Shading that directly would paint over
        // the layer wherever the two still overlap, so the layer's coverage is taken back out first.
        var moved = EffectCoverage.Offset(coverage, dx, dy);
        var soft = EffectCoverage.Blur(moved, shadow.Blur);
        var outside = EffectCoverage.Outside(soft, coverage);
        Shade(target, outside, shadow.Red, shadow.Green, shadow.Blue, shadow.Opacity);
    }

    private static void DrawOuterGlow(PixelBuffer target, MaskBuffer coverage, GlowEffect glow)
    {
        // Grow so the glow spreads outward, take the layer away so it never tints the pixels, then
        // blur — and take the layer away again, because blurring pulls coverage back inward.
        var grown = EffectCoverage.Grow(coverage, (int)Math.Ceiling(glow.Size));
        var band = EffectCoverage.Outside(grown, coverage);
        var soft = EffectCoverage.Blur(band, glow.Size);
        var outside = EffectCoverage.Outside(soft, coverage);
        Shade(target, outside, glow.Red, glow.Green, glow.Blue, glow.Opacity);
    }

    private static void DrawInnerGlow(PixelBuffer target, MaskBuffer coverage, GlowEffect glow)
    {
        var shrunk = EffectCoverage.Shrink(coverage, (int)Math.Ceiling(glow.Size));
        var band = EffectCoverage.Outside(coverage, shrunk);
        var soft = EffectCoverage.Blur(band, glow.Size);

        // An inner glow recolours the pixels that are already there, keeping their alpha.
        Recolor(target, coverage, soft, glow.Red, glow.Green, glow.Blue, glow.Opacity);
    }

    private static void DrawInnerShadow(PixelBuffer target, MaskBuffer coverage, ShadowEffect shadow)
    {
        var (dx, dy) = shadow.Offset;

        // An inner shadow falls on the opposite side from a drop shadow: the shape is moved the other
        // way and what is left inside the edge is the shadow.
        var moved = EffectCoverage.Offset(coverage, -dx, -dy);
        var soft = EffectCoverage.Blur(moved, shadow.Blur);
        var inside = EffectCoverage.Inside(coverage, soft);

        Recolor(target, coverage, inside, shadow.Red, shadow.Green, shadow.Blue, shadow.Opacity);
    }

    private static void DrawStroke(PixelBuffer target, MaskBuffer coverage, StrokeEffect stroke)
    {
        var width = (int)Math.Ceiling(stroke.Size);

        if (stroke.Inside)
        {
            // An inside stroke is a band just within the edge, drawn over the pixels but keeping
            // their alpha — so it is a recolour, not a shade.
            var shrunk = EffectCoverage.Shrink(coverage, width);
            var inner = EffectCoverage.Outside(coverage, shrunk);
            Recolor(target, coverage, inner, stroke.Red, stroke.Green, stroke.Blue, stroke.Opacity);
            return;
        }

        // An outside stroke is a hard band beyond the edge. It is not blurred, and it is clipped to
        // what the layer does not cover so it never sits on top of the pixels it surrounds.
        var grown = EffectCoverage.Grow(coverage, width);
        var band = EffectCoverage.Outside(grown, coverage);
        Shade(target, band, stroke.Red, stroke.Green, stroke.Blue, stroke.Opacity);
    }

    private static void DrawColorOverlay(PixelBuffer target, ColorOverlayEffect overlay)
    {
        // A flat colour where the pixels are, bounded by their alpha so transparency survives.
        for (var offset = 0; offset < target.Pixels.Length; offset += PixelBuffer.BytesPerPixel)
        {
            var alpha = target.Pixels[offset + 3] / 255.0;
            if (alpha <= 0)
            {
                continue;
            }

            var source = overlay.Opacity;
            if (source <= 0)
            {
                continue;
            }

            var outR = ((overlay.Red * source) + ((target.Pixels[offset] / 255.0) * (1 - source))) * 255;
            var outG = ((overlay.Green * source) + ((target.Pixels[offset + 1] / 255.0) * (1 - source))) * 255;
            var outB = ((overlay.Blue * source) + ((target.Pixels[offset + 2] / 255.0) * (1 - source))) * 255;

            target.Pixels[offset] = ToByte(outR);
            target.Pixels[offset + 1] = ToByte(outG);
            target.Pixels[offset + 2] = ToByte(outB);
        }
    }

    /// <summary>
    /// Paints a colour over the target wherever the coverage says, keeping the target's alpha where
    /// it is higher. This is the path for effects that sit <i>behind</i> the layer.
    /// </summary>
    private static void Shade(
        PixelBuffer target,
        MaskBuffer coverage,
        double red,
        double green,
        double blue,
        double opacity)
    {
        for (var y = 0; y < target.Height; y++)
        {
            for (var x = 0; x < target.Width; x++)
            {
                var source = coverage.Pixels[(y * coverage.Stride) + x] / 255.0 * opacity;
                if (source <= 0)
                {
                    continue;
                }

                var offset = (y * target.Stride) + (x * PixelBuffer.BytesPerPixel);
                var backdrop = target.Pixels[offset + 3] / 255.0;
                var outA = source + (backdrop * (1 - source));
                if (outA <= 0)
                {
                    continue;
                }

                // Colours are straight, so each channel is the source-over mixture of the effect's
                // colour and whatever is already there.
                var keep = backdrop * (1 - source) / outA;
                var take = source / outA;

                target.Pixels[offset] = ToByte(((red * take) + ((target.Pixels[offset] / 255.0) * keep)) * 255);
                target.Pixels[offset + 1] = ToByte(((green * take) + ((target.Pixels[offset + 1] / 255.0) * keep)) * 255);
                target.Pixels[offset + 2] = ToByte(((blue * take) + ((target.Pixels[offset + 2] / 255.0) * keep)) * 255);
                target.Pixels[offset + 3] = ToByte(outA * 255);
            }
        }
    }

    /// <summary>
    /// Recolours the layer's own pixels where the coverage says, keeping their alpha.
    /// </summary>
    /// <remarks>
    /// The alpha is deliberately not changed: an inner glow or inner shadow tints a pixel, it does
    /// not make a transparent one appear. The <paramref name="shape"/> argument is the layer's
    /// coverage, which bounds where the effect may show at all.
    /// </remarks>
    private static void Recolor(
        PixelBuffer target,
        MaskBuffer shape,
        MaskBuffer coverage,
        double red,
        double green,
        double blue,
        double opacity)
    {
        for (var y = 0; y < target.Height; y++)
        {
            for (var x = 0; x < target.Width; x++)
            {
                var offset = (y * target.Stride) + (x * PixelBuffer.BytesPerPixel);
                if (target.Pixels[offset + 3] == 0)
                {
                    continue;
                }

                var present = shape.Pixels[(y * shape.Stride) + x] / 255.0;
                if (present <= 0)
                {
                    continue;
                }

                var amount = coverage.Pixels[(y * coverage.Stride) + x] / 255.0 * opacity;
                if (amount <= 0)
                {
                    continue;
                }

                target.Pixels[offset] = ToByte(((red * amount) + ((target.Pixels[offset] / 255.0) * (1 - amount))) * 255);
                target.Pixels[offset + 1] = ToByte(((green * amount) + ((target.Pixels[offset + 1] / 255.0) * (1 - amount))) * 255);
                target.Pixels[offset + 2] = ToByte(((blue * amount) + ((target.Pixels[offset + 2] / 255.0) * (1 - amount))) * 255);
            }
        }
    }

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 255));
}
