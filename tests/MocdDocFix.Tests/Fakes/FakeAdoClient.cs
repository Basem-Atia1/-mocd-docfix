using MocdDocFix.Clients;
using MocdDocFix.Domain;

namespace MocdDocFix.Tests.Fakes;

public sealed class FakeAdoClient : IAdoClient
{
    /// <summary>Search phrase → the work item titles it finds.</summary>
    public Dictionary<string, List<string>> Titles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every phrase searched for, in order — so a test can prove it asked once.</summary>
    public List<string> Searched { get; } = new();

    public Exception? Throws { get; set; }

    /// <summary>Called on every search, with the phrase and how many searches have now run —
    /// so a test can change what the backlog holds between one look and the next.</summary>
    public Action<string, int>? OnSearched { get; set; }

    /// <summary>Search phrase → the spreadsheets attached to the work items it finds.</summary>
    public Dictionary<string, List<AdoAttachment>> Attachments { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True to behave like an attachment the sign-in is not allowed to fetch.</summary>
    public bool DownloadFails { get; set; }

    public Task<IReadOnlyList<AdoAttachment>> FindSpreadsheetsAsync(string phrase, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AdoAttachment>>(
            Attachments.TryGetValue(phrase, out var found) ? found : new List<AdoAttachment>());

    public Task<bool> DownloadAttachmentAsync(AdoAttachment attachment, string toPath, CancellationToken ct)
    {
        if (DownloadFails) return Task.FromResult(false);

        Directory.CreateDirectory(Path.GetDirectoryName(toPath)!);
        File.WriteAllText(toPath, "pretend workbook");
        return Task.FromResult(true);
    }

    public string LinkTo(int workItemId) =>
        $"https://devops.mocd.gov.ae/MOCD/NPO%20-%20Phase%202/_workitems/edit/{workItemId}";

    public Task<IReadOnlyList<AdoHit>> FindByTitleAsync(string phrase, CancellationToken ct)
    {
        Searched.Add(phrase);
        OnSearched?.Invoke(phrase, Searched.Count);

        if (Throws is not null) throw Throws;

        var found = Titles.TryGetValue(phrase, out var titles) ? titles : new List<string>();

        IReadOnlyList<AdoHit> hits = found
            .Select((t, i) => new AdoHit(1000 + i, t, DocumentTypeAuthority.ServiceInTitle(t)))
            .ToList();

        return Task.FromResult(hits);
    }
}
