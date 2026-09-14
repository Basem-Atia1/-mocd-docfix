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
    StateRecord? State);

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
    private readonly StateStore _state;
    private readonly IPrompts _prompts;

    public LookupCommand(IFileServiceClient files, ICrmReadClient read, BackupStore backups,
        StateStore state, IPrompts prompts)
    {
        _files = files;
        _read = read;
        _backups = backups;
        _state = state;
        _prompts = prompts;
    }

    public async Task<IReadOnlyList<LookupReport>> RunAsync(
        IEnumerable<string> identifiers, CancellationToken ct)
    {
        var reports = new List<LookupReport>();

        foreach (var identifier in identifiers)
        {
            ct.ThrowIfCancellationRequested();

            var report = await FindAsync(identifier, ct);
            Write(report);
            reports.Add(report);
        }

        return reports;
    }

    /// <summary>Reads only — the file server, CRM, and this tool's own notes.</summary>
    public async Task<LookupReport> FindAsync(string identifier, CancellationToken ct)
    {
        var asked = identifier.Trim();
        Guid? askedId = Guid.TryParse(asked.Trim('{', '}'), out var parsed) ? parsed : null;

        CrmFileRecord? record = null;
        var recordIsGone = false;
        string? path = askedId is null ? FilePaths.Normalise(asked) : null;

        if (askedId is { } id)
        {
            var json = await _read.GetRawRecordAsync("mocd_documentfiles", id, ct);

            if (json is null) recordIsGone = true;
            else
            {
                record = ReadRecord(id, json);
                path = record.FilePath;
            }
        }

        // A record that has gone takes its path with it, so fall back to what was written down
        // at backup time — which is the whole point of asking about a deleted file.
        var backup = FindBackup(askedId, path);
        path ??= backup?.OldFilePath;

        bool? onTheServer = null;
        long bytes = 0;
        string? vendorHash = null;

        if (!string.IsNullOrWhiteSpace(path))
        {
            var download = await _files.DownloadAsync(path!, ct);
            var content = download.Data?.File;

            // A 200 with an empty file means "no such path" — the vendor does not 404.
            onTheServer = download.Success && !string.IsNullOrEmpty(content);
            if (onTheServer is true)
            {
                bytes = SizeOf(content!);
                vendorHash = download.Data?.Hash;
            }
        }

        var pointing = string.IsNullOrWhiteSpace(path)
            ? Array.Empty<Guid>()
            : await _read.FindDocumentFilesByPathAsync(path!, ct);

        return new LookupReport(asked, askedId, path, record, recordIsGone, onTheServer,
            bytes, vendorHash, pointing, backup, StateOf(backup, path));
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

        var byNewPath = _state.LoadLatest().Values
            .FirstOrDefault(r => FilePaths.Same(r.NewFilePath, path));

        return byNewPath is null
            ? null
            : manifest.FirstOrDefault(m => m.DocumentId == byNewPath.DocumentId);
    }

    private StateRecord? StateOf(ManifestEntry? backup, string? path)
    {
        var latest = _state.LoadLatest();

        if (backup is not null && latest.TryGetValue(backup.DocumentId, out var byDocument))
            return byDocument;

        return path is null
            ? null
            : latest.Values.FirstOrDefault(r => FilePaths.Same(r.NewFilePath, path));
    }

    // ---- the screen ----

    private void Write(LookupReport report)
    {
        _prompts.Section($"Look-up — {report.Asked}");

        if (report.Path is null)
        {
            _prompts.Warn("Nothing here to check: that id is not in CRM and was never backed " +
                          "up by this tool, so there is no path to ask the file server about.");
            return;
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

        if (report.Backup is null && report.State is null)
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

        if (report.State is { } state)
        {
            _prompts.Info($"    got as far as {state.State} on {state.At.LocalDateTime:yyyy-MM-dd HH:mm}",
                Tone.Muted);

            if (!string.IsNullOrWhiteSpace(state.Detail))
                foreach (var line in Screen.Wrap(state.Detail!, Screen.Width - 6))
                    _prompts.Info($"      {line}", Tone.Muted);

            if (state.NewFilePath is not null)
                _prompts.Info($"      new file    {state.NewFilePath}", Tone.Muted);
        }
    }

    /// <summary>
    /// The one line the question was really asking. Said last, after the evidence for it.
    /// </summary>
    private void WriteVerdict(LookupReport report)
    {
        _prompts.Blank();

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
