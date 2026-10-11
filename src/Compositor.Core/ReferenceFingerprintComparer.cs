namespace Compositor.Core;

/// <summary>
/// A document comparer that treats two snapshots as equal when they share the same manifests and
/// the same image buffers by reference.
/// </summary>
/// <remarks>
/// <b>Why this exists rather than a deep comparison.</b> The undo stack asks "did the document
/// change?" on every <c>End</c>, and a wrong answer either records hundreds of empty undo entries
/// or loses real ones. Swift answers it cheaply because a snapshot holds the same immutable
/// <c>CGImage</c> objects when pixels did not change (DocumentHistory.swift:6), so equality
/// short-circuits on identity.
///
/// <para>
/// In C#, comparing two <c>ProjectSnapshot</c>s deeply would walk every layer's pixels — on a
/// 4K document that is hundreds of megabytes per edit, on the main thread, twice. So equality here
/// is: the manifests are equal, and every image and mask byte array is <i>the same array instance</i>.
/// Layer edits that replace pixels allocate a new array, which is exactly the signal wanted; edits
/// that only touch a layer's name or opacity produce a new manifest record and are caught by the
/// manifest comparison.
/// </para>
///
/// <para>
/// The cost is that an edit which copies pixels into a fresh array with identical content counts as
/// a change. That records one redundant undo step rather than losing a real one, which is the safer
/// direction to be wrong in.
/// </para>
/// </remarks>
public sealed class ReferenceFingerprintComparer : IEqualityComparer<ProjectSnapshot>
{
    /// <summary>The shared instance.</summary>
    public static readonly ReferenceFingerprintComparer Instance = new();

    public bool Equals(ProjectSnapshot? left, ProjectSnapshot? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (!ManifestsEqual(left.Manifest, right.Manifest))
        {
            return false;
        }

        return SameBuffers(left.Images, right.Images) && SameBuffers(left.Masks, right.Masks);
    }

    public int GetHashCode(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var hash = new HashCode();
        hash.Add(snapshot.Manifest.Layers.Count);
        hash.Add(snapshot.Manifest.Width);
        hash.Add(snapshot.Manifest.Height);

        foreach (var layer in snapshot.Manifest.Layers)
        {
            hash.Add(layer.Id);
            hash.Add(layer.Name, StringComparer.Ordinal);
            hash.Add(layer.Opacity);
            hash.Add(layer.BlendMode);
            hash.Add(layer.IsVisible);
        }

        hash.Add(snapshot.Images.Count);
        hash.Add(snapshot.Masks.Count);
        return hash.ToHashCode();
    }

    private static bool SameBuffers(
        Dictionary<Guid, byte[]> left,
        Dictionary<Guid, byte[]> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (id, buffer) in left)
        {
            if (!right.TryGetValue(id, out var other) || !ReferenceEquals(buffer, other))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ManifestsEqual(ProjectManifest left, ProjectManifest right)
    {
        if (left.Version != right.Version
            || left.Width != right.Width
            || left.Height != right.Height
            || left.DocumentID != right.DocumentID
            || left.ActiveLayerID != right.ActiveLayerID
            || !string.Equals(left.ColorSpace, right.ColorSpace, StringComparison.Ordinal)
            || left.Resolution != right.Resolution
            || left.Layers.Count != right.Layers.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Layers.Count; index++)
        {
            if (!LayerEquals(left.Layers[index], right.Layers[index]))
            {
                return false;
            }
        }

        var leftGuides = left.Guides ?? [];
        var rightGuides = right.Guides ?? [];
        if (leftGuides.Count != rightGuides.Count)
        {
            return false;
        }

        for (var index = 0; index < leftGuides.Count; index++)
        {
            if (leftGuides[index] != rightGuides[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool LayerEquals(ProjectLayerRecord left, ProjectLayerRecord right) =>
        left.Id == right.Id
        && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && left.IsVisible == right.IsVisible
        && left.Transform == right.Transform
        && string.Equals(left.ImageFile, right.ImageFile, StringComparison.Ordinal)
        && left.ParentID == right.ParentID
        && left.IsGroup == right.IsGroup
        && left.Opacity == right.Opacity
        && left.BlendMode == right.BlendMode
        && string.Equals(left.MaskFile, right.MaskFile, StringComparison.Ordinal)
        && left.MaskEnabled == right.MaskEnabled
        && left.MaskSourceID == right.MaskSourceID
        && left.MaskLinked == right.MaskLinked
        && left.MaskPlacement == right.MaskPlacement
        && JsonEquals(left.Adjustment, right.Adjustment)
        && JsonEquals(left.Effects, right.Effects)
        && JsonEquals(left.Shape, right.Shape)
        && JsonEquals(left.Text, right.Text);

    private static bool JsonEquals(System.Text.Json.JsonElement? left, System.Text.Json.JsonElement? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return JsonDeepEquals(left.Value, right.Value);
    }

    /// <summary>
    /// Structural comparison of two JSON values.
    /// </summary>
    /// <remarks>
    /// .NET 8 has no <c>JsonElement.DeepEquals</c> (it arrived in .NET 9), so this walks the values.
    /// Object members compare by name rather than position, because a record written by this port
    /// has its keys sorted while one written by the macOS app may not.
    /// </remarks>
    private static bool JsonDeepEquals(System.Text.Json.JsonElement left, System.Text.Json.JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
            {
                var count = 0;
                foreach (var property in left.EnumerateObject())
                {
                    count++;
                    if (!right.TryGetProperty(property.Name, out var other)
                        || !JsonDeepEquals(property.Value, other))
                    {
                        return false;
                    }
                }

                return count == right.EnumerateObject().Count();
            }

            case System.Text.Json.JsonValueKind.Array:
            {
                var leftItems = left.EnumerateArray().ToList();
                var rightItems = right.EnumerateArray().ToList();
                if (leftItems.Count != rightItems.Count)
                {
                    return false;
                }

                for (var index = 0; index < leftItems.Count; index++)
                {
                    if (!JsonDeepEquals(leftItems[index], rightItems[index]))
                    {
                        return false;
                    }
                }

                return true;
            }

            case System.Text.Json.JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);

            case System.Text.Json.JsonValueKind.Number:
                // Compare numerically so 1 and 1.0 agree, which they should: they are the same
                // value and the manifest's round trip is not byte-preserving across writers.
                return left.TryGetDouble(out var leftNumber)
                    && right.TryGetDouble(out var rightNumber)
                    && leftNumber.Equals(rightNumber);

            case System.Text.Json.JsonValueKind.True:
            case System.Text.Json.JsonValueKind.False:
                return left.GetBoolean() == right.GetBoolean();

            case System.Text.Json.JsonValueKind.Null:
            case System.Text.Json.JsonValueKind.Undefined:
                return true;

            default:
                return left.GetRawText() == right.GetRawText();
        }
    }
}
