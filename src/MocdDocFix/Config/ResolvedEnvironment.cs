namespace MocdDocFix.Config;

/// <summary>An environment with its secrets and fully-built endpoint URLs.</summary>
public sealed record ResolvedEnvironment(
    string Name,
    string FileServiceBaseUrl,
    string UploadUrl,
    string DownloadUrlPrefix,
    string DeleteUrlPrefix,
    string ApiKey,
    string CrmUrl,
    string CrmDomain,
    string CrmUser,
    string CrmPassword,
    bool IsProduction);
