using MocdDocFix.Config;
using Xunit;

namespace MocdDocFix.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-cfg-" + Guid.NewGuid());
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public ConfigStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Load_on_a_missing_file_returns_defaults_with_the_seven_in_scope_catalogues()
    {
        var store = new ConfigStore(ConfigPath, new InMemorySecretStore());

        var cfg = store.Load();

        Assert.Equal(7, cfg.ServiceCatalogues.Count);
        Assert.Contains(Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"), cfg.ServiceCatalogues);
        Assert.Contains(Guid.Parse("930f636a-077a-f111-b119-005056010908"), cfg.ServiceCatalogues);

        // Membership Managment, removed from scope on 2026-09-13 at the operator's instruction.
        Assert.DoesNotContain(Guid.Parse("6bcb221c-6c2b-f111-b119-005056010908"), cfg.ServiceCatalogues);

        Assert.Empty(cfg.Environments);
    }

    [Fact]
    public void Save_then_load_round_trips_an_environment()
    {
        var store = new ConfigStore(ConfigPath, new InMemorySecretStore());
        var cfg = store.Load();
        cfg.Environments["dev"] = new EnvironmentConfig(
            "http://devfiles.mocd.gov.ae:83",
            "https://devdigitalplatform.mocd.gov.ae/MoCD",
            "MOCD", "svc.docfix", IsProduction: false);

        store.Save(cfg);
        var reloaded = new ConfigStore(ConfigPath, new InMemorySecretStore()).Load();

        Assert.True(reloaded.Environments.ContainsKey("dev"));
        Assert.Equal("MOCD", reloaded.Environments["dev"].CrmDomain);
        Assert.False(reloaded.Environments["dev"].IsProduction);
    }

    [Fact]
    public void Secrets_are_never_written_into_the_config_file()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("dev:apiKey", "SUPER-SECRET-KEY");
        secrets.Set("dev:crmPassword", "hunter2");
        var store = new ConfigStore(ConfigPath, secrets);
        var cfg = store.Load();
        cfg.Environments["dev"] = new EnvironmentConfig("http://x", "https://y", "MOCD", "u", false);
        store.Save(cfg);

        var text = File.ReadAllText(ConfigPath);

        Assert.DoesNotContain("SUPER-SECRET-KEY", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void Resolve_builds_the_endpoint_urls_and_injects_secrets()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("dev:apiKey", "KEY123");
        secrets.Set("dev:crmPassword", "PW123");
        var store = new ConfigStore(ConfigPath, secrets);
        var cfg = store.Load();
        cfg.Environments["dev"] = new EnvironmentConfig(
            "http://devfiles.mocd.gov.ae:83", "https://crm/MoCD", "MOCD", "svc", false);
        store.Save(cfg);

        var env = store.Resolve("dev");

        Assert.Equal("http://devfiles.mocd.gov.ae:83/api/File/Upload", env.UploadUrl);
        Assert.Equal("http://devfiles.mocd.gov.ae:83/api/File/Download?path=", env.DownloadUrlPrefix);
        Assert.Equal("http://devfiles.mocd.gov.ae:83/api/File/Delete?path=", env.DeleteUrlPrefix);
        Assert.Equal("KEY123", env.ApiKey);
        Assert.Equal("PW123", env.CrmPassword);
    }

    [Fact]
    public void A_trailing_slash_on_the_base_url_does_not_double_up()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("dev:apiKey", "K");
        secrets.Set("dev:crmPassword", "P");
        var store = new ConfigStore(ConfigPath, secrets);
        var cfg = store.Load();
        cfg.Environments["dev"] = new EnvironmentConfig("http://devfiles/", "https://crm/MoCD", "D", "u", false);
        store.Save(cfg);

        Assert.Equal("http://devfiles/api/File/Upload", store.Resolve("dev").UploadUrl);
    }

    [Fact]
    public void Resolve_throws_for_an_unknown_environment()
    {
        var store = new ConfigStore(ConfigPath, new InMemorySecretStore());

        var ex = Assert.Throws<InvalidOperationException>(() => store.Resolve("prod"));

        Assert.Contains("not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_throws_when_a_secret_is_missing()
    {
        var store = new ConfigStore(ConfigPath, new InMemorySecretStore());
        var cfg = store.Load();
        cfg.Environments["dev"] = new EnvironmentConfig("http://x", "https://y", "D", "u", false);
        store.Save(cfg);

        var ex = Assert.Throws<InvalidOperationException>(() => store.Resolve("dev"));

        Assert.Contains("apiKey", ex.Message);
    }
}
