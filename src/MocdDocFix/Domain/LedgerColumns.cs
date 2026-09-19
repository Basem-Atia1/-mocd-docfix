using System.Reflection;

namespace MocdDocFix.Domain;

/// <param name="Header">The name the operator reads at the top of the column.</param>
/// <param name="Read">That cell's value, as text.</param>
/// <param name="Set">
/// Puts a cell back on a row when the workbook is read. Anything that will not convert is left
/// at the property's default rather than throwing — one unreadable cell must not cost the
/// whole ledger.
/// </param>
public sealed record LedgerColumn(
    string Header, Func<LedgerRow, string> Read, Action<LedgerRow, string> Set);

/// <summary>
/// The ledger's columns, in order, read off <see cref="LedgerRow"/> itself.
///
/// Derived by reflection rather than listed again here, so the header the operator reads and the
/// property the tool writes cannot drift apart. Adding a column to LedgerRow adds it to the
/// workbook with no second edit.
/// </summary>
public static class LedgerColumns
{
    public static readonly IReadOnlyList<LedgerColumn> All = Build();

    private static IReadOnlyList<LedgerColumn> Build() =>
        typeof(LedgerRow)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Column: p.GetCustomAttribute<ColumnAttribute>()))
            .Where(x => x.Column is not null)
            .OrderBy(x => x.Column!.Index)
            .Select(x => new LedgerColumn(
                x.Column!.Header,
                row => x.Property.GetValue(row)?.ToString() ?? string.Empty,
                (row, text) => x.Property.SetValue(row, Convert(x.Property.PropertyType, text))))
            .ToList();

    /// <summary>
    /// A cell back into its property. The ledger holds three kinds — text, the row number and
    /// two GUIDs — and an unreadable one yields the default rather than an exception, because a
    /// single bad cell must not make the whole workbook unopenable.
    /// </summary>
    private static object? Convert(Type type, string text)
    {
        if (type == typeof(string)) return text;
        if (type == typeof(int)) return int.TryParse(text, out var n) ? n : 0;
        if (type == typeof(Guid)) return Guid.TryParse(text, out var g) ? g : Guid.Empty;

        return null;
    }
}
