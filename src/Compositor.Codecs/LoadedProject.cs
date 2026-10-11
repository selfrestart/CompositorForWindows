using Compositor.Core;

namespace Compositor.Codecs;

/// <summary>
/// A project that has been read from disk with its pixels decoded and ready to composite.
/// </summary>
/// <remarks>
/// <see cref="ProjectStore"/> deliberately knows nothing about image encoding: it hands back the
/// raw asset bytes so that <c>Compositor.Core</c> stays free of any image library and can be built
/// and tested on Linux. Decoding is this project's job, and this type is where the two meet.
///
/// It also carries the digest taken at load time, because the question "has this file changed
/// underneath me?" is answered by comparing that digest against a fresh one — see
/// <see cref="HasChangedOnDisk"/>.
/// </remarks>
public sealed class LoadedProject
{
    internal LoadedProject(
        string packagePath,
        ProjectSnapshot snapshot,
        Dictionary<Guid, PixelBuffer> images,
        Dictionary<Guid, MaskBuffer> masks,
        ProjectDigest digest)
    {
        PackagePath = packagePath;
        Snapshot = snapshot;
        Images = images;
        Masks = masks;
        Digest = digest;
    }

    /// <summary>Where the package was read from.</summary>
    public string PackagePath { get; }

    /// <summary>The raw package: manifest plus undecoded asset bytes.</summary>
    public ProjectSnapshot Snapshot { get; }

    /// <summary>Decoded layer pixels, keyed by layer id, in straight RGBA.</summary>
    public Dictionary<Guid, PixelBuffer> Images { get; }

    /// <summary>
    /// Decoded mask coverage, keyed by layer id. Single-channel: white reveals, black hides.
    /// </summary>
    public Dictionary<Guid, MaskBuffer> Masks { get; }

    /// <summary>The digest taken when this project was read.</summary>
    public ProjectDigest Digest { get; }

    /// <summary>The manifest.</summary>
    public ProjectManifest Manifest => Snapshot.Manifest;

    /// <summary>How many assets are embedded.</summary>
    public int AssetCount => Snapshot.Images.Count + Snapshot.Masks.Count;

    /// <summary>
    /// Whether the package on disk differs from what was loaded.
    /// </summary>
    /// <remarks>
    /// This is the read side of the app's external-change detection. Upstream compares a digest
    /// taken before against one taken after (IO/ProjectController+ExternalChanges.swift), so a
    /// package that was merely touched — metadata rewritten, permissions changed, the same bytes
    /// saved again — does not register as a change.
    /// </remarks>
    public bool HasChangedOnDisk()
    {
        try
        {
            return ProjectDigest.ComputeFor(PackagePath) != Digest;
        }
        catch (ProjectException)
        {
            // A package caught half-written has no readable manifest. That is not "unchanged", and
            // treating it as changed makes the caller wait for the next write.
            return true;
        }
    }

    /// <summary>Composites this project's layers into one buffer.</summary>
    public PixelBuffer Composite() => LayerCompositor.Composite(Manifest, Images, Masks);
}

/// <summary>Opens a <c>.comp</c> package and decodes everything it references.</summary>
public static class ProjectPackage
{
    /// <summary>Reads a package and decodes its assets.</summary>
    /// <param name="packagePath">The project folder.</param>
    /// <param name="physicalMemoryBytes">
    /// Physical memory, for the document pixel budget. Defaults to 8 GB, which lands on the floor
    /// of the budget formula.
    /// </param>
    /// <exception cref="ProjectException">
    /// The package is malformed, declares an unsupported version, breaks a version gate, or holds
    /// an asset that is missing or not a decodable image.
    /// </exception>
    public static LoadedProject Open(
        string packagePath,
        long physicalMemoryBytes = 8L * 1024 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        var snapshot = ProjectStore.Read(packagePath, physicalMemoryBytes);
        var digest = ProjectDigest.ComputeFor(packagePath);

        var images = new Dictionary<Guid, PixelBuffer>(snapshot.Images.Count);
        foreach (var (id, bytes) in snapshot.Images)
        {
            images[id] = DecodeImage(bytes, id, snapshot);
        }

        var masks = new Dictionary<Guid, MaskBuffer>(snapshot.Masks.Count);
        foreach (var (id, bytes) in snapshot.Masks)
        {
            masks[id] = DecodeMask(bytes, id, snapshot);
        }

        return new LoadedProject(packagePath, snapshot, images, masks, digest);
    }

    /// <summary>Writes a loaded project back, re-encoding every buffer.</summary>
    public static void Save(LoadedProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var snapshot = new ProjectSnapshot
        {
            Manifest = project.Manifest,
            Images = project.Images.ToDictionary(pair => pair.Key, pair => PngCodec.Encode(pair.Value)),
            Masks = project.Masks.ToDictionary(pair => pair.Key, pair => PngCodec.Encode(pair.Value)),
        };

        ProjectStore.Write(project.PackagePath, snapshot);
    }

    private static PixelBuffer DecodeImage(byte[] bytes, Guid layerID, ProjectSnapshot snapshot)
    {
        var name = LayerName(snapshot, layerID) ?? ProjectStore.Uuid(layerID);

        PixelBuffer decoded;
        try
        {
            decoded = PngCodec.Decode(bytes);
        }
        catch (InvalidDataException exception)
        {
            throw new ProjectException(
                ProjectErrorKind.MissingImage,
                $"The image for layer '{name}' is not a decodable image: {exception.Message}",
                exception);
        }

        var (width, height, bits) = PngCodec.Inspect(bytes);

        // The upstream loader rejects anything deeper than eight bits per channel
        // (ProjectStore.swift:179); a 16-bit asset would be silently truncated otherwise.
        if (bits > 8)
        {
            throw new ProjectException(
                ProjectErrorKind.MissingImage,
                $"The image for layer '{name}' is {bits} bits per channel; the format allows at most 8.");
        }

        if (width < 1 || height < 1
            || width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide)
        {
            throw new ProjectException(
                ProjectErrorKind.MissingImage,
                $"The image for layer '{name}' is {width}×{height}, outside 1…{DocumentLimits.MaxSide}.");
        }

        return decoded;
    }

    private static MaskBuffer DecodeMask(byte[] bytes, Guid layerID, ProjectSnapshot snapshot)
    {
        var name = LayerName(snapshot, layerID) ?? ProjectStore.Uuid(layerID);

        MaskBuffer decoded;
        try
        {
            decoded = PngCodec.DecodeMask(bytes);
        }
        catch (InvalidDataException exception)
        {
            throw new ProjectException(
                ProjectErrorKind.MissingImage,
                $"The mask for layer '{name}' is not a decodable image: {exception.Message}",
                exception);
        }

        var (width, height, _) = PngCodec.Inspect(bytes);
        if (width < 1 || height < 1
            || width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide)
        {
            throw new ProjectException(
                ProjectErrorKind.MissingImage,
                $"The mask for layer '{name}' is {width}×{height}, outside 1…{DocumentLimits.MaxSide}.");
        }

        return decoded;
    }

    private static string? LayerName(ProjectSnapshot snapshot, Guid layerID) =>
        snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == layerID)?.Name;
}
