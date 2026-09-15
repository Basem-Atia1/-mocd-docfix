using System.Text;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// What broke, at length. The ledger's error column holds a sentence that fits a spreadsheet
/// cell; this holds the request, the response and the step, which is what is actually needed to
/// find out why.
/// </summary>
public sealed class ErrorLog
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);

    public ErrorLog(string path) => Path = path;

    public string Path { get; }

    public void Append(int number, int total, LedgerRow row, string step, string detail)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        var text = new StringBuilder();
        text.AppendLine(new string('-', 78));
        text.AppendLine($"[ {number}/{total} ]  {row.DocFileName}   {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"  document     {row.DocId}");
        text.AppendLine($"  record       {row.DocFileId}");
        text.AppendLine($"  name         {row.DocName}");
        text.AppendLine($"  step: {step}");
        text.AppendLine();

        foreach (var line in detail.Split('\n')) text.AppendLine("  " + line.TrimEnd());
        text.AppendLine();

        File.AppendAllText(Path, text.ToString(), Utf8);
    }
}
