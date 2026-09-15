using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Weighing what DevOps says against what CRM says. Every case here was measured against the
/// live backlog first — the counts and titles are real.
/// </summary>
public class DocumentTypeAuthorityTests
{
    private const string Emap = "Employee Appointment Request";

    private static AdoHit Hit(int id, string title) =>
        new(id, title, DocumentTypeAuthority.ServiceInTitle(title));

    // ---- reading the service off a title ----

    [Fact]
    public void The_service_is_the_first_segment_that_is_not_a_channel_or_a_step()
    {
        Assert.Equal("Employee Appointment Request", DocumentTypeAuthority.ServiceInTitle(
            "NPOP|Employee Appointment Request|Documents|Verify \"A copy of the certificate\""));

        Assert.Equal("Register New Member", DocumentTypeAuthority.ServiceInTitle(
            "Portal | NPOP- Register New Member | Documents | Verify when the file is too large"));

        Assert.Equal("Confirm Employment", DocumentTypeAuthority.ServiceInTitle(
            "Portal | Confirm Employment | Verify medical examination certificate is required"));

        Assert.Equal("By-Laws Amendment", DocumentTypeAuthority.ServiceInTitle(
            "CRM | By-Laws Amendment | FAHR Integration - Workflow Type 1 | Verify no placeholder"));
    }

    [Fact]
    public void A_title_that_names_no_service_yields_nothing_rather_than_a_guess()
    {
        Assert.Null(DocumentTypeAuthority.ServiceInTitle("Verify the upload works"));
        Assert.Null(DocumentTypeAuthority.ServiceInTitle("Portal | Verify | Documents"));
        Assert.Null(DocumentTypeAuthority.ServiceInTitle(""));
        Assert.Null(DocumentTypeAuthority.ServiceInTitle(null));
    }

    // ---- what to search for ----

    /// <summary>
    /// CRM's full phrasing finds nothing — "A copy of the certificate of good conduct" returned
    /// zero work items live, while "good conduct" returned four. So the phrase has to relax.
    /// </summary>
    [Fact]
    public void The_search_relaxes_into_shorter_phrases_that_really_appear()
    {
        var terms = DocumentTypeAuthority.SearchTerms("A Copy of Certificate of Good Conduct and Behavior");

        Assert.Equal("A Copy of Certificate of Good Conduct and Behavior", terms[0]);
        Assert.True(terms.Count >= 3, "it should get shorter, not just try the one phrasing");

        // Every term is a run of words out of the original, in order. WIQL CONTAINS matches a
        // run of characters, so a reshuffled bag of words — "Certificate Behavior" — is a phrase
        // that appears in no title anywhere, and searching it found nothing at all.
        foreach (var term in terms)
            Assert.Contains(term, "A Copy of Certificate of Good Conduct and Behavior");
    }

    /// <summary>
    /// The phrase that matters: CRM's full wording finds nothing live, and "Good Conduct" finds
    /// four work items. It has to be among the terms tried, or the check is silent on a document
    /// type DevOps can actually answer for.
    /// </summary>
    [Fact]
    public void A_phrase_devops_really_uses_is_among_the_terms_tried()
    {
        var terms = DocumentTypeAuthority.SearchTerms("A Copy of Certificate of Good Conduct and Behavior");

        Assert.Contains("Good Conduct", terms);
    }

    [Fact]
    public void No_term_begins_or_ends_on_a_word_that_carries_nothing()
    {
        foreach (var term in DocumentTypeAuthority.SearchTerms("A Copy of Academic Qualification Certificate"))
        {
            Assert.False(term.StartsWith("of ", StringComparison.OrdinalIgnoreCase));
            Assert.False(term.EndsWith(" of", StringComparison.OrdinalIgnoreCase));
            Assert.False(term.EndsWith(" and", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// "FAHR" alone matches 694 work items. A bare short word is an acronym or a category, never
    /// an identity, so it is never searched for.
    /// </summary>
    [Fact]
    public void A_single_short_word_is_never_searched_for()
    {
        Assert.Empty(DocumentTypeAuthority.SearchTerms("FAHR"));
        Assert.Empty(DocumentTypeAuthority.SearchTerms("Document"));
        Assert.Empty(DocumentTypeAuthority.SearchTerms(""));
        Assert.Empty(DocumentTypeAuthority.SearchTerms(null));
    }

    // ---- comparing two names for the same service ----

    [Fact]
    public void The_same_service_written_two_ways_still_matches()
    {
        Assert.True(DocumentTypeAuthority.SameService("Employee Appointment Request",
                                                      "employee appointment request"));
        Assert.True(DocumentTypeAuthority.SameService("Employee Appointment Request",
                                                      "Employee Appointment"));
        Assert.True(DocumentTypeAuthority.SameService("By-Laws Amendment", "By Laws Amendment"));
    }

    [Fact]
    public void Two_different_services_never_match_and_neither_does_nothing()
    {
        Assert.False(DocumentTypeAuthority.SameService("Employee Appointment Request", "By-Laws Amendment"));
        Assert.False(DocumentTypeAuthority.SameService("Employee Appointment Request", null));
        Assert.False(DocumentTypeAuthority.SameService(null, "Employee Appointment Request"));
        Assert.False(DocumentTypeAuthority.SameService("GAM", "GAM Request"));   // too short to swallow
    }

    // ---- the verdict ----

    [Fact]
    public void Agreeing_hits_confirm_what_crm_says()
    {
        var opinion = DocumentTypeAuthority.Weigh("medical examination certificate", Emap, new[]
        {
            Hit(34143, "NPOP|Employee Appointment Request|Documents| Verify the medical examination certificate"),
            Hit(34867, "Portal | Confirm Employment | Verify medical examination certificate"),
            Hit(37054, "Portal | NPOP- Edit Employee Details | Verify the medical examination certificate")
        });

        Assert.Equal(AdoVerdict.Agrees, opinion.Verdict);
        Assert.Equal(Emap, opinion.Service);
    }

    /// <summary>
    /// The case the whole design turns on. Live, "FAHR Document" matches exactly one work item —
    /// a By-Laws test named "Verify no FAHR document placeholder is created" — while CRM says the
    /// type belongs to Employee Appointment Request. One passing mention must never overrule two
    /// CRM authorities, so this is put to the operator rather than called a conflict.
    /// </summary>
    [Fact]
    public void A_single_passing_mention_never_contradicts_crm()
    {
        var opinion = DocumentTypeAuthority.Weigh("FAHR Document", Emap, new[]
        {
            Hit(56630, "CRM | By-Laws Amendment | FAHR Integration - Workflow Type 1 | " +
                       "Verify no FAHR document placeholder is created")
        });

        Assert.Equal(AdoVerdict.CannotTell, opinion.Verdict);
        Assert.Null(opinion.Service);
        Assert.Contains("too thin", opinion.Detail);
    }

    [Fact]
    public void Two_or_more_hits_agreeing_on_another_service_is_a_real_disagreement()
    {
        var opinion = DocumentTypeAuthority.Weigh("Board Decision", Emap, new[]
        {
            Hit(1, "CRM | By-Laws Amendment | Documents | Verify the board decision is attached"),
            Hit(2, "Portal | By-Laws Amendment | Documents | Verify the board decision upload")
        });

        Assert.Equal(AdoVerdict.Disagrees, opinion.Verdict);
        Assert.Equal("By-Laws Amendment", opinion.Service);
        Assert.Contains("By-Laws Amendment", opinion.Detail);
        Assert.Contains(Emap, opinion.Detail);
    }

    [Fact]
    public void One_agreeing_hit_is_enough_to_agree_even_among_others()
    {
        // Agreeing with CRM needs no extra weight: it changes nothing and halts nothing.
        var opinion = DocumentTypeAuthority.Weigh("Good Conduct", Emap, new[]
        {
            Hit(1, "Portal | NPOP- Register New Member | Documents | Verify good conduct"),
            Hit(2, "NPOP|Employee Appointment Request|Documents|Verify good conduct")
        });

        Assert.Equal(AdoVerdict.Agrees, opinion.Verdict);
    }

    [Fact]
    public void Nothing_found_is_said_plainly_rather_than_treated_as_agreement()
    {
        var opinion = DocumentTypeAuthority.Weigh("Attendance Sheet", Emap, Array.Empty<AdoHit>());

        Assert.Equal(AdoVerdict.CannotTell, opinion.Verdict);
        Assert.Contains("no work item", opinion.Detail);
    }

    [Fact]
    public void Hundreds_of_hits_mean_the_name_was_too_general_to_say_anything()
    {
        var many = Enumerable.Range(1, 694)
            .Select(i => Hit(i, "CRM | By-Laws Amendment | FAHR Integration | Verify something"))
            .ToList();

        var opinion = DocumentTypeAuthority.Weigh("FAHR", Emap, many);

        Assert.Equal(AdoVerdict.CannotTell, opinion.Verdict);
        Assert.Contains("too many", opinion.Detail);
        Assert.True(opinion.Evidence.Count <= 5, "the report must not carry 694 titles");
    }

    [Fact]
    public void Hits_whose_titles_name_no_service_cannot_settle_anything()
    {
        var opinion = DocumentTypeAuthority.Weigh("Trade Licence", Emap, new[]
        {
            Hit(1, "Verify the trade licence upload"),
            Hit(2, "Verify the trade licence size")
        });

        Assert.Equal(AdoVerdict.CannotTell, opinion.Verdict);
        Assert.Contains("which service", opinion.Detail);
    }

    // ---- what we search the backlog for ----

    [Fact]
    public void The_name_with_its_noisy_ends_trimmed_is_the_second_thing_we_try()
    {
        var terms = DocumentTypeAuthority.SearchTerms("A Copy of Board of Director's Decision");

        Assert.Equal("A Copy of Board of Director's Decision", terms[0]);
        Assert.Equal("Board of Director's Decision", terms[1]);
    }

    [Fact]
    public void A_name_with_nothing_noisy_on_its_ends_is_not_searched_for_twice()
    {
        var terms = DocumentTypeAuthority.SearchTerms("Board of Director's Decision");

        Assert.Equal("Board of Director's Decision", terms[0]);
        Assert.DoesNotContain(terms.Skip(1), t =>
            t.Equals("Board of Director's Decision", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_name_that_is_nothing_but_noise_still_searches_for_something_or_nothing_safely()
    {
        // "A copy of the document" trims away to nothing. It must not throw, and it must not
        // produce an empty search term that would match every work item in the project.
        var terms = DocumentTypeAuthority.SearchTerms("A copy of the document");

        Assert.DoesNotContain(terms, string.IsNullOrWhiteSpace);
    }

    // ---- the order-free matcher over story bodies and workbooks ----

    [Fact]
    public void The_words_that_carry_identity_survive_and_the_noise_does_not()
    {
        Assert.Equal(new[] { "good", "conduct", "life" },
            DocumentTypeAuthority.MeaningfulWords("a good conduct life"));

        // "Copy" and "of" are noise; "A" and the possessive "s" are too short to identify anything.
        Assert.Equal(new[] { "board", "director", "decision" },
            DocumentTypeAuthority.MeaningfulWords("A Copy of Board of Director's Decision"));
    }

    [Fact]
    public void A_name_written_in_another_order_with_other_words_between_is_still_a_mention()
    {
        const string body =
            "The system shall display the below list of documents. " +
            "Certificate of good conduct and behavior, valid for the life of the appointment.";

        Assert.True(DocumentTypeAuthority.Mentions(body, "a good conduct life"));
    }

    [Fact]
    public void The_same_words_scattered_across_a_whole_file_are_not_a_mention()
    {
        // Exactly the words, far enough apart that they are three unrelated cells of a workbook
        // rather than one phrase. Without the window this is the false positive that would fire
        // on almost any shared string table.
        var body = "good " + new string('x', DocumentTypeAuthority.NearbyWindow) +
                   " conduct " + new string('y', DocumentTypeAuthority.NearbyWindow) + " life";

        Assert.False(DocumentTypeAuthority.Mentions(body, "a good conduct life"));
    }

    [Fact]
    public void Punctuation_and_case_are_not_allowed_to_hide_a_mention()
    {
        Assert.True(DocumentTypeAuthority.Mentions(
            "Upload the BOARD OF DIRECTORS' DECISION here.",
            "A Copy of Board of Director's Decision"));
    }

    [Fact]
    public void A_name_with_one_meaningful_word_falls_back_to_looking_for_the_name_itself()
    {
        // One word is not a set — "passport" alone would match any sentence mentioning a
        // passport, so the whole phrase has to appear.
        Assert.True(DocumentTypeAuthority.Mentions("Attach the passport copy.", "Passport Copy"));
        Assert.False(DocumentTypeAuthority.Mentions("Attach the passport.", "Passport Copy"));
    }

    /// <summary>
    /// CRM writes "Board of Director's Decision" and the backlog writes "Board of Directors'
    /// Decision". Refusing to match on a plural would be the same miss this whole matcher exists
    /// to stop, so a trailing "s" is taken off both sides before they are compared.
    /// </summary>
    [Fact]
    public void A_plural_and_a_singular_are_the_same_word()
    {
        Assert.True(DocumentTypeAuthority.Mentions(
            "Attach the board of directors decision.", "Board of Director's Decision"));

        Assert.True(DocumentTypeAuthority.Mentions(
            "Attach the board of director decision.", "Board of Directors' Decision"));

        // "address" was never a plural, and must not become "addres".
        Assert.True(DocumentTypeAuthority.Mentions(
            "The registered address proof is required.", "Address Proof"));
    }

    [Fact]
    public void A_word_is_not_answered_by_a_longer_word_that_contains_it()
    {
        Assert.False(DocumentTypeAuthority.Mentions(
            "Reported misconduct by a director of the board.", "Board Director Conduct"));
    }

    [Fact]
    public void Nothing_to_match_against_is_never_a_mention()
    {
        Assert.False(DocumentTypeAuthority.Mentions(null, "Board Decision"));
        Assert.False(DocumentTypeAuthority.Mentions("", "Board Decision"));
        Assert.False(DocumentTypeAuthority.Mentions("Board Decision", null));
        Assert.False(DocumentTypeAuthority.Mentions("Board Decision", "   "));
    }
}
