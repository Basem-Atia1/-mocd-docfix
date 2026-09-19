using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>Which services a sitting is working on.</summary>
public enum LedgerScope
{
    /// <summary>The eight this tool was built for.</summary>
    Ours,

    /// <summary>Every service catalogue in CRM.</summary>
    All
}

/// <summary>
/// One or two ledger files, according to the scope, presented as one.
///
/// The two files hold sets that do not overlap — the services we work on, and every other — so a
/// document lives in exactly one of them and they can never disagree about it. That is why the
/// second file is "the others" rather than "all".
///
/// Two files rather than two tabs of one, and the reason is speed alone. An .xlsx is a single
/// zip archive, so the cost of a write follows the file: at 50,000 rows a save is eleven
/// seconds, and the ledger is saved after every corrected document. Keeping the other services
/// in their own file means a sitting scoped to the eight never opens it and never pays for it.
/// </summary>
public sealed class LedgerSet : ILedger
{
    private readonly LedgerStore _ours;
    private readonly LedgerStore? _others;
    private readonly HashSet<string> _ourCatalogues;

    /// <summary>
    /// Which file each document was last read from, so a move between them can be noticed. A
    /// document type moved to another service in CRM moves its row, and silently that reads as a
    /// row vanishing from one file and appearing in another.
    /// </summary>
    private readonly Dictionary<Guid, bool> _wasOurs = new();

    /// <param name="path">The .xlsx of the services we work on. The other is derived from it.</param>
    /// <param name="ourCatalogues">
    /// The services belonging in the first file. Everything else belongs in the second.
    /// </param>
    public LedgerSet(string path, LedgerScope scope, IReadOnlyList<Guid> ourCatalogues)
    {
        Scope = scope;

        _ours = new LedgerStore(path);

        _others = scope == LedgerScope.All
            ? new LedgerStore(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(path)!,
                System.IO.Path.GetFileNameWithoutExtension(path) + "-other-services.xlsx"))
            : null;

        _ourCatalogues = ourCatalogues
            .Select(c => c.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public LedgerScope Scope { get; }

    /// <summary>The file the operator is told about — the one they work in.</summary>
    public string Path => _ours.Path;

    /// <summary>The other services' file, when the scope has one.</summary>
    public string? OtherPath => _others?.Path;

    /// <summary>
    /// How many rows changed file on the last write, because their document type was moved to a
    /// different service in CRM.
    /// </summary>
    public int LastMoved { get; private set; }

    public bool Exists => _ours.Exists || (_others?.Exists ?? false);

    public bool CanWrite() => _ours.CanWrite() && (_others?.CanWrite() ?? true);

    /// <summary>Set on both stores, so either file being open in Excel asks the same question.</summary>
    public Func<string, bool>? AskToRetry
    {
        set
        {
            _ours.AskToRetry = value;
            if (_others is not null) _others.AskToRetry = value;
        }
    }

    public string PreviousDirectory => _ours.PreviousDirectory;

    public IReadOnlyList<LedgerRow> Read()
    {
        var rows = new List<LedgerRow>();

        foreach (var row in _ours.Read())
        {
            _wasOurs[row.DocId] = true;
            rows.Add(row);
        }

        if (_others is not null)
            foreach (var row in _others.Read())
            {
                _wasOurs[row.DocId] = false;
                rows.Add(row);
            }

        return rows;
    }

    public void Write(IReadOnlyList<LedgerRow> rows)
    {
        LastMoved = 0;

        if (_others is null)
        {
            // Scoped to our services, so there is one file and everything goes in it — including
            // any row whose service is not one of ours. Such a row can only have got there by
            // being worked on when the scope was wider, and dropping it would lose a pending
            // delete that nobody could then find.
            _ours.Write(rows);
            return;
        }

        var mine = new List<LedgerRow>();
        var theirs = new List<LedgerRow>();
        var moved = 0;

        foreach (var row in rows)
        {
            var ours = IsOurs(row);

            if (_wasOurs.TryGetValue(row.DocId, out var before) && before != ours) moved++;

            (ours ? mine : theirs).Add(row);
            _wasOurs[row.DocId] = ours;
        }

        LastMoved = moved;

        _ours.Write(mine);
        _others.Write(theirs);
    }

    /// <summary>
    /// A row with no service catalogue on it at all counts as ours. It is almost always a row
    /// whose document CRM no longer returns, and the file we work in is where it can be seen.
    /// </summary>
    private bool IsOurs(LedgerRow row) =>
        row.ServiceCatalogueId.Length == 0 || _ourCatalogues.Contains(row.ServiceCatalogueId);
}
