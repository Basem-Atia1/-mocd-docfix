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
    [InlineData(RowVerdicts.Skip)]
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
        Assert.Contains(summary.Skips, s => s.Why.Contains("already"));
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
        Assert.Equal(RowState.Corrected, _ledger.Read()[0].State());
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

        var onDisk = _ledger.Read();
        Assert.Equal(3, onDisk.Count);
        Assert.Equal(RowState.NotStarted, onDisk[0].State());
        Assert.Equal(RowState.Corrected, onDisk[1].State());
        Assert.Equal(RowState.NotStarted, onDisk[2].State());
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
    /// Quiet never interrupts, so the way out is a keypress noticed between documents. The one
    /// in progress finishes and is recorded; nothing after it is started.
    /// </summary>
    [Fact]
    public async Task Pressing_q_in_quiet_stops_after_the_document_in_progress()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2), Fixable(3) };

        _prompts.Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);
        _prompts.StopRequests = new Queue<bool>(new[] { true });

        var summary = await Subject(WatchMode.Quiet).RunAsync(rows, rows, CancellationToken.None);

        Assert.True(summary.Stopped);
        Assert.Equal(1, summary.Corrected);
        Assert.Equal(RowState.Corrected, rows[0].State());
        Assert.Equal(RowState.NotStarted, rows[1].State());

        // And the ledger on disk says so, because it is written as each row finishes.
        Assert.Equal(RowState.Corrected, _ledger.Read().Single(r => r.Row == 1).State());
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
}
