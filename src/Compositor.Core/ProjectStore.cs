using System.Text;
using System.Text.Json;

namespace Compositor.Core;

/// <summary>Why a project could not be read or written.</summary>
public enum ProjectErrorKind
{
    /// <summary>Not a Compositor project, or its metadata is damaged.</summary>
    Invalid,

    /// <summary>The manifest declares a format version this build does not support.</summary>
    Version,

    /// <summary>An image inside the project is missing or damaged.</summary>
    MissingImage,

    /// <summary>The project exceeds a canvas, layer, file-size or document-pixel limit.</summary>
    TooLarge,

    /// <summary>An image could not be saved.</summary>
    Encode,
}

/// <summary>A failure reading or writing a <c>.comp</c> package.</summary>
public sealed class ProjectException : Exception
{
    public ProjectException(ProjectErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public ProjectErrorKind Kind { get; }
}

/// <summary>A project as read from disk: the manifest plus its referenced image assets.</summary>
public sealed class ProjectSnapshot
{
    public required ProjectManifest Manifest { get; init; }

    /// <summary>Layer pixels, keyed by the layer's own UUID.</summary>
    public Dictionary<Guid, byte[]> Images { get; init; } = [];

    /// <summary>Mask coverage (8-bit gray), keyed by the layer's own UUID.</summary>
    public Dictionary<Guid, byte[]> Masks { get; init; } = [];
}

/// <summary>
/// Reads and writes a <c>.comp</c> document package: <c>manifest.json</c> plus an <c>images/</c>
/// directory of <c>&lt;layer UUID&gt;.png</c> assets.
/// </summary>
/// <remarks>
/// Contract for this port: <b>read versions 1-11, write version 11.</b> The macOS app writes 11 on
/// every save (ProjectManifest.current), so a reader that stopped at 9 would reject every project
/// the current app produces.
///
/// Reading is deliberately more forgiving than writing, matching the macOS validator, which does
/// not check <c>effects</c>, <c>shape</c>, or the blend-mode value set at all. Rejecting a project
/// macOS opens is a worse failure than carrying a value this build cannot render.
/// </remarks>
public static class ProjectStore
{
    /// <summary>The manifest's file name inside the package.</summary>
    public const string ManifestName = "manifest.json";

    /// <summary>The directory holding layer and mask PNGs inside the package.</summary>
    public const string ImagesDirectory = "images";

    /// <summary>Reads a package, validating it the way <c>ProjectStore.load</c> does.</summary>
    /// <param name="packagePath">The project folder.</param>
    /// <param name="physicalMemoryBytes">
    /// Physical memory, for the document pixel budget. Defaults to 8 GB, which lands on the floor
    /// of the budget formula, so a caller that does not care about the machine still gets a
    /// defensible limit.
    /// </param>
    public static ProjectSnapshot Read(
        string packagePath,
        long physicalMemoryBytes = 8L * 1024 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        if (!Directory.Exists(packagePath))
        {
            throw new ProjectException(ProjectErrorKind.Invalid, $"'{packagePath}' is not a Compositor project folder.");
        }

        var manifestPath = Path.Combine(packagePath, ManifestName);
        if (!File.Exists(manifestPath))
        {
            throw new ProjectException(ProjectErrorKind.Invalid, $"'{ManifestName}' is missing from the project.");
        }

        var info = new FileInfo(manifestPath);
        if (info.Length > DocumentLimits.MaxManifestBytes)
        {
            throw new ProjectException(
                ProjectErrorKind.TooLarge,
                $"{ManifestName} is {info.Length} bytes; the limit is {DocumentLimits.MaxManifestBytes}.");
        }

        ProjectManifest manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize<ProjectManifest>(stream, CompJson.Options)
                ?? throw new ProjectException(ProjectErrorKind.Invalid, $"{ManifestName} is empty.");
        }
        catch (JsonException ex)
        {
            throw new ProjectException(ProjectErrorKind.Invalid, $"{ManifestName} could not be parsed: {ex.Message}", ex);
        }

        Validate(manifest, physicalMemoryBytes);

        var images = new Dictionary<Guid, byte[]>();
        var masks = new Dictionary<Guid, byte[]>();

        foreach (var layer in manifest.Layers)
        {
            if (layer.ImageFile is { Length: > 0 } imageFile)
            {
                images[layer.Id] = ReadAsset(packagePath, imageFile);
            }

            if (layer.MaskFile is { Length: > 0 } maskFile)
            {
                masks[layer.Id] = ReadAsset(packagePath, maskFile);
            }
        }

        return new ProjectSnapshot { Manifest = manifest, Images = images, Masks = masks };
    }

    /// <summary>
    /// The validation the macOS loader performs (ProjectStore.swift:146-201) plus the numeric
    /// ceilings from DocumentLimits.
    /// </summary>
    /// <param name="physicalMemoryBytes">
    /// Physical memory, for the document pixel budget. Defaults to 8 GB, which yields the 200 MP
    /// floor, so a caller that does not care about the machine still gets a defensible limit.
    /// </param>
    public static void Validate(ProjectManifest manifest, long physicalMemoryBytes = 8L * 1024 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!string.Equals(manifest.Format, "com.compositor.project", StringComparison.Ordinal))
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Unrecognized format '{manifest.Format}'; expected 'com.compositor.project'.");
        }

        if (manifest.Version is < ProjectManifest.MinSupportedVersion or > ProjectManifest.CurrentVersion)
        {
            throw new ProjectException(
                ProjectErrorKind.Version,
                $"This project uses format version {manifest.Version}. "
                + $"This app supports versions {ProjectManifest.MinSupportedVersion}-{ProjectManifest.CurrentVersion}.");
        }

        if (!string.Equals(manifest.ColorSpace, "sRGB", StringComparison.Ordinal))
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"The working space is '{manifest.ColorSpace}'; only sRGB is supported.");
        }

        if (manifest.Width <= 0 || manifest.Height <= 0
            || manifest.Width > DocumentLimits.MaxSide || manifest.Height > DocumentLimits.MaxSide)
        {
            throw new ProjectException(
                ProjectErrorKind.TooLarge,
                $"Canvas {manifest.Width}x{manifest.Height} is outside 1-{DocumentLimits.MaxSide}.");
        }

        if (manifest.Layers.Count > DocumentLimits.MaxLayers)
        {
            throw new ProjectException(
                ProjectErrorKind.TooLarge,
                $"The project holds {manifest.Layers.Count} layers; the limit is {DocumentLimits.MaxLayers}.");
        }

        ValidateGuides(manifest.Guides);
        ValidateLayerGraph(manifest);

        // Every rule that depends on the declared format version, plus the per-layer field checks.
        LayerValidator.Validate(manifest);

        var budget = DocumentLimits.DocumentPixelBudget(physicalMemoryBytes);
        if ((long)manifest.Width * manifest.Height > DocumentLimits.MaxSurfacePixels)
        {
            throw new ProjectException(
                ProjectErrorKind.TooLarge,
                $"The canvas holds {(long)manifest.Width * manifest.Height} pixels; "
                + $"a single surface is limited to {DocumentLimits.MaxSurfacePixels}.");
        }

        _ = budget; // Hook for per-layer pixel accounting, which arrives with image loading.
    }

    /// <summary>Writes a package, replacing whatever was there.</summary>
    public static void Write(string packagePath, ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var manifest = snapshot.Manifest;
        Validate(manifest);

        Directory.CreateDirectory(packagePath);
        var imagesDirectory = Path.Combine(packagePath, ImagesDirectory);
        Directory.CreateDirectory(imagesDirectory);

        // Write every asset first, so a failure leaves the old manifest in place rather than
        // pointing at images that were never written.
        foreach (var layer in manifest.Layers)
        {
            if (layer.ImageFile is { Length: > 0 } imageFile)
            {
                if (!snapshot.Images.TryGetValue(layer.Id, out var png))
                {
                    throw new ProjectException(
                        ProjectErrorKind.MissingImage,
                        $"Layer '{layer.Name}' references '{imageFile}' but no pixels were supplied.");
                }

                if (png.LongLength > DocumentLimits.MaxAssetBytes)
                {
                    throw new ProjectException(
                        ProjectErrorKind.TooLarge,
                        $"Asset '{imageFile}' is {png.LongLength} bytes; the limit is {DocumentLimits.MaxAssetBytes}.");
                }

                WriteAsset(packagePath, imageFile, png);
            }

            if (layer.MaskFile is { Length: > 0 } maskFile && snapshot.Masks.TryGetValue(layer.Id, out var mask))
            {
                WriteAsset(packagePath, maskFile, mask);
            }
        }

        WriteManifest(packagePath, manifest);
    }

    /// <summary>
    /// Serializes a manifest exactly as Swift's encoder does: sorted keys, two-space indent,
    /// relaxed escaping, no BOM, <c>\n</c> line endings.
    /// </summary>
    public static string SerializeManifest(ProjectManifest manifest) =>
        JsonSerializer.Serialize(manifest, CompJson.Options);

    /// <summary>Parses a manifest from text. Used by the round-trip tests.</summary>
    public static ProjectManifest DeserializeManifest(string json) =>
        JsonSerializer.Deserialize<ProjectManifest>(json, CompJson.Options)
        ?? throw new ProjectException(ProjectErrorKind.Invalid, "The manifest is empty.");

    /// <summary>
    /// Builds the asset file name for a layer: the UUID in upper case with a <c>.png</c> suffix.
    /// </summary>
    public static string ImageFileName(Guid layerID) => $"{Uuid(layerID)}.png";

    /// <summary>Builds the mask file name for a layer.</summary>
    public static string MaskFileName(Guid layerID) => $"{Uuid(layerID)}.mask.png";

    /// <summary>
    /// Swift's <c>UUID</c> encodes as an upper-case string. .NET's default is lower case, so every
    /// identifier that reaches the JSON or a file name goes through here.
    /// </summary>
    public static string Uuid(Guid value) => value.ToString("D").ToUpperInvariant();

    private static void WriteManifest(string packagePath, ProjectManifest manifest)
    {
        var json = SerializeManifest(manifest);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        if (bytes.LongLength > DocumentLimits.MaxManifestBytes)
        {
            throw new ProjectException(
                ProjectErrorKind.TooLarge,
                $"{ManifestName} would be {bytes.LongLength} bytes; the limit is {DocumentLimits.MaxManifestBytes}.");
        }

        var manifestPath = Path.Combine(packagePath, ManifestName);
        var temporary = manifestPath + ".writing";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, manifestPath, overwrite: true);
    }

    private static byte[] ReadAsset(string packagePath, string fileName)
    {
        var path = ResolveAssetPath(packagePath, fileName);
        if (!File.Exists(path))
        {
            throw new ProjectException(ProjectErrorKind.MissingImage, $"'{fileName}' is missing from the project.");
        }

        var info = new FileInfo(path);
        if (info.Length > DocumentLimits.MaxAssetBytes)
        {
            throw new ProjectException(
                ProjectErrorKind.TooLarge,
                $"Asset '{fileName}' is {info.Length} bytes; the limit is {DocumentLimits.MaxAssetBytes}.");
        }

        return File.ReadAllBytes(path);
    }

    private static void WriteAsset(string packagePath, string fileName, byte[] bytes)
    {
        var path = ResolveAssetPath(packagePath, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Resolves an asset name under <c>images/</c>, refusing anything that escapes the package.
    /// The manifest is untrusted input: a name like <c>../../etc/passwd</c> must never be opened.
    /// </summary>
    private static string ResolveAssetPath(string packagePath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ProjectException(ProjectErrorKind.Invalid, "An asset name is empty.");
        }

        if (fileName.Contains("..", StringComparison.Ordinal)
            || fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(fileName))
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Asset name '{fileName}' is not a plain file name inside the project.");
        }

        var root = Path.GetFullPath(Path.Combine(packagePath, ImagesDirectory));
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectException(ProjectErrorKind.Invalid, $"Asset name '{fileName}' escapes the project.");
        }

        return candidate;
    }

    private static void ValidateGuides(List<CanvasGuide>? guides)
    {
        if (guides is null)
        {
            return;
        }

        if (guides.Count > DocumentLimits.MaxGuides)
        {
            throw new ProjectException(
                ProjectErrorKind.TooLarge,
                $"The project holds {guides.Count} guides; the limit is {DocumentLimits.MaxGuides}.");
        }

        foreach (var guide in guides)
        {
            if (!double.IsFinite(guide.Position) || Math.Abs(guide.Position) > DocumentLimits.MaxGuidePosition)
            {
                throw new ProjectException(
                    ProjectErrorKind.Invalid,
                    $"Guide {Uuid(guide.Id)} has position {guide.Position}, which is not a finite value "
                    + $"within ±{DocumentLimits.MaxGuidePosition}.");
            }
        }
    }

    private static void ValidateLayerGraph(ProjectManifest manifest)
    {
        var byID = new Dictionary<Guid, ProjectLayerRecord>(manifest.Layers.Count);
        foreach (var layer in manifest.Layers)
        {
            if (!byID.TryAdd(layer.Id, layer))
            {
                throw new ProjectException(
                    ProjectErrorKind.Invalid,
                    $"Two layers share the id {Uuid(layer.Id)}.");
            }
        }

        foreach (var layer in manifest.Layers)
        {
            if (layer.ParentID is { } parentID)
            {
                if (!byID.TryGetValue(parentID, out var parent))
                {
                    throw new ProjectException(
                        ProjectErrorKind.Invalid,
                        $"Layer '{layer.Name}' names parent {Uuid(parentID)}, which is not in the project.");
                }

                if (parent.IsGroup != true)
                {
                    throw new ProjectException(
                        ProjectErrorKind.Invalid,
                        $"Layer '{layer.Name}' names parent '{parent.Name}', which is not a folder.");
                }
            }

            if (layer.IsGroup == true && layer.ImageFile is { Length: > 0 })
            {
                throw new ProjectException(
                    ProjectErrorKind.Invalid,
                    $"Folder '{layer.Name}' carries an image asset.");
            }

            ValidateDepth(layer, byID);
        }

        if (manifest.ActiveLayerID is { } active && !byID.ContainsKey(active))
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"The active layer {Uuid(active)} is not in the project.");
        }
    }

    private static void ValidateDepth(
        ProjectLayerRecord layer,
        Dictionary<Guid, ProjectLayerRecord> byID)
    {
        var depth = 0;
        var current = layer;
        var seen = new HashSet<Guid> { layer.Id };

        while (current.ParentID is { } parentID)
        {
            if (!seen.Add(parentID))
            {
                throw new ProjectException(
                    ProjectErrorKind.Invalid,
                    $"Folder nesting forms a cycle at {Uuid(parentID)}.");
            }

            if (++depth > DocumentLimits.MaxAncestorDepth)
            {
                throw new ProjectException(
                    ProjectErrorKind.Invalid,
                    $"Layer '{layer.Name}' nests deeper than {DocumentLimits.MaxAncestorDepth} folders.");
            }

            current = byID[parentID];
        }
    }
}
