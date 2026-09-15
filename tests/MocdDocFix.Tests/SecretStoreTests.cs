using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using MocdDocFix.Config;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The secrets file is encrypted to one Windows account. That is the point of it — but it means
/// a copied app folder meets a file it cannot read, and how the tool behaves at that moment
/// decides whether somebody sees a sentence or a stack trace.
/// </summary>
[SupportedOSPlatform("windows")]
public class SecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-secrets-" + Guid.NewGuid());

    public SecretStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string Path_ => System.IO.Path.Combine(_dir, "secrets.dat");

    [Fact]
    public void A_secret_written_is_a_secret_read_back()
    {
        new DpapiSecretStore(Path_).Set("dev:apiKey", "KEY123");

        Assert.Equal("KEY123", new DpapiSecretStore(Path_).Get("dev:apiKey"));
    }

    [Fact]
    public void No_file_yet_is_not_a_problem()
    {
        var store = new DpapiSecretStore(Path_);

        Assert.Null(store.Get("dev:apiKey"));
        Assert.Null(store.Problem);
    }

    /// <summary>
    /// A secrets.dat from another Windows account cannot be decrypted here, and DPAPI throws.
    /// Thrown from a constructor that was the crash on startup — before the tool had said a
    /// single word about what was wrong or what to do.
    /// </summary>
    [Fact]
    public void A_file_this_account_cannot_decrypt_is_explained_rather_than_thrown()
    {
        File.WriteAllBytes(Path_, RandomNumberGenerator.GetBytes(256));

        var store = new DpapiSecretStore(Path_);

        Assert.Null(store.Get("dev:apiKey"));
        Assert.NotNull(store.Problem);
        Assert.Contains("different Windows account", store.Problem);
    }

    [Fact]
    public void A_damaged_file_is_explained_too()
    {
        File.WriteAllText(Path_, "this is not encrypted anything", Encoding.UTF8);

        var store = new DpapiSecretStore(Path_);

        Assert.NotNull(store.Problem);
    }

    /// <summary>
    /// And it must still be usable afterwards: the remedy is to enter the secrets again, which
    /// only works if writing over the unreadable file succeeds.
    /// </summary>
    [Fact]
    public void Secrets_can_be_set_again_over_a_file_that_could_not_be_read()
    {
        File.WriteAllBytes(Path_, RandomNumberGenerator.GetBytes(256));

        new DpapiSecretStore(Path_).Set("dev:crmPassword", "new one");

        var reopened = new DpapiSecretStore(Path_);
        Assert.Equal("new one", reopened.Get("dev:crmPassword"));
        Assert.Null(reopened.Problem);
    }
}
