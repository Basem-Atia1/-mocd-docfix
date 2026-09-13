using System.Text;
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;

namespace MocdDocFix.Commands;

/// <param name="Problems">Empty means everything is where it should be.</param>
public sealed record DocumentVerdict(
    Guid DocumentId,
    string? FileName,
    MigrationState State,
    bool OldFileOnServer,
    bool NewFileOnServer,
    bool OldRecordInCrm,
    bool NewRecordInCrm,
    string? NewRecordPath,
    Guid? DocumentPointsAt,
    IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

public sealed record VerifySummary(
    int Checked, int Ok, int WithProblems, string ReportPath, IReadOnlyList<DocumentVerdict> Verdicts);

/// <summary>
/// The final proof. Everything else reports what it believed at the time it ran; this asks the
/// file server and CRM what is actually true now, for every document the tool has touched.
///
/// It reads only — it never repairs anything. Finding a problem and fixing it silently would
/// hide exactly the sort of disagreement this exists to surface.
/// </summary>
public sealed class VerifyCommand
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly ICrmWriteClient _write;
    private readonly BackupStore _backups;
    private readonly StateStore _state;
    private readonly string _crmUrl;
    private readonly string _reportsDir;

    public VerifyCommand(IFileServiceClient files, ICrmReadClient read, ICrmWriteClient write,
        BackupStore backups, StateStore state, string crmUrl, string reportsDir)
    {
        _files = files;
        _read = read;
        _write = write;
        _backups = backups;
        _state = state;
        _crmUrl = crmUrl;
        _reportsDir = reportsDir;
    }

    public async Task<VerifySummary> RunAsync(string env, CancellationToken ct)
    {
        var latest = _state.LoadLatest();
        var verdicts = new List<DocumentVerdict>();

        foreach (var entry in _backups.LoadManifest())
        {
            ct.ThrowIfCancellationRequested();

            if (!latest.TryGetValue(entry.DocumentId, out var record)) continue;
            if (record.State is not (MigrationState.Repointed or MigrationState.Deleted)) continue;

            verdicts.Add(await VerifyOneAsync(entry, record, ct));
        }

        var reportPath = Write(env, verdicts);

        return new VerifySummary(verdicts.Count, verdicts.Count(v => v.Ok),
            verdicts.Count(v => !v.Ok), reportPath, verdicts);
    }

    private async Task<DocumentVerdict> VerifyOneAsync(
        ManifestEntry entry, StateRecord record, CancellationToken ct)
    {
        var problems = new List<string>();
        var deleted = record.State == MigrationState.Deleted;

        // ---- the file server ----

        var oldOnServer = await ExistsAsync(entry.OldFilePath, ct);
        var newOnServer = record.NewFilePath is not null && await ExistsAsync(record.NewFilePath, ct);

        if (!newOnServer)
            problems.Add("the NEW file is not on the file server");

        if (deleted && oldOnServer)
            problems.Add("the old file was reported deleted but is still on the file server");

        if (!deleted && !oldOnServer)
            problems.Add("the old file has gone from the server, but nothing recorded deleting it");

        // ---- CRM ----

        var oldRecord = await _read.GetRawRecordAsync("mocd_documentfiles", entry.OldFileId, ct);
        var oldInCrm = oldRecord is not null;

        string? newRecord = null;
        var newInCrm = false;
        if (record.NewFileId is { } newId)
        {
            newRecord = await _read.GetRawRecordAsync("mocd_documentfiles", newId, ct);
            newInCrm = newRecord is not null;
        }

        if (!newInCrm) problems.Add("the NEW mocd_documentfile record does not exist in CRM");
        if (deleted && oldInCrm) problems.Add("the old mocd_documentfile record is still in CRM");
        if (!deleted && !oldInCrm) problems.Add("the old mocd_documentfile record has gone from CRM early");

        var recordPath = ReadFilePath(newRecord);

        if (newInCrm && record.NewFilePath is not null)
        {
            var check = Verification.Verifier.FileRecordPointsAtTheNewFile(record.NewFilePath, recordPath);
            if (!check.Passed)
                problems.Add($"the new record's mocd_filepath is '{recordPath ?? "null"}', not the new file");
        }

        var pointsAt = await _write.GetDocumentFileLinkAsync(entry.DocumentId, ct);
        if (pointsAt != record.NewFileId)
            problems.Add($"the document points at '{pointsAt?.ToString() ?? "null"}', not the new file");

        return new DocumentVerdict(entry.DocumentId, entry.FileName, record.State,
            oldOnServer, newOnServer, oldInCrm, newInCrm, recordPath, pointsAt, problems);
    }

    private async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        // The vendor answers 200 with an empty body for a path that is not there, so presence is
        // decided by whether any content came back — not by the success flag.
        var response = await _files.DownloadAsync(path, ct);
        return response.Success && !string.IsNullOrEmpty(response.Data?.File);
    }

    private string Write(string env, IReadOnlyList<DocumentVerdict> verdicts)
    {
        Directory.CreateDirectory(_reportsDir);
        var path = Path.Combine(_reportsDir, $"verify-{env}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var text = new StringBuilder();
        text.AppendLine($"Final check — {env} — {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine(new string('=', 78));
        text.AppendLine();
        text.AppendLine("Asked of the file server and CRM directly, just now.");
        text.AppendLine();
        text.AppendLine($"  {verdicts.Count} document(s) checked");
        text.AppendLine($"  {verdicts.Count(v => v.Ok)} correct");
        text.AppendLine($"  {verdicts.Count(v => !v.Ok)} with a problem");
        text.AppendLine();

        foreach (var v in verdicts)
        {
            text.AppendLine(new string('-', 78));
            text.AppendLine($"{(v.Ok ? "OK   " : "WRONG")}  {v.FileName}   ({v.State})");
            text.AppendLine($"       document        {v.DocumentId}");
            text.AppendLine($"       open it         {Reporter.CrmLink(_crmUrl, v.DocumentId)}");
            text.AppendLine();
            text.AppendLine($"       old file on server   {YesNo(v.OldFileOnServer)}");
            text.AppendLine($"       old record in CRM    {YesNo(v.OldRecordInCrm)}");
            text.AppendLine($"       new file on server   {YesNo(v.NewFileOnServer)}");
            text.AppendLine($"       new record in CRM    {YesNo(v.NewRecordInCrm)}");
            text.AppendLine($"       record's path        {v.NewRecordPath ?? "(none)"}");
            text.AppendLine($"       document points at   {v.DocumentPointsAt?.ToString() ?? "(nothing)"}");

            if (!v.Ok)
            {
                text.AppendLine();
                foreach (var p in v.Problems) text.AppendLine($"       PROBLEM  {p}");
            }

            text.AppendLine();
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string? ReadFilePath(string? recordJson)
    {
        if (string.IsNullOrWhiteSpace(recordJson)) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(recordJson);
            return json.RootElement.TryGetProperty("mocd_filepath", out var v) &&
                   v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
