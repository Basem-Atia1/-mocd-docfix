using MocdDocFix.Cli;
using MocdDocFix.Config;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class EnvironmentPickerTests
{
    private readonly List<(string Name, NewEnvironment Env)> _saved = new();
    private AppConfig _config = new();

    private EnvironmentPicker Picker(ScriptedPrompts prompts) =>
        new(prompts, () => _config, (name, env) => { _saved.Add((name, env)); Configure(name, env.Config.IsProduction); });

    private void Configure(string name, bool isProduction = false) =>
        _config.Environments[name] = new EnvironmentConfig(
            "http://files:83", "https://crm/MoCD", "msa", "someone", isProduction);

    // ---- the happy path ----

    [Fact]
    public void A_configured_environment_given_on_the_command_line_is_used_without_asking()
    {
        Configure("dev");
        var prompts = new ScriptedPrompts();

        Assert.Equal("dev", Picker(prompts).Choose("dev", confirmProductionFlag: false));
        Assert.Empty(prompts.Questions);
    }

    [Fact]
    public void Choosing_a_ready_environment_returns_it()
    {
        Configure("dev");

        Assert.Equal("dev", Picker(new ScriptedPrompts("1")).Choose(null, false));
    }

    [Fact]
    public void Every_well_known_environment_is_listed_even_when_none_are_configured()
    {
        var prompts = new ScriptedPrompts("q");

        Picker(prompts).Choose(null, false);

        Assert.True(prompts.Said("dev"));
        Assert.True(prompts.Said("test"));
        Assert.True(prompts.Said("preprod"));
        Assert.True(prompts.Said("prod"));
    }

    [Fact]
    public void The_list_says_which_are_ready_and_which_are_not()
    {
        Configure("dev");
        var prompts = new ScriptedPrompts("q");

        Picker(prompts).Choose(null, false);

        Assert.True(prompts.Said("ready"));
        Assert.True(prompts.Said("not set up yet"));
    }

    [Fact]
    public void A_ready_environment_shows_its_crm_url_so_you_can_see_where_you_are_pointed()
    {
        Configure("dev");
        var prompts = new ScriptedPrompts("q");

        Picker(prompts).Choose(null, false);

        Assert.True(prompts.Said("https://crm/MoCD"));
    }

    // ---- never dead-ends ----

    [Fact]
    public void An_unconfigured_choice_offers_setup_instead_of_exiting()
    {
        var prompts = new ScriptedPrompts("2", "2", "1");   // test -> pick another -> dev...
        Configure("dev");

        var chosen = Picker(prompts).Choose(null, false);

        Assert.True(prompts.Said("not been set up"));
        Assert.Equal("dev", chosen);
    }

    [Fact]
    public void An_unconfigured_environment_named_on_the_command_line_asks_rather_than_failing()
    {
        Configure("dev");
        var prompts = new ScriptedPrompts("1");   // falls through to the list, picks dev

        var chosen = Picker(prompts).Choose("test", false);

        Assert.True(prompts.Said("test"));
        Assert.Equal("dev", chosen);
    }

    [Fact]
    public void Setting_one_up_asks_for_everything_and_saves_it()
    {
        var prompts = new ScriptedPrompts(
            "2",                        // choose test
            "1",                        // set it up now
            "http://files:83",          // file service base url
            "https://crm/MoCD",         // crm url
            "msa",                      // domain
            "itworx.Someone",           // user
            "a-password",               // crm password
            "an-api-key",               // api key
            "y");                       // save

        var chosen = Picker(prompts).Choose(null, false);

        Assert.Equal("test", chosen);
        var (name, saved) = Assert.Single(_saved);
        Assert.Equal("test", name);
        Assert.Equal("http://files:83", saved.Config.FileServiceBaseUrl);
        Assert.Equal("https://crm/MoCD", saved.Config.CrmUrl);
        Assert.Equal("msa", saved.Config.CrmDomain);
        Assert.Equal("itworx.Someone", saved.Config.CrmUser);
        Assert.Equal("a-password", saved.CrmPassword);
        Assert.Equal("an-api-key", saved.ApiKey);
        Assert.False(saved.Config.IsProduction);
    }

    [Fact]
    public void Setup_never_echoes_the_password_back()
    {
        var prompts = new ScriptedPrompts("2", "1", "http://f", "https://c", "d", "u", "s3cret", "k", "y");

        Picker(prompts).Choose(null, false);

        Assert.DoesNotContain("s3cret", prompts.Transcript);
    }

    [Fact]
    public void Setup_shows_what_it_will_save_and_can_be_abandoned()
    {
        var prompts = new ScriptedPrompts(
            "2", "1", "http://f", "https://c", "d", "u", "pw", "k",
            "n",        // do not save
            "q");       // back at the list, quit

        Assert.Null(Picker(prompts).Choose(null, false));
        Assert.Empty(_saved);
    }

    [Fact]
    public void Setup_refuses_a_blank_url_and_asks_again()
    {
        var prompts = new ScriptedPrompts(
            "2", "1",
            "", "http://f",             // blank file service url, then a real one
            "https://c", "d", "u", "pw", "k", "y");

        Assert.Equal("test", Picker(prompts).Choose(null, false));
        Assert.True(prompts.Said("cannot be blank"));
    }

    // ---- production ----

    [Fact]
    public void Production_cannot_be_chosen_from_the_list_without_the_flag()
    {
        Configure("dev");
        Configure("prod", isProduction: true);
        var prompts = new ScriptedPrompts("4", "1");   // prod refused, then dev

        var chosen = Picker(prompts).Choose(null, confirmProductionFlag: false);

        Assert.Equal("dev", chosen);
        Assert.True(prompts.Said("--confirm-production"));
    }

    [Fact]
    public void Production_with_the_flag_still_needs_the_name_typed_in_full()
    {
        Configure("prod", isProduction: true);
        var prompts = new ScriptedPrompts("4", "prod");

        Assert.Equal("prod", Picker(prompts).Choose(null, confirmProductionFlag: true));
    }

    [Fact]
    public void A_mistyped_production_name_does_not_select_production()
    {
        Configure("dev");
        Configure("prod", isProduction: true);
        var prompts = new ScriptedPrompts("4", "PROD", "1");   // wrong case, back to the list

        Assert.Equal("dev", Picker(prompts).Choose(null, confirmProductionFlag: true));
    }

    [Fact]
    public void Production_named_on_the_command_line_without_the_flag_is_refused()
    {
        Configure("dev");
        Configure("prod", isProduction: true);
        var prompts = new ScriptedPrompts("1");

        Assert.Equal("dev", Picker(prompts).Choose("prod", confirmProductionFlag: false));
        Assert.True(prompts.Said("--confirm-production"));
    }

    [Fact]
    public void The_production_row_is_marked_in_the_list()
    {
        Configure("prod", isProduction: true);
        var prompts = new ScriptedPrompts("q");

        Picker(prompts).Choose(null, false);

        Assert.True(prompts.Said("PRODUCTION"));
    }

    // ---- quitting ----

    [Fact]
    public void Quitting_returns_nothing_chosen()
    {
        Configure("dev");

        Assert.Null(Picker(new ScriptedPrompts("q")).Choose(null, false));
    }

    [Fact]
    public void Quitting_from_the_setup_offer_returns_nothing_chosen()
    {
        var prompts = new ScriptedPrompts("1", "3");   // dev (unconfigured) -> quit

        Assert.Null(Picker(prompts).Choose(null, false));
    }

    [Fact]
    public void An_environment_configured_outside_the_well_known_four_is_still_offered()
    {
        Configure("sandbox");
        var prompts = new ScriptedPrompts("5");

        Assert.Equal("sandbox", Picker(prompts).Choose(null, false));
    }
}
