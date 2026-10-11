using Compositor.Codecs;
using Compositor.Core;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// The Wave 0 vertical slice, end to end: a <c>.comp</c> package on disk is read, its layers are
/// composited, and the result is exported as a PNG.
/// </summary>
/// <remarks>
/// This is exit criterion 3 of the Wave 0 plan (docs/port/05-roadmap.md §4): "open a 3-layer .comp,
/// composite it, and export". The macOS half of that criterion — comparing against a PNG the
/// macOS app exported from the *same* project, to within 1 LSB — cannot run yet: it needs a golden
/// sample produced on a Mac, which this repository does not have. Until then this test pins the
/// behaviour against values derived independently, so the pipeline is proven to be self-consistent
/// rather than merely non-crashing.
/// </remarks>
public sealed class CompEndToEndTests : IDisposable
{
    private static readonly Guid BaseLayer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DodgeLayer = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BlueLayer = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly string _package =
        Path.Combine(Path.GetTempPath(), "comp-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_package))
        {
            Directory.Delete(_package, recursive: true);
        }
    }

    private static LayerTransform At(int x, int y, int width, int height) => new()
    {
        Origin = new DocPoint(x, y),
        Size = new DocSize(width, height),
    };

    private static ProjectLayerRecord Layer(
        Guid id,
        string name,
        BlendMode? mode = null,
        double? opacity = null,
        bool visible = true,
        LayerTransform? transform = null) => new()
    {
        Id = id,
        Name = name,
        IsVisible = visible,
        ImageFile = ProjectStore.ImageFileName(id),
        Transform = transform ?? At(0, 0, 2, 2),
        BlendMode = mode,
        Opacity = opacity,
    };

    /// <summary>
    /// Writes a three-layer project: a 25% grey base, an 80% grey Color Dodge layer over it, and a
    /// half-transparent blue on top.
    /// </summary>
    private ProjectSnapshot BuildThreeLayerProject()
    {
        var baseLayer = new PixelBuffer(2, 2);
        baseLayer.Clear(64, 64, 64, 255);

        var dodge = new PixelBuffer(2, 2);
        dodge.Clear(204, 204, 204, 255);

        var blue = new PixelBuffer(2, 2);
        blue.Clear(0, 0, 255, 128);

        var manifest = new ProjectManifest
        {
            DocumentID = Guid.NewGuid(),
            Width = 2,
            Height = 2,
            ActiveLayerID = BlueLayer,
            Layers =
            [
                Layer(BaseLayer, "base"),
                Layer(DodgeLayer, "dodge", BlendMode.ColorDodge),
                Layer(BlueLayer, "blue"),
            ],
        };

        var images = new Dictionary<Guid, byte[]>
        {
            [BaseLayer] = PngCodec.Encode(baseLayer),
            [DodgeLayer] = PngCodec.Encode(dodge),
            [BlueLayer] = PngCodec.Encode(blue),
        };

        return new ProjectSnapshot { Manifest = manifest, Images = images };
    }

    private static Dictionary<Guid, PixelBuffer> Decode(ProjectSnapshot snapshot) =>
        snapshot.Images.ToDictionary(pair => pair.Key, pair => PngCodec.Decode(pair.Value));

    [Fact]
    public void ThreeLayerProject_RoundTripsThroughDisk()
    {
        var written = BuildThreeLayerProject();
        ProjectStore.Write(_package, written);

        var read = ProjectStore.Read(_package);

        Assert.Equal(3, read.Manifest.Layers.Count);
        Assert.Equal(
            ProjectStore.SerializeManifest(written.Manifest),
            ProjectStore.SerializeManifest(read.Manifest));
        Assert.Equal(3, read.Images.Count);
    }

    [Fact]
    public void ColorDodgeLayer_PushesTheGreyBaseToWhite()
    {
        // 64 grey dodged by 204 grey: 0.251 / (1 - 0.8) clamps to 1, so the pair composites to
        // pure white. This is the visible consequence of the sRGB-space rule, and it is asserted
        // on the intermediate result so the next test's alpha maths cannot mask it.
        var written = BuildThreeLayerProject();
        var decoded = Decode(written);

        var twoLayers = written.Manifest with
        {
            Layers =
            [
                Layer(BaseLayer, "base"),
                Layer(DodgeLayer, "dodge", BlendMode.ColorDodge),
            ],
        };

        var pixel = LayerCompositor.Composite(twoLayers, decoded).GetPixel(0, 0);

        Assert.Equal((255, 255, 255, 255), ((int)pixel.R, (int)pixel.G, (int)pixel.B, (int)pixel.A));
    }

    [Fact]
    public void HalfTransparentLayer_CompositesByAlpha()
    {
        // Over white, a 50.2% blue contributes 255 × (1 − 0.502) = 127 to red and green, and
        // 255 × 0.502 + 255 × 0.498 = 255 to blue. Every channel lands on an exact byte, so this
        // needs no tolerance.
        var written = BuildThreeLayerProject();
        ProjectStore.Write(_package, written);
        var read = ProjectStore.Read(_package);

        var pixel = LayerCompositor.Composite(read.Manifest, Decode(read)).GetPixel(0, 0);

        Assert.Equal((127, 127, 255, 255), ((int)pixel.R, (int)pixel.G, (int)pixel.B, (int)pixel.A));
    }

    [Fact]
    public void ExportedPng_CarriesTheCompositedPixels()
    {
        var written = BuildThreeLayerProject();
        ProjectStore.Write(_package, written);
        var read = ProjectStore.Read(_package);

        var flattened = LayerCompositor.Composite(read.Manifest, Decode(read));
        var exported = Path.Combine(_package, "export.png");
        PngCodec.EncodeFile(flattened, exported);

        Assert.True(File.Exists(exported));
        var reloaded = PngCodec.DecodeFile(exported);
        Assert.Equal((127, 127, 255, 255), ((int)reloaded.GetPixel(0, 0).R, (int)reloaded.GetPixel(0, 0).G, (int)reloaded.GetPixel(0, 0).B, (int)reloaded.GetPixel(0, 0).A));
    }

    [Fact]
    public void HiddenLayers_AreSkipped()
    {
        var written = BuildThreeLayerProject();
        var decoded = Decode(written);

        var allHidden = written.Manifest with
        {
            Layers = [Layer(BaseLayer, "base", visible: false)],
        };

        var pixel = LayerCompositor.Composite(allHidden, decoded).GetPixel(0, 0);

        Assert.Equal(0, pixel.A);
    }

    [Fact]
    public void Opacity_ScalesTheContribution()
    {
        // A 204-grey layer at half opacity, in the default Normal mode, over the 64-grey base.
        // The layer's own alpha is 1 and its opacity is 0.5, so the source alpha is 0.5 and the
        // source colour is 204/255 = 0.8. Normal blending leaves the source colour alone, so the
        // result is the straight alpha mixture:
        //
        //   αo = 0.5 + 1·(1 − 0.5)                = 1
        //   Co = (0.5·0.8 + 1·(64/255)·0.5) / 1   = 0.52549…
        //   byte = round(0.52549 × 255)           = 134
        //
        // Note this is NOT 204 at half alpha: the layer is half transparent, not half dimmed, so
        // it mixes with the grey underneath and the output is fully opaque.
        var written = BuildThreeLayerProject();
        var decoded = Decode(written);

        var halfOpacity = written.Manifest with
        {
            Layers =
            [
                Layer(BaseLayer, "base"),
                Layer(DodgeLayer, "top", opacity: 0.5),
            ],
        };

        var pixel = LayerCompositor.Composite(halfOpacity, decoded).GetPixel(0, 0);

        Assert.Equal((134, 134, 134, 255), ((int)pixel.R, (int)pixel.G, (int)pixel.B, (int)pixel.A));
    }

    [Fact]
    public void LayersOffTheCanvas_AreClipped()
    {
        var written = BuildThreeLayerProject();
        var decoded = Decode(written);

        var offCanvas = written.Manifest with
        {
            Layers = [Layer(BaseLayer, "base", transform: At(10, 10, 2, 2))],
        };

        var pixel = LayerCompositor.Composite(offCanvas, decoded).GetPixel(0, 0);

        Assert.Equal(0, pixel.A);
    }

    [Fact]
    public void RotatedLayers_AreResampledRatherThanRefused()
    {
        // Wave 0 refused rotation outright. Now the layer goes through the surface's resampling
        // path, so a rotated layer draws instead of throwing.
        var written = BuildThreeLayerProject();
        var decoded = Decode(written);

        var rotated = written.Manifest with
        {
            Layers =
            [
                Layer(BaseLayer, "base", transform: new LayerTransform
                {
                    Origin = new DocPoint(0, 0),
                    Size = new DocSize(2, 2),
                    Rotation = 45,
                }),
            ],
        };

        var flat = LayerCompositor.Composite(rotated, decoded);

        Assert.Equal(2, flat.Width);
        Assert.Equal(2, flat.Height);

        // A 45° rotation of a uniform layer still covers its centre.
        Assert.True(flat.GetPixel(1, 1).A > 0, "A rotated layer should paint its centre.");
    }

    [Fact]
    public void FlippedLayers_AreResampled()
    {
        var written = BuildThreeLayerProject();
        var decoded = Decode(written);

        var flipped = written.Manifest with
        {
            Layers =
            [
                Layer(BaseLayer, "base", transform: new LayerTransform
                {
                    Origin = new DocPoint(0, 0),
                    Size = new DocSize(2, 2),
                    FlipX = true,
                }),
            ],
        };

        var flat = LayerCompositor.Composite(flipped, decoded);

        // A 2x2 uniform grey flipped is the same uniform grey.
        Assert.Equal((64, 64, 64, 255), ((int)flat.GetPixel(0, 0).R, (int)flat.GetPixel(0, 0).G, (int)flat.GetPixel(0, 0).B, (int)flat.GetPixel(0, 0).A));
    }

    [Fact]
    public void AMaskedLayerThatNeedsResampling_IsRefusedWithTheLayerName()
    {
        // A mask's extent matches the layer's own rectangle, so a rotated layer would need the mask
        // taken through the identical transform. Refusing beats drawing an unrotated mask across a
        // rotated layer, and the message has to name the layer so the caller can act on it.
        var written = BuildThreeLayerProject();
        var decoded = Decode(written);
        var masks = new Dictionary<Guid, MaskBuffer>
        {
            [BaseLayer] = MaskBuffer.Solid(revealing: true),
        };

        var masked = written.Manifest with
        {
            Layers =
            [
                Layer(BaseLayer, "the masked one") with
                {
                    ImageFile = ProjectStore.ImageFileName(BaseLayer),
                    MaskFile = ProjectStore.MaskFileName(BaseLayer),
                    MaskEnabled = true,
                    Transform = new LayerTransform
                    {
                        Origin = new DocPoint(0, 0),
                        Size = new DocSize(2, 2),
                        Rotation = 30,
                    },
                },
            ],
        };

        var error = Assert.Throws<NotSupportedException>(
            () => LayerCompositor.Composite(masked, decoded, masks));

        Assert.Contains("the masked one", error.Message, StringComparison.Ordinal);
        Assert.Contains("Bake the mask", error.Message, StringComparison.Ordinal);
    }
}
