using Compositor.Codecs;
using Compositor.Core;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// The package digest: what counts as a change and what does not.
/// </summary>
/// <remarks>
/// The digest decides whether the app reports an external change, so a false positive nags the
/// reader about a file nobody edited, and a false negative silently discards outside work. The
/// upstream design notes (ProjectDigest.swift:4-10) state the intent precisely, and these tests
/// pin it.
/// </remarks>
public sealed class ProjectDigestTests : IDisposable
{
    private static readonly Guid LayerA = Guid.Parse("AAAAAAAA-0000-0000-0000-00000000000A");
    private static readonly Guid LayerB = Guid.Parse("BBBBBBBB-0000-0000-0000-00000000000B");

    private readonly string _package =
        Path.Combine(Path.GetTempPath(), "digest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_package))
        {
            Directory.Delete(_package, recursive: true);
        }
    }

    private static ProjectSnapshot Snapshot(int width = 2, int height = 2, byte grey = 64)
    {
        var pixels = new PixelBuffer(width, height);
        pixels.Clear(grey, grey, grey, 255);
        var png = PngCodec.Encode(pixels);

        LayerTransform transform() => new()
        {
            Origin = new DocPoint(0, 0),
            Size = new DocSize(width, height),
        };

        var manifest = new ProjectManifest
        {
            DocumentID = Guid.Parse("CCCCCCCC-0000-0000-0000-00000000000C"),
            Width = width,
            Height = height,
            Layers =
            [
                new ProjectLayerRecord
                {
                    Id = LayerA,
                    Name = "a",
                    IsVisible = true,
                    ImageFile = ProjectStore.ImageFileName(LayerA),
                    Transform = transform(),
                },
                new ProjectLayerRecord
                {
                    Id = LayerB,
                    Name = "b",
                    IsVisible = true,
                    ImageFile = ProjectStore.ImageFileName(LayerB),
                    Transform = transform(),
                },
            ],
        };

        return new ProjectSnapshot
        {
            Manifest = manifest,
            Images = new Dictionary<Guid, byte[]> { [LayerA] = png, [LayerB] = png },
        };
    }

    [Fact]
    public void SamePackage_YieldsTheSameDigest()
    {
        var first = Snapshot();
        var second = Snapshot();

        // Two independently built snapshots with identical content.
        Assert.Equal(
            ProjectDigest.ComputeFor(first).Hex,
            ProjectDigest.ComputeFor(second).Hex);
    }

    [Fact]
    public void SnapshotAndDisk_AgreeAfterAWrite()
    {
        // The app takes a digest before a save and another after; if these disagreed, every save
        // would look like an external change.
        var snapshot = Snapshot();
        ProjectStore.Write(_package, snapshot);

        Assert.Equal(
            ProjectDigest.ComputeFor(snapshot).Hex,
            ProjectDigest.ComputeFor(_package).Hex);
    }

    [Fact]
    public void EditingTheManifest_ChangesTheDigest()
    {
        var snapshot = Snapshot();
        var edited = new ProjectSnapshot
        {
            Manifest = snapshot.Manifest with
            {
                Layers = [.. snapshot.Manifest.Layers.Select(layer => layer with { Name = layer.Name + "!" })],
            },
            Images = snapshot.Images,
            Masks = snapshot.Masks,
        };
        ProjectStore.Write(_package, snapshot);
        var before = ProjectDigest.ComputeFor(_package);

        ProjectStore.Write(_package, edited);
        var after = ProjectDigest.ComputeFor(_package);

        Assert.NotEqual(before.Hex, after.Hex);
    }

    [Fact]
    public void RewritingTheSameBytes_LeavesTheDigestAlone()
    {
        // A sync client rewriting metadata, or the same project saved twice, must not look like an
        // edit.
        var snapshot = Snapshot();
        ProjectStore.Write(_package, snapshot);
        var before = ProjectDigest.ComputeFor(_package);

        ProjectStore.Write(_package, Snapshot());

        Assert.Equal(before.Hex, ProjectDigest.ComputeFor(_package).Hex);
    }

    [Fact]
    public void ChangingAnAssetSize_ChangesTheDigest()
    {
        // Assets are not read, so size is the only signal the digest has. This test documents that
        // a content change of the same length would be missed — the upstream design accepts that.
        ProjectStore.Write(_package, Snapshot());
        var before = ProjectDigest.ComputeFor(_package);

        ProjectStore.Write(_package, Snapshot(width: 4, height: 4));
        var after = ProjectDigest.ComputeFor(_package);

        Assert.NotEqual(before.Hex, after.Hex);
    }

    [Fact]
    public void DigestIsThirtyTwoBytesOfSha256()
    {
        ProjectStore.Write(_package, Snapshot());
        var digest = ProjectDigest.ComputeFor(_package);

        Assert.Equal(ProjectDigest.Length, digest.Value.Length);
        Assert.Equal(64, digest.Hex.Length);
    }

    [Fact]
    public void AMissingManifest_IsReported()
    {
        Directory.CreateDirectory(_package);

        Assert.Throws<ProjectException>(() => ProjectDigest.ComputeFor(_package));
    }

    [Fact]
    public void APackageWithNoImagesDirectory_StillDigests()
    {
        // A brand-new project with only blank layers has no assets at all.
        Directory.CreateDirectory(_package);
        var manifest = new ProjectManifest
        {
            DocumentID = Guid.Parse("DDDDDDDD-0000-0000-0000-00000000000D"),
            Width = 2,
            Height = 2,
            Layers =
            [
                new ProjectLayerRecord
                {
                    Id = LayerA,
                    Name = "blank",
                    IsVisible = true,
                    ImageFile = null,
                    Transform = new LayerTransform { Origin = new DocPoint(0, 0), Size = new DocSize(2, 2) },
                },
            ],
        };
        File.WriteAllText(
            Path.Combine(_package, ProjectStore.ManifestName),
            ProjectStore.SerializeManifest(manifest));

        var digest = ProjectDigest.ComputeFor(_package);

        Assert.Equal(ProjectDigest.Length, digest.Value.Length);
    }

    [Fact]
    public void EqualDigests_CompareEqual()
    {
        ProjectStore.Write(_package, Snapshot());
        var first = ProjectDigest.ComputeFor(_package);
        var second = ProjectDigest.ComputeFor(_package);

        Assert.Equal(first, second);
    }
}
