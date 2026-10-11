namespace Compositor.Core;

/// <summary>
/// The size and memory ceilings a document is held to (DocumentLimits.swift:16-42).
/// </summary>
/// <remarks>
/// These are the values the macOS app actually enforces. docs/project-format.md:9 states the
/// document limit as "100 million image pixels plus 100 million mask pixels", which is NOT what
/// the implementation does: the budget scales with physical memory, and images and masks each get
/// their own counter. Copying the doc's number would make this port 2-8x stricter than macOS and
/// reject projects macOS opens happily.
/// </remarks>
public static class DocumentLimits
{
    /// <summary>Longest side, in pixels, of any canvas, layer, mask or generated surface.</summary>
    public const int MaxSide = 30_000;

    /// <summary>Largest single surface: a canvas, an export, a filter target, an adjustment or mask render.</summary>
    public const long MaxSurfacePixels = 200_000_000;

    /// <summary>Longest side of any manifest's embedded PNG, as validated on load.</summary>
    public const long MaxManifestBytes = 4 * 1024 * 1024;

    /// <summary>Largest single embedded asset.</summary>
    public const long MaxAssetBytes = 512L * 1024 * 1024;

    /// <summary>Most layer records a project may hold.</summary>
    public const int MaxLayers = 10_000;

    /// <summary>Deepest a layer may nest inside groups.</summary>
    public const int MaxAncestorDepth = 64;

    /// <summary>Most alignment guides a project may store.</summary>
    public const int MaxGuides = 1_000;

    /// <summary>Largest magnitude of a guide's position, in document pixels.</summary>
    public const double MaxGuidePosition = 1_000_000;

    /// <summary>
    /// Total imported raster one document may hold, summed across every layer and mask.
    /// Scaled to the machine: a quarter of its memory at 4 bytes a pixel, never less than one
    /// surface and never more than 800 MP.
    /// </summary>
    /// <param name="physicalMemoryBytes">
    /// Total physical memory, as <c>ProcessInfo.processInfo.physicalMemory</c> reports it on macOS.
    /// On Windows read it from <c>GlobalMemoryStatusEx</c>'s <c>ullTotalPhys</c>.
    /// </param>
    public static long DocumentPixelBudget(long physicalMemoryBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(physicalMemoryBytes);
        var quarter = physicalMemoryBytes / 16;
        return Math.Min(800_000_000L, Math.Max(MaxSurfacePixels, quarter));
    }

    /// <summary>Megapixels, for the messages that quote a ceiling back to the reader.</summary>
    public static int MaxSurfaceMegapixels => (int)(MaxSurfacePixels / 1_000_000);

    /// <summary>Megapixels of the document budget, for the same messages.</summary>
    public static int DocumentBudgetMegapixels(long physicalMemoryBytes) =>
        (int)(DocumentPixelBudget(physicalMemoryBytes) / 1_000_000);
}
