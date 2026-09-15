using System.Net;
using MocdDocFix.Clients;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Reading work item bodies off the server. This is how a description is searched on a server
/// that refuses to search one: WIQL picks the candidates by title — the one thing it will match
/// — and the bodies are fetched and read here.
///
/// The descriptions are HTML and the test steps are XML with HTML encoded inside them; both have
/// to come back as something a word can be found in.
/// </summary>
public class AdoClientBodyTests
{
    /// <summary>A search is two calls: the WIQL POST, then the GET of the ids it returned.</summary>
    private static AdoClient Client(FakeHttpMessageHandler handler) =>
        new(handler, "https://devops.example/MOCD", "NPO - Phase 2");

    [Fact]
    public async Task A_description_comes_back_as_words_not_markup()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, """{"workItems":[{"id":27628}]}""")
            .Enqueue(HttpStatusCode.OK,
                """
                {"value":[{"id":27628,"fields":{
                  "System.Title":"1.1.6 NPOP- Employee Appointment Request Form- Documents",
                  "System.Description":"<div>The system shall display:<br/>A Copy of Board of Director&#39;s Decision</div>"
                }}]}
                """);

        var found = await Client(handler).FindCandidatesAsync("Employee Appointment Request",
            CancellationToken.None);

        var item = Assert.Single(found);
        Assert.Equal(27628, item.Id);
        Assert.Equal("1.1.6 NPOP- Employee Appointment Request Form- Documents", item.Title);
        Assert.Contains("A Copy of Board of Director's Decision", item.Text);
        Assert.DoesNotContain("<div>", item.Text);
    }

    [Fact]
    public async Task Test_steps_survive_being_xml_with_html_encoded_inside_them()
    {
        // This is the shape the server really returns: <steps> wrapping <parameterizedString>
        // whose content is HTML that has been encoded a second time.
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, """{"workItems":[{"id":31000}]}""")
            .Enqueue(HttpStatusCode.OK,
                """
                {"value":[{"id":31000,"fields":{
                  "System.Title":"NPOP|Employee Appointment Request|Documents|Verify uploads",
                  "Microsoft.VSTS.TCM.Steps":"<steps id=\"0\"><step id=\"2\"><parameterizedString isformatted=\"true\">&lt;P&gt;Upload the Good Conduct certificate&lt;/P&gt;</parameterizedString></step></steps>"
                }}]}
                """);

        var found = await Client(handler).FindCandidatesAsync("Employee Appointment Request",
            CancellationToken.None);

        Assert.Contains("Upload the Good Conduct certificate", Assert.Single(found).Text);
    }

    [Fact]
    public async Task Acceptance_criteria_are_read_as_well_as_the_description()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, """{"workItems":[{"id":27629}]}""")
            .Enqueue(HttpStatusCode.OK,
                """
                {"value":[{"id":27629,"fields":{
                  "System.Title":"1.1.7 NPOP- Employee Appointment Request Form- Documents",
                  "Microsoft.VSTS.Common.AcceptanceCriteria":"<p>The Trade Licence is mandatory.</p>"
                }}]}
                """);

        var found = await Client(handler).FindCandidatesAsync("Employee Appointment Request",
            CancellationToken.None);

        Assert.Contains("Trade Licence is mandatory", Assert.Single(found).Text);
    }

    [Fact]
    public async Task A_phrase_nothing_matches_asks_for_no_bodies_at_all()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, """{"workItems":[]}""");

        Assert.Empty(await Client(handler).FindCandidatesAsync("nothing", CancellationToken.None));

        // One call only — the WIQL POST. Asking for the bodies of no work items would be a
        // wasted round trip, and with nothing queued the fake would answer 500.
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// A refused batch is skipped, not allowed to end the search. Some servers answer 400 to a
    /// field a work item type does not have, and asking for fewer fields would lose the
    /// descriptions, which are the point.
    /// </summary>
    [Fact]
    public async Task A_batch_the_server_refuses_does_not_take_the_search_down_with_it()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, """{"workItems":[{"id":1}]}""")
            .Enqueue(HttpStatusCode.BadRequest, """{"message":"TF51005"}""");

        Assert.Empty(await Client(handler).FindCandidatesAsync("anything", CancellationToken.None));
    }
}
