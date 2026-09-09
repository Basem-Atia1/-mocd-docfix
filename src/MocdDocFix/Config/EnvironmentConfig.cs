namespace MocdDocFix.Config;

/// <summary>Non-secret settings for one environment. Secrets live in ISecretStore.</summary>
public sealed record EnvironmentConfig(
    string FileServiceBaseUrl,
    string CrmUrl,
    string CrmDomain,
    string CrmUser,
    bool IsProduction);
