using System.Text.Json;

namespace MocdDocFix.Config;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _configPath;
    private readonly ISecretStore _secrets;

    public ConfigStore(string configPath, ISecretStore secrets)
    {
        _configPath = configPath;
        _secrets = secrets;
    }

    /// <summary>Default location: %APPDATA%\mocd-docfix\ — outside the repo.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mocd-docfix");

    public AppConfig Load()
    {
        if (!File.Exists(_configPath)) return AppConfig.Default();

        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath));
        if (cfg is null) return AppConfig.Default();

        // A service added to the tool's scope since this file was written must not be missing
        // just because this machine has an older config.json. Falling back to the defaults only
        // when the stored list was empty meant exactly that: an install that already had a file
        // stayed on seven services and never saw the eighth arrive.
        //
        // The consequence, deliberately: taking one of these out by hand does not stick. Nothing
        // in the tool offers to remove a service from scope, so a missing one is a stale file
        // rather than somebody's decision.
        foreach (var (id, _) in AppConfig.InScopeServices)
            if (!cfg.ServiceCatalogues.Contains(id))
                cfg.ServiceCatalogues.Add(id);

        return cfg;
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, Json));
    }

    public ResolvedEnvironment Resolve(string envName)
    {
        var cfg = Load();
        if (!cfg.Environments.TryGetValue(envName, out var env))
            throw new InvalidOperationException(
                $"Environment '{envName}' is not configured. Run 'docfix config --env {envName}' to set it up.");

        var apiKey = _secrets.Get($"{envName}:apiKey")
            ?? throw new InvalidOperationException($"Missing secret 'apiKey' for environment '{envName}'.");
        var crmPassword = _secrets.Get($"{envName}:crmPassword")
            ?? throw new InvalidOperationException($"Missing secret 'crmPassword' for environment '{envName}'.");

        var baseUrl = env.FileServiceBaseUrl.TrimEnd('/');

        return new ResolvedEnvironment(
            Name: envName,
            FileServiceBaseUrl: baseUrl,
            UploadUrl: $"{baseUrl}/api/File/Upload",
            DownloadUrlPrefix: $"{baseUrl}/api/File/Download?path=",
            DeleteUrlPrefix: $"{baseUrl}/api/File/Delete?path=",
            ApiKey: apiKey,
            CrmUrl: env.CrmUrl.TrimEnd('/'),
            CrmDomain: env.CrmDomain,
            CrmUser: env.CrmUser,
            CrmPassword: crmPassword,
            IsProduction: env.IsProduction);
    }
}
