using Compositor.Core;
using Xunit;

namespace Compositor.Tests;

/// <summary>
/// The undo stack: bracketing, no-op detection, revision tracking, and trimming.
/// </summary>
/// <remarks>
/// These behaviours are ported from <c>DocumentHistory</c> (DocumentHistory.swift:34-122) and each
/// one has a failure mode that is easy to ship: recording empty undo steps, clearing redo on a
/// selection change, or reporting a document clean that was edited during a save.
/// </remarks>
public sealed class DocumentHistoryTests
{
    /// <summary>
    /// A stand-in document whose identity is explicit, so a test can say whether the stack should
    /// see it as changed rather than inferring it from content.
    /// </summary>
    private sealed class Doc(string name)
    {
        public string Name { get; } = name;

        public override string ToString() => Name;
    }

    private sealed class ByNameComparer : IEqualityComparer<Doc>
    {
        public static readonly ByNameComparer Instance = new();

        public bool Equals(Doc? left, Doc? right) =>
            ReferenceEquals(left, right) || (left is not null && right is not null && left.Name == right.Name);

        public int GetHashCode(Doc doc) => doc.Name.GetHashCode();
    }

    private static DocumentHistory<Doc> NewHistory(int entryLimit = 100) =>
        new(ByNameComparer.Instance, entryLimit);

    [Fact]
    public void ANewHistory_IsCleanAndEmpty()
    {
        var history = NewHistory();

        Assert.False(history.IsModified);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void AnEdit_BecomesOneUndoStep()
    {
        var history = NewHistory();
        var before = new Doc("v1");

        history.Begin("Rename", before, null);
        history.End(new Doc("v2"), null);

        Assert.True(history.CanUndo);
        Assert.True(history.IsModified);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal("Rename", history.UndoName);
    }

    [Fact]
    public void NestedEdits_CollapseIntoOneStep()
    {
        var history = NewHistory();
        var before = new Doc("v1");

        history.Begin("Outer", before, null);
        history.Begin("Inner", new Doc("v1.5"), null);
        history.End(new Doc("v1.5"), null);
        history.End(new Doc("v2"), null);

        Assert.Equal(1, history.UndoCount);
        Assert.Equal("Outer", history.UndoName);
    }

    [Fact]
    public void ANoOpEdit_RecordsNothing()
    {
        // Selecting a layer, navigating, or a filter that changed nothing.
        var history = NewHistory();
        var document = new Doc("v1");

        history.Begin("Select", document, null);
        history.End(document, null);

        Assert.False(history.CanUndo);
        Assert.False(history.IsModified);
    }

    [Fact]
    public void ANoOpEdit_PreservesRedo()
    {
        // The trap: a plain "nothing changed, so record nothing" implementation that clears the
        // redo list on the way would destroy a user's redo chain the moment they click a layer.
        var history = NewHistory();
        var v1 = new Doc("v1");
        history.Begin("Edit", v1, null);
        history.End(new Doc("v2"), null);
        history.Undo();
        Assert.True(history.CanRedo);

        history.Begin("Select", new Doc("v1"), null);
        history.End(new Doc("v1"), null);

        Assert.True(history.CanRedo, "A no-op edit must not clear redo history.");
    }

    [Fact]
    public void ARealEdit_ClearsRedo()
    {
        var history = NewHistory();
        history.Begin("Edit", new Doc("v1"), null);
        history.End(new Doc("v2"), null);
        history.Undo();
        Assert.True(history.CanRedo);

        history.Begin("Edit2", new Doc("v1"), null);
        history.End(new Doc("v3"), null);

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void UndoAndRedo_RestoreTheirSnapshots()
    {
        var history = NewHistory();
        var v1 = new Doc("v1");
        var v2 = new Doc("v2");
        history.Begin("Edit", v1, Guid.Parse("11111111-1111-1111-1111-111111111111"));
        history.End(v2, Guid.Parse("22222222-2222-2222-2222-222222222222"));

        var undone = history.Undo();

        Assert.NotNull(undone);
        Assert.Same(v1, undone!.Document);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), undone.ActiveLayerID);

        var redone = history.Redo();

        Assert.NotNull(redone);
        Assert.Same(v2, redone!.Document);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), redone.ActiveLayerID);
    }

    [Fact]
    public void UndoWithNothingToUndo_ReturnsNull()
    {
        var history = NewHistory();

        Assert.Null(history.Undo());
        Assert.Null(history.Redo());
    }

    [Fact]
    public void CannotUndoWhileAnEditIsOpen()
    {
        var history = NewHistory();
        history.Begin("Edit", new Doc("v1"), null);
        history.End(new Doc("v2"), null);

        history.Begin("Open", new Doc("v2"), null);

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);

        history.End(new Doc("v3"), null);
    }

    [Fact]
    public void UndoingBackToTheSavedState_ClearsModified()
    {
        // Undo works by revision, so returning to the saved revision makes the document clean
        // again — which a content comparison would get wrong if an edit round-tripped.
        var history = NewHistory();
        history.Begin("Edit", new Doc("v1"), null);
        history.End(new Doc("v2"), null);
        Assert.True(history.IsModified);

        history.Undo();

        Assert.False(history.IsModified);
    }

    [Fact]
    public void RedoAwayFromTheSavedState_SetsModifiedAgain()
    {
        var history = NewHistory();
        history.Begin("Edit", new Doc("v1"), null);
        history.End(new Doc("v2"), null);
        history.Undo();
        Assert.False(history.IsModified);

        history.Redo();

        Assert.True(history.IsModified);
    }

    [Fact]
    public void ASaveThatFinishedLate_DoesNotSwallowEditsMadeWhileItWrote()
    {
        // Capture the revision, start writing, edit, then the write completes.
        var history = NewHistory();
        history.Begin("Edit", new Doc("v1"), null);
        history.End(new Doc("v2"), null);

        var captured = history.CurrentRevision;

        history.Begin("Edit2", new Doc("v2"), null);
        history.End(new Doc("v3"), null);

        history.MarkSaved(captured);

        Assert.True(history.IsModified, "An edit made during the write must keep the document modified.");
    }

    [Fact]
    public void ASaveThatFinishedLate_LeavesAnUndoSaveClean()
    {
        var history = NewHistory();
        history.Begin("Edit", new Doc("v1"), null);
        history.End(new Doc("v2"), null);

        var captured = history.CurrentRevision;

        history.MarkSaved(captured);

        Assert.False(history.IsModified);
    }

    [Fact]
    public void MarkSaved_MakesTheCurrentStateClean()
    {
        var history = NewHistory();
        history.Begin("Edit", new Doc("v1"), null);
        history.End(new Doc("v2"), null);

        history.MarkSaved();

        Assert.False(history.IsModified);
    }

    [Fact]
    public void Reset_ClearsEverythingAndStartsClean()
    {
        var history = NewHistory();
        history.Begin("Edit", new Doc("v1"), null);
        history.End(new Doc("v2"), null);

        history.Reset();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.False(history.IsModified);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void TheEntryLimit_DropsTheOldestSteps()
    {
        var history = NewHistory(entryLimit: 3);

        for (var index = 0; index < 6; index++)
        {
            history.Begin($"Edit {index}", new Doc($"v{index}"), null);
            history.End(new Doc($"v{index + 1}"), null);
        }

        Assert.Equal(3, history.UndoCount);
        Assert.Equal("Edit 5", history.UndoName);
    }

    [Fact]
    public void TrimmingAlsoCoversTheRedoList()
    {
        // Dropping only from `past` would let a long redo chain pin memory forever.
        var history = NewHistory(entryLimit: 2);
        for (var index = 0; index < 4; index++)
        {
            history.Begin($"Edit {index}", new Doc($"v{index}"), null);
            history.End(new Doc($"v{index + 1}"), null);
        }

        history.Undo();
        history.Undo();

        Assert.True(history.UndoCount + history.RedoCount <= 2);
    }

    [Fact]
    public void AnUnbalancedEnd_IsIgnored()
    {
        var history = NewHistory();

        history.End(new Doc("v1"), null);

        Assert.False(history.CanUndo);
    }

    [Fact]
    public void ANullDocumentEdit_IsHandled()
    {
        var history = NewHistory();

        history.Begin("Edit", null, null);
        history.End(new Doc("v1"), null);

        Assert.True(history.CanUndo);
        Assert.Null(history.Undo()!.Document);
    }

    [Fact]
    public void RetainedBytes_AreReportedWhenACalculatorIsSupplied()
    {
        var history = new DocumentHistory<Doc>(
            ByNameComparer.Instance,
            entryLimit: 100,
            retainedByteLimit: 1024,
            retainedBytes: _ => 512);

        Assert.Equal(512, history.RetainedBytes(null));
    }

    [Fact]
    public void AZeroEntryLimit_KeepsNothing()
    {
        var history = NewHistory(entryLimit: 0);

        history.Begin("Edit", new Doc("v1"), null);
        history.End(new Doc("v2"), null);

        Assert.Equal(0, history.UndoCount);
    }
}
