using Compositor.Codecs;
using Compositor.Core;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// Layer masks: coverage that multiplies a layer's alpha.
/// </summary>
/// <remarks>
/// A mask is a single-channel coverage map — white reveals, black hides — and its normalized extent
/// matches the layer's own rectangle, so the same transform applies to both
/// (docs/project-format.md:21). These tests cover the behaviour that had no coverage before the
/// mask buffer became its own type.
/// </remarks>
public sealed class LayerMaskTests
{
    private static readonly Guid Layer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ProjectManifest Manifest(bool maskEnabled = true, string? maskFile = "mask") => new()
    {
        DocumentID = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Width = 2,
        Height = 2,
        Layers =
        [
            new ProjectLayerRecord
            {
                Id = Layer,
                Name = "layer",
                IsVisible = true,
                ImageFile = ProjectStore.ImageFileName(Layer),
                MaskFile = maskFile is null ? null : ProjectStore.MaskFileName(Layer),
                MaskEnabled = maskFile is null ? null : maskEnabled,
                Transform = new LayerTransform { Origin = new DocPoint(0, 0), Size = new DocSize(2, 2) },
            },
        ],
    };

    private static Dictionary<Guid, PixelBuffer> Pixels(byte r = 255, byte g = 0, byte b = 0, byte a = 255)
    {
        var buffer = new PixelBuffer(2, 2);
        buffer.Clear(r, g, b, a);
        return new Dictionary<Guid, PixelBuffer> { [Layer] = buffer };
    }

    private static Dictionary<Guid, MaskBuffer> Mask(byte coverage)
    {
        var mask = new MaskBuffer(2, 2);
        mask.Clear(coverage);
        return new Dictionary<Guid, MaskBuffer> { [Layer] = mask };
    }

    [Fact]
    public void WithoutAMask_TheLayerIsUnchanged()
    {
        var result = LayerCompositor.Composite(Manifest(maskFile: null), Pixels());

        Assert.Equal((255, 0, 0, 255), Read(result, 0, 0));
    }

    [Fact]
    public void AWhiteMask_RevealsEverything()
    {
        var result = LayerCompositor.Composite(Manifest(), Pixels(), Mask(255));

        Assert.Equal((255, 0, 0, 255), Read(result, 0, 0));
    }

    [Fact]
    public void ABlackMask_HidesEverything()
    {
        var result = LayerCompositor.Composite(Manifest(), Pixels(), Mask(0));

        Assert.Equal((0, 0, 0, 0), Read(result, 0, 0));
    }

    [Fact]
    public void AGreyMask_ScalesAlpha()
    {
        // Half coverage over a transparent canvas: the layer survives at half alpha.
        var result = LayerCompositor.Composite(Manifest(), Pixels(), Mask(128));

        var (r, g, b, a) = Read(result, 0, 0);
        Assert.Equal(255, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
        Assert.Equal(128, a);
    }

    [Fact]
    public void ADisabledMask_IsIgnored()
    {
        // A disabled mask stays embedded and editable but does not affect compositing.
        var result = LayerCompositor.Composite(Manifest(maskEnabled: false), Pixels(), Mask(0));

        Assert.Equal((255, 0, 0, 255), Read(result, 0, 0));
    }

    [Fact]
    public void AMaskMultipliesWithLayerOpacity()
    {
        // Half opacity and half coverage is a quarter.
        var manifest = Manifest() with
        {
            Layers = [Manifest().Layers[0] with { Opacity = 0.5 }],
        };

        var result = LayerCompositor.Composite(manifest, Pixels(), Mask(128));

        Assert.Equal(64, Read(result, 0, 0).A);
    }

    [Fact]
    public void AOnePixelMask_AppliesEverywhere()
    {
        // The format explicitly allows a uniform 1×1 mask to avoid allocating full-resolution
        // pixels before painting (docs/project-format.md:21).
        var masks = new Dictionary<Guid, MaskBuffer> { [Layer] = MaskBuffer.Solid(revealing: true) };

        var result = LayerCompositor.Composite(Manifest(), Pixels(), masks);

        Assert.Equal(255, Read(result, 0, 0).A);
        Assert.Equal(255, Read(result, 1, 1).A);
    }

    [Fact]
    public void AOnePixelHidingMask_HidesEverywhere()
    {
        var masks = new Dictionary<Guid, MaskBuffer> { [Layer] = MaskBuffer.Solid(revealing: false) };

        var result = LayerCompositor.Composite(Manifest(), Pixels(), masks);

        Assert.Equal(0, Read(result, 0, 0).A);
        Assert.Equal(0, Read(result, 1, 1).A);
    }

    [Fact]
    public void AMaskCanCoverPartOfALayer()
    {
        // A 2-wide mask on a 2-wide layer: the left column revealed, the right hidden.
        var mask = new MaskBuffer(2, 2, [
            255, 0,
            255, 0,
        ]);
        var masks = new Dictionary<Guid, MaskBuffer> { [Layer] = mask };

        var result = LayerCompositor.Composite(Manifest(), Pixels(), masks);

        Assert.Equal(255, Read(result, 0, 0).A);
        Assert.Equal(0, Read(result, 1, 0).A);
        Assert.Equal(255, Read(result, 0, 1).A);
        Assert.Equal(0, Read(result, 1, 1).A);
    }

    [Fact]
    public void AMaskIsResampledOntoTheLayersOwnExtent()
    {
        // A 1×2 mask on a 2×2 layer: the left half of the mask governs the left half of the layer.
        var mask = new MaskBuffer(1, 2, [255, 0]);
        var masks = new Dictionary<Guid, MaskBuffer> { [Layer] = mask };

        var result = LayerCompositor.Composite(Manifest(), Pixels(), masks);

        Assert.Equal(255, Read(result, 0, 0).A);
        Assert.Equal(0, Read(result, 0, 1).A);
    }

    [Fact]
    public void AMaskForAnAbsentLayer_IsIgnored()
    {
        // A stale mask entry from a deleted layer must not throw or displace anything.
        var masks = new Dictionary<Guid, MaskBuffer>
        {
            [Guid.Parse("33333333-3333-3333-3333-333333333333")] = new MaskBuffer(2, 2),
        };

        var result = LayerCompositor.Composite(Manifest(), Pixels(), masks);

        Assert.Equal(255, Read(result, 0, 0).A);
    }

    [Fact]
    public void AMaskedLayerBlendsWithWhatIsUnderneath()
    {
        // The mask scales the source alpha, so the backdrop shows through the masked part rather
        // than the layer simply being dimmed.
        var lower = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var manifest = Manifest() with
        {
            Layers =
            [
                new ProjectLayerRecord
                {
                    Id = lower,
                    Name = "base",
                    IsVisible = true,
                    ImageFile = ProjectStore.ImageFileName(lower),
                    Transform = new LayerTransform { Origin = new DocPoint(0, 0), Size = new DocSize(2, 2) },
                },
                Manifest().Layers[0],
            ],
        };

        var basePixels = new PixelBuffer(2, 2);
        basePixels.Clear(0, 0, 255, 255); // blue base
        var redPixels = new PixelBuffer(2, 2);
        redPixels.Clear(255, 0, 0, 255);
        var pixels = new Dictionary<Guid, PixelBuffer>
        {
            [lower] = basePixels,
            [Layer] = redPixels,
        };

        var result = LayerCompositor.Composite(manifest, pixels, Mask(0));

        // Fully hidden red leaves the blue base untouched.
        Assert.Equal((0, 0, 255, 255), Read(result, 0, 0));
    }

    private static (int R, int G, int B, int A) Read(PixelBuffer buffer, int x, int y)
    {
        var (r, g, b, a) = buffer.GetPixel(x, y);
        return (r, g, b, a);
    }
}
