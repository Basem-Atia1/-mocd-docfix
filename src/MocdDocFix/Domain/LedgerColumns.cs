using System.Reflection;
using CsvHelper.Configuration.Attributes;

namespace MocdDocFix.Domain;

/// <param name="Header">The name the operator reads, exactly as the CSV writes it.</param>
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
/// Derived by reflection rather than listed again here, so the workbook and the CSV cannot
/// drift: both take their order and their headers from the same attributes. Adding a column to
/// LedgerRow adds it to the workbook with no second edit.
/// </summary>
public static class LedgerColumns
{
    public static readonly IReadOnlyList<LedgerColumn> All = Build();

    private static IReadOnlyList<LedgerColumn> Build() =>
        typeof(LedgerRow)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<IndexAttribute>() is not null)
            .OrderBy(p => p.GetCustomAttribute<IndexAttribute>()!.Index)
            .Select(p => new LedgerColumn(
                p.GetCustomAttribute<NameAttribute>()?.Names.FirstOrDefault() ?? p.Name,
                row => p.GetValue(row)?.ToString() ?? string.Empty,
                (row, text) => p.SetValue(row, Convert(p.PropertyType, text))))
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
