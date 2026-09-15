using System.Reflection;
using CsvHelper.Configuration.Attributes;

namespace MocdDocFix.Domain;

/// <param name="Header">The name the operator reads, exactly as the CSV writes it.</param>
/// <param name="Read">That cell's value, as text.</param>
public sealed record LedgerColumn(string Header, Func<LedgerRow, string> Read);

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
                row => p.GetValue(row)?.ToString() ?? string.Empty))
            .ToList();
}
