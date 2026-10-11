using Compositor.Core;

namespace Compositor.Codecs;

/// <summary>
/// Flattens a layer stack into one buffer, the way the canvas and the exporter do.
/// </summary>
/// <remarks>
/// The composite is the standard source-over blend:
///
/// <code>
/// αo = αs + αb·(1 − αs)
/// Co = (1 − αb)·Cs + αb·B(Cb, Cs)          (the blended source colour)
/// co = αs·Co + αb·Cb·(1 − αs)              (premultiplied result)
/// </code>
///
/// where αs already includes the layer's opacity and mask coverage. This is the same formula
/// Core Image applies through <c>CIColorBlendMode</c> and friends, which is the path upstream takes
/// for the eleven modes Core Graphics gets wrong (LayerAppearance.swift:51-69).
///
/// <b>All of it happens on sRGB-encoded values.</b> See <see cref="Blend"/>.
///
/// Layer order is bottom-to-top, matching the manifest's array order
/// (docs/project-format.md:15). Visibility is inherited from folders without changing child flags,
/// so a folder's own visibility gates its whole subtree.
/// </remarks>
public static class LayerCompositor
{
    /// <summary>
    /// Composites the manifest's layers into one buffer of the document's size using
    /// <paramref name="pixels"/> for layer pixels, keyed by layer id.
    /// </summary>
    /// <param name="manifest">The document.</param>
    /// <param name="pixels">Decoded layer pixels, keyed by layer id.</param>
    /// <param name="masks">
    /// Optional mask coverage, keyed by layer id. A mask is a single-channel coverage map, not an
    /// image: white reveals, black hides. Its normalized extent matches the layer's own rectangle,
    /// so the same transform applies to both.
    /// </param>
    public static PixelBuffer Composite(
        ProjectManifest manifest,
        IReadOnlyDictionary<Guid, PixelBuffer> pixels,
        IReadOnlyDictionary<Guid, MaskBuffer>? masks = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(pixels);

        var canvas = new PixelBuffer(manifest.Width, manifest.Height);

        // Folders gate their whole subtree without touching descendant flags
        // (docs/project-format.md:15), so resolve effective visibility once.
        var byID = manifest.Layers.ToDictionary(layer => layer.Id);
        var folderOpacity = FolderOpacity(manifest, byID);

        foreach (var layer in manifest.Layers)
        {
            if (layer.IsGroup == true || !IsEffectivelyVisible(layer, byID))
            {
                continue;
            }

            if (!pixels.TryGetValue(layer.Id, out var source))
            {
                continue;
            }

            var opacity = EffectiveOpacity(layer, byID, folderOpacity);
            if (opacity <= 0)
            {
                continue;
            }

            var mode = layer.BlendMode ?? BlendMode.Normal;
            var mask = masks is not null && layer.MaskEnabled != false && masks.TryGetValue(layer.Id, out var m)
                ? m
                : null;

            DrawLayer(canvas, source, layer.Transform, mode, opacity, mask, layer.Name);
        }

        return canvas;
    }

    /// <summary>Draws one layer onto the canvas at position, honouring opacity and mask.</summary>
    /// <remarks>
    /// The layer is drawn through a <see cref="Surface"/>, so a rotated, flipped or scaled layer is
    /// resampled by the same code that draws anything else and honours the same clip stack.
    ///
    /// <para>
    /// <b>A mask is applied only when the layer lands on whole pixels at its own size.</b> A mask's
    /// normalized extent matches the layer's own rectangle (docs/project-format.md:21), so resampling
    /// it independently of the layer would need the mask taken through the identical transform.
    /// Upstream clips to the mask at the layer's extent for the same reason. Rather than quietly
    /// drawing an unrotated mask across a rotated layer, a masked layer that needs resampling is
    /// refused — the caller can flatten the mask into the pixels first.
    /// </para>
    /// </remarks>
    private static void DrawLayer(
        PixelBuffer canvas,
        PixelBuffer source,
        LayerTransform transform,
        BlendMode mode,
        double opacity,
        MaskBuffer? mask,
        string layerName)
    {
        var placement = Placement.From(transform);
        var pixelExact = placement.RotationDegrees == 0
            && !placement.FlipX
            && !placement.FlipY
            && placement.Width == source.Width
            && placement.Height == source.Height
            && placement.X == Math.Floor(placement.X)
            && placement.Y == Math.Floor(placement.Y);

        if (mask is not null && !pixelExact)
        {
            throw new NotSupportedException(
                $"Layer '{layerName}' has a mask and a transform that needs resampling "
                + $"(rotation {placement.RotationDegrees}°, flip {placement.FlipX}/{placement.FlipY}, "
                + $"placement {placement.Width}×{placement.Height} for {source.Width}×{source.Height} pixels). "
                + "Bake the mask into the layer's pixels first.");
        }

        using var surface = new Surface(canvas);

        if (mask is not null)
        {
            // The mask scales the source alpha before anything is drawn. Applying it here rather
            // than in the blend loop keeps one implementation of the blend maths.
            var masked = new PixelBuffer(source.Width, source.Height);
            Array.Copy(source.Pixels, masked.Pixels, source.Pixels.Length);
            ApplyMask(masked, mask);

            surface.SetAlpha(opacity);
            surface.SetBlendMode(mode);
            surface.Draw(masked, placement);
            return;
        }

        surface.SetAlpha(opacity);
        surface.SetBlendMode(mode);
        surface.Draw(source, placement);
    }

    /// <summary>
    /// Multiplies each pixel's alpha by the mask's coverage, resampling the mask's own extent onto
    /// the layer's.
    /// </summary>
    private static void ApplyMask(PixelBuffer pixels, MaskBuffer mask)
    {
        for (var y = 0; y < pixels.Height; y++)
        {
            for (var x = 0; x < pixels.Width; x++)
            {
                var coverage = MaskCoverage(mask, x, y, pixels.Width, pixels.Height);
                if (coverage >= 1)
                {
                    continue;
                }

                var offset = (y * pixels.Stride) + (x * PixelBuffer.BytesPerPixel);
                if (coverage <= 0)
                {
                    pixels.Pixels[offset + 3] = 0;
                    continue;
                }

                var alpha = pixels.Pixels[offset + 3] * coverage;
                pixels.Pixels[offset + 3] = (byte)Math.Round(Math.Clamp(alpha, 0, 255));
            }
        }
    }

    /// <summary>One pixel of source-over blending, all inputs and outputs in 0…1.</summary>
    internal static (double R, double G, double B, double A) CompositePixel(
        double sr, double sg, double sb, double sa,
        double br, double bg, double bb, double ba,
        BlendMode mode)
    {
        var outA = sa + (ba * (1 - sa));
        if (outA <= 0)
        {
            return (0, 0, 0, 0);
        }

        // Non-separable modes work on the whole colour at once, so they need their own path.
        if (mode is BlendMode.Hue or BlendMode.Saturation or BlendMode.Color or BlendMode.Luminosity)
        {
            var blended = NonSeparable.Composite(mode, br, bg, bb, sr, sg, sb);
            return Mix(blended.R, blended.G, blended.B, outA, sa, ba, br, bg, bb);
        }

        var cr = MixChannel(sr, br, sa, ba, mode);
        var cg = MixChannel(sg, bg, sa, ba, mode);
        var cb2 = MixChannel(sb, bb, sa, ba, mode);
        return Mix(cr, cg, cb2, outA, sa, ba, br, bg, bb);
    }

    /// <summary>
    /// The blended source colour for one channel: <c>(1 − αb)·Cs + αb·B(Cb, Cs)</c>.
    /// </summary>
    private static double MixChannel(double cs, double cb, double sa, double ba, BlendMode mode) =>
        ((1 - ba) * cs) + (ba * Apply(mode, cb, cs));

    /// <summary>Premultiplies the blended source over the backdrop and un-premultiplies the result.</summary>
    private static (double R, double G, double B, double A) Mix(
        double cr, double cg, double cb2,
        double outA, double sa, double ba,
        double br, double bg, double bb)
    {
        var r = (((sa * cr) + (ba * br * (1 - sa))) / outA);
        var g = (((sa * cg) + (ba * bg * (1 - sa))) / outA);
        var b = (((sa * cb2) + (ba * bb * (1 - sa))) / outA);

        return (Clamp01(r), Clamp01(g), Clamp01(b), Clamp01(outA));
    }

    /// <summary>The blend function for a mode.</summary>
    private static double Apply(BlendMode mode, double cb, double cs) => mode switch
    {
        BlendMode.Normal => cs,
        BlendMode.Multiply => Blend.Multiply(cb, cs),
        BlendMode.Screen => Blend.Screen(cb, cs),
        BlendMode.Overlay => Blend.Overlay(cb, cs),
        BlendMode.Darken => Blend.Darken(cb, cs),
        BlendMode.Lighten => Blend.Lighten(cb, cs),
        BlendMode.Difference => Blend.Difference(cb, cs),
        BlendMode.Exclusion => Blend.Exclusion(cb, cs),
        BlendMode.ColorDodge => Blend.ColorDodge(cb, cs),
        BlendMode.ColorBurn => Blend.ColorBurn(cb, cs),
        BlendMode.HardLight => Blend.HardLight(cb, cs),
        BlendMode.SoftLight => Blend.SoftLight(cb, cs),
        BlendMode.LinearBurn => Blend.LinearBurn(cb, cs),
        BlendMode.LinearDodge => Blend.LinearDodge(cb, cs),
        BlendMode.VividLight => Blend.VividLight(cb, cs),
        BlendMode.LinearLight => Blend.LinearLight(cb, cs),
        BlendMode.PinLight => Blend.PinLight(cb, cs),
        BlendMode.HardMix => Blend.HardMix(cb, cs),
        BlendMode.Subtract => Blend.Subtract(cb, cs),
        BlendMode.Divide => Blend.Divide(cb, cs),
        _ => throw new NotSupportedException($"{mode} is composited through the non-separable path."),
    };

    /// <summary>
    /// Mask coverage in 0…1, resampling the mask's own extent onto the layer's.
    /// </summary>
    /// <remarks>
    /// A mask stores coverage directly — white reveals — so there is no channel packing to undo and
    /// no alpha to consult. Nearest sampling keeps a hard-edged mask hard, and the format's 1×1
    /// uniform mask falls out for free because the index collapses to 0.
    /// </remarks>
    private static double MaskCoverage(MaskBuffer mask, int x, int y, int layerWidth, int layerHeight)
    {
        // MaskBuffer enforces a minimum of one pixel per side, but the arithmetic below would
        // produce a negative index if that ever stopped being true, so it is guarded rather than
        // assumed. Integer division is what maps the mask's own extent onto the layer's.
        if (mask.Width < 1 || mask.Height < 1)
        {
            return 0;
        }

        var mx = layerWidth <= 0 ? 0 : Math.Min(x * mask.Width / layerWidth, mask.Width - 1);
        var my = layerHeight <= 0 ? 0 : Math.Min(y * mask.Height / layerHeight, mask.Height - 1);
        return mask.Pixels[(my * mask.Stride) + mx] / 255.0;
    }

    private static bool IsEffectivelyVisible(
        ProjectLayerRecord layer,
        Dictionary<Guid, ProjectLayerRecord> byID)
    {
        var current = layer;
        var guard = 0;
        while (true)
        {
            if (!current.IsVisible)
            {
                return false;
            }

            if (current.ParentID is not { } parentID || ++guard > DocumentLimits.MaxAncestorDepth)
            {
                return true;
            }

            current = byID[parentID];
        }
    }

    /// <summary>
    /// A folder's opacity multiplies into every layer inside it, however deep
    /// (docs/project-format.md:17).
    /// </summary>
    private static Dictionary<Guid, double> FolderOpacity(
        ProjectManifest manifest,
        Dictionary<Guid, ProjectLayerRecord> byID)
    {
        var products = new Dictionary<Guid, double>();
        foreach (var layer in manifest.Layers)
        {
            var product = 1.0;
            var current = layer;
            var guard = 0;
            while (current.ParentID is { } parentID && ++guard <= DocumentLimits.MaxAncestorDepth)
            {
                current = byID[parentID];
                product *= current.Opacity ?? 1.0;
            }

            products[layer.Id] = product;
        }

        return products;
    }

    private static double EffectiveOpacity(
        ProjectLayerRecord layer,
        Dictionary<Guid, ProjectLayerRecord> byID,
        Dictionary<Guid, double> folderOpacity)
    {
        _ = byID;
        var own = layer.Opacity ?? 1.0;
        var folders = folderOpacity.TryGetValue(layer.Id, out var value) ? value : 1.0;
        return Math.Clamp(own * folders, 0, 1);
    }

    private static double Clamp01(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
