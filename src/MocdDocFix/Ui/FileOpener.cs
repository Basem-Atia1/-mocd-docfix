using System.Diagnostics;

namespace MocdDocFix.Ui;

public interface IFileOpener
{
    void Open(string path);
}

public sealed class ShellFileOpener : IFileOpener
{
    public void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Never let a viewer problem abort a migration.
            Console.WriteLine($"  (could not open {path}: {ex.Message})");
        }
    }
}

/// <summary>For tests and unattended runs.</summary>
public sealed class NullFileOpener : IFileOpener
{
    public List<string> Opened { get; } = new();
    public void Open(string path) => Opened.Add(path);
}
