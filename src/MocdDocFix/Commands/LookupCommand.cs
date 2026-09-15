using System.Text.Json;
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

/// <summary>One mocd_documentfile as CRM holds it right now.</summary>
public sealed record CrmFileRecord(Guid Id, string? FilePath, string? Name, string? Category);

/// <summary>
/// What both systems and this tool's own notes say about one file.
/// </summary>
/// <param name="Asked">Exactly what was typed.</param>
/// <param name="AskedId">The documentfile id, when that is what was typed.</param>
/// <param name="Path">The path that was checked — given, or read off the record or the backup.</param>
/// <param name="Record">The record CRM returned for <paramref name="AskedId"/>.</param>
/// <param name="RecordIsGone">An id was asked for and CRM no longer has it.</param>
/// <param name="OnTheServer">Null when no path could be worked out, so nothing was asked.</param>
/// <param name="PointingAtThePath">Every documentfile whose mocd_filepath is this path.</param>
/// <param name="Backup">The manifest entry, if this file was backed up by this tool.</param>
/// <param name="State">Where that document got to, if this tool has touched it.</param>
public sealed record LookupReport(
    string Asked,
    Guid? AskedId,
    string? Path,
    CrmFileRecord? Record,
    bool RecordIsGone,
    bool? OnTheServer,
    long Bytes,
    string? VendorHash,
    IReadOnlyList<Guid> PointingAtThePath,
    ManifestEntry? Backup,

    /// <summary>Why CRM could not be asked, when it could not. Not the same as "not there".</summary>
    string? CrmProblem = null,

    /// <summary>Why the file server could not be asked, when it could not.</summary>
    string? ServerProblem = null,

    /// <summary>
    /// Records whose path carries the GUID that was typed — filled in when that GUID turns out
    /// to be the file server's file id rather than a CRM record id.
    /// </summary>
    IReadOnlyList<Guid>? UsingThisFileId = null);

/// <summary>
/// Answers one question, for one file: is it still there?
///
/// It exists because after a delete — or a halted run, or a fix made by hand — the only honest
/// answer comes from asking both systems, and there was no way to ask about a single file
/// without running a phase that also wanted to change something. This changes nothing. It reads
/// the file server, reads CRM, and reads this tool's own backup manifest and state file, and
/// says what each of the three claims.
/// </summary>
public sealed class LookupCommand
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly BackupStore _backups;
    private readonly IPrompts _prompts;
    private readonly LedgerStore? _ledger;

    /// <param name="ledger">
    /// Optional. Asking about the file a document uses NOW is as reasonable as asking about the
    /// one it used to, and only the ledger knows which document a corrected path belongs to —
    /// the backup manifest records old paths alone. Without it a new path is still checked
    /// against both systems; it just cannot be attributed to a document.
    /// </param>
    public LookupCommand(IFileServiceClient files, ICrmReadClient read, BackupStore backups,
        IPrompts prompts, LedgerStore? ledger = null)
    {
        _files = files;
        _read = read;
        _backups = backups;
        _prompts = prompts;
        _ledger = ledger;
    }

    public async Task<IReadOnlyList<LookupReport>> RunAsync(
        IEnumerable<string> identifiers, CancellationToken ct)
    {
        var reports = new List<LookupReport>();

        foreach (var identifier in identifiers)
        {
            ct.ThrowIfCancellationRequested();

            // One identifier that cannot be looked up must not throw away the answers for the
            // others. Whatever went wrong is said here, under that identifier, and the next one
            // is asked about as though nothing had happened.
            try
            {
                var report = await FindAsync(identifier, ct);
                Write(report);
                reports.Add(report);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _prompts.Section($"Look-up — {identifier}");
                _prompts.Warn($"Could not be looked up: {Innermost(e)}");
                _prompts.Say("Nothing was changed. The other identifiers are still being asked about.",
                    Tone.Muted);
            }
        }

        return reports;
    }

    private static string Innermost(Exception e)
    {
        var innermost = e;
        while (innermost.InnerException is not null) innermost = innermost.InnerException;

        return $"{innermost.GetType().Name}: {innermost.Message}";
    }

    /// <summary>Reads only — the file server, CRM, and this tool's own notes.</summary>
    public async Task<LookupReport> FindAsync(string identifier, CancellationToken ct)
    {
        var asked = identifier.Trim();
        Guid? askedId = Guid.TryParse(asked.Trim('{', '}'), out var parsed) ? parsed : null;

        CrmFileRecord? record = null;
        var recordIsGone = false;
        string? crmProblem = null;
        string? path = askedId is null ? FilePaths.Normalise(asked) : null;
        IReadOnlyList<Guid>? usingThisFileId = null;

        if (askedId is { } id)
        {
            var answer = await _read.GetDocumentFileAsync(id, ct);

            if (!answer.Answered) crmProblem = answer.Problem;
            else if (answer.Json is null) recordIsGone = true;
            else
            {
                record = ReadRecord(id, answer.Json);
                path = record.FilePath;
            }
        }

        // A record that has gone takes its path with it, so fall back to what was written down
        // at backup time — which is the whole point of asking about a deleted file.
        var backup = FindBackup(askedId, path);
        path ??= backup?.OldFilePath;

        // Still nothing, and a GUID was typed. The GUID on a file is the file server's id, not
        // the CRM record's, and it is the one an operator reads off a path — so ask which
        // records use it before concluding that there is nothing to find.
        if (path is null && askedId is { } fileId)
        {
            usingThisFileId = await Safely(() => _read.FindDocumentFilesByFileIdAsync(fileId, ct),
                Array.Empty<Guid>(), p => crmProblem ??= p);

            if (usingThisFileId.Count > 0)
            {
                var answer = await _read.GetDocumentFileAsync(usingThisFileId[0], ct);

                if (answer.Json is { } json)
                {
                    record = ReadRecord(usingThisFileId[0], json);
                    path = record.FilePath;
                    recordIsGone = false;
                    backup ??= FindBackup(null, path);
                }
            }
        }

        bool? onTheServer = null;
        long bytes = 0;
        string? vendorHash = null;
        string? serverProblem = null;

        if (!string.IsNullOrWhiteSpace(path))
        {
            var download = await Safely<ApiResponse<FileData>?>(
                async () => await _files.DownloadAsync(path!, ct), null, p => serverProblem = p);

            if (download is not null)
            {
                var content = download.Data?.File;

                // A 200 with an empty file means "no such path" — the vendor does not 404.
                onTheServer = download.Success && !string.IsNullOrEmpty(content);
                if (onTheServer is true)
                {
                    bytes = SizeOf(content!);
                    vendorHash = download.Data?.Hash;
                }
            }
        }

        var pointing = string.IsNullOrWhiteSpace(path)
            ? Array.Empty<Guid>()
            : await Safely(() => _read.FindDocumentFilesByPathAsync(path!, ct),
                Array.Empty<Guid>(), p => crmProblem ??= p);

        return new LookupReport(asked, askedId, path, record, recordIsGone, onTheServer,
            bytes, vendorHash, pointing, backup,
            crmProblem, serverProblem, usingThisFileId);
    }

    /// <summary>
    /// Runs one question, and on failure records why rather than abandoning the rest. A look-up
    /// asks three things of two systems; one of them being unreachable is worth saying plainly,
    /// but it is no reason to withhold the two answers that did come back.
    /// </summary>
    private static async Task<T> Safely<T>(Func<Task<T>> ask, T fallback, Action<string> problem)
    {
        try
        {
            return await ask();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            problem(Innermost(e));
            return fallback;
        }
    }

    /// <summary>
    /// The manifest entry for this file. Matched on the id or on the path, because the operator
    /// may have either to hand, and on the NEW path too — asking about the file a document uses
    /// now is as reasonable a question as asking about the one it used to.
    /// </summary>
    private ManifestEntry? FindBackup(Guid? askedId, string? path)
    {
        var manifest = _backups.LoadManifest();

        var byId = askedId is { } id ? manifest.FirstOrDefault(m => m.OldFileId == id) : null;
        if (byId is not null) return byId;

        if (string.IsNullOrWhiteSpace(path)) return null;

        var byOldPath = manifest.FirstOrDefault(m => FilePaths.Same(m.OldFilePath, path));
        if (byOldPath is not null) return byOldPath;

        // A corrected path names no old file, so the manifest cannot match it. The ledger holds
        // both, and is the only thing that can say which document a new path belongs to.
        var corrected = _ledger?.Read()
            .FirstOrDefault(r => FilePaths.Same(r.NewFilePath, path));

        return corrected is null
            ? null
            : manifest.FirstOrDefault(m => m.DocumentId == corrected.DocId);
    }


    // ---- the screen ----

    private void Write(LookupReport report)
    {
        _prompts.Section($"Look-up — {report.Asked}");

        if (report.Path is null)
        {
            if (report.CrmProblem is { } why)
            {
                _prompts.Warn($"CRM could not be asked: {why}");
                _prompts.Say("So nothing can be said about this one — it is NOT a statement that " +
                             "the record has gone. Try it again, or check the VPN.", Tone.Muted);
                return;
            }

            _prompts.Warn("Nothing here to check: that id is not a documentfile record, no " +
                          "record's path carries it as a file id, and it was never backed up by " +
                          "this tool — so there is no path to ask the file server about.");
            _prompts.Say("If it came off a file name, the whole path finds it: " +
                         @"DigitalServices\<date>\<id>.png", Tone.Muted);
            return;
        }

        if (report.UsingThisFileId is { Count: > 0 } sharing)
        {
            _prompts.Say($"That GUID is the file server's file id, not a record id. " +
                         $"{sharing.Count} CRM record(s) use it; the first is shown below.", Tone.Muted);
        }

        if (!FilePaths.Same(report.Path, report.Asked))
            _prompts.Field("the path", report.Path, Tone.Muted);

        WriteCrm(report);
        WriteServer(report);
        WriteOwnNotes(report);
        WriteVerdict(report);
    }

    private void WriteCrm(LookupReport report)
    {
        _prompts.Section("IN CRM", Tone.Normal);

        if (report.CrmProblem is { } why)
            _prompts.Info($"    could not be asked — {why}", Tone.Danger);

        if (report.RecordIsGone)
            _prompts.Info($"    the record {report.AskedId} is NOT in CRM — it has been deleted",
                Tone.Warn);

        if (report.Record is { } found)
        {
            _prompts.Info($"    the record {found.Id} is still there", Tone.Good);
            _prompts.Info($"      name      {found.Name ?? "(none)"}", Tone.Muted);
            _prompts.Info($"      path      {found.FilePath ?? "(none)"}", Tone.Muted);
            _prompts.Info($"      category  {found.Category ?? "(none)"}", Tone.Muted);
        }

        var others = report.PointingAtThePath.Where(id => id != report.AskedId).ToList();

        if (report.PointingAtThePath.Count == 0)
            _prompts.Info("    no documentfile record points at this path", Tone.Muted);
        else
        {
            _prompts.Info($"    {report.PointingAtThePath.Count} record(s) point at this path",
                Tone.Normal);
            foreach (var id in report.PointingAtThePath.Take(10))
                _prompts.Info($"      {id}{(id == report.AskedId ? "   (the one asked about)" : "")}",
                    Tone.Muted);
            if (report.PointingAtThePath.Count > 10)
                _prompts.Info($"      … and {report.PointingAtThePath.Count - 10} more", Tone.Muted);
        }

        if (others.Count > 0 && report.AskedId is not null)
            _prompts.Warn($"{others.Count} other record(s) share this file. Deleting it would " +
                          "break them.");
    }

    private void WriteServer(LookupReport report)
    {
        _prompts.Section("ON THE FILE SERVER", Tone.Normal);

        if (report.ServerProblem is { } why)
        {
            _prompts.Info($"    could not be asked — {why}", Tone.Danger);
            return;
        }

        switch (report.OnTheServer)
        {
            case true:
                _prompts.Info($"    the file IS there — {report.Bytes:N0} bytes" +
                              (report.VendorHash is null ? "" : $", vendor hash {report.VendorHash}"),
                    Tone.Good);
                break;

            case false:
                _prompts.Info("    the file is NOT there — the server returned nothing for this path",
                    Tone.Warn);
                break;

            default:
                _prompts.Info("    not asked — no path to ask about", Tone.Muted);
                break;
        }
    }

    private void WriteOwnNotes(LookupReport report)
    {
        _prompts.Section("WHAT THIS TOOL KNOWS", Tone.Normal);

        if (report.Backup is null)
        {
            _prompts.Info("    nothing — this file has never been through this tool", Tone.Muted);
            return;
        }

        if (report.Backup is { } backup)
        {
            _prompts.Info($"    backed up   {backup.At:yyyy-MM-dd HH:mm} — {backup.Bytes:N0} bytes",
                Tone.Muted);
            _prompts.Info($"      local copy  {backup.LocalPath}", Tone.Muted);
            _prompts.Info($"      document    {backup.DocumentId}", Tone.Muted);
        }

    }

    /// <summary>
    /// The one line the question was really asking. Said last, after the evidence for it.
    /// </summary>
    private void WriteVerdict(LookupReport report)
    {
        _prompts.Blank();

        // "GONE" means both systems said no. If one of them never answered, saying so is the
        // only honest verdict — an unreachable server is not evidence that a file was removed.
        if (report.CrmProblem is not null || report.ServerProblem is not null)
        {
            _prompts.Say("NOT ANSWERED — " +
                         (report.CrmProblem is not null ? "CRM" : "the file server") +
                         " could not be asked, so this file's whereabouts are unknown. " +
                         "Nothing here says it has gone.", Tone.Warn);
            return;
        }

        var inCrm = report.Record is not null || report.PointingAtThePath.Count > 0;

        var verdict = (report.OnTheServer, inCrm) switch
        {
            (true, true) => ("BOTH — the file is on the server and CRM still refers to it.", Tone.Good),
            (true, false) => ("FILE ONLY — the file is on the server, but no CRM record refers " +
                              "to it. Nothing in the app will open it.", Tone.Warn),
            (false, true) => ("CRM ONLY — a record refers to this path but the file is gone. " +
                              "Opening that document will fail.", Tone.Danger),
            (false, false) => ("GONE — neither the file server nor CRM has it any more.", Tone.Good),
            _ => ("Not enough to say.", Tone.Muted)
        };

        _prompts.Say(verdict.Item1, verdict.Item2);
    }

    /// <summary>
    /// The file's real size, read off the base64 without decoding it: every four characters
    /// carry three bytes, less whatever the padding stands in for. Worth being exact about —
    /// this number is what an operator compares against the backup to decide they match.
    /// </summary>
    private static long SizeOf(string base64)
    {
        var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2
            : base64.EndsWith("=", StringComparison.Ordinal) ? 1
            : 0;

        return base64.Length / 4L * 3 - padding;
    }

    private static CrmFileRecord ReadRecord(Guid id, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return new CrmFileRecord(id, Text(root, "mocd_filepath"), Text(root, "mocd_name"),
                Text(root, "mocd_category"));
        }
        catch (JsonException)
        {
            return new CrmFileRecord(id, null, null, null);
        }

        static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
