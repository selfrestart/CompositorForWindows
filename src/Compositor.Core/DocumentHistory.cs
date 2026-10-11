namespace Compositor.Core;

/// <summary>
/// Undo and redo over value snapshots of a document.
/// </summary>
/// <remarks>
/// Ported from <c>DocumentHistory</c> (DocumentHistory.swift:7-123). The behaviour that matters,
/// and that is easy to get wrong:
///
/// <list type="bullet">
/// <item>
/// <b>Edits are bracketed.</b> <see cref="Begin"/> captures the "before" snapshot only at the
/// outermost level, so a nested edit is one undo step. <see cref="End"/> discards the entry when
/// the document did not actually change — selecting a layer, navigating, or a no-op edit must
/// <i>preserve redo history</i> rather than clear it.
/// </item>
/// <item>
/// <b>Undo is by revision, not by name.</b> Each accepted entry gets a fresh revision id, and
/// undoing restores the "before" revision. <see cref="IsModified"/> compares against the last saved
/// revision, so undoing back to the saved state makes the document clean again — which comparing
/// content hashes would not do if an edit round-trips to the same bytes through a different path.
/// </item>
/// <item>
/// <b>A save that finishes later must not swallow edits made while it wrote.</b>
/// <see cref="MarkSaved(Guid)"/> takes the revision captured before the write started; anything
/// edited during the write stays modified.
/// </item>
/// <item>
/// <b>Two limits, and trimming takes from the older end of whichever list is longer.</b> The entry
/// count and the retained bytes are independent ceilings; dropping only from <c>past</c> would let
/// a long redo chain pin memory.
/// </item>
/// </list>
/// </remarks>
/// <typeparam name="TDocument">The document snapshot type. Its comparer decides what "changed" means.</typeparam>
public sealed class DocumentHistory<TDocument>
    where TDocument : class
{
    /// <summary>One point in the document's history.</summary>
    public sealed record Snapshot(TDocument? Document, Guid? ActiveLayerID, Guid Revision);

    private sealed record Entry(string Name, Snapshot Before, Snapshot After);

    private readonly List<Entry> _past = [];
    private readonly List<Entry> _future = [];
    private readonly IEqualityComparer<TDocument> _comparer;
    private readonly Func<TDocument?, long>? _retainedBytes;

    private Guid _revision = Guid.NewGuid();
    private Guid? _savedRevision;
    private Snapshot? _pending;
    private string _pendingName = "Edit";
    private int _depth;

    /// <summary>Creates a history with the upstream defaults: 100 entries or 256 MiB.</summary>
    /// <param name="comparer">
    /// Decides whether the document changed. Pass a cheap one — see
    /// <see cref="ReferenceFingerprintComparer{TDocument}"/> for why a deep comparison here is a
    /// performance trap rather than a correctness measure.
    /// </param>
    /// <param name="retainedBytes">
    /// Optional byte estimate for trimming. The upstream implementation walks the retained images;
    /// a caller that cannot compute it cheaply may leave this null and rely on the entry limit.
    /// </param>
    public DocumentHistory(
        IEqualityComparer<TDocument> comparer,
        int entryLimit = 100,
        long retainedByteLimit = 256L * 1024 * 1024,
        Func<TDocument?, long>? retainedBytes = null)
    {
        ArgumentNullException.ThrowIfNull(comparer);

        _comparer = comparer;
        EntryLimit = Math.Max(0, entryLimit);
        RetainedByteLimit = Math.Max(0, retainedByteLimit);
        _retainedBytes = retainedBytes;
        _savedRevision = _revision;
    }

    /// <summary>Most entries kept across both directions.</summary>
    public int EntryLimit { get; }

    /// <summary>Most bytes kept across both directions.</summary>
    public long RetainedByteLimit { get; }

    /// <summary>Whether an undo is available. False while an edit is in progress.</summary>
    public bool CanUndo => _depth == 0 && _past.Count > 0;

    /// <summary>Whether a redo is available. False while an edit is in progress.</summary>
    public bool CanRedo => _depth == 0 && _future.Count > 0;

    /// <summary>The name of the edit undo would reverse, or empty.</summary>
    public string UndoName => _past.Count > 0 ? _past[^1].Name : string.Empty;

    /// <summary>The name of the edit redo would reapply, or empty.</summary>
    public string RedoName => _future.Count > 0 ? _future[^1].Name : string.Empty;

    /// <summary>Whether anything has been edited since the last save.</summary>
    public bool IsModified => _revision != _savedRevision;

    /// <summary>How many undo steps are held.</summary>
    public int UndoCount => _past.Count;

    /// <summary>How many redo steps are held.</summary>
    public int RedoCount => _future.Count;

    /// <summary>The document's revision now, for a save that captures it and finishes later.</summary>
    public Guid CurrentRevision => _revision;

    /// <summary>Marks the current state saved.</summary>
    public void MarkSaved() => _savedRevision = _revision;

    /// <summary>
    /// Marks the state captured by <paramref name="saved"/> as saved. Edits made while that save
    /// was writing leave the document modified.
    /// </summary>
    public void MarkSaved(Guid saved) => _savedRevision = saved;

    /// <summary>Drops all history and starts a new revision.</summary>
    public void Reset()
    {
        _past.Clear();
        _future.Clear();
        _pending = null;
        _depth = 0;
        _revision = Guid.NewGuid();
        _savedRevision = _revision;
    }

    /// <summary>Opens an edit. Nested calls collapse into one undo step.</summary>
    public void Begin(string name, TDocument? document, Guid? selection)
    {
        if (_depth == 0)
        {
            _pending = new Snapshot(document, selection, _revision);
            _pendingName = name;
        }

        _depth++;
    }

    /// <summary>
    /// Closes an edit. A no-op closes without recording anything and without clearing redo.
    /// </summary>
    public void End(TDocument? document, Guid? selection)
    {
        if (_depth == 0)
        {
            return;
        }

        _depth--;
        if (_depth != 0 || _pending is not { } before)
        {
            return;
        }

        _pending = null;

        // Selecting, navigating, and no-op edits must preserve redo history.
        if (Same(before.Document, document))
        {
            return;
        }

        _revision = Guid.NewGuid();
        _past.Add(new Entry(
            _pendingName,
            before,
            new Snapshot(document, selection, _revision)));
        _future.Clear();
        Trim(document);
    }

    /// <summary>Steps back one edit, or returns null when there is nothing to undo.</summary>
    public Snapshot? Undo()
    {
        if (!CanUndo)
        {
            return null;
        }

        var entry = _past[^1];
        _past.RemoveAt(_past.Count - 1);
        _future.Add(entry);
        _revision = entry.Before.Revision;
        Trim(entry.Before.Document);
        return entry.Before;
    }

    /// <summary>Steps forward one edit, or returns null when there is nothing to redo.</summary>
    public Snapshot? Redo()
    {
        if (!CanRedo)
        {
            return null;
        }

        var entry = _future[^1];
        _future.RemoveAt(_future.Count - 1);
        _past.Add(entry);
        _revision = entry.After.Revision;
        Trim(entry.After.Document);
        return entry.After;
    }

    /// <summary>Bytes held only by history, excluding documents still live.</summary>
    public long RetainedBytes(TDocument? current) => _retainedBytes?.Invoke(current) ?? 0;

    private bool Same(TDocument? left, TDocument? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return _comparer.Equals(left, right);
    }

    private void Trim(TDocument? current)
    {
        while (_past.Count + _future.Count > EntryLimit
            || (_retainedBytes is not null && RetainedBytes(current) > RetainedByteLimit))
        {
            if (_past.Count > 0)
            {
                _past.RemoveAt(0);
            }
            else if (_future.Count > 0)
            {
                _future.RemoveAt(0);
            }
            else
            {
                break;
            }
        }
    }
}
