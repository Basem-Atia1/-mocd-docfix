namespace MocdDocFix.Config;

/// <summary>Non-secret settings for one environment. Secrets live in ISecretStore.</summary>
/// <param name="Scope">
/// Which services this environment was last worked on: "ours" or "all".
///
/// Remembered per environment rather than globally, because dev may be surveying every catalogue
/// while pre-prod — over fifty thousand documents — stays on the eight. Anything unrecognised,
/// including a config file written by an earlier build, means "ours".
/// </param>
public sealed record EnvironmentConfig(
    string FileServiceBaseUrl,
    string CrmUrl,
    string CrmDomain,
    string CrmUser,
    bool IsProduction,
    string Scope = "ours");
