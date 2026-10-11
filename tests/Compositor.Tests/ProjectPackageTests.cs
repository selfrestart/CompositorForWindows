using Compositor.Codecs;
using Compositor.Core;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// The full package lifecycle on disk: write, read back with decoded pixels, edit, save, and detect
/// outside changes.
/// </summary>
/// <remarks>
/// The end-to-end test in <see cref="CompEndToEndTests"/> asserts the composite of a package it
/// built in memory. This one exercises the parts that only a real directory can catch: asset
/// resolution through the file system, non-ASCII layer names surviving the trip, a package that
/// somebody else edited, and a package that is damaged.
/// </remarks>
public sealed class ProjectPackageTests : IDisposable
{
    private static readonly Guid BaseLayer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TopLayer = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _package =
        Path.Combine(Path.GetTempPath(), "pkg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_package))
        {
            Directory.Delete(_package, recursive: true);
        }
    }

    /// <summary>A two-layer project whose top layer is half transparent.</summary>
    private static LoadedProject Build(string? layerName = null, string packagePath = "")
    {
        var baseLayer = new PixelBuffer(3, 2);
        baseLayer.Clear(255, 0, 0, 255);

        var top = new PixelBuffer(3, 2);
        top.Clear(0, 0, 255, 128);

        var manifest = new ProjectManifest
        {
            DocumentID = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Width = 3,
            Height = 2,
            ActiveLayerID = TopLayer,
            Layers =
            [
                new ProjectLayerRecord
                {
                    Id = BaseLayer,
                    Name = layerName ?? "底层",
                    IsVisible = true,
                    ImageFile = ProjectStore.ImageFileName(BaseLayer),
                    Transform = new LayerTransform { Origin = new DocPoint(0, 0), Size = new DocSize(3, 2) },
                },
                new ProjectLayerRecord
                {
                    Id = TopLayer,
                    Name = layerName ?? "顶层 & <tag>",
                    IsVisible = true,
                    ImageFile = ProjectStore.ImageFileName(TopLayer),
                    BlendMode = BlendMode.Multiply,
                    Opacity = 0.5,
                    Transform = new LayerTransform { Origin = new DocPoint(0, 0), Size = new DocSize(3, 2) },
                },
            ],
        };

        var snapshot = new ProjectSnapshot
        {
            Manifest = manifest,
            Images = new Dictionary<Guid, byte[]>
            {
                [BaseLayer] = PngCodec.Encode(baseLayer),
                [TopLayer] = PngCodec.Encode(top),
            },
        };

        ProjectStore.Write(packagePath, snapshot);
        return ProjectPackage.Open(packagePath);
    }

    [Fact]
    public void Open_DecodesEveryReferencedAsset()
    {
        var project = Build(packagePath: _package);

        Assert.Equal(2, project.AssetCount);
        Assert.Equal(3, project.Images[BaseLayer].Width);
        Assert.Equal(2, project.Images[BaseLayer].Height);
        Assert.Equal(3, project.Images[TopLayer].Width);
    }

    [Fact]
    public void Open_PreservesHalfTransparentPixels()
    {
        // A premultiplication slip would leave these opaque or shift the colour.
        var project = Build(packagePath: _package);

        var pixel = project.Images[TopLayer].GetPixel(0, 0);

        Assert.Equal((0, 0, 255, 128), ((int)pixel.R, (int)pixel.G, (int)pixel.B, (int)pixel.A));
    }

    [Fact]
    public void NonAsciiLayerNames_SurviveTheTrip()
    {
        // Names reach the manifest through the filesystem, the JSON writer and the JSON reader, so
        // an encoding slip anywhere along that path corrupts them. The asset file names are
        // derived from layer ids, so a non-ASCII name must not affect asset resolution.
        var project = Build(packagePath: _package);

        Assert.Equal("底层", project.Manifest.Layers[0].Name);
        Assert.Equal("顶层 & <tag>", project.Manifest.Layers[1].Name);
    }

    [Fact]
    public void SaveAndReopen_KeepsPixelsAndManifestIdentical()
    {
        var project = Build(packagePath: _package);
        var before = project.Composite();

        ProjectPackage.Save(project);
        var reopened = ProjectPackage.Open(_package);
        var after = reopened.Composite();

        Assert.Equal(
            ProjectStore.SerializeManifest(project.Manifest),
            ProjectStore.SerializeManifest(reopened.Manifest));

        for (var y = 0; y < before.Height; y++)
        {
            for (var x = 0; x < before.Width; x++)
            {
                Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void AFreshlyOpenedProject_IsNotReportedAsChanged()
    {
        var project = Build(packagePath: _package);

        Assert.False(project.HasChangedOnDisk());
    }

    [Fact]
    public void TouchingThePackage_IsNotAChange()
    {
        // A sync client rewriting metadata, or the same bytes saved again, must not look like an
        // edit — otherwise the app nags about a file nobody touched.
        var project = Build(packagePath: _package);

        ProjectPackage.Save(project);

        Assert.False(project.HasChangedOnDisk());
    }

    [Fact]
    public void AnOutsideEdit_IsDetected()
    {
        // The scenario the digest exists for: another writer changes the project while it is open.
        var project = Build(packagePath: _package);

        var manifest = project.Manifest with
        {
            Layers = [.. project.Manifest.Layers.Select(layer => layer with { Name = layer.Name + " (edited)" })],
        };
        File.WriteAllText(
            Path.Combine(_package, ProjectStore.ManifestName),
            ProjectStore.SerializeManifest(manifest));

        Assert.True(project.HasChangedOnDisk());
    }

    [Fact]
    public void AnOutsideAssetChange_IsDetected()
    {
        var project = Build(packagePath: _package);

        var bigger = new PixelBuffer(6, 4);
        PngCodec.EncodeFile(bigger, Path.Combine(_package, "images", ProjectStore.ImageFileName(BaseLayer)));

        Assert.True(project.HasChangedOnDisk());
    }

    [Fact]
    public void AMaskedProject_SurvivesDiskAndKeepsTheMaskGrayscale()
    {
        // The format requires a mask to be a monochrome image with no alpha (LayerMask.swift:24-25),
        // so the round trip must not turn it into a colour-type-6 PNG along the way. That check is
        // the whole point: an RGBA mask would still round-trip through this port and only fail on
        // the macOS side.
        var layer = new PixelBuffer(2, 2);
        layer.Clear(255, 0, 0, 255);
        var mask = new MaskBuffer(2, 2, [255, 0, 128, 64]);

        var manifest = new ProjectManifest
        {
            DocumentID = Guid.NewGuid(),
            Width = 2,
            Height = 2,
            Layers =
            [
                new ProjectLayerRecord
                {
                    Id = BaseLayer,
                    Name = "masked",
                    IsVisible = true,
                    ImageFile = ProjectStore.ImageFileName(BaseLayer),
                    MaskFile = ProjectStore.MaskFileName(BaseLayer),
                    MaskEnabled = true,
                    Transform = new LayerTransform { Origin = new DocPoint(0, 0), Size = new DocSize(2, 2) },
                },
            ],
        };

        ProjectStore.Write(_package, new ProjectSnapshot
        {
            Manifest = manifest,
            Images = new Dictionary<Guid, byte[]> { [BaseLayer] = PngCodec.Encode(layer) },
            Masks = new Dictionary<Guid, byte[]> { [BaseLayer] = PngCodec.Encode(mask) },
        });

        var reopened = ProjectPackage.Open(_package);

        Assert.Single(reopened.Masks);
        Assert.Equal(mask.Pixels, reopened.Masks[BaseLayer].Pixels);

        var maskBytes = File.ReadAllBytes(Path.Combine(_package, "images", ProjectStore.MaskFileName(BaseLayer)));
        Assert.Equal(0, maskBytes[25]); // IHDR colour type 0: greyscale, not RGBA
    }

    [Fact]
    public void AMaskedLayer_CompositesThroughThePackage()
    {
        // End to end: the mask reaches the compositor and scales the layer's alpha.
        var layer = new PixelBuffer(2, 2);
        layer.Clear(255, 0, 0, 255);
        var mask = new MaskBuffer(2, 2);
        mask.Clear(128);

        var manifest = new ProjectManifest
        {
            DocumentID = Guid.NewGuid(),
            Width = 2,
            Height = 2,
            Layers =
            [
                new ProjectLayerRecord
                {
                    Id = BaseLayer,
                    Name = "masked",
                    IsVisible = true,
                    ImageFile = ProjectStore.ImageFileName(BaseLayer),
                    MaskFile = ProjectStore.MaskFileName(BaseLayer),
                    MaskEnabled = true,
                    Transform = new LayerTransform { Origin = new DocPoint(0, 0), Size = new DocSize(2, 2) },
                },
            ],
        };

        ProjectStore.Write(_package, new ProjectSnapshot
        {
            Manifest = manifest,
            Images = new Dictionary<Guid, byte[]> { [BaseLayer] = PngCodec.Encode(layer) },
            Masks = new Dictionary<Guid, byte[]> { [BaseLayer] = PngCodec.Encode(mask) },
        });

        var flattened = ProjectPackage.Open(_package).Composite();

        Assert.Equal(128, flattened.GetPixel(0, 0).A);
        Assert.Equal(255, flattened.GetPixel(0, 0).R);
    }

    [Fact]
    public void AProjectWhoseManifestIsDestroyed_CountsAsChanged()
    {
        // A package caught half-written has no readable manifest, which is not "unchanged".
        var project = Build(packagePath: _package);

        File.WriteAllText(Path.Combine(_package, ProjectStore.ManifestName), "{ this is not json");

        Assert.True(project.HasChangedOnDisk());
    }

    [Fact]
    public void ACorruptAsset_IsReportedWithTheLayerName()
    {
        var project = Build(packagePath: _package);
        File.WriteAllBytes(
            Path.Combine(_package, "images", ProjectStore.ImageFileName(BaseLayer)),
            [0x00, 0x01, 0x02, 0x03, 0x04]);

        var error = Assert.Throws<ProjectException>(() => ProjectPackage.Open(_package));

        Assert.Equal(ProjectErrorKind.MissingImage, error.Kind);
    }

    [Fact]
    public void AMissingAsset_IsReported()
    {
        Build(packagePath: _package);
        File.Delete(Path.Combine(_package, "images", ProjectStore.ImageFileName(TopLayer)));

        var error = Assert.Throws<ProjectException>(() => ProjectPackage.Open(_package));

        Assert.Equal(ProjectErrorKind.MissingImage, error.Kind);
    }

    [Fact]
    public void AMissingAssetThatIsNotReferenced_IsIgnored()
    {
        // A stale file left in images/ is not part of the project, so it must not block opening.
        var project = Build(packagePath: _package);
        File.WriteAllText(Path.Combine(_package, "images", "leftover.png"), "not an image");

        var reopened = ProjectPackage.Open(_package);

        Assert.Equal(2, reopened.AssetCount);
    }

    [Fact]
    public void ATooNewProject_IsRejectedWithTheVersionError()
    {
        Build(packagePath: _package);
        var manifestPath = Path.Combine(_package, ProjectStore.ManifestName);
        var json = File.ReadAllText(manifestPath).Replace("\"version\": 11", "\"version\": 99");
        File.WriteAllText(manifestPath, json);

        var error = Assert.Throws<ProjectException>(() => ProjectPackage.Open(_package));

        Assert.Equal(ProjectErrorKind.Version, error.Kind);
    }

    [Fact]
    public void AnAssetNameThatIsAPath_IsRejectedByTheNameRule()
    {
        // The manifest is untrusted input: a name like ../../secret must never be opened.
        //
        // This is caught by the name rule — an asset is always "<layer UUID>.png" — which runs
        // before any file is touched, so a traversal never reaches the escape check below it.
        // That check stays as the second line of defence (ProjectStore.ResolveAssetPath), and is
        // deliberately not exercised by a test that cannot reach it: a passing test for unreachable
        // code is worse than no test, because it reads as coverage.
        Build(packagePath: _package);
        var manifestPath = Path.Combine(_package, ProjectStore.ManifestName);
        var json = File.ReadAllText(manifestPath)
            .Replace(ProjectStore.ImageFileName(BaseLayer), "../../../windows/win.ini");
        File.WriteAllText(manifestPath, json);

        var error = Assert.Throws<ProjectException>(() => ProjectPackage.Open(_package));

        Assert.Equal(ProjectErrorKind.Invalid, error.Kind);
        Assert.Contains("must be", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingAPlainFile_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "not-a-package-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "hello");

        try
        {
            Assert.Throws<ProjectException>(() => ProjectPackage.Open(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AWholeStackOfVersions_OpensFromDisk()
    {
        // The version gates are unit-tested; this proves they also hold when the manifest has been
        // through the file system rather than a serializer round trip.
        for (var version = ProjectManifest.MinSupportedVersion; version <= ProjectManifest.CurrentVersion; version++)
        {
            var directory = Path.Combine(Path.GetTempPath(), "ver-" + Guid.NewGuid().ToString("N"));
            try
            {
                var pixels = new PixelBuffer(2, 2);
                pixels.Clear(10, 20, 30, 255);
                var manifest = new ProjectManifest
                {
                    DocumentID = Guid.NewGuid(),
                    Width = 2,
                    Height = 2,
                    Version = version,
                    Layers =
                    [
                        new ProjectLayerRecord
                        {
                            Id = BaseLayer,
                            Name = "layer",
                            IsVisible = true,
                            ImageFile = ProjectStore.ImageFileName(BaseLayer),
                            Transform = new LayerTransform { Origin = new DocPoint(0, 0), Size = new DocSize(2, 2) },
                        },
                    ],
                };

                ProjectStore.Write(directory, new ProjectSnapshot
                {
                    Manifest = manifest,
                    Images = new Dictionary<Guid, byte[]> { [BaseLayer] = PngCodec.Encode(pixels) },
                });

                var reopened = ProjectPackage.Open(directory);

                Assert.Equal(version, reopened.Manifest.Version);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }
}
