using MocdDocFix.Cli;
using MocdDocFix.Config;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class EnvironmentSelectorTests
{
    private static AppConfig Config(bool withProd = true)
    {
        var c = AppConfig.Default();
        c.Environments["dev"] = new EnvironmentConfig("http://d", "https://d", "D", "u", false);
        if (withProd) c.Environments["prod"] = new EnvironmentConfig("http://p", "https://p", "P", "u", true);
        return c;
    }

    [Fact]
    public void A_non_production_environment_from_args_is_accepted()
        => Assert.Equal("dev", EnvironmentSelector.Select(new FakePrompts(), "dev", Config(), false));

    [Fact]
    public void Production_from_args_still_requires_typing_the_word()
    {
        var refused = new FakePrompts { TypedWordResponse = "" };
        Assert.Null(EnvironmentSelector.Select(refused, "prod", Config(), confirmProductionFlag: true));

        var confirmed = new FakePrompts { TypedWordResponse = "prod" };
        Assert.Equal("prod", EnvironmentSelector.Select(confirmed, "prod", Config(), confirmProductionFlag: true));
    }

    [Fact]
    public void Production_without_the_confirm_flag_is_refused_outright()
    {
        var prompts = new FakePrompts { TypedWordResponse = "prod" };

        Assert.Null(EnvironmentSelector.Select(prompts, "prod", Config(), confirmProductionFlag: false));
        Assert.Contains(prompts.Messages, m => m.Contains("--confirm-production"));
    }

    [Fact]
    public void An_unconfigured_environment_is_rejected()
        => Assert.Null(EnvironmentSelector.Select(new FakePrompts(), "test", Config(), false));

    [Fact]
    public void The_menu_never_offers_production_as_a_default_keypress()
    {
        var prompts = new FakePrompts { ReadLineResponse = "" };

        EnvironmentSelector.Select(prompts, null, Config(), false);

        var menu = string.Join("\n", prompts.Messages);
        Assert.Contains("dev", menu);
        Assert.Contains("PRODUCTION", menu);
        Assert.Contains(prompts.Questions, q => q.Contains("in full", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_typed_name_from_the_menu_is_resolved_like_an_argument()
    {
        var prompts = new FakePrompts { ReadLineResponse = "dev" };

        Assert.Equal("dev", EnvironmentSelector.Select(prompts, null, Config(), false));
    }
}
