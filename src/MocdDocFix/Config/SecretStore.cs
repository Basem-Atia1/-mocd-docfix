using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MocdDocFix.Config;

public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
}

/// <summary>For tests only.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value) => _values[key] = value;
}

/// <summary>
/// Secrets encrypted with Windows DPAPI, scoped to the current user. The file is unreadable
/// by any other Windows account and never leaves the machine.
/// </summary>
/// <remarks>
/// Guarded at runtime rather than annotated with [SupportedOSPlatform], so callers do not have
/// to be Windows-scoped too. The tool is Windows-only regardless (DPAPI, NTLM, ShellExecute),
/// so a non-Windows host fails loudly here instead of silently storing secrets in the clear.
/// </remarks>
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _cache;

    public DpapiSecretStore(string path)
    {
        RequireWindows();
        _path = path;
        _cache = Read();
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Secret storage uses Windows DPAPI. Run docfix on Windows.");
    }

    public string? Get(string key) => _cache.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string value)
    {
        _cache[key] = value;
        Write(_cache);
    }

    private Dictionary<string, string> Read()
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path)) return empty;
        if (!OperatingSystem.IsWindows()) return empty;

        var protectedBytes = File.ReadAllBytes(_path);
        var plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var json = Encoding.UTF8.GetString(plain);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? empty;
    }

    private void Write(Dictionary<string, string> values)
    {
        RequireWindows();
        if (!OperatingSystem.IsWindows()) return;   // narrows the type for the analyzer

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(values);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json), optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, protectedBytes);
    }
}
