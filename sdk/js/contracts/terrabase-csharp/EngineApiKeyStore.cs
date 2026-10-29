using System.Diagnostics;
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
/// Keeps keys as documents in the system database (<see cref="IStorageEngine.OpenSystemDatabaseAsync"/>), keyed by the
/// SHA-256 of the secret. Secrets are 256 random bits, so an unsalted hash is sufficient.
/// A project key is an <see cref="ApiKey"/> narrowed to its project; it also has a <see cref="ProjectKeyEntry"/> in
/// <see cref="ProjectKeys"/>, written and deleted in the same transaction as its hash document.
/// </summary>
internal sealed class EngineApiKeyStore(SystemCatalog catalog, TimeProvider time, ISystemDocumentReader documents) : IApiKeyStore
{
    private const string Collection = "api_keys";
    private const string Hashes = "api_key_hashes";
    private const string KeySequence = "api_key_sequence";
    private const long MaxPageBytes = 1024 * 1024;

    /// <summary>Every API key secret starts with this; other bearer tokens (ts-auth access tokens) are not API keys.</summary>
    internal const string SecretPrefix = "tb_";

    /// <summary>The collection that lists the keys of a project, keyed by key id.</summary>
    public static string ProjectKeys(string projectId) => ProjectState(projectId) + "api_keys";

    private static string ProjectKeyOrder(string projectId) => ProjectState(projectId) + "api_key_order";

    public async ValueTask<CreateApiKeyResult> CreateAsync(
        string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken)
    {
        var secret = NewSecret();
        // Checked against the catalog version, like the existence check the endpoint made before: a key's scope and a
        // project key's list entries are management state.
        return await catalog.WriteAsync<CreateApiKeyResult>(async transaction =>
            await EngineManagementService.ScopeExistsUncheckedAsync(transaction, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false)
                ? new IssuedApiKey((await StoreAsync(transaction, secret, name, scopes, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false)).Key, secret)
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
            ? new NotFound()
            : key;
    }

    public async ValueTask<CreateProjectKeyResult> CreateProjectKeyAsync(
        string userId, string organizationId, string projectId, string name, IReadOnlyList<string> scopes, string? databaseId, CancellationToken cancellationToken)
    {
        var secret = NewSecret();
        return await catalog.WriteAsync<CreateProjectKeyResult>(async transaction =>
        {
            var found = databaseId is null
                ? await EngineManagementService.GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not null
                : await EngineManagementService.GetDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is not null;
            if (!found)
            {
                return new NotFound();
            }

            // Keys are project state: a viewer cannot issue them, and issuing one changes the organization.
            var access = await EngineManagementService.WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    Forbidden => ManagementErrors.RoleTooLow,
                    NotFound => new NotFound(),
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            await EngineManagementService.TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            var (_, listed) = await StoreAsync(transaction, secret, name, scopes, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false);
            return new CreatedProjectApiKey(listed!, secret);
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ListProjectKeysResult> ListProjectKeysAsync(
        string userId, string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken) =>
        catalog.ReadAsync<ListProjectKeysResult>(async transaction =>
        {
            if (await EngineManagementService.GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            var page = await transaction.ListAsync(ProjectKeyOrder(projectId), after, limit, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var keys = new List<ProjectApiKey>(page.Documents.Count);
            foreach (var item in page.Documents)
            {
                var keyId = JsonSerializer.Deserialize(item.Json.Span, TerraBaseJsonContext.Default.String)!;
                var entry = await GetAsync(transaction, ProjectKeys(projectId), keyId, TerraBaseJsonContext.Default.ProjectKeyEntry, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("the API key order names a missing key");
                keys.Add(entry.Key);
            }

            return new ManagementPage<ProjectApiKey>(keys, page.HasMore ? page.Documents[^1].Id : null);
        }, static error => error, cancellationToken);

    public async ValueTask<RevokeProjectKeyResult> RevokeProjectKeyAsync(string userId, string organizationId, string projectId, string keyId, CancellationToken cancellationToken) =>
        await catalog.WriteAsync<RevokeProjectKeyResult>(async transaction =>
        {
            if (await EngineManagementService.GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null
                || await GetAsync(transaction, ProjectKeys(projectId), keyId, TerraBaseJsonContext.Default.ProjectKeyEntry, cancellationToken).ConfigureAwait(false) is not { } entry)
            {
                return new NotFound();
            }

            var access = await EngineManagementService.WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    Forbidden => ManagementErrors.RoleTooLow,
                    NotFound => new NotFound(),
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            await EngineManagementService.TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Collection, keyId, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Hashes, entry.Hash, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(ProjectKeys(projectId), keyId, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(ProjectKeyOrder(projectId), entry.Order, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);

    // Stores a key's authentication document. A project key, whichever route issued it, is also listed: it can be listed
    // and revoked through its project, and keeps the project from being deleted.
    private async ValueTask<(ApiKey Key, ProjectApiKey? Listed)> StoreAsync(
        IStorageTransaction transaction, string secret, string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var key = new ApiKey(EntityIds.New(time).ToString(), name, scopes, organizationId, projectId, databaseId);
        var hash = Hash(secret);
        await PutAsync(transaction, Collection, key.Id, key, TerraBaseJsonContext.Default.ApiKey, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, Hashes, hash, key.Id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
        if (projectId is null)
        {
            return (key, null);
        }

        var listed = new ProjectApiKey(key.Id, projectId, name, scopes, databaseId, secret[^4..], now);
        await ListProjectKeyAsync(transaction, organizationId, listed, hash, cancellationToken).ConfigureAwait(false);
        return (key, listed);
    }

    // A project key's entry and its list position, from the organization's persisted sequence.
    private static async ValueTask ListProjectKeyAsync(IStorageTransaction transaction, string organizationId, ProjectApiKey key, string hash, CancellationToken cancellationToken)
    {
        var order = EngineManagementService.AscendingKey(await EngineManagementService.NextSequenceAsync(
            transaction, EngineManagementService.State(organizationId), KeySequence, cancellationToken).ConfigureAwait(false));
        await PutAsync(transaction, ProjectKeys(key.ProjectId), key.Id, new ProjectKeyEntry(key, hash, order), TerraBaseJsonContext.Default.ProjectKeyEntry, cancellationToken)
            .ConfigureAwait(false);
        await PutAsync(transaction, ProjectKeyOrder(key.ProjectId), order, key.Id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
    }

    private static string NewSecret() => SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A project key as listed, with the hash that names its authentication document.</summary>
/// <param name="Key">The listed key.</param>
/// <param name="Hash">SHA-256 lookup key of the secret in <c>api_key_hashes</c>.</param>
/// <param name="Order">Persisted per-organization sequence, allocated in the key's write transaction.</param>
internal sealed record ProjectKeyEntry(ProjectApiKey Key, string Hash, string Order);
