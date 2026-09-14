using System.Text;
using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The backlog, asked again for each document at the moment that document is about to move.
///
/// A check made during the scan and never mentioned afterwards is no protection when the file
/// is actually re-filed — minutes later, possibly in another session. So the standing is said
/// out loud per document, and anything short of agreement is put to the operator with leaving
/// the document alone as the default.
/// </summary>
public class MigrateTypeCheckTests : IDisposable
{
    private static readonly Guid Correct = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid NewFileId = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");
    private static readonly Guid OldFileId = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");
    private static readonly Guid DocumentId = Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a");

    private const string OldPath = @"DigitalServices\goodConductCertificate\20260330\5b05398a.jpg";
    private const string TypeName = "A Medical Examination Certificate";

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("certificate bytes");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-migtype-" + Guid.NewGuid());
    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakeCrmWriteClient _write = new();
    private readonly NullFileOpener _opener = new();

    /// <summary>Every (type, service) the upload step asked about, in order.</summary>
    private readonly List<string> _asked = new();

    private BackupStore Backups() => new(Path.Combine(_root, "backup"));
    private StateStore States() => new(Path.Combine(_root, "state.jsonl"));

    public MigrateTypeCheckTests()
    {
        Directory.CreateDirectory(_root);
        _read.CatalogueNames[Correct.ToString()] = "Employee Appointment";

        var backups = Backups();
        var saved = backups.Save(DocumentId, OldFileId, ".jpg", Content);

        backups.AppendManifest(new ManifestEntry(DocumentId, OldFileId, OldPath, "VHASH", "cert.jpg",
            "image/jpeg", ".jpg", "goodConductCertificate", Correct, saved.LocalPath, saved.Bytes,
            saved.OurHash, DateTimeOffset.UtcNow, DocumentTypeName: TypeName));

        States().Append(new StateRecord(DocumentId, MigrationState.BackedUp,
            DateTimeOffset.UtcNow, null, null, null));

        _files.UploadResponder = _ =>
        {
            var path = $@"DigitalServices\{Correct}\20260914\{NewFileId}.jpg";
            _files.Files[path] = (Convert.ToBase64String(Content), "VHASH");
            _read.RawRecords[$"mocd_documentfiles:{NewFileId}"] =
                "{\"mocd_filepath\":\"" + path.Replace("\\", "\\\\") + "\"}";
            return new ApiResponse<FileData>(true, null,
                new FileData(NewFileId, path, "VHASH", "cert.jpg", "image/jpeg", null), null);
        };

        _write.OnCreated = (id, attributes) =>
            _read.RawRecords[$"mocd_documentfiles:{id}"] =
                "{\"mocd_filepath\":\"" +
                ((string?)attributes["mocd_filepath"] ?? "").Replace("\\", "\\\\") + "\"}";
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private MigrateCommand Command(IPrompts prompts, TypeRuling ruling) =>
        new(_files, _read, _write, Backups(), States(), new Reporter(Path.Combine(_root, "reports")),
            prompts, _opener, "https://crm/MoCD", null, null, null,
            (type, service, _) =>
            {
                _asked.Add($"{type} / {service}");
                return Task.FromResult(ruling);
            });

    private static TypeRuling Ruling(AdoVerdict verdict, string? service = "Employee Appointment") =>
        new(TypeName, verdict, service, verdict switch
        {
            AdoVerdict.Agrees => "2 work items put it under Employee Appointment.",
            AdoVerdict.Disagrees => "2 work items put it under By-Laws Amendment.",
            _ => "Nothing in the backlog names it."
        }, new[] { new AdoHit(27628, "NPOP | Employee Appointment | Documents", service) }, "DevOps");

    /// <summary>The four yes answers a clean migration needs, once the check is out of the way.</summary>
    private static FakePrompts Yes() => new FakePrompts().Answer(
        ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

    // ---- agreement ----

    [Fact]
    public async Task The_backlog_is_asked_for_the_document_being_moved()
    {
        var summary = await Command(Yes(), Ruling(AdoVerdict.Agrees)).RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Migrated);
        // Asked with the document type and the service CRM holds — the two halves of the
        // question, so the check can say whether they agree.
        Assert.Equal(new[] { $"{TypeName} / Employee Appointment" }, _asked);
    }

    [Fact]
    public async Task Agreement_is_said_out_loud_and_asks_nothing_extra()
    {
        var prompts = Yes();

        await Command(prompts, Ruling(AdoVerdict.Agrees)).RunAsync("dev", CancellationToken.None);

        Assert.Contains(prompts.Messages, m => m.Contains("agrees", StringComparison.Ordinal));
        Assert.DoesNotContain(prompts.Questions,
            q => q.Contains("What should I do with this document", StringComparison.Ordinal));
    }

    // ---- disagreement ----

    /// <summary>
    /// The backlog and CRM naming different services means the one thing the run is for — which
    /// folder this file belongs in — is in doubt. Doing nothing is the default.
    /// </summary>
    [Fact]
    public async Task A_disagreement_stops_the_document_and_asks_with_leave_it_as_the_default()
    {
        var prompts = Yes();
        prompts.ReadLineQueue = new Queue<string>(new[] { "" });     // take the default
        prompts.YesNoResponse = true;                                // yes, I am sure

        var summary = await Command(prompts, Ruling(AdoVerdict.Disagrees, "By-Laws Amendment"))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Empty(_files.Uploads);
        Assert.Empty(_write.CreatedFiles);

        var skip = Assert.Single(summary.Skips!);
        Assert.Contains("DevOps check", skip.Why);

        Assert.Contains(prompts.Messages, m => m.Contains("DISAGREES", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Choosing_to_go_on_anyway_migrates_the_document()
    {
        var prompts = Yes();
        prompts.ReadLineQueue = new Queue<string>(new[] { "2" });     // go on anyway
        prompts.YesNoResponse = true;

        var summary = await Command(prompts, Ruling(AdoVerdict.Disagrees, "By-Laws Amendment"))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Migrated);
        Assert.Single(_files.Uploads);
    }

    [Fact]
    public async Task Choosing_to_stop_halts_the_run_without_writing()
    {
        var prompts = Yes();
        prompts.ReadLineQueue = new Queue<string>(new[] { "3" });     // stop the run
        prompts.YesNoResponse = true;

        var summary = await Command(prompts, Ruling(AdoVerdict.Disagrees, "By-Laws Amendment"))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.True(summary.Halted);
        Assert.Contains("DevOps", summary.HaltReason!);
        Assert.Empty(_files.Uploads);
    }

    // ---- no answer at all ----

    [Fact]
    public async Task An_unsettled_type_is_put_to_the_operator_rather_than_moved_quietly()
    {
        var prompts = Yes();
        prompts.ReadLineQueue = new Queue<string>(new[] { "" });
        prompts.YesNoResponse = true;

        var summary = await Command(prompts, Ruling(AdoVerdict.CannotTell, null))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Contains(prompts.Messages,
            m => m.Contains("could not confirm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_the_check_wired_in_the_upload_behaves_exactly_as_before()
    {
        var prompts = Yes();

        var summary = await new MigrateCommand(_files, _read, _write, Backups(), States(),
                new Reporter(Path.Combine(_root, "reports")), prompts, _opener, "https://crm/MoCD")
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Migrated);
        Assert.Empty(_asked);
    }
}
