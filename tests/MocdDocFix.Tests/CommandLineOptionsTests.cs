using MocdDocFix.Cli;
using Xunit;

namespace MocdDocFix.Tests;

public class CommandLineOptionsTests
{
    [Fact]
    public void Parses_a_simple_scan()
    {
        var o = CommandLineOptions.Parse(new[] { "scan", "--env", "dev" });

        Assert.Equal("scan", o.Command);
        Assert.Equal("dev", o.Environment);
        Assert.Null(o.Error);
    }

    [Fact]
    public void Parses_a_comma_separated_identifier_list()
    {
        var o = CommandLineOptions.Parse(new[] { "targeted", "--env", "dev", "--docs", "a.jpg,b.jpg, c.jpg " });

        Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg" }, o.Identifiers);
    }

    [Fact]
    public void Parses_repeated_docs_flags()
    {
        var o = CommandLineOptions.Parse(new[] { "targeted", "--docs", "a.jpg", "--docs", "b.jpg" });

        Assert.Equal(new[] { "a.jpg", "b.jpg" }, o.Identifiers);
    }

    [Fact]
    public void Parses_the_boolean_flags()
    {
        var o = CommandLineOptions.Parse(new[] { "targeted", "--force-review", "--dry-run", "--confirm-production" });

        Assert.True(o.ForceReview);
        Assert.True(o.DryRun);
        Assert.True(o.ConfirmProduction);
    }

    [Fact]
    public void An_unknown_flag_is_an_error_not_a_silent_ignore()
    {
        var o = CommandLineOptions.Parse(new[] { "scan", "--enviroment", "dev" });

        Assert.NotNull(o.Error);
        Assert.Contains("--enviroment", o.Error);
    }

    [Fact]
    public void An_unknown_command_is_an_error()
    {
        Assert.Contains("frobnicate", CommandLineOptions.Parse(new[] { "frobnicate" }).Error!);
    }

    [Fact]
    public void No_arguments_starts_the_guided_menu_rather_than_erroring()
    {
        var o = CommandLineOptions.Parse(Array.Empty<string>());

        Assert.Null(o.Error);
        Assert.Equal("guided", o.Command);
    }

    [Fact]
    public void Guided_can_also_be_asked_for_by_name_with_an_environment()
    {
        var o = CommandLineOptions.Parse(new[] { "guided", "--env", "dev" });

        Assert.Equal("guided", o.Command);
        Assert.Equal("dev", o.Environment);
        Assert.Null(o.Error);
    }

    [Fact]
    public void A_flag_with_a_missing_value_is_an_error()
    {
        Assert.Contains("--env", CommandLineOptions.Parse(new[] { "scan", "--env" }).Error!);
    }
}
