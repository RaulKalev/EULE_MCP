using RevitMCP.Addin.Checkpoints;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>H11: a text note's text and width are snapshotted, diffed and rolled back as pseudo-parameters.</summary>
public class TextNoteCheckpointTests
{
    private const double WidthFeet = 51.9 / 304.8;

    private static ElementSnapshot Note(string text, double widthFeet)
    {
        var snapshot = new ElementSnapshot { Category = "Text Notes", Parameters = { ["Comments"] = null } };
        TextNoteSnapshot.Add(snapshot, text, widthFeet);
        return snapshot;
    }

    [Fact]
    public void TextAndWidthAreStoredAsRestorablePseudoParameters()
    {
        var snapshot = Note("ATS keskseade\r", WidthFeet);

        Assert.Equal("ATS keskseade\r", snapshot.Parameters[TextNoteSnapshot.TextParameter]);
        Assert.Equal("S:ATS keskseade\r", snapshot.Raw[TextNoteSnapshot.TextParameter]);
        Assert.Equal("51.9 mm", snapshot.Parameters[TextNoteSnapshot.WidthParameter]);
        Assert.StartsWith("D:", snapshot.Raw[TextNoteSnapshot.WidthParameter]);

        var unreadable = new ElementSnapshot();
        TextNoteSnapshot.Add(unreadable, null, null);
        Assert.Empty(unreadable.Parameters);
    }

    [Fact]
    public void AnEditedNoteDiffsAndRollsBackItsTextBeforeItsWidth()
    {
        var before = Note("ATS keskseade", WidthFeet);
        var now = Note("ATS keskseade (uus)", 69.6 / 304.8);

        var delta = SnapshotDiff.Compare(36262, before, now);
        Assert.Equal(new[] { "(Text)", "(Width)" }, delta.Parameters.Select(p => p.Name));
        Assert.Equal("51.9 mm", delta.Parameters[1].Before);
        Assert.Equal("69.6 mm", delta.Parameters[1].Now);

        var cp = new Checkpoint { Id = "cp-0a0b0c", Title = "t" };
        cp.Snapshot[36262] = before;
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_set_text_notes", Modified = new[] { 36262L } });
        var diff = CheckpointDiffBuilder.Build(cp, new Dictionary<long, ElementDelta> { [36262] = delta });
        Assert.False(CheckpointDiffReport.IsTouchedUnchanged(diff.Entries[36262]));

        var plan = RollbackPlanner.Plan(diff, cp, new RollbackOptions());
        Assert.Equal(new[] { "(Text)", "(Width)" }, plan.Values.Select(v => v.Parameter));
        Assert.Empty(plan.Skipped);

        Assert.True(TextNoteSnapshot.TryParseRestore(plan.Values[0].Parameter, plan.Values[0].Raw, out var field, out var text, out _, out var error), error);
        Assert.Equal(TextNoteField.Text, field);
        Assert.Equal("ATS keskseade", text);
        Assert.True(TextNoteSnapshot.TryParseRestore(plan.Values[1].Parameter, plan.Values[1].Raw, out field, out _, out var width, out error), error);
        Assert.Equal(TextNoteField.Width, field);
        Assert.Equal(WidthFeet, width, 12);
    }

    [Fact]
    public void AnUnchangedNoteIsTouchedButUnchanged()
    {
        var delta = SnapshotDiff.Compare(1, Note("A", WidthFeet), Note("A", WidthFeet + 1e-9));
        Assert.False(delta.HasChanges);
    }

    [Theory]
    [InlineData("Comments", "S:x", false, null)]
    [InlineData("(Text)", "D:1", false, "the stored text is not a string")]
    [InlineData("(Width)", "S:wide", false, "the stored width is not a positive length")]
    [InlineData("(Width)", "D:0", false, "the stored width is not a positive length")]
    [InlineData("(Width)", "garbage", false, "no stored value")]
    [InlineData("(Text)", "S:", true, null)]
    public void RestoreParsing(string parameter, string raw, bool ok, string? expectedError)
    {
        Assert.Equal(ok, TextNoteSnapshot.TryParseRestore(parameter, raw, out _, out _, out _, out var error));
        Assert.Equal(expectedError, error);
    }
}
