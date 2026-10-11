using System.Text.Json;

namespace Compositor.Core;

/// <summary>
/// The per-version rules that decide whether a project is legal for the version it declares.
/// </summary>
/// <remarks>
/// A <c>.comp</c> declares its format version, and each version forbids the fields that a later
/// version introduced: "Files declaring older versions cannot contain non-default appearance
/// values", "Files declaring 1–3 cannot contain mask metadata", and so on. These are not cosmetic.
/// The reason the upstream loader enforces them is stated in docs/project-format.md:15 — older app
/// builds reject a newer version rather than mis-rendering it, so a file that claims to be version
/// 2 while carrying version-5 links would be read by an old build as something it is not.
///
/// <para>
/// This port mirrors <c>ProjectStore.validate</c> (ProjectStore.swift:198-262). Where the format
/// document and the implementation disagree, <b>the implementation wins</b> — that function is
/// what actually decides whether a project opens, and it is stricter than the prose in several
/// places (for example <c>text</c> may not sit on a folder or on an adjustment layer, which
/// docs/project-format.md:48 never says).
/// </para>
/// </remarks>
public static class LayerValidator
{
    /// <summary>Version that introduced layer and folder opacity and blend modes.</summary>
    public const int AppearanceVersion = 3;

    /// <summary>Version that introduced layer masks.</summary>
    public const int LayerMaskVersion = 4;

    /// <summary>Version that introduced clipping masks (live alpha links).</summary>
    public const int ClippingMaskVersion = 5;

    /// <summary>Version that introduced folder masks.</summary>
    public const int FolderMaskVersion = 6;

    /// <summary>Version that introduced adjustment layers.</summary>
    public const int AdjustmentVersion = 7;

    /// <summary>Version that let a folder carry its own opacity.</summary>
    public const int FolderOpacityVersion = 8;

    /// <summary>Version that introduced the three adjustment kinds which sample neighbours.</summary>
    public const int NeighbourSamplingAdjustmentVersion = 9;

    /// <summary>Version that introduced per-letter colours.</summary>
    public const int ColorRunsVersion = 10;

    /// <summary>Version that introduced per-letter faces.</summary>
    public const int FontRunsVersion = 11;

    /// <summary>Checks every layer against the version the manifest declares.</summary>
    public static void Validate(ProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Resolution is { } resolution
            && (!double.IsFinite(resolution) || resolution < 1 || resolution > 9600))
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Resolution {resolution} is outside 1…9600 pixels per inch.");
        }

        foreach (var layer in manifest.Layers)
        {
            ValidateLayer(manifest.Version, layer);
        }

        if (manifest.Version < AppearanceVersion
            && manifest.Layers.Any(layer =>
                (layer.Opacity ?? 1.0) != 1.0 || (layer.BlendMode ?? BlendMode.Normal) != BlendMode.Normal))
        {
            Reject(manifest.Version, $"appearance values (opacity or blend mode); they arrived in version {AppearanceVersion}");
        }

        if (manifest.Version < ClippingMaskVersion
            && manifest.Layers.Any(layer => layer.MaskSourceID is not null))
        {
            Reject(manifest.Version, $"clipping masks (maskSourceID); they arrived in version {ClippingMaskVersion}");
        }

        if (manifest.Version < FolderOpacityVersion
            && manifest.Layers.Any(layer => layer.IsGroup == true && (layer.Opacity ?? 1.0) != 1.0))
        {
            Reject(manifest.Version, $"a folder with its own opacity; that arrived in version {FolderOpacityVersion}");
        }

        if (manifest.Version < FolderOpacityVersion && manifest.Guides is { Count: > 0 })
        {
            Reject(manifest.Version, $"alignment guides; they arrived in version {FolderOpacityVersion}");
        }

        if (manifest.Version == 1
            && manifest.Layers.Any(layer => layer.ParentID is not null || layer.IsGroup == true))
        {
            Reject(manifest.Version, "folders; they arrived in version 2");
        }
    }

    private static void ValidateLayer(int version, ProjectLayerRecord layer)
    {
        ValidateName(layer);

        if (!layer.Transform.IsValid())
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Layer '{layer.Name}' has a transform outside the supported range.");
        }

        if (layer.ImageFile is { Length: > 0 } imageFile
            && !string.Equals(imageFile, ProjectStore.ImageFileName(layer.Id), StringComparison.Ordinal))
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Layer '{layer.Name}' names image asset '{imageFile}'; it must be '{ProjectStore.ImageFileName(layer.Id)}'.");
        }

        if (layer.IsGroup == true && layer.ImageFile is { Length: > 0 })
        {
            throw new ProjectException(ProjectErrorKind.Invalid, $"Folder '{layer.Name}' carries an image asset.");
        }

        ValidateMaskFile(version, layer);
        ValidateText(version, layer);
        ValidateAdjustment(version, layer);

        // A folder's blend mode stays Normal because folders are pass-through
        // (docs/project-format.md:17).
        if (layer.IsGroup == true && (layer.BlendMode ?? BlendMode.Normal) != BlendMode.Normal)
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Folder '{layer.Name}' has blend mode '{layer.BlendMode}'; folders are pass-through and stay Normal.");
        }
    }

    private static void ValidateName(ProjectLayerRecord layer)
    {
        if (string.IsNullOrWhiteSpace(layer.Name))
        {
            throw new ProjectException(ProjectErrorKind.Invalid, $"Layer {ProjectStore.Uuid(layer.Id)} has an empty name.");
        }

        // The manifest's own size ceiling is 4 MiB, so this is a sanity bound rather than a real
        // limit, but it matches the upstream check (ProjectStore.swift:242).
        if (System.Text.Encoding.UTF8.GetByteCount(layer.Name) > 16_384)
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Layer {ProjectStore.Uuid(layer.Id)} has a name longer than 16,384 bytes.");
        }
    }

    private static void ValidateMaskFile(int version, ProjectLayerRecord layer)
    {
        if (layer.MaskFile is not { Length: > 0 } maskFile)
        {
            if (layer.MaskEnabled is not null)
            {
                throw new ProjectException(
                    ProjectErrorKind.Invalid,
                    $"Layer '{layer.Name}' sets maskEnabled with no maskFile.");
            }

            if (layer.MaskPlacement is not null)
            {
                throw new ProjectException(
                    ProjectErrorKind.Invalid,
                    $"Layer '{layer.Name}' sets maskPlacement with no maskFile.");
            }

            return;
        }

        var required = layer.IsGroup == true ? FolderMaskVersion : LayerMaskVersion;
        if (version < required)
        {
            Reject(
                version,
                layer.IsGroup == true
                    ? $"a folder mask; it arrived in version {FolderMaskVersion}"
                    : $"a layer mask; it arrived in version {LayerMaskVersion}");
        }

        var expected = ProjectStore.MaskFileName(layer.Id);
        if (!string.Equals(maskFile, expected, StringComparison.Ordinal))
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Layer '{layer.Name}' names mask asset '{maskFile}'; it must be '{expected}'.");
        }

        if (layer.MaskPlacement is { } placement && !placement.IsValid())
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Layer '{layer.Name}' has a mask placement outside the supported range.");
        }
    }

    /// <summary>
    /// Validates the text metadata and the fields that depend on it.
    /// </summary>
    /// <remarks>
    /// The rule that a text layer must be a plain image layer — not a folder, not an adjustment
    /// layer, and not blank — is in the implementation rather than the format document
    /// (ProjectStore.swift:213).
    /// </remarks>
    private static void ValidateText(int version, ProjectLayerRecord layer)
    {
        if (layer.Text is not { } text)
        {
            return;
        }

        if (layer.IsGroup == true)
        {
            throw new ProjectException(ProjectErrorKind.Invalid, $"Folder '{layer.Name}' carries text metadata.");
        }

        if (layer.Adjustment is not null)
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Layer '{layer.Name}' carries both text and an adjustment; they are mutually exclusive.");
        }

        if (layer.ImageFile is not { Length: > 0 })
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Text layer '{layer.Name}' has no image asset to fall back on.");
        }

        var errors = TextStyleValidator.Validate(text, out var colorRuns, out var fontRuns);
        if (errors is { Count: > 0 })
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Text layer '{layer.Name}' is invalid: {string.Join("; ", errors)}.");
        }

        if (colorRuns && version < ColorRunsVersion)
        {
            Reject(version, $"per-letter colours (colorRuns); they arrived in version {ColorRunsVersion}");
        }

        if (fontRuns && version < FontRunsVersion)
        {
            Reject(version, $"per-letter faces (fontRuns); they arrived in version {FontRunsVersion}");
        }
    }

    private static void ValidateAdjustment(int version, ProjectLayerRecord layer)
    {
        if (layer.Adjustment is not { } adjustment)
        {
            return;
        }

        if (version < AdjustmentVersion)
        {
            Reject(version, $"adjustment layers; they arrived in version {AdjustmentVersion}");
        }

        if (layer.IsGroup == true)
        {
            throw new ProjectException(ProjectErrorKind.Invalid, $"Folder '{layer.Name}' carries an adjustment.");
        }

        if (layer.ImageFile is { Length: > 0 })
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Adjustment layer '{layer.Name}' carries an image asset; adjustment layers have none.");
        }

        if (layer.Text is not null)
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"Layer '{layer.Name}' carries both an adjustment and text.");
        }

        // Three adjustment kinds sample neighbouring pixels and arrived later than the rest
        // (docs/project-format.md:33).
        if (adjustment.ValueKind == JsonValueKind.Object
            && adjustment.TryGetProperty("kind", out var kind)
            && kind.ValueKind == JsonValueKind.String
            && kind.GetString() is "Gaussian Blur" or "Motion Blur" or "Add Noise"
            && version < NeighbourSamplingAdjustmentVersion)
        {
            Reject(
                version,
                $"the '{kind.GetString()}' adjustment; it arrived in version {NeighbourSamplingAdjustmentVersion}");
        }
    }

    private static void Reject(int version, string what) =>
        throw new ProjectException(
            ProjectErrorKind.Invalid,
            $"This project declares format version {version} but contains {what}, which that version cannot hold. "
            + "Older app builds reject a newer version rather than mis-render it.");
}

/// <summary>
/// The text-metadata checks from <c>LayerTextStyle.isValid</c> (TypeTool.swift:35-62).
/// </summary>
/// <remarks>
/// Reads the raw JSON rather than a typed DTO because Wave 0 keeps <c>text</c> as a
/// <see cref="JsonElement"/> to guarantee it round-trips losslessly. The checks are the same ones
/// the upstream validator performs, so a file this accepts is a file macOS accepts.
/// </remarks>
internal static class TextStyleValidator
{
    private const int MaxContentLength = 100_000;
    private const double MinFontSize = 1;
    private const double MaxFontSize = 2000;
    private const double MinTracking = -100;
    private const double MaxTracking = 1000;
    private const double MinLeading = 0;
    private const double MaxLeading = 5000;
    private const double MinBoxSide = 16;
    private const int MaxFontNameLength = 200;

    /// <summary>
    /// Checks a text record, reporting whether <c>colorRuns</c> and <c>fontRuns</c> are present so
    /// the caller can apply the version gates.
    /// </summary>
    public static List<string> Validate(JsonElement text, out bool hasColorRuns, out bool hasFontRuns)
    {
        var errors = new List<string>();
        hasColorRuns = text.TryGetProperty("colorRuns", out var colorRuns) && colorRuns.ValueKind == JsonValueKind.Array;
        hasFontRuns = text.TryGetProperty("fontRuns", out var fontRuns) && fontRuns.ValueKind == JsonValueKind.Array;

        var contentLength = 0;
        if (text.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            // The upstream limit counts UTF-16 units, which is what .NET's string length is too.
            contentLength = content.GetString()!.Length;
            if (contentLength > MaxContentLength)
            {
                errors.Add($"content is {contentLength} UTF-16 units, over the {MaxContentLength} limit");
            }
        }

        RequireFinite(text, "fontSize", MinFontSize, MaxFontSize, errors);
        RequireFinite(text, "tracking", MinTracking, MaxTracking, errors);
        RequireFinite(text, "leading", MinLeading, MaxLeading, errors);
        RequireUnitRange(text, "red", errors);
        RequireUnitRange(text, "green", errors);
        RequireUnitRange(text, "blue", errors);
        ValidateBox(text, errors);
        ValidateRuns(colorRuns, hasColorRuns, contentLength, errors, isFont: false);
        ValidateRuns(fontRuns, hasFontRuns, contentLength, errors, isFont: true);

        return errors;
    }

    private static void RequireFinite(JsonElement text, string name, double min, double max, List<string> errors)
    {
        // Absent properties fall back to the record's default, which is always in range.
        if (!text.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        var number = value.GetDouble();
        if (!double.IsFinite(number) || number < min || number > max)
        {
            errors.Add($"{name} is {number}, outside {min}…{max}");
        }
    }

    private static void RequireUnitRange(JsonElement text, string name, List<string> errors)
    {
        if (!text.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        var number = value.GetDouble();
        if (!double.IsFinite(number) || number < 0 || number > 1)
        {
            errors.Add($"{name} is {number}, outside 0…1");
        }
    }

    private static void ValidateBox(JsonElement text, List<string> errors)
    {
        if (!text.TryGetProperty("boxSize", out var box) || box.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!box.TryGetProperty("width", out var widthElement)
            || !box.TryGetProperty("height", out var heightElement)
            || widthElement.ValueKind != JsonValueKind.Number
            || heightElement.ValueKind != JsonValueKind.Number)
        {
            errors.Add("boxSize is missing a numeric width or height");
            return;
        }

        var width = widthElement.GetDouble();
        var height = heightElement.GetDouble();
        if (!double.IsFinite(width) || !double.IsFinite(height)
            || width < MinBoxSide || width > DocumentLimits.MaxSide
            || height < MinBoxSide || height > DocumentLimits.MaxSide)
        {
            errors.Add($"boxSize {width}×{height} is outside {MinBoxSide}…{DocumentLimits.MaxSide} per side");
            return;
        }

        if (width * height > DocumentLimits.MaxSurfacePixels)
        {
            errors.Add($"boxSize {width}×{height} exceeds one surface's pixel budget");
        }
    }

    /// <summary>
    /// Runs must be sorted, non-overlapping, non-empty, and end within the content
    /// (TypeTool.swift:43-62).
    /// </summary>
    private static void ValidateRuns(
        JsonElement runs,
        bool present,
        int contentLength,
        List<string> errors,
        bool isFont)
    {
        if (!present)
        {
            return;
        }

        var name = isFont ? "fontRuns" : "colorRuns";
        var items = runs.EnumerateArray().ToList();
        if (items.Count == 0)
        {
            errors.Add($"{name} is present but empty; omit it instead");
            return;
        }

        var end = 0;
        foreach (var run in items)
        {
            if (run.ValueKind != JsonValueKind.Object
                || !run.TryGetProperty("location", out var locationElement)
                || !run.TryGetProperty("length", out var lengthElement)
                || locationElement.ValueKind != JsonValueKind.Number
                || lengthElement.ValueKind != JsonValueKind.Number)
            {
                errors.Add($"{name} holds a run with no numeric location or length");
                return;
            }

            var location = locationElement.GetInt64();
            var length = lengthElement.GetInt64();
            if (location < end || length <= 0)
            {
                errors.Add($"{name} runs must be sorted, non-overlapping and of positive length");
                return;
            }

            end = checked((int)(location + length));

            if (isFont)
            {
                if (!run.TryGetProperty("fontName", out var fontName) || fontName.ValueKind != JsonValueKind.String)
                {
                    errors.Add($"{name} holds a run with no fontName");
                    return;
                }

                var text = fontName.GetString()!;
                if (text.Length == 0 || text.Length > MaxFontNameLength || text.Contains('\n') || text.Contains('\r'))
                {
                    errors.Add($"{name} holds an unusable fontName");
                    return;
                }
            }
            else
            {
                foreach (var channel in new[] { "red", "green", "blue" })
                {
                    if (!run.TryGetProperty(channel, out var value) || value.ValueKind != JsonValueKind.Number)
                    {
                        errors.Add($"{name} holds a run with no numeric {channel}");
                        return;
                    }

                    var number = value.GetDouble();
                    if (!double.IsFinite(number) || number < 0 || number > 1)
                    {
                        errors.Add($"{name} run {channel} is {number}, outside 0…1");
                        return;
                    }
                }
            }
        }

        if (end > contentLength)
        {
            errors.Add($"{name} runs end at {end}, past the content's {contentLength} units");
        }
    }
}
