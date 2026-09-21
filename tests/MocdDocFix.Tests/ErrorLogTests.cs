using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The log is where a step's reasons live now. The screen says how many there were, so if these
/// never reach the file they are not anywhere.
/// </summary>
public class ErrorLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-log-" + Guid.NewGuid());
    private readonly ErrorLog _log;

    public ErrorLogTests()
    {
        Directory.CreateDirectory(_dir);
        _log = new ErrorLog(Path.Combine(_dir, "errors-dev.txt"));
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Every_reason_reaches_the_file_under_its_heading()
    {
        _log.AppendLines("why the redo was refused",
            new[] { "row 4 · doc a3f1b2c4: no crm.json snapshot", "row 9 · doc 7c20a1f4: file gone" });

        var written = File.ReadAllText(_log.Path);

        Assert.Contains("why the redo was refused", written);
        Assert.Contains("no crm.json snapshot", written);
        Assert.Contains("file gone", written);
    }

    /// <summary>A step with nothing to report leaves no heading behind to read as one.</summary>
    [Fact]
    public void Nothing_is_written_when_there_is_nothing_to_say()
    {
        _log.AppendLines("what the check found", Array.Empty<string>());

        Assert.False(File.Exists(_log.Path));
    }

    /// <summary>Two steps in one sitting both keep their reasons.</summary>
    [Fact]
    public void A_second_call_appends_rather_than_replacing()
    {
        _log.AppendLines("first", new[] { "one" });
        _log.AppendLines("second", new[] { "two" });

        var written = File.ReadAllText(_log.Path);

        Assert.Contains("one", written);
        Assert.Contains("two", written);
    }
}
