using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.ApiKeys;

/// <summary>
/// Keeps keys as documents in the <see cref="DatabaseName.System"/> database, keyed by the
/// SHA-256 of the secret. Secrets are 256 random bits, so an unsalted hash is sufficient.
/// </summary>
internal sealed class EngineApiKeyStore(IStorageEngine engine) : IApiKeyStore
{
    private const string Collection = "api_keys";
    private const string SecretPrefix = "tb_";

    public async ValueTask<(ApiKey Key, string Secret)> CreateAsync(string name, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        var secret = SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey(Base64Url(RandomNumberGenerator.GetBytes(12)), name, scopes);
        await engine.CreateDatabaseAsync(DatabaseName.System, cancellationToken).ConfigureAwait(false);
        var store = await engine.OpenDatabaseAsync(DatabaseName.System, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the system database disappeared after creation");
        var transaction = await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(key, TerraBaseJsonContext.Default.ApiKey);
            await transaction.PutAsync(Collection, Hash(secret), json, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return (key, secret);
    }

    public async ValueTask<ApiKey?> FindAsync(string secret, CancellationToken cancellationToken)
    {
        if (!secret.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var store = await engine.OpenDatabaseAsync(DatabaseName.System, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return null;
        }

        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var document = await transaction.GetAsync(Collection, Hash(secret), cancellationToken).ConfigureAwait(false);
            return document is null ? null : JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.ApiKey);
        }
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
