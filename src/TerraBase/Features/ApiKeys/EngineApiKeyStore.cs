using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.ApiKeys;

internal sealed class EngineApiKeyStore(SystemCatalog catalog, TimeProvider time) : IApiKeyStore
{
    private const string Kind = "api_keys";
    private const string Hashes = "api_key_hashes";

    internal const string SecretPrefix = "tb_";

    public static string ProjectKeys(string projectId) => ProjectState(projectId) + "api_keys";

    public async ValueTask<CreateApiKeyResult> CreateAsync(
        string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken,
        DateTimeOffset? expiresAt = null)
    {
        var secret = NewSecret();
        return await catalog.WriteAsync<CreateApiKeyResult>(async transaction =>
            await EngineManagementService.ScopeExistsUncheckedAsync(transaction, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false)
            && (projectId is null || !await ProjectClosure.IsClosedAsync(transaction, projectId, cancellationToken).ConfigureAwait(false))
                ? new IssuedApiKey(await StoreAsync(transaction, secret, name, scopes, organizationId, projectId, databaseId, cancellationToken, expiresAt).ConfigureAwait(false), secret)
                : new NotFound(),
            static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Found<ApiKey>> FindAsync(string secret, CancellationToken cancellationToken)
    {
        if (!secret.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            return new NotFound();
        }

        var stored = await catalog.ReadUncheckedAsync<StoredRecord?>(async transaction =>
        {
            var hash = await transaction.GetAsync(Hashes, Hash(secret), cancellationToken).ConfigureAwait(false);
            if (hash is null)
            {
                return null;
            }

            var keyId = JsonSerializer.Deserialize(hash.Json.Span, TerraBaseJsonContext.Default.String);
            if (!EntityIds.IsValid(keyId))
            {
                throw new InvalidDataException("the API key lookup names an invalid id");
            }

            return await transaction.GetAsync(Kind, keyId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return stored is null || EntityPayload.Read(stored, ApiKeysJsonContext.Default.ApiKey) is not { OrganizationId: not null } key
            || key.ExpiresAt <= time.GetUtcNow()
            ? new NotFound()
            : key;
    }

    public async ValueTask<RevocationResult> RevokeAsync(string keyId, CancellationToken cancellationToken) =>
        await catalog.WriteAsync<RevocationResult>(async transaction =>
        {
            var key = await GetAsync(transaction, Kind, keyId, ApiKeysJsonContext.Default.ApiKey, cancellationToken).ConfigureAwait(false);
            if (key is null)
            {
                return new NotFound();
            }

            var hash = await GetAsync(transaction, Hashes, keyId, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false)
                ?? (key.ProjectId is { } owner
                    ? (await GetAsync(transaction, ProjectKeys(owner), keyId, ApiKeysJsonContext.Default.ProjectKeyEntry, cancellationToken).ConfigureAwait(false))?.Hash
                    : null)
                ?? throw new InvalidOperationException("the API key has no secret lookup");
            await RevokeCoreAsync(transaction, keyId, hash, key.ProjectId, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);

    internal static async ValueTask RevokeProjectBatchAsync(IStorageTransaction transaction, string project, int limit, long maxBytes, CancellationToken token)
    {
        var page = await transaction.ListAsync(ProjectKeys(project), null, limit, maxBytes, token).ConfigureAwait(false);
        foreach (var stored in page.Records)
        {
            var entry = EntityPayload.Read(stored, ApiKeysJsonContext.Default.ProjectKeyEntry)
                ?? throw new InvalidOperationException("a closing project's API key is invalid");
            await RevokeCoreAsync(transaction, stored.Id, entry.Hash, project, token).ConfigureAwait(false);
        }
    }

    private static async ValueTask RevokeCoreAsync(IStorageTransaction transaction, string keyId, string hash, string? projectId, CancellationToken token)
    {
        await transaction.DeleteAsync(Kind, keyId, token).ConfigureAwait(false);
        await transaction.DeleteAsync(Hashes, hash, token).ConfigureAwait(false);
        await transaction.DeleteAsync(Hashes, keyId, token).ConfigureAwait(false);
        if (projectId is { } project)
        {
            await transaction.DeleteAsync(ProjectKeys(project), keyId, token).ConfigureAwait(false);
        }
    }

    private async ValueTask<ApiKey> StoreAsync(
        IStorageTransaction transaction, string secret, string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId,
        CancellationToken cancellationToken, DateTimeOffset? expiresAt = null)
    {
        var key = new ApiKey(EntityIds.New(time).ToString(), name, scopes, organizationId, projectId, databaseId, expiresAt);
        var hash = Hash(secret);
        await PutAsync(transaction, Kind, key.Id, key, ApiKeysJsonContext.Default.ApiKey, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, Hashes, hash, key.Id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, Hashes, key.Id, hash, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
        if (projectId is not null)
        {
            await PutAsync(transaction, ProjectKeys(projectId), key.Id, new ProjectKeyEntry(hash), ApiKeysJsonContext.Default.ProjectKeyEntry, cancellationToken).ConfigureAwait(false);
        }

        return key;
    }

    private static string NewSecret() => SecretPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

internal sealed record ProjectKeyEntry(string Hash);
