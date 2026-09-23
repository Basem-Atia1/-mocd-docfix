using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

[Collection(LedgerCollection.Name)]
public class RepairRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-run-" + Guid.NewGuid());

    private static readonly Guid Correct = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");

    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakeCrmWriteClient _write = new();
    private readonly FakePrompts _prompts = new();
    private readonly NullOpener _opener = new();
    private readonly BackupStore _backups;
    private readonly ChangeJournal _journal;
    private readonly LedgerStore _ledger;
    private readonly ErrorLog _errors;

    public RepairRunTests()
    {
        Directory.CreateDirectory(_dir);
        _backups = new BackupStore(Path.Combine(_dir, "backup"));
        _journal = new ChangeJournal(Path.Combine(_dir, "changes-dev.jsonl"));
        _ledger = new LedgerStore(Path.Combine(_dir, "repair-dev.xlsx"));
        _errors = new ErrorLog(Path.Combine(_dir, "errors-dev.txt"));

        // CRM would hold what was just written; the fakes are independent, so mirror it.
        _write.OnUpdated = (id, attributes) =>
            _read.RawRecords[$"mocd_documentfiles:{id}"] =
                $$"""{"mocd_documentfileid":"{{id}}","mocd_filepath":"{{
                    ((string?)attributes["mocd_filepath"] ?? "").Replace("\\", "\\\\")}}"}""";
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class NullOpener : IFileOpener { public void Open(string path) { } }

    /// <summary>Gives a row a real old file on the server and a record CRM can answer about.</summary>
    private LedgerRow Fixable(int number)
    {
        var doc = Guid.Parse($"a3f1b2c4-0000-0000-0000-{number:D12}");
        var record = Guid.Parse($"2a1c51a3-0000-0000-0000-{number:D12}");
        var newId = Guid.Parse($"b2c3d4e5-0000-0000-0000-{number:D12}");
        var oldPath = $@"DigitalServices\docTypeCatalogue\20250509\a3f10000-0000-0000-0000-{number:D12}.jpg";
        var newPath = $@"DigitalServices\{Correct}\20260915\{newId}.jpg";
        var bytes = new byte[] { 1, 2, 3, (byte)number };
        var base64 = Convert.ToBase64String(bytes);

        _files.Files[oldPath] = (base64, "hash" + number);
        _files.Files[newPath] = (base64, "hash" + number);
        _read.RawRecords[$"mocd_documentfiles:{record}"] =
            $$"""{"mocd_documentfileid":"{{record}}","mocd_mediatype":"image/jpeg"}""";

        return new LedgerRow
        {
            Row = number,
            DocId = doc,
            DocName = "Document " + number,
            DocFileId = record,
            DocFileName = $"cert{number}.jpg",
            CorrectServiceCatalogueId = Correct.ToString(),
            OldFilePath = oldPath,
            OldHash = "hash" + number,
            OldCategory = "docTypeCatalogue",
            Verdict = RowVerdicts.Fix,
            WayOfUpload = "portal"
        };
    }

    /// <summary>The upload responder has to answer differently per row, keyed on the bytes.</summary>
    private void UploadsSucceed() =>
        _files.UploadResponder = request =>
        {
            var number = Convert.FromBase64String(request.File)[3];
            var newId = Guid.Parse($"b2c3d4e5-0000-0000-0000-{number:D12}");
            var newPath = $@"DigitalServices\{Correct}\20260915\{newId}.jpg";

            return new ApiResponse<FileData>(true, null,
                new FileData(newId, newPath, "hash" + number, $"{newId}.jpg", "image/jpeg", null), null);
        };

    private RepairRun Subject(WatchMode mode = WatchMode.Unattended)
    {
        var progress = new RunProgress(_prompts, mode);

        return new RepairRun(_ledger,
            new RepairOneRow(_files, _read, _write, _backups, _journal, _prompts, _opener, progress, "dev"),
            progress, _prompts, _errors);
    }

    [Fact]
    public async Task Every_fix_row_is_corrected_and_counted()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2), Fixable(3) };

        var summary = await Subject().RunAsync(rows, rows, CancellationToken.None);

        Assert.Equal(3, summary.Corrected);
        Assert.Equal(0, summary.Failed);
        Assert.False(summary.Stopped);
        Assert.All(rows, r => Assert.Equal(RowState.Corrected, r.State()));
    }

    /// <summary>
    /// The ledger is rewritten as each row finishes, not once at the end. An interruption must
    /// lose at most the row in flight.
    /// </summary>
    [Fact]
    public async Task The_ledger_on_disk_is_current_after_every_row()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2) };

        await Subject().RunAsync(rows, rows, CancellationToken.None);

        var onDisk = _ledger.Read();
        Assert.Equal(2, onDisk.Count);
        Assert.All(onDisk, r => Assert.Equal(RowState.Corrected, r.State()));
        Assert.All(onDisk, r => Assert.NotEqual(string.Empty, r.NewFilePath));
    }

    [Theory]
    [InlineData(RowVerdicts.Review)]
    [InlineData(RowVerdicts.Done)]
    [InlineData(RowVerdicts.Ignore)]
    [InlineData(RowVerdicts.Redo)]
    public async Task Only_fix_rows_are_touched(string verdict)
    {
        UploadsSucceed();
        var row = Fixable(1);
        row.Verdict = verdict;

        var summary = await Subject().RunAsync(new[] { row }, new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Corrected);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Empty(_files.Uploads);
        Assert.Equal(RowState.NotStarted, row.State());
    }

    /// <summary>
    /// The safety rule made visible. A typo is not acted on, and it is not silent either — it is
    /// named at the end so the operator finds out before assuming the row was done.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_verdict_is_left_alone_and_reported()
    {
        UploadsSucceed();
        var row = Fixable(1);
        row.Verdict = "fixx";

        var summary = await Subject().RunAsync(new[] { row }, new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Corrected);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Contains(summary.Unrecognised, u => u.Contains("fixx"));
    }

    /// <summary>A row already done is not done again, however its verdict still reads.</summary>
    [Fact]
    public async Task A_row_already_corrected_is_not_uploaded_a_second_time()
    {
        UploadsSucceed();
        var row = Fixable(1);
        row.FinalState = RowStates.Text(RowState.Corrected);

        var summary = await Subject().RunAsync(new[] { row }, new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Corrected);
        Assert.Empty(_files.Uploads);
    }

    [Fact]
    public async Task A_failure_marks_the_row_writes_the_error_log_and_asks()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2) };
        _files.Files.Remove(rows[0].OldFilePath);                 // row 1 cannot be backed up
        _prompts.YesNoQueue = new Queue<bool>(new[] { true });    // carry on

        var summary = await Subject().RunAsync(rows, rows, CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Corrected);
        Assert.Equal(RowState.Failed, rows[0].State());
        Assert.NotEqual(string.Empty, rows[0].Error);
        Assert.True(File.Exists(_errors.Path));
        Assert.Contains("cert1.jpg", File.ReadAllText(_errors.Path));
        Assert.Contains(_prompts.Questions, q => q.Contains("Carry on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Saying stop ends the run there, with everything before it recorded.</summary>
    [Fact]
    public async Task Answering_stop_ends_the_run_and_leaves_the_earlier_rows_intact()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2), Fixable(3) };
        _files.Files.Remove(rows[1].OldFilePath);                  // row 2 breaks
        _prompts.YesNoQueue = new Queue<bool>(new[] { false });    // stop

        var summary = await Subject().RunAsync(rows, rows, CancellationToken.None);

        Assert.True(summary.Stopped);
        Assert.Equal(1, summary.Corrected);
        Assert.Equal(RowState.Corrected, rows[0].State());
        Assert.Equal(RowState.Failed, rows[1].State());
        Assert.Equal(RowState.NotStarted, rows[2].State());

        // And the ledger says the same, because it was written as each row finished.
        Assert.Equal(RowState.Corrected,
            _ledger.Read().Single(r => r.DocId == rows[0].DocId).State());
    }

    /// <summary>Unattended is unattended, not unstoppable.</summary>
    [Fact]
    public async Task Unattended_still_halts_on_a_failure_and_asks()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1) };
        _files.Files.Remove(rows[0].OldFilePath);
        _prompts.YesNoQueue = new Queue<bool>(new[] { false });

        var summary = await Subject(WatchMode.Unattended).RunAsync(rows, rows, CancellationToken.None);

        Assert.True(summary.Stopped);
        Assert.NotEmpty(_prompts.Questions);
    }

    /// <summary>
    /// The choice is asked once, before the loop. Whichever was chosen, the same input must
    /// produce the same ledger — the mode changes what is printed, never what is written.
    /// </summary>
    [Fact]
    public async Task Quiet_and_unattended_write_the_same_ledger_for_the_same_input()
    {
        UploadsSucceed();
        var unattended = new[] { Fixable(1), Fixable(2) };
        await Subject(WatchMode.Unattended).RunAsync(unattended, unattended, CancellationToken.None);
        var first = _ledger.Read().Select(r => (r.Row, r.FinalState, r.NewFilePath)).ToList();

        File.Delete(_ledger.Path);
        _files.Uploads.Clear();
        _write.UpdatedFiles.Clear();
        _prompts.Answer(ConfirmChoice.Yes, ConfirmChoice.Yes);

        var quiet = new[] { Fixable(1), Fixable(2) };
        await Subject(WatchMode.Quiet).RunAsync(quiet, quiet, CancellationToken.None);

        Assert.Equal(first, _ledger.Read().Select(r => (r.Row, r.FinalState, r.NewFilePath)).ToList());
    }

    [Fact]
    public async Task A_declined_eye_check_is_counted_apart_from_a_failure()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1) };
        _prompts.Answer(ConfirmChoice.No);

        var summary = await Subject(WatchMode.Quiet).RunAsync(rows, rows, CancellationToken.None);

        Assert.Equal(1, summary.Declined);
        Assert.Equal(0, summary.Failed);
        Assert.False(summary.Stopped);
        Assert.Equal(RowState.NotStarted, rows[0].State());
    }

    /// <summary>
    /// Working on one document must not truncate the ledger. The loop rewrites the file after
    /// every row, so if it wrote only what it was iterating, choosing one document out of four
    /// hundred would destroy the other three hundred and ninety-nine — and with them the only
    /// record of which old files are still waiting to be deleted.
    /// </summary>
    [Fact]
    public async Task Working_on_one_document_leaves_every_other_row_in_the_ledger()
    {
        UploadsSucceed();
        var all = new[] { Fixable(1), Fixable(2), Fixable(3) };
        var justOne = new[] { all[1] };

        var summary = await Subject().RunAsync(justOne, all, CancellationToken.None);

        Assert.Equal(1, summary.Corrected);

        // By doc id, not position: a corrected row sinks to the bottom of the sheet, and the
        // row number is positional by design.
        var onDisk = _ledger.Read();
        Assert.Equal(3, onDisk.Count);
        Assert.Equal(RowState.NotStarted, onDisk.Single(r => r.DocId == all[0].DocId).State());
        Assert.Equal(RowState.Corrected, onDisk.Single(r => r.DocId == all[1].DocId).State());
        Assert.Equal(RowState.NotStarted, onDisk.Single(r => r.DocId == all[2].DocId).State());
    }

    /// <summary>
    /// Watch asks after every document, and no means no. Before this the pause took any
    /// keystroke as "carry on", so an operator who typed "no" watched the run continue anyway.
    /// </summary>
    [Fact]
    public async Task Saying_no_between_documents_stops_the_run_there()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2), Fixable(3) };

        _prompts.Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);
        _prompts.YesNoQueue = new Queue<bool>(new[] { false });   // no, do not carry on

        var summary = await Subject(WatchMode.Watch).RunAsync(rows, rows, CancellationToken.None);

        Assert.True(summary.Stopped);
        Assert.Equal(1, summary.Corrected);
        Assert.Equal(RowState.Corrected, rows[0].State());
        Assert.Equal(RowState.NotStarted, rows[1].State());
        Assert.Equal(RowState.NotStarted, rows[2].State());
    }

    /// <summary>
    /// Quiet never interrupts, so the way out is a keypress. Whenever it is pressed, the
    /// document in progress is finished and recorded before the run ends — nothing is left
    /// half done, and nothing after it is started.
    /// </summary>
    [Fact]
    public async Task Pressing_q_in_quiet_finishes_the_document_in_progress_then_stops()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2), Fixable(3) };

        _prompts.Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

        // Pressed while document 1 was uploading — so it is noticed at the check that runs
        // before that document's own eye-check, not at a boundary.
        _prompts.StopRequests = new Queue<bool>(new[] { true });

        var summary = await Subject(WatchMode.Quiet).RunAsync(rows, rows, CancellationToken.None);

        Assert.True(summary.Stopped);

        // Finished, not abandoned: uploaded, CRM updated, recorded.
        Assert.Equal(1, summary.Corrected);
        Assert.Equal(RowState.Corrected, rows[0].State());
        Assert.Single(_write.UpdatedFiles);
        Assert.Equal(RowState.Corrected,
            _ledger.Read().Single(r => r.DocId == rows[0].DocId).State());

        // And nothing after it was begun.
        Assert.Equal(RowState.NotStarted, rows[1].State());
        Assert.Single(_files.Uploads);
    }

    /// <summary>
    /// Unattended has no question to intercept the keystroke, so it is noticed at the boundary
    /// — with the same result: the document in progress is complete.
    /// </summary>
    [Fact]
    public async Task Pressing_q_in_unattended_also_finishes_the_document_first()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2) };
        _prompts.StopRequests = new Queue<bool>(new[] { true });

        var summary = await Subject(WatchMode.Unattended).RunAsync(rows, rows, CancellationToken.None);

        Assert.True(summary.Stopped);
        Assert.Equal(1, summary.Corrected);
        Assert.Equal(RowState.Corrected, rows[0].State());
        Assert.Equal(RowState.NotStarted, rows[1].State());
    }

    /// <summary>
    /// The hole this closes: a q pressed while a document was uploading was swallowed by the
    /// eye-check prompt, read as "the copies do not match", and the run carried on to the next
    /// document with the keystroke already consumed.
    /// </summary>
    [Fact]
    public async Task Quitting_at_the_eye_check_stops_the_run_rather_than_skipping_one_document()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2), Fixable(3) };

        _prompts.Answer(ConfirmChoice.Quit, ConfirmChoice.Yes, ConfirmChoice.Yes);

        var summary = await Subject(WatchMode.Quiet).RunAsync(rows, rows, CancellationToken.None);

        Assert.True(summary.Stopped);
        Assert.Equal(0, summary.Corrected);
        Assert.Equal(0, summary.Declined);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Equal(RowState.NotStarted, rows[1].State());
    }

    [Fact]
    public async Task An_empty_ledger_is_not_an_error()
    {
        var summary = await Subject().RunAsync(
            Array.Empty<LedgerRow>(), Array.Empty<LedgerRow>(), CancellationToken.None);

        Assert.Equal(0, summary.Corrected);
        Assert.False(summary.Stopped);
        Assert.Empty(_prompts.Questions);
    }

    /// <summary>
    /// The counter is over the work, not over the sheet.
    ///
    /// It used to run over every row in the ledger, so a run with two documents to correct among
    /// five rows announced "[ 1/5 ]" — a number that says nothing about how far through the work
    /// you are, and reads as more of it than there is.
    /// </summary>
    [Fact]
    public async Task The_counter_is_over_the_rows_that_will_be_worked_on()
    {
        UploadsSucceed();

        LedgerRow PassedOver(int number, string verdict)
        {
            var row = Fixable(number);
            row.Verdict = verdict;
            return row;
        }

        var rows = new List<LedgerRow>
        {
            Fixable(1),
            PassedOver(2, RowVerdicts.Review),
            PassedOver(3, RowVerdicts.Ignore),
            Fixable(4),
            PassedOver(5, RowVerdicts.Done)
        };

        _prompts.YesNoResponse = true;
        _prompts.Answer(ConfirmChoice.Yes, ConfirmChoice.Yes);

        await Subject().RunAsync(rows, rows, CancellationToken.None);

        var said = string.Join("\n", _prompts.Messages);

        Assert.Contains("[ 1/2 ]", said);
        Assert.Contains("[ 2/2 ]", said);
        Assert.DoesNotContain("/5 ]", said);
    }

    /// <summary>
    /// The rows the loop walks past get no line of their own, in any mode.
    ///
    /// A run over 2,478 rows with 668 to fix printed 1,810 of them before touching the first
    /// document. The tally in the summary says the same thing in one line per reason.
    /// </summary>
    [Theory]
    [InlineData(WatchMode.Quiet)]
    [InlineData(WatchMode.Unattended)]
    public async Task A_row_that_is_walked_past_leaves_no_trace_in_the_run(WatchMode mode)
    {
        var ignored = Fixable(1);
        ignored.Verdict = RowVerdicts.Ignore;

        var reviewed = Fixable(2);
        reviewed.Verdict = RowVerdicts.Review;

        var rows = new[] { ignored, reviewed };

        var summary = await Subject(mode).RunAsync(rows, rows, CancellationToken.None);

        // Not a line while it runs, and not a tally at the end. "642 — its verdict is ignore"
        // under a run that corrected five is a bigger number than anything the run did, about
        // rows it was never going to touch. What is in the sheet is the sheet's to say.
        Assert.Equal(0, summary.Corrected);
        Assert.Empty(_files.Uploads);
        Assert.DoesNotContain(_prompts.Messages, m =>
            m.Contains("skipped", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("its verdict is", StringComparison.OrdinalIgnoreCase));
    }

    // ---- rows that share a document file record ----
    //
    // One mocd_documentfile can be the file of several mocd_document records, so correcting one
    // row moves the file under every row that shares it. Those rows stop being work the moment
    // the correction lands, and the run says so rather than letting them go green in silence.

    /// <summary>A second document pointing at the same record as <paramref name="of"/>.</summary>
    private static LedgerRow Sibling(LedgerRow of, int number)
    {
        var row = new LedgerRow
        {
            Row = number,
            DocId = Guid.Parse($"c4d5e6f7-0000-0000-0000-{number:D12}"),
            DocName = "Document " + number,
            DocFileId = of.DocFileId,
            DocFileName = of.DocFileName,
            CorrectServiceCatalogueId = of.CorrectServiceCatalogueId,
            OldFilePath = of.OldFilePath,
            OldHash = of.OldHash,
            OldCategory = of.OldCategory,
            Verdict = RowVerdicts.Fix,
            WayOfUpload = of.WayOfUpload
        };

        return row;
    }

    [Fact]
    public async Task Correcting_a_row_settles_every_row_that_shares_its_file()
    {
        UploadsSucceed();

        var first = Fixable(1);
        var shares = Sibling(first, 2);
        var rows = new[] { first, shares };

        var summary = await Subject().RunAsync(rows, rows, CancellationToken.None);

        Assert.Equal(1, summary.Corrected);
        Assert.Equal(1, summary.SettledBySibling);

        Assert.Equal(RowVerdict.Done, shares.Verdict2());
        Assert.Equal(first.NewFilePath, shares.NewFilePath);
    }

    /// <summary>
    /// The old file belongs to the row that corrected it, and that row owns its deletion. A
    /// second row saying "pending the delete of old docs" would queue the same file twice.
    /// </summary>
    [Fact]
    public async Task A_row_settled_by_a_sibling_queues_no_delete_of_its_own()
    {
        UploadsSucceed();

        var first = Fixable(1);
        var shares = Sibling(first, 2);
        var rows = new[] { first, shares };

        await Subject().RunAsync(rows, rows, CancellationToken.None);

        Assert.Equal(RowState.Corrected, first.State());
        Assert.Equal(string.Empty, shares.FinalState);
        Assert.Equal(RowState.NotStarted, shares.State());
    }

    /// <summary>Nothing is uploaded for it — that is the whole point of settling it here.</summary>
    [Fact]
    public async Task Nothing_is_uploaded_for_a_row_settled_by_a_sibling()
    {
        UploadsSucceed();

        var first = Fixable(1);
        var rows = new[] { first, Sibling(first, 2) };

        await Subject().RunAsync(rows, rows, CancellationToken.None);

        Assert.Single(_files.Uploads);
    }

    /// <summary>
    /// A verdict somebody typed is an instruction. The file having moved does not tell us what
    /// they meant by "ignore", so it is left exactly as it is.
    /// </summary>
    [Fact]
    public async Task A_sibling_the_operator_excluded_is_left_alone()
    {
        UploadsSucceed();

        var first = Fixable(1);
        var shares = Sibling(first, 2);
        shares.Verdict = RowVerdicts.Ignore;

        var rows = new[] { first, shares };

        var summary = await Subject().RunAsync(rows, rows, CancellationToken.None);

        Assert.Equal(0, summary.SettledBySibling);
        Assert.Equal(RowVerdicts.Ignore, shares.Verdict);
    }

    /// <summary>
    /// The total is what the run set out to do, settled before the first document and not moved
    /// afterwards. A denominator that changes while the run is going cannot be read; how many
    /// rows a sibling settled is said on the line itself and again in the summary.
    /// </summary>
    [Fact]
    public async Task The_total_does_not_move_when_siblings_are_settled()
    {
        UploadsSucceed();

        var first = Fixable(1);
        var rows = new[] { first, Sibling(first, 2), Sibling(first, 3), Fixable(4) };

        var summary = await Subject().RunAsync(rows, rows, CancellationToken.None);

        // Four rows said fix; two were settled by the first, so two were ever started — and the
        // total stays at four throughout.
        Assert.Contains(_prompts.Messages, m => m.Contains("[ 1/4 ]"));
        Assert.Contains(_prompts.Messages, m => m.Contains("[ 2/4 ]"));
        Assert.DoesNotContain(_prompts.Messages, m => m.Contains("[ 2/2 ]"));

        Assert.Equal(2, summary.SettledBySibling);
    }

    /// <summary>
    /// Said out loud, in every mode. A row going green that the run never appeared to touch
    /// reads as a bug, and the count dropping by three wants explaining as it happens.
    /// </summary>
    [Theory]
    [InlineData(WatchMode.Quiet)]
    [InlineData(WatchMode.Unattended)]
    public async Task Settling_siblings_is_said_as_one_line(WatchMode mode)
    {
        UploadsSucceed();

        var first = Fixable(1);
        var rows = new[] { first, Sibling(first, 2), Sibling(first, 3) };

        // Quiet still shows the two copies and asks; Unattended never does.
        _prompts.Answer(ConfirmChoice.Yes);

        await Subject(mode).RunAsync(rows, rows, CancellationToken.None);

        Assert.Single(_prompts.Messages, m => m.Contains("2 other row(s) share this file"));
    }

    // ---- a failure is not tried again by itself ----
    //
    // Most failures here are permanent. A file server that reports success and returns no bytes
    // will do it again tomorrow, and a row left saying fix is worked on every run for ever,
    // failing identically and asking the operator the same question each time.

    [Fact]
    public async Task A_failed_row_is_set_to_review()
    {
        UploadsSucceed();
        var row = Fixable(1);
        var onTheServer = _files.Files[row.OldFilePath];
        _files.Files.Remove(row.OldFilePath);
        _prompts.YesNoQueue = new Queue<bool>(new[] { true });

        await Subject().RunAsync(new[] { row }, new[] { row }, CancellationToken.None);

        Assert.Equal(RowVerdict.Review, row.Verdict2());

        // The final state still says what happened, and the error column still says why.
        Assert.Equal(RowState.Failed, row.State());
        Assert.NotEqual(string.Empty, row.Error);
    }

    [Fact]
    public async Task The_next_run_does_not_work_a_row_that_failed_before()
    {
        UploadsSucceed();
        var row = Fixable(1);
        var onTheServer = _files.Files[row.OldFilePath];
        _files.Files.Remove(row.OldFilePath);
        _prompts.YesNoQueue = new Queue<bool>(new[] { true });

        await Subject().RunAsync(new[] { row }, new[] { row }, CancellationToken.None);

        // Everything put back as it would be on a second run — except the row's own verdict.
        _files.Files[row.OldFilePath] = onTheServer;
        _prompts.Questions.Clear();

        var again = await Subject().RunAsync(new[] { row }, new[] { row }, CancellationToken.None);

        Assert.Equal(0, again.Failed);
        Assert.Equal(0, again.Corrected);
        Assert.DoesNotContain(_prompts.Questions, q => q.Contains("Carry on"));
    }

    /// <summary>One cell back to fix and it is work again. Nothing else has to be undone.</summary>
    [Fact]
    public async Task Putting_the_verdict_back_to_fix_makes_it_work_again()
    {
        UploadsSucceed();
        var row = Fixable(1);
        var onTheServer = _files.Files[row.OldFilePath];
        _files.Files.Remove(row.OldFilePath);
        _prompts.YesNoQueue = new Queue<bool>(new[] { true });

        await Subject().RunAsync(new[] { row }, new[] { row }, CancellationToken.None);

        _files.Files[row.OldFilePath] = onTheServer;
        row.Verdict = RowVerdicts.Fix;

        var again = await Subject().RunAsync(new[] { row }, new[] { row }, CancellationToken.None);

        Assert.Equal(1, again.Corrected);
    }
}
