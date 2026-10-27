using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.ApiKeys;

/// <summary>
/// Keeps keys as documents in the system database (<see cref="IStorageEngine.OpenSystemDatabaseAsync"/>), keyed by ULID
/// with a SHA-256 secret lookup. Secrets are 256 random bits, so an unsalted hash is sufficient.
/// Project-scoped operator keys also have a hash entry in <see cref="ProjectKeys"/> so project closure can revoke them.
/// </summary>
internal sealed class EngineApiKeyStore(SystemCatalog catalog, TimeProvider time, ISystemDocumentReader documents) : IApiKeyStore
{
    private const string Collection = "api_keys";
    private const string Hashes = "api_key_hashes";

    /// <summary>Every API key secret starts with this; other bearer tokens (ts-auth access tokens) are not API keys.</summary>
    internal const string SecretPrefix = "tb_";

    /// <summary>The project-scoped operator keys to revoke during project closure, keyed by key id.</summary>
    public static string ProjectKeys(string projectId) => ProjectState(projectId) + "api_keys";

    public async ValueTask<CreateApiKeyResult> CreateAsync(
        string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken,
        DateTimeOffset? expiresAt = null)
    {
        var secret = NewSecret();
        // Check scope existence and project closure in the same catalog write as key issuance.
        return await catalog.WriteAsync<CreateApiKeyResult>(async transaction =>
            await EngineManagementService.ScopeExistsUncheckedAsync(transaction, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false)
            // A closing project issues no key once this transaction sees its fence (docs/terrabase/project-closure.md).
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

        var lookup = await documents.GetSystemAsync(Hashes, Hash(secret), cancellationToken).ConfigureAwait(false);
        if (lookup is null)
        {
            return new NotFound();
        }

        var id = JsonSerializer.Deserialize(lookup.Json.Span, TerraBaseJsonContext.Default.String);
        if (!EntityIds.IsValid(id))
        {
            throw new InvalidDataException("the API key lookup names an invalid id");
        }

        var document = await documents.GetSystemAsync(Collection, id, cancellationToken).ConfigureAwait(false);
        // A key without a tenant scope authenticates nothing; data-plane reads never initialize the catalog.
        return document is null || EntityPayload.Read(document, TerraBaseJsonContext.Default.ApiKey) is not { OrganizationId: not null } key
            || key.ExpiresAt <= time.GetUtcNow()
            ? new NotFound()
            : key;
    }

    public async ValueTask<RevocationResult> RevokeAsync(string keyId, CancellationToken cancellationToken) =>
        await catalog.WriteAsync<RevocationResult>(async transaction =>
        {
            var key = await GetAsync(transaction, Collection, keyId, TerraBaseJsonContext.Default.ApiKey, cancellationToken).ConfigureAwait(false);
            if (key is null)
            {
                return new NotFound();
            }

            // Project keys issued before the reverse lookup existed keep their hash in the project entry.
            var hash = await GetAsync(transaction, Hashes, keyId, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false)
                ?? (key.ProjectId is { } owner
                    ? (await GetAsync(transaction, ProjectKeys(owner), keyId, TerraBaseJsonContext.Default.ProjectKeyEntry, cancellationToken).ConfigureAwait(false))?.Hash
                    : null)
                ?? throw new InvalidOperationException("the API key has no secret lookup");
            await transaction.DeleteAsync(Collection, keyId, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Hashes, hash, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Hashes, keyId, cancellationToken).ConfigureAwait(false);
            if (key.ProjectId is { } project)
            {
                await transaction.DeleteAsync(ProjectKeys(project), keyId, cancellationToken).ConfigureAwait(false);
            }

            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);

    // The caller has already checked the permanent account-erasure fence in this transaction.
    internal static async ValueTask RevokeProjectBatchAsync(IStorageTransaction transaction, string project, int limit, long maxBytes, CancellationToken token)
    {
        var page = await transaction.ListAsync(ProjectKeys(project), null, limit, maxBytes, token).ConfigureAwait(false);
        foreach (var document in page.Documents)
        {
            var entry = EntityPayload.Read(document, TerraBaseJsonContext.Default.ProjectKeyEntry)
                ?? throw new InvalidOperationException("a closing project's API key is invalid");
            await transaction.DeleteAsync(Collection, document.Id, token).ConfigureAwait(false);
            await transaction.DeleteAsync(Hashes, entry.Hash, token).ConfigureAwait(false);
            await transaction.DeleteAsync(Hashes, document.Id, token).ConfigureAwait(false);
            await transaction.DeleteAsync(ProjectKeys(project), document.Id, token).ConfigureAwait(false);
        }
    }

    // Keep project-scoped keys discoverable by project closure and the project-deletion guard.
    private async ValueTask<ApiKey> StoreAsync(
        IStorageTransaction transaction, string secret, string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId,
        CancellationToken cancellationToken, DateTimeOffset? expiresAt = null)
    {
        var key = new ApiKey(EntityIds.New(time).ToString(), name, scopes, organizationId, projectId, databaseId, expiresAt);
        var hash = Hash(secret);
        await PutAsync(transaction, Collection, key.Id, key, TerraBaseJsonContext.Default.ApiKey, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, Hashes, hash, key.Id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, Hashes, key.Id, hash, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
        if (projectId is not null)
        {
            await PutAsync(transaction, ProjectKeys(projectId), key.Id, new ProjectKeyEntry(hash), TerraBaseJsonContext.Default.ProjectKeyEntry, cancellationToken).ConfigureAwait(false);
        }

        return key;
    }

    private static string NewSecret() => SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A project-scoped key's closure entry: the secret hash to delete with it.</summary>
/// <param name="Hash">The SHA-256 of the key's secret.</param>
internal sealed record ProjectKeyEntry(string Hash);
