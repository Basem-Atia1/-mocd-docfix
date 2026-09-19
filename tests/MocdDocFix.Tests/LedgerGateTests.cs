using MocdDocFix.Cli;
using MocdDocFix.Domain;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Every mode reads the sheet the moment it is chosen, so edits made before that are picked up
/// already. The gap this closes is the pause after a mode says what it is about to do — which is
/// exactly when somebody thinks "wait, not that row" and opens Excel.
///
/// Menu numbers are 1-based: "1" is Go ahead, "2" is Read again, "3" is Cancel.
/// </summary>
public class LedgerGateTests
{
    private static readonly Guid Doc = Guid.Parse("33333333-0000-0000-0000-000000000001");

    private static LedgerRow Row(string verdict) => new()
    {
        DocId = Doc,
        DocFileName = "a.pdf",
        Verdict = verdict
    };

    [Fact]
    public void Going_ahead_keeps_the_rows_it_was_given()
    {
        var before = new[] { Row(RowVerdicts.Fix) };

        var (answer, after) = LedgerGate.Ask(new ScriptedPrompts("1"), "Delete them?", before,
            () => new[] { Row(RowVerdicts.Ignore) });

        Assert.Equal(GateAnswer.GoAhead, answer);
        Assert.Same(before, after);
    }

    [Fact]
    public void Reading_again_replaces_the_rows_then_asks_once_more()
    {
        var (answer, after) = LedgerGate.Ask(
            new ScriptedPrompts("2", "1"), "Delete them?",
            new[] { Row(RowVerdicts.Fix) },
            () => new[] { Row(RowVerdicts.Ignore) });

        Assert.Equal(GateAnswer.GoAhead, answer);
        Assert.Equal(RowVerdict.Ignore, after[0].Verdict2());
    }

    /// <summary>Compared by document, never by row number — those move on every write.</summary>
    [Fact]
    public void Reading_again_says_what_changed()
    {
        var prompts = new ScriptedPrompts("2", "1");

        LedgerGate.Ask(prompts, "Delete them?", new[] { Row(RowVerdicts.Fix) },
            () => new[] { Row(RowVerdicts.Ignore) });

        Assert.True(prompts.Said("fix → ignore"));
    }

    [Fact]
    public void Reading_again_with_nothing_changed_says_so()
    {
        var prompts = new ScriptedPrompts("2", "1");

        LedgerGate.Ask(prompts, "Delete them?", new[] { Row(RowVerdicts.Fix) },
            () => new[] { Row(RowVerdicts.Fix) });

        Assert.True(prompts.Said("No verdict changed"));
    }

    /// <summary>An empty read is a mistake, not an instruction to act on nothing.</summary>
    [Fact]
    public void An_empty_read_back_keeps_what_was_already_there()
    {
        var before = new[] { Row(RowVerdicts.Fix) };

        var (answer, after) = LedgerGate.Ask(new ScriptedPrompts("2", "1"), "Delete them?",
            before, Array.Empty<LedgerRow>);

        Assert.Equal(GateAnswer.GoAhead, answer);
        Assert.Same(before, after);
    }

    [Fact]
    public void Cancelling_says_so()
    {
        var (answer, _) = LedgerGate.Ask(new ScriptedPrompts("3"), "Delete them?",
            new[] { Row(RowVerdicts.Fix) }, Array.Empty<LedgerRow>);

        Assert.Equal(GateAnswer.Cancel, answer);
    }
}
