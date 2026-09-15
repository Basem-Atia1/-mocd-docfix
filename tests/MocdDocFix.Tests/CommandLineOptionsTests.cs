using MocdDocFix.Cli;
using Xunit;

namespace MocdDocFix.Tests;

public class CommandLineOptionsTests
{
    [Fact]
    public void Parses_a_simple_command()
    {
        var o = CommandLineOptions.Parse(new[] { "repair", "--env", "dev" });

        Assert.Equal("repair", o.Command);
        Assert.Equal("dev", o.Environment);
        Assert.Null(o.Error);
    }

    /// <summary>The four ledger modes, and config. Nothing else.</summary>
    [Theory]
    [InlineData("repair")]
    [InlineData("delete")]
    [InlineData("redo")]
    [InlineData("check")]
    [InlineData("config")]
    public void Every_command_the_tool_still_has_is_accepted(string command) =>
        Assert.Null(CommandLineOptions.Parse(new[] { command }).Error);

    /// <summary>
    /// The old pipeline's verbs went with it. Accepting one silently would run something other
    /// than what was typed.
    /// </summary>
    [Theory]
    [InlineData("scan")]
    [InlineData("backup")]
    [InlineData("migrate")]
    [InlineData("targeted")]
    public void A_command_from_the_old_pipeline_is_refused(string command)
    {
        var o = CommandLineOptions.Parse(new[] { command });

        Assert.NotNull(o.Error);
        Assert.Contains(command, o.Error);
    }

    [Fact]
    public void Parses_the_boolean_flags()
    {
        var o = CommandLineOptions.Parse(new[] { "repair", "--dry-run", "--confirm-production" });

        Assert.True(o.DryRun);
        Assert.True(o.ConfirmProduction);
    }

    [Fact]
    public void An_unknown_flag_is_an_error_not_a_silent_ignore()
    {
        var o = CommandLineOptions.Parse(new[] { "repair", "--enviroment", "dev" });

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
        Assert.Contains("--env", CommandLineOptions.Parse(new[] { "repair", "--env" }).Error!);
    }
}
