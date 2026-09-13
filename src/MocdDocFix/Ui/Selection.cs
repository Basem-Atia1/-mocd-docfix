namespace MocdDocFix.Ui;

/// <param name="Indexes">Zero-based, ordered, no duplicates. Empty when <paramref name="Error"/> is set.</param>
public sealed record SelectionResult(IReadOnlyList<int> Indexes, string? Error);

/// <summary>
/// Turns what the operator types at a numbered list — "1,3,5", "3-6", "all" — into row indexes.
/// This is what spares them typing GUIDs (spec 2026-09-13 section 6.3).
/// </summary>
public static class Selection
{
    public static SelectionResult Parse(string input, int count)
    {
        if (count <= 0) return Fail("There is nothing in this list to choose from.");

        var trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0) return Fail("Nothing typed. Choose at least one, or type all.");

        if (trimmed is "*" || trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
            return new SelectionResult(Enumerable.Range(0, count).ToList(), null);

        var chosen = new SortedSet<int>();

        foreach (var part in trimmed.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-');

            if (ends.Length == 1)
            {
                if (!TryOne(ends[0], count, out var only, out var error)) return Fail(error);
                chosen.Add(only);
                continue;
            }

            if (ends.Length != 2)
                return Fail($"'{part}' is not a number or a range. Use 3, or 3-6, or all.");

            if (!TryOne(ends[0], count, out var from, out var fromError)) return Fail(fromError);
            if (!TryOne(ends[1], count, out var to, out var toError)) return Fail(toError);

            // A backwards range is a typo with an obvious meaning, not an error.
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) chosen.Add(i);
        }

        return new SelectionResult(chosen.ToList(), null);
    }

    private static bool TryOne(string text, int count, out int index, out string error)
    {
        index = -1;
        error = string.Empty;

        if (!int.TryParse(text.Trim(), out var number))
        {
            error = $"'{text.Trim()}' is not a number. Use 3, or 3-6, or all.";
            return false;
        }

        if (number < 1 || number > count)
        {
            error = $"{number} is not in the list — the numbers run 1 to {count}.";
            return false;
        }

        index = number - 1;
        return true;
    }

    private static SelectionResult Fail(string message) => new(Array.Empty<int>(), message);
}
