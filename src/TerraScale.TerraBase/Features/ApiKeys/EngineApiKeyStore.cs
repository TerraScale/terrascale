using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.Management;
using TerraScale.TerraBase.Hosting;
using static TerraScale.TerraBase.Features.Management.SystemCatalog;

namespace TerraScale.TerraBase.Features.ApiKeys;

/// <summary>
/// Keeps keys as documents in the system database (<see cref="IStorageEngine.OpenSystemDatabaseAsync"/>), keyed by the
/// SHA-256 of the secret. Secrets are 256 random bits, so an unsalted hash is sufficient.
/// A project key is an <see cref="ApiKey"/> narrowed to its project; it also has a <see cref="ProjectKeyEntry"/> in
/// <see cref="ProjectKeys"/>, written and deleted in the same transaction as its hash document.
/// </summary>
internal sealed class EngineApiKeyStore(SystemCatalog catalog, TimeProvider time) : IApiKeyStore
{
    private const string Collection = "api_keys";
    private const string KeySequence = "api_key_sequence";
    private const long MaxPageBytes = 1024 * 1024;
    private const int ListPage = 100;

    /// <summary>Every API key secret starts with this; other bearer tokens (ts-auth access tokens) are not API keys.</summary>
    internal const string SecretPrefix = "tb_";

    /// <summary>The collection that lists the keys of a project, keyed by key id.</summary>
    public static string ProjectKeys(string projectId) => ProjectState(projectId) + "api_keys";

    private static string ProjectKeyOrder(string projectId) => ProjectState(projectId) + "api_key_order";

    public async ValueTask<(ApiKey Key, string Secret)?> CreateAsync(
        string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken)
    {
        var secret = NewSecret();
        // Checked against the catalog version, like the existence check the endpoint made before: a key's scope and a
        // project key's list entries are management state.
        var key = await catalog.WriteAsync(async transaction =>
            await EngineManagementService.ScopeExistsUncheckedAsync(transaction, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false)
                ? (await StoreAsync(transaction, secret, name, scopes, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false)).Key
                : null,
            cancellationToken).ConfigureAwait(false);
        return key is null ? null : (key, secret);
    }

    public async ValueTask<ApiKey?> FindAsync(string secret, CancellationToken cancellationToken) =>
        secret.StartsWith(SecretPrefix, StringComparison.Ordinal)
            // A key stored before tenancy names no organization: it reaches nothing, so it does not authenticate.
            && await catalog.ReadUncheckedAsync(
                transaction => GetAsync(transaction, Collection, Hash(secret), TerraBaseJsonContext.Default.ApiKey, cancellationToken),
                cancellationToken).ConfigureAwait(false) is { OrganizationId: not null } key
            ? key
            : null;

    public async ValueTask<ManagementResult<CreatedProjectApiKey>> CreateProjectKeyAsync(
        string userId, string organizationId, string projectId, string name, IReadOnlyList<string> scopes, string? databaseId, CancellationToken cancellationToken)
    {
        var secret = NewSecret();
        return await catalog.WriteAsync<ManagementResult<CreatedProjectApiKey>>(async transaction =>
        {
            var found = databaseId is null
                ? await EngineManagementService.GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not null
                : await EngineManagementService.GetDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is not null;
            if (!found)
            {
                return ManagementError.NotFound;
            }

            // Keys are project state: a viewer cannot issue them, and issuing one changes the organization.
            var writer = await EngineManagementService.WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (writer != ManagementError.None)
            {
                return writer;
            }

            await EngineManagementService.TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            var (_, listed) = await StoreAsync(transaction, secret, name, scopes, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false);
            return new CreatedProjectApiKey(listed!, secret);
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ManagementPage<ProjectApiKey>?> ListProjectKeysAsync(
        string userId, string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken) =>
        catalog.ReadAsync<ManagementPage<ProjectApiKey>?>(async transaction =>
        {
            if (await EngineManagementService.GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null)
            {
                return null;
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
        }, cancellationToken);

    public async ValueTask<ManagementError> RevokeProjectKeyAsync(string userId, string organizationId, string projectId, string keyId, CancellationToken cancellationToken) =>
        await catalog.WriteAsync(async transaction =>
        {
            if (await EngineManagementService.GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null
                || await GetAsync(transaction, ProjectKeys(projectId), keyId, TerraBaseJsonContext.Default.ProjectKeyEntry, cancellationToken).ConfigureAwait(false) is not { } entry)
            {
                return ManagementError.NotFound;
            }

            var writer = await EngineManagementService.WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (writer != ManagementError.None)
            {
                return writer;
            }

            await EngineManagementService.TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Collection, entry.Hash, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(ProjectKeys(projectId), keyId, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(ProjectKeyOrder(projectId), entry.Order, cancellationToken).ConfigureAwait(false);
            return ManagementError.None;
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Lists the project keys stored before every issuance listed them (operator keys narrowed to a project), so they can
    /// be listed and revoked and keep their project from being deleted. Their secrets are not stored: the last four
    /// characters are empty, and <paramref name="now"/> stands for their creation time. Run once per catalog.
    /// </summary>
    internal static async ValueTask ListUnlistedProjectKeysAsync(IStorageTransaction transaction, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string? after = null;
        DocumentPage page;
        do
        {
            page = await transaction.ListAsync(Collection, after, ListPage, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            foreach (var document in page.Documents)
            {
                if (JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.ApiKey) is { OrganizationId: { } organizationId, ProjectId: { } projectId } key
                    && await GetAsync(transaction, ProjectKeys(projectId), key.Id, TerraBaseJsonContext.Default.ProjectKeyEntry, cancellationToken).ConfigureAwait(false) is null
                    // The project decides; a key whose database was deleted since is listed too, so it can still be revoked.
                    && await EngineManagementService.ScopeExistsUncheckedAsync(transaction, organizationId, projectId, null, cancellationToken).ConfigureAwait(false))
                {
                    await ListProjectKeyAsync(transaction, organizationId, new ProjectApiKey(key.Id, projectId, key.Name, key.Scopes, key.DatabaseId, "", now), document.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            after = page.Documents.Count > 0 ? page.Documents[^1].Id : after;
        }
        while (page.HasMore);
    }

    // Stores a key's authentication document. A project key, whichever route issued it, is also listed: it can be listed
    // and revoked through its project, and keeps the project from being deleted.
    private async ValueTask<(ApiKey Key, ProjectApiKey? Listed)> StoreAsync(
        IStorageTransaction transaction, string secret, string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var key = new ApiKey(Guid.CreateVersion7(now).ToString(), name, scopes, organizationId, projectId, databaseId);
        var hash = Hash(secret);
        await PutAsync(transaction, Collection, hash, key, TerraBaseJsonContext.Default.ApiKey, cancellationToken).ConfigureAwait(false);
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
/// <param name="Hash">SHA-256 of the secret: the id of its document in <c>api_keys</c>.</param>
/// <param name="Order">Persisted per-organization sequence, allocated in the key's write transaction.</param>
internal sealed record ProjectKeyEntry(ProjectApiKey Key, string Hash, string Order);
