using System.Text;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Verification;
using Xunit;

namespace MocdDocFix.Tests;

public class DeleteCommandTests : IDisposable
{
    private static readonly Guid Correct    = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid OldFileId  = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");
    private static readonly Guid NewFileId  = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");
    private static readonly Guid DocumentId = Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a");

    private const string OldPath = @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg";
    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260910\{NewFileId}.jpg";

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("certificate bytes");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-del-" + Guid.NewGuid());
    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakeCrmWriteClient _write = new();

    private BackupStore Backups() => new(Path.Combine(_root, "backup"));
    private StateStore States() => new(Path.Combine(_root, "state.jsonl"));

    public DeleteCommandTests()
    {
        Directory.CreateDirectory(_root);

        var backups = Backups();
        var saved = backups.Save(DocumentId, OldFileId, ".jpg", Content);
        backups.AppendManifest(new ManifestEntry(DocumentId, OldFileId, OldPath, "VHASH", "cert.jpg",
            "image/jpeg", ".jpg", "goodConductCertificate", Correct, saved.LocalPath,
            saved.Bytes, Verifier.OurHash(Content), DateTimeOffset.UtcNow));

        States().Append(new StateRecord(DocumentId, MigrationState.Repointed,
            DateTimeOffset.UtcNow, NewFileId, NewPath, null));

        _files.Files[OldPath] = (Convert.ToBase64String(Content), "VHASH");
        _files.Files[NewPath] = (Convert.ToBase64String(Content), "VHASH");
        _write.Links[DocumentId] = NewFileId;

        // The new documentfile record, as CRM would return it after the repoint.
        _read.RawRecords[$"mocd_documentfiles:{NewFileId}"] =
            "{\"mocd_filepath\":\"" + NewPath.Replace("\\", "\\\\") + "\"}";

        // And the old one, still holding the path that was backed up. The delete step re-reads it
        // to prove the row it is about to remove is the row the backup captured.
        _read.RawRecords[$"mocd_documentfiles:{OldFileId}"] =
            "{\"mocd_filepath\":\"" + OldPath.Replace("\\", "\\\\") + "\"}";
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private DeleteCommand Command(FakePrompts prompts) =>
        new(_files, _read, _write, Backups(), States(), prompts);

    private static FakePrompts Confirmed() => new() { YesNoResponse = true };

    /// <summary>
    /// The first numbered question in the delete step is now how to work through the list. These
    /// tests are about the question after it, so they take "all of them" and carry on.
    /// </summary>
    private static Queue<string> AllOfThemThen(string answer) => new(new[] { "2", answer });

    [Fact]
    public async Task Deletes_the_old_vendor_file_and_the_old_documentfile_row()
    {
        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Deleted);
        Assert.Contains(OldPath, _files.Deleted);
        Assert.Contains(OldFileId, _write.DeletedFiles);
        Assert.DoesNotContain(NewFileId, _write.DeletedFiles);
        Assert.True(States().IsAtLeast(DocumentId, MigrationState.Deleted));
    }

    [Fact]
    public async Task Answering_no_deletes_nothing()
    {
        var prompts = new FakePrompts { YesNoResponse = false };

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.True(summary.Aborted);
        Assert.Empty(_files.Deleted);
        Assert.Empty(_write.DeletedFiles);
    }

    /// <summary>
    /// The confirmation used to be the word DELETE, typed back exactly. Typing "delete" aborted
    /// a whole run — no file touched, no explanation beyond "did not type DELETE". A question
    /// that punishes the right answer in the wrong case is not a safety feature.
    /// </summary>
    [Fact]
    public async Task The_delete_is_confirmed_with_a_plain_yes_or_no()
    {
        var prompts = Confirmed();

        await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Contains(prompts.Questions, q => q.Contains("Delete 1 old file(s) from dev now?"));
        Assert.DoesNotContain(prompts.Questions, q => q.Contains("DELETE", StringComparison.Ordinal));
    }

    // ---- what is about to be deleted is stated, document by document ----

    /// <summary>
    /// A list of paths is not something anyone can check. What can be checked is the pairing:
    /// for each document, the file and record about to be destroyed beside the file and record
    /// it will be left using. Said after the first yes, while it can still change the answer.
    /// </summary>
    [Fact]
    public async Task Every_document_is_stated_before_anything_is_deleted()
    {
        var prompts = Confirmed();

        await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        var said = string.Join("\n", prompts.Messages);

        Assert.Contains("What will be deleted — 1 document(s)", said);
        Assert.Contains("cert.jpg", said);                    // which file it is
        Assert.Contains(DocumentId.ToString(), said);          // which document
        Assert.Contains($"GOES   file    {OldPath}", said);    // what is destroyed
        Assert.Contains($"GOES   record  {OldFileId}", said);
        Assert.Contains($"STAYS  file    {NewPath}", said);    // and what it will use
        Assert.Contains($"STAYS  record  {NewFileId}", said);
    }

    [Fact]
    public async Task The_statement_comes_after_the_yes_and_before_any_deleting()
    {
        var prompts = Confirmed();

        await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        var briefing = prompts.Messages.FindIndex(m => m.Contains("What will be deleted"));
        var goneFromServer = prompts.Messages.FindIndex(m => m.Contains("Deleted", StringComparison.Ordinal));

        Assert.True(briefing >= 0);
        Assert.True(goneFromServer < 0 || briefing < goneFromServer,
            "the account of what goes must be printed before anything goes");
    }

    [Fact]
    public async Task Stopping_after_reading_the_list_deletes_nothing()
    {
        var prompts = Confirmed();
        prompts.ReadLineQueue = new Queue<string>(new[] { "3" });   // stop

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.True(summary.Aborted);
        Assert.Empty(_files.Deleted);
        Assert.Empty(_write.DeletedFiles);
        Assert.False(States().IsAtLeast(DocumentId, MigrationState.Deleted));
    }

    [Fact]
    public async Task One_at_a_time_asks_again_for_each_document()
    {
        var prompts = new FakePrompts { YesNoQueue = new Queue<bool>(new[] { true, true }) };
        prompts.ReadLineQueue = new Queue<string>(new[] { "1" });   // one at a time

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Deleted);
        Assert.Contains(prompts.Questions, q => q.Contains("Delete this one?"));
    }

    [Fact]
    public async Task Saying_no_to_one_document_leaves_that_one_entirely_alone()
    {
        // Yes to the whole step, no to this document.
        var prompts = new FakePrompts { YesNoQueue = new Queue<bool>(new[] { true, false }) };
        prompts.ReadLineQueue = new Queue<string>(new[] { "1" });   // one at a time

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Skipped);
        Assert.Empty(_files.Deleted);
        Assert.Empty(_write.DeletedFiles);
        Assert.False(summary.Aborted);          // the step ran; this one was simply left
    }

    [Fact]
    public async Task All_of_them_does_not_ask_per_document()
    {
        var prompts = Confirmed();
        prompts.ReadLineQueue = new Queue<string>(new[] { "2" });   // all of them

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Deleted);
        Assert.DoesNotContain(prompts.Questions, q => q.Contains("Delete this one?"));
    }

    [Fact]
    public async Task Documents_not_yet_repointed_are_never_considered()
    {
        States().Append(new StateRecord(Guid.NewGuid(), MigrationState.BackedUp,
            DateTimeOffset.UtcNow, null, null, null));

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Deleted);          // only the repointed one
    }

    [Fact]
    public async Task A_new_file_that_no_longer_downloads_refuses_the_delete()
    {
        _files.Files.Remove(NewPath);

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task A_new_file_whose_content_drifted_refuses_the_delete()
    {
        _files.Files[NewPath] = (Convert.ToBase64String(Encoding.UTF8.GetBytes("DIFFERENT")), "VHASH");

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task A_document_no_longer_pointing_at_the_new_file_refuses_the_delete()
    {
        _write.Links[DocumentId] = OldFileId;      // someone repointed it back

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Production_additionally_requires_typing_the_exact_count()
    {
        // Yes to the delete question, but the count is never typed, so production still aborts.
        var prompts = new FakePrompts { YesNoResponse = true, TypedWordResponse = "whatever" };

        var summary = await Command(prompts).RunAsync("prod", isProduction: true, CancellationToken.None);

        Assert.True(summary.Aborted);
        Assert.Empty(_files.Deleted);
        Assert.Contains(prompts.Questions, q => q.Contains("PRODUCTION", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Already_deleted_documents_are_skipped_on_a_re_run()
    {
        await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);
        _files.Deleted.Clear();

        var second = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, second.Deleted);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Nothing_to_delete_is_reported_without_prompting()
    {
        File.Delete(Path.Combine(_root, "state.jsonl"));
        StillOnTheOldFile();
        var prompts = Confirmed();

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Empty(prompts.Questions);
    }

    // ---- when nothing can be deleted, say why ----

    [Fact]
    public async Task Nothing_to_delete_explains_where_each_document_actually_got_to()
    {
        StillOnTheOldFile();
        States().Append(new StateRecord(DocumentId, MigrationState.Failed,
            DateTimeOffset.UtcNow, NewFileId, NewPath, "file-record-path: the record and the file disagree."));
        var prompts = Confirmed();

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        var said = string.Join("|", prompts.Messages);
        Assert.Equal(0, summary.Deleted);
        Assert.Contains("Nothing is awaiting deletion", said);
        Assert.Contains("Only a document that has been repointed", said);
        Assert.Contains("FAILED, so not eligible", said);
        Assert.Contains("the record and the file disagree", said);   // the actual reason
        Assert.Contains("cert.jpg", said);                            // and which document
    }

    [Fact]
    public async Task Nothing_migrated_at_all_says_so_plainly()
    {
        File.Delete(Path.Combine(_root, "state.jsonl"));
        StillOnTheOldFile();
        var prompts = Confirmed();

        await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Contains("nothing that could be deleted", string.Join("|", prompts.Messages));
    }

    // ---- CRM is the authority; the state file is only a note ----

    /// <summary>
    /// The bug this exists to stop. A run can finish every CRM write and then fail a later check,
    /// recording Failed over work that actually succeeded. Reading only the state file, this step
    /// then finds nothing to do, for ever, and the old file and old record stay behind.
    /// </summary>
    [Fact]
    public async Task A_document_CRM_says_is_done_is_deleted_even_though_the_state_file_says_failed()
    {
        States().Append(new StateRecord(DocumentId, MigrationState.Failed,
            DateTimeOffset.UtcNow, null, null, "check 7 halted the run"));

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Deleted);
        Assert.Contains(OldPath, _files.Deleted);
        Assert.Contains(OldFileId, _write.DeletedFiles);
    }

    [Fact]
    public async Task The_correction_is_announced_rather_than_made_silently()
    {
        States().Append(new StateRecord(DocumentId, MigrationState.Failed,
            DateTimeOffset.UtcNow, null, null, "check 7 halted the run"));

        var prompts = Confirmed();
        await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        var said = string.Join("|", prompts.Messages);
        Assert.Contains("disagreed with what this tool had written down", said);
        Assert.Contains("recorded as   Failed", said);
        Assert.Contains(NewFileId.ToString(), said);
        Assert.Contains("CORRECTED", said);
    }

    [Fact]
    public async Task A_reconciled_document_keeps_the_path_so_the_safety_checks_can_run()
    {
        States().Append(new StateRecord(DocumentId, MigrationState.Failed,
            DateTimeOffset.UtcNow, null, null, "check 7 halted the run"));

        await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        // Recorded as Repointed with BOTH halves, or the delete step refuses with
        // "no new file recorded" — which is how the same document got stuck twice.
        var record = States().LoadLatest()[DocumentId];
        Assert.Equal(MigrationState.Deleted, record.State);
        Assert.Equal(NewFileId, record.NewFileId);
        Assert.Equal(NewPath, record.NewFilePath);
    }

    [Fact]
    public async Task A_document_that_CRM_says_is_still_wrong_is_never_promoted()
    {
        // The document points at a new record, but one filed under the wrong catalogue.
        var wrong = Guid.NewGuid();
        _write.Links[DocumentId] = wrong;
        _read.RawRecords[$"mocd_documentfiles:{wrong}"] =
            "{\"mocd_filepath\":\"DigitalServices\\\\20260910\\\\" + wrong + ".jpg\"}";

        States().Append(new StateRecord(DocumentId, MigrationState.Failed,
            DateTimeOffset.UtcNow, null, null, "check 7 halted the run"));

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Empty(_files.Deleted);
        Assert.Equal(MigrationState.Failed, States().LoadLatest()[DocumentId].State);
    }

    [Fact]
    public async Task A_document_still_on_its_old_file_is_never_promoted()
    {
        StillOnTheOldFile();
        States().Append(new StateRecord(DocumentId, MigrationState.Failed,
            DateTimeOffset.UtcNow, null, null, "upload failed"));

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Empty(_files.Deleted);
        Assert.Empty(_write.DeletedFiles);
    }

    /// <summary>CRM still has the document on its original file — nothing was ever migrated.</summary>
    private void StillOnTheOldFile() => _write.Links[DocumentId] = OldFileId;

    // ---- proving it is about to delete the right thing ----

    /// <summary>
    /// The old record is re-read by the id saved at backup time, and its path must still be the
    /// path saved at backup time. A record edited by hand between the two — which has happened
    /// once in dev — no longer describes the file the backup captured.
    /// </summary>
    [Fact]
    public async Task An_old_record_whose_path_changed_since_the_backup_is_never_deleted()
    {
        _read.RawRecords[$"mocd_documentfiles:{OldFileId}"] =
            "{\"mocd_filepath\":\"DigitalServices\\\\somewhere-else\\\\20260401\\\\other.jpg\"}";

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
        Assert.Empty(_write.DeletedFiles);
    }

    [Fact]
    public async Task The_refusal_says_which_path_it_expected_and_which_it_found()
    {
        _read.RawRecords[$"mocd_documentfiles:{OldFileId}"] =
            "{\"mocd_filepath\":\"DigitalServices\\\\somewhere-else\\\\20260401\\\\other.jpg\"}";

        var prompts = Confirmed();
        await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        var said = string.Join("|", prompts.Messages);
        Assert.Contains("REFUSED", said);
        Assert.Contains("somewhere-else", said);        // what CRM holds now
        Assert.Contains("goodConductCertificate", said); // what the backup recorded
    }

    [Fact]
    public async Task An_old_record_that_has_gone_from_crm_is_never_deleted_by_path_alone()
    {
        _read.RawRecords.Remove($"mocd_documentfiles:{OldFileId}");
        _read.MissingRecords.Add($"mocd_documentfiles:{OldFileId}");

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Empty(_files.Deleted);
    }

    /// <summary>
    /// If the two paths have converged — a hand-edit, or the vendor deduplicating by content —
    /// then "delete the old file" destroys the file the document now depends on. Every other
    /// check passes in that case, because the new file downloads and hashes correctly.
    /// </summary>
    [Fact]
    public async Task A_new_file_at_the_same_path_as_the_old_one_is_never_deleted()
    {
        States().Append(new StateRecord(DocumentId, MigrationState.Repointed,
            DateTimeOffset.UtcNow, NewFileId, OldPath, null));

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
        Assert.Empty(_write.DeletedFiles);
    }

    [Fact]
    public async Task The_old_and_new_documentfile_being_one_record_is_never_deleted()
    {
        _write.Links[DocumentId] = OldFileId;
        States().Append(new StateRecord(DocumentId, MigrationState.Repointed,
            DateTimeOffset.UtcNow, OldFileId, NewPath, null));

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Empty(_write.DeletedFiles);
    }

    /// <summary>
    /// The shared-path question can itself delete the old CRM record, so it must not be asked
    /// until the record has been proved to be the one that was backed up.
    /// </summary>
    [Fact]
    public async Task A_stale_identity_is_caught_before_the_shared_path_question_is_asked()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId, Guid.NewGuid() };
        _read.RawRecords[$"mocd_documentfiles:{OldFileId}"] =
            "{\"mocd_filepath\":\"DigitalServices\\\\somewhere-else\\\\20260401\\\\other.jpg\"}";

        var prompts = Confirmed();
        prompts.ReadLineQueue = AllOfThemThen("2");   // then: would delete the CRM record

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Empty(_write.DeletedFiles);
        Assert.DoesNotContain(prompts.Questions, q => q.Contains("What should happen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_identity_check_is_made_before_anything_is_removed()
    {
        _read.RawRecords[$"mocd_documentfiles:{OldFileId}"] =
            "{\"mocd_filepath\":\"DigitalServices\\\\somewhere-else\\\\20260401\\\\other.jpg\"}";

        await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        // Not even the file-server delete was attempted.
        Assert.Empty(_files.Deleted);
    }

    // ---- the delete step shows its working ----

    // ---- a file that more than one record points at ----

    [Fact]
    public async Task A_shared_file_is_never_deleted()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId, Guid.NewGuid() };
        var prompts = Confirmed();
        prompts.ReadLineQueue = AllOfThemThen("1");   // then: leave everything

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Empty(_files.Deleted);
        Assert.Empty(_write.DeletedFiles);
        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
    }

    [Fact]
    public async Task A_shared_file_is_explained_before_the_question()
    {
        var other = Guid.NewGuid();
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId, other };
        var prompts = Confirmed();
        prompts.ReadLineQueue = AllOfThemThen("1");

        await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        var said = string.Join("|", prompts.Messages);
        Assert.Contains("PROBLEM", said);
        Assert.Contains(other.ToString(), said);
        Assert.Contains("would leave those records pointing at nothing", said);
    }

    [Fact]
    public async Task The_operator_can_remove_only_the_old_crm_record_and_keep_the_shared_file()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId, Guid.NewGuid() };
        var prompts = Confirmed();
        prompts.ReadLineQueue = AllOfThemThen("2");   // then: CRM record only

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Empty(_files.Deleted);                    // the file is kept for the others
        Assert.Contains(OldFileId, _write.DeletedFiles); // our old row is gone
        Assert.Equal(1, summary.Deleted);
    }

    [Fact]
    public async Task A_file_nobody_else_points_at_is_deleted_as_usual()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId };   // only us

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Contains(OldPath, _files.Deleted);
        Assert.Equal(1, summary.Deleted);
    }

    [Fact]
    public async Task The_log_says_the_file_was_found_before_it_was_deleted()
    {
        var prompts = Confirmed();

        await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        var log = string.Join("|", prompts.Messages);
        Assert.Contains("before      found on the server", log);
        Assert.Contains("delete      the file server accepted", log);
        Assert.Contains("after       confirmed gone from the server", log);
        Assert.Contains("CRM         mocd_documentfile", log);
        Assert.Contains("RESULT      done", log);
    }

    [Fact]
    public async Task A_file_already_absent_is_said_so_and_the_crm_record_still_goes()
    {
        _files.Files.Remove(OldPath);
        var prompts = Confirmed();

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Contains("before      NOT on the server", string.Join("|", prompts.Messages));
        Assert.Contains(OldFileId, _write.DeletedFiles);
        Assert.Equal(1, summary.Deleted);
    }

    [Fact]
    public async Task A_server_that_says_deleted_but_keeps_the_file_is_caught()
    {
        _files.PretendToDelete = true;
        var prompts = Confirmed();

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Contains("STILL ON THE SERVER", string.Join("|", prompts.Messages));
        Assert.Empty(_write.DeletedFiles);      // the CRM record is kept, so the two agree
    }

    [Fact]
    public async Task A_refused_delete_leaves_the_crm_record_in_place()
    {
        _files.DeleteRefusal = "access denied";
        var prompts = Confirmed();

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Contains("the file server refused: access denied", string.Join("|", prompts.Messages));
        Assert.Empty(_write.DeletedFiles);
    }
}
