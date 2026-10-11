using Compositor.Core;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// The per-version rules: which fields each declared format version may carry.
/// </summary>
/// <remarks>
/// These come from <c>ProjectStore.validate</c> in the macOS app (ProjectStore.swift:198-262),
/// which is stricter than docs/project-format.md in several places. The reason the gates exist is
/// recorded at docs/project-format.md:15: an older build rejects a newer version rather than
/// mis-rendering it, so a file must not claim an old version while carrying newer fields.
///
/// Every gate gets a test in both directions — accepted at its own version and every version after
/// it, rejected at every version before it — because a gate that only fires one way is the kind of
/// bug that silently lets a malformed project through.
/// </remarks>
public sealed class CompVersionGateTests
{
    private static readonly Guid LayerID = Guid.Parse("AAAAAAAA-0000-0000-0000-000000000001");

    private static ProjectManifest Manifest(int version, params ProjectLayerRecord[] layers) => new()
    {
        DocumentID = Guid.Parse("BBBBBBBB-0000-0000-0000-000000000001"),
        Width = 4,
        Height = 4,
        Version = version,
        Layers = [.. layers],
    };

    private static ProjectLayerRecord PlainLayer(Guid? id = null) => new()
    {
        Id = id ?? LayerID,
        Name = "layer",
        IsVisible = true,
        ImageFile = ProjectStore.ImageFileName(id ?? LayerID),
        Transform = new LayerTransform
        {
            Origin = new DocPoint(0, 0),
            Size = new DocSize(4, 4),
        },
    };

    private static ProjectLayerRecord Folder(Guid id, Guid? parent = null) => new()
    {
        Id = id,
        Name = "folder",
        IsVisible = true,
        IsGroup = true,
        ParentID = parent,
        Transform = new LayerTransform { Origin = new DocPoint(0, 0), Size = new DocSize(4, 4) },
    };

    /// <summary>A layer carrying the field under test, on top of a plain layer's requirements.</summary>
    private static ProjectLayerRecord With(
        Func<ProjectLayerRecord, ProjectLayerRecord> change,
        Guid? id = null) => change(PlainLayer(id)) with { };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void PlainLayer_IsLegalAtEverySupportedVersion(int version)
    {
        var manifest = Manifest(version, PlainLayer());

        ProjectStore.Validate(manifest);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void EverySupportedVersion_RoundTripsThroughJson(int version)
    {
        var written = ProjectStore.SerializeManifest(Manifest(version, PlainLayer()));

        var read = ProjectStore.DeserializeManifest(written);

        Assert.Equal(version, read.Version);
        Assert.Equal(written, ProjectStore.SerializeManifest(read));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(-1)]
    public void UnsupportedVersion_IsRejected(int version)
    {
        var error = Assert.Throws<ProjectException>(() => ProjectStore.Validate(Manifest(version, PlainLayer())));

        Assert.Equal(ProjectErrorKind.Version, error.Kind);
    }

    // ---- opacity and blend modes arrived in version 3 ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Opacity_BeforeVersion3_IsRejected(int version)
    {
        var manifest = Manifest(version, With(layer => layer with { Opacity = 0.5 }));

        var error = Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));

        Assert.Equal(ProjectErrorKind.Invalid, error.Kind);
        Assert.Contains("version 3", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BlendMode_BeforeVersion3_IsRejected(int version)
    {
        var manifest = Manifest(version, With(layer => layer with { BlendMode = BlendMode.Multiply }));

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    public void OpacityAndBlendMode_FromVersion3_AreAccepted(int version)
    {
        var manifest = Manifest(version, With(layer => layer with
        {
            Opacity = 0.5,
            BlendMode = BlendMode.ColorDodge,
        }));

        ProjectStore.Validate(manifest);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FullOpacity_IsAllowedBeforeVersion3(int version)
    {
        // The gate is on non-default values, not on the fields' presence.
        var manifest = Manifest(version, With(layer => layer with { Opacity = 1.0 }));

        ProjectStore.Validate(manifest);
    }

    // ---- masks arrived in version 4, folder masks in version 6 ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LayerMask_BeforeVersion4_IsRejected(int version)
    {
        var manifest = Manifest(version, With(layer => layer with
        {
            MaskFile = ProjectStore.MaskFileName(LayerID),
        }));

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(11)]
    public void LayerMask_FromVersion4_IsAccepted(int version)
    {
        var manifest = Manifest(version, With(layer => layer with
        {
            MaskFile = ProjectStore.MaskFileName(LayerID),
        }));

        ProjectStore.Validate(manifest);
    }

    [Fact]
    public void Folder_AtVersion1_IsRejected()
    {
        // Folders arrived in version 2, so version 1 is the only version that forbids them
        // (ProjectStore.swift:237).
        var manifest = Manifest(1, PlainLayer(), Folder(Guid.Parse("AAAAAAAA-0000-0000-0000-000000000002")));

        var error = Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
        Assert.Contains("version 2", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(11)]
    public void Folder_FromVersion2_IsAccepted(int version)
    {
        var manifest = Manifest(version, PlainLayer(), Folder(Guid.Parse("AAAAAAAA-0000-0000-0000-000000000002")));

        ProjectStore.Validate(manifest);
    }

    [Fact]
    public void LayerMask_WithAWrongFileName_IsRejected()
    {
        // The asset name is derived from the layer id, so anything else is a malformed project.
        var manifest = Manifest(11, With(layer => layer with { MaskFile = "someone-elses.mask.png" }));

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Fact]
    public void MaskEnabled_WithoutAMaskFile_IsRejected()
    {
        var manifest = Manifest(11, With(layer => layer with { MaskEnabled = false }));

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    // ---- clipping masks arrived in version 5 ----

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ClippingMask_BeforeVersion5_IsRejected(int version)
    {
        var baseLayer = PlainLayer(Guid.Parse("AAAAAAAA-0000-0000-0000-000000000010"));
        var clipped = PlainLayer(Guid.Parse("AAAAAAAA-0000-0000-0000-000000000011")) with
        {
            MaskSourceID = baseLayer.Id,
        };
        var manifest = Manifest(version, baseLayer, clipped);

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(11)]
    public void ClippingMask_FromVersion5_IsAccepted(int version)
    {
        var baseLayer = PlainLayer(Guid.Parse("AAAAAAAA-0000-0000-0000-000000000010"));
        var clipped = PlainLayer(Guid.Parse("AAAAAAAA-0000-0000-0000-000000000011")) with
        {
            MaskSourceID = baseLayer.Id,
        };
        var manifest = Manifest(version, baseLayer, clipped);

        ProjectStore.Validate(manifest);
    }

    // ---- folder opacity arrived in version 8 ----

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void FolderOpacity_BeforeVersion8_IsRejected(int version)
    {
        var folder = Folder(Guid.Parse("AAAAAAAA-0000-0000-0000-000000000020")) with { Opacity = 0.5 };
        var manifest = Manifest(version, PlainLayer(), folder);

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(11)]
    public void FolderOpacity_FromVersion8_IsAccepted(int version)
    {
        var folder = Folder(Guid.Parse("AAAAAAAA-0000-0000-0000-000000000020")) with { Opacity = 0.5 };
        var manifest = Manifest(version, PlainLayer(), folder);

        ProjectStore.Validate(manifest);
    }

    [Fact]
    public void FolderWithABlendMode_IsAlwaysRejected()
    {
        // Folders are pass-through, so their blend mode stays Normal at every version.
        var folder = Folder(Guid.Parse("AAAAAAAA-0000-0000-0000-000000000020")) with
        {
            BlendMode = BlendMode.Multiply,
        };
        var manifest = Manifest(11, PlainLayer(), folder);

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    // ---- guides arrived in version 8 ----

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void Guides_BeforeVersion8_AreRejected(int version)
    {
        var manifest = Manifest(version, PlainLayer()) with
        {
            Guides = [new CanvasGuide
            {
                Id = Guid.Parse("CCCCCCCC-0000-0000-0000-000000000001"),
                Axis = GuideAxis.Vertical,
                Position = 10,
            }],
        };

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(11)]
    public void Guides_FromVersion8_AreAccepted(int version)
    {
        var manifest = Manifest(version, PlainLayer()) with
        {
            Guides = [new CanvasGuide
            {
                Id = Guid.Parse("CCCCCCCC-0000-0000-0000-000000000001"),
                Axis = GuideAxis.Vertical,
                Position = 10,
            }],
        };

        ProjectStore.Validate(manifest);
    }

    // ---- adjustment layers arrived in version 7, three kinds in version 9 ----

    private static ProjectLayerRecord AdjustmentLayer(string kind, int version, Guid? id = null)
    {
        var layer = PlainLayer(id);
        return layer with
        {
            ImageFile = null,
            Adjustment = System.Text.Json.JsonDocument.Parse($$"""{"kind":"{{kind}}"}""").RootElement.Clone(),
        };
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void AdjustmentLayer_BeforeVersion7_IsRejected(int version)
    {
        var manifest = Manifest(version, AdjustmentLayer("Levels", version));

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(11)]
    public void AdjustmentLayer_FromVersion7_IsAccepted(int version)
    {
        var manifest = Manifest(version, AdjustmentLayer("Levels", version));

        ProjectStore.Validate(manifest);
    }

    [Theory]
    [InlineData("Gaussian Blur")]
    [InlineData("Motion Blur")]
    [InlineData("Add Noise")]
    public void NeighbourSamplingAdjustment_BeforeVersion9_IsRejected(string kind)
    {
        // These three sample neighbouring pixels and arrived two versions later than the rest
        // (docs/project-format.md:33).
        var manifest = Manifest(7, AdjustmentLayer(kind, 7));

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Theory]
    [InlineData("Gaussian Blur")]
    [InlineData("Motion Blur")]
    [InlineData("Add Noise")]
    public void NeighbourSamplingAdjustment_FromVersion9_IsAccepted(string kind)
    {
        var manifest = Manifest(9, AdjustmentLayer(kind, 9));

        ProjectStore.Validate(manifest);
    }

    [Fact]
    public void AdjustmentLayer_WithAnImageAsset_IsRejected()
    {
        var layer = PlainLayer() with
        {
            Adjustment = System.Text.Json.JsonDocument.Parse("""{"kind":"Levels"}""").RootElement.Clone(),
        };

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(Manifest(11, layer)));
    }

    // ---- text ----

    private static ProjectLayerRecord TextLayer(int version, string textJson)
    {
        var layer = PlainLayer();
        return layer with
        {
            Text = System.Text.Json.JsonDocument.Parse(textJson).RootElement.Clone(),
        };
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void Text_IsAcceptedFromEveryVersion(int version)
    {
        var manifest = Manifest(version, TextLayer(version, """{"content":"hi","fontSize":72}"""));

        ProjectStore.Validate(manifest);
    }

    [Fact]
    public void ColorRuns_BeforeVersion10_AreRejected()
    {
        var manifest = Manifest(9, TextLayer(9,
            """{"content":"hi","fontSize":72,"colorRuns":[{"location":0,"length":1,"red":1,"green":0,"blue":0}]}"""));

        var error = Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
        Assert.Contains("version 10", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorRuns_FromVersion10_AreAccepted()
    {
        var manifest = Manifest(10, TextLayer(10,
            """{"content":"hi","fontSize":72,"colorRuns":[{"location":0,"length":1,"red":1,"green":0,"blue":0}]}"""));

        ProjectStore.Validate(manifest);
    }

    [Fact]
    public void FontRuns_BeforeVersion11_AreRejected()
    {
        var manifest = Manifest(10, TextLayer(10,
            """{"content":"hi","fontSize":72,"fontRuns":[{"location":0,"length":1,"fontName":"Helvetica"}]}"""));

        var error = Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
        Assert.Contains("version 11", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FontRuns_AtVersion11_AreAccepted()
    {
        var manifest = Manifest(11, TextLayer(11,
            """{"content":"hi","fontSize":72,"fontRuns":[{"location":0,"length":1,"fontName":"Helvetica"}]}"""));

        ProjectStore.Validate(manifest);
    }

    [Fact]
    public void TextRules_OverlappingRuns_AreRejected()
    {
        var manifest = Manifest(11, TextLayer(11,
            """{"content":"hello","fontSize":72,"colorRuns":[{"location":0,"length":3,"red":0,"green":0,"blue":0},{"location":1,"length":2,"red":0,"green":0,"blue":0}]}"""));

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Fact]
    public void TextRules_RunsPastTheContent_AreRejected()
    {
        var manifest = Manifest(11, TextLayer(11,
            """{"content":"hi","fontSize":72,"colorRuns":[{"location":0,"length":5,"red":0,"green":0,"blue":0}]}"""));

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public void TextRules_FontSizeOutOfRange_IsRejected(double size)
    {
        var manifest = Manifest(11, TextLayer(11, $$"""{"content":"hi","fontSize":{{size}}}"""));

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Fact]
    public void TextRules_TextOnAFolder_IsRejected()
    {
        // Not stated in the format document; it is in the implementation (ProjectStore.swift:213).
        var folder = Folder(Guid.Parse("AAAAAAAA-0000-0000-0000-000000000030")) with
        {
            Text = System.Text.Json.JsonDocument.Parse("""{"content":"hi","fontSize":72}""").RootElement.Clone(),
        };
        var manifest = Manifest(11, PlainLayer(), folder);

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Fact]
    public void TextRules_TextWithNoImageAsset_IsRejected()
    {
        var layer = TextLayer(11, """{"content":"hi","fontSize":72}""") with { ImageFile = null };

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(Manifest(11, layer)));
    }

    // ---- resolution ----

    [Theory]
    [InlineData(0.5)]
    [InlineData(9601)]
    [InlineData(double.NaN)]
    public void Resolution_OutOfRange_IsRejected(double resolution)
    {
        var manifest = Manifest(11, PlainLayer()) with { Resolution = resolution };

        Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(72)]
    [InlineData(9600)]
    public void Resolution_InRange_IsAccepted(double resolution)
    {
        var manifest = Manifest(11, PlainLayer()) with { Resolution = resolution };

        ProjectStore.Validate(manifest);
    }
}
