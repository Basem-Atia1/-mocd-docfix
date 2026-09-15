using System.Net;
using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The real backlog, over the client VPN. Every one of these skips itself when the server cannot
/// be reached, so the suite stays green off the VPN — but on it, they are the only thing that
/// proves the two guesses in the body search: that the fields come back at all, and that the
/// document name is where we think it is.
/// </summary>
public class LiveBacklogTests : IDisposable
{
    private const string Collection = "https://devops.mocd.gov.ae/MOCD";
    private const string Project = "NPO - Phase 2";
    private const string Emap = "Employee Appointment Request";

    /// <summary>
    /// Exactly as CRM spells it, curly apostrophe and all. The backlog writes the same document
    /// with a straight one, which is the sort of difference that used to end a search: WIQL
    /// CONTAINS matches characters, and U+2019 is not U+0027.
    /// </summary>
    private const string TheName = "A Copy of Board of Director’s Decision";

    /// <summary>The backlog's spelling of the same name.</summary>
    private const string TheNameStraight = "A Copy of Board of Director's Decision";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-live-" + Guid.NewGuid());

    public LiveBacklogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>Windows SSO, which is what reaches this server from a domain workstation.</summary>
    private static AdoClient? Reachable()
    {
        var client = new AdoClient(
            new HttpClientHandler { UseDefaultCredentials = true, PreAuthenticate = true },
            Collection, Project);

        try
        {
            // Cheap, and it fails the same way every other call would if the VPN is down.
            client.FindByTitleAsync("Employee Appointment", CancellationToken.None)
                .GetAwaiter().GetResult();

            return client;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The case that prompted all of this. No work item is called "A Copy of Board of Director's
    /// Decision"; it is written in the acceptance criteria of user story 27628, whose title names
    /// the service. Before the body search this stopped a run and asked the operator.
    /// </summary>
    [Fact]
    public void The_name_that_defeated_the_title_search_is_settled_by_a_story_body()
    {
        if (Reachable() is not { } ado) return;

        var candidates = ado.FindCandidatesAsync(Emap, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.NotEmpty(candidates);

        var naming = candidates.Where(c => DocumentTypeAuthority.Mentions(c.Text, TheName)).ToList();

        Assert.NotEmpty(naming);
        Assert.Contains(naming, c => c.Id == 27628);

        // The curly apostrophe CRM holds and the straight one the story writes must not be the
        // difference between finding it and not. Normalising flattens both to a space.
        Assert.Contains(candidates.Where(c => DocumentTypeAuthority.Mentions(c.Text, TheNameStraight)),
            c => c.Id == 27628);
    }

    /// <summary>
    /// End to end, through the thing the run actually calls: no prompt, and CRM confirmed.
    /// </summary>
    [Fact]
    public async Task The_whole_enquiry_settles_it_without_asking_anybody()
    {
        if (Reachable() is not { } ado) return;

        var prompts = new FakePrompts();

        var ruling = await new DocumentTypeCheck(ado,
                new DocumentTypeDecisions(Path.Combine(_dir, "live.json")), prompts)
            .RuleOnAsync(TheName, Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
        Assert.Empty(prompts.Questions);

        // Which stage answers depends on how CRM spelled the apostrophe, and both are correct.
        // Asserting one of them would make this test a hostage to a character in a CRM row.
        Assert.Contains(ruling.Source, new[] { "DevOps titles", "DevOps bodies" });
    }

    /// <summary>
    /// The backlog's own spelling settles at the title stage, on one work item — 34145, a test
    /// case whose title names the service. One hit may confirm CRM; it is only contradicting CRM
    /// that needs two.
    /// </summary>
    [Fact]
    public async Task The_backlogs_own_spelling_is_settled_by_a_title()
    {
        if (Reachable() is not { } ado) return;

        var ruling = await new DocumentTypeCheck(ado,
                new DocumentTypeDecisions(Path.Combine(_dir, "straight.json")), new FakePrompts())
            .RuleOnAsync(TheNameStraight, Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
        Assert.Equal("DevOps titles", ruling.Source);
    }
}
