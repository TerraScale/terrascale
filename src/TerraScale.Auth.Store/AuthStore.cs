using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Auth.Store;

/// <summary>A durable single-use challenge. Only the digest of the bearer secret is persisted.</summary>
public sealed record StoredChallenge(string InstanceId, string Purpose, string TokenHash, string SubjectId, DateTimeOffset ExpiresAt, bool Consumed = false);

internal sealed record UniqueClaim(string InstanceId, string Kind, string OwnerId);

/// <summary>
/// Documents v1 persistence. Each entity uses its own collection; addresses include the instance.
/// No unconditional updates, automatic write retries, process-local coordination or memory fallback.
/// Callers must authorize the instance before using this internal persistence boundary.
/// </summary>
public sealed class AuthStore(TerraBaseDatabase database, TimeProvider clock)
{
    /// <summary>Natural-key reservations.</summary>
    public const string UniqueCollection = "uniques";

    /// <summary>Verification and recovery challenges.</summary>
    public const string ChallengeCollection = "verification_tokens";

    /// <summary>Dedicated synthetic storage probes, separate from identity state.</summary>
    public const string HealthCollection = "storage_health";

    /// <summary>User accounts, addressed by instance and user id.</summary>
    public const string UserCollection = "users";

    /// <summary>Browser sessions, addressed by instance and the SHA-256 of the bearer token.</summary>
    public const string SessionCollection = "sessions";

    /// <summary>Email passcodes, one per user, addressed by instance and user id.</summary>
    public const string PasscodeCollection = "passcodes";

    /// <summary>OpenID Connect clients, addressed by instance and client id.</summary>
    public const string OidcApplicationCollection = "oidc_applications";

    /// <summary>OpenID Connect authorizations (one per sign-in), addressed by instance and id.</summary>
    public const string OidcAuthorizationCollection = "oidc_authorizations";

    /// <summary>OpenID Connect token entries, addressed by instance and id.</summary>
    public const string OidcTokenCollection = "oidc_tokens";

    /// <summary>
    /// Every collection the service needs; provisioning creates them and readiness requires them. <c>signing_key_rings</c>
    /// belongs to the TS-488 key ring, which signs OpenID Connect tokens, and <c>rate_limits</c> to the TS-488 attempt
    /// limiter, which every headless call (TS-582) charges.
    /// </summary>
    public static IReadOnlyList<string> RequiredCollections { get; } =
    [
        UniqueCollection, ChallengeCollection, HealthCollection, UserCollection, SessionCollection, PasscodeCollection,
        "signing_key_rings", OidcApplicationCollection, OidcAuthorizationCollection, OidcTokenCollection, "rate_limits",
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Explicit operator/test setup of <see cref="RequiredCollections"/> in an already provisioned database. Never
    /// called automatically by the host or readiness probe; missing state fails closed.
    /// </summary>
    public async Task ProvisionAsync(CancellationToken cancellationToken = default)
    {
        foreach (var collection in RequiredCollections)
        {
            try
            {
                await database.CreateCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
            }
            catch (TerraBaseConflictException error) when (error.ErrorCode == "name_taken")
            {
                // Concurrent operator setup is idempotent; other failures propagate.
            }
        }
    }

    /// <summary>Stable UUIDv8 from a SHA-256 digest of unambiguously framed instance, kind and key.</summary>
    public static string NaturalKeyId(string instanceId, string kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var framed = string.Create(CultureInfo.InvariantCulture, $"{instanceId.Length}:{instanceId}{kind.Length}:{kind}{key.Length}:{key}");
        var hash = SHA256.HashData(StrictUtf8.GetBytes(framed));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    /// <summary>Canonical email key; email syntax validation belongs to the account boundary.</summary>
    public static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return email.Trim().ToLowerInvariant();
    }

    /// <summary>Reads a document and its opaque ETag version. Missing parents and outages propagate.</summary>
    public Task<TerraBaseDocument?> GetAsync(string collection, string instanceId, string key, CancellationToken cancellationToken = default) =>
        database.GetDocumentAsync(collection, NaturalKeyId(instanceId, collection, key), cancellationToken);

    /// <summary>Up to <paramref name="limit"/> documents matching a TerraBase query filter, in id order.</summary>
    public async Task<IReadOnlyList<TerraBaseDocument>> QueryAsync(string collection, JsonElement filter, int limit, CancellationToken cancellationToken = default) =>
        (await database.QueryAsync(collection, new TerraBaseQuery { Filter = filter, Limit = limit }, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>Creates only; a taken address throws a precondition failure.</summary>
    public Task<TerraBaseWriteResult> CreateAsync(string collection, string instanceId, string key, JsonElement record, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        return database.PutDocumentAsync(collection, NaturalKeyId(instanceId, collection, key), record, TerraBasePrecondition.Absent, cancellationToken);
    }

    /// <summary>Replaces only the version read by the caller; false means another writer won.</summary>
    public async Task<bool> CompareAndPutAsync(string collection, string instanceId, string key, JsonElement record, string expectedVersion, CancellationToken cancellationToken = default) =>
        await this.ReplaceAsync(collection, instanceId, key, record, expectedVersion, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Replaces only the version read by the caller and returns the new version; null means another writer won.</summary>
    public async Task<string?> ReplaceAsync(string collection, string instanceId, string key, JsonElement record, string expectedVersion, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        try
        {
            return (await database.PutDocumentAsync(collection, NaturalKeyId(instanceId, collection, key), record, TerraBasePrecondition.Matches(expectedVersion), cancellationToken).ConfigureAwait(false)).Version;
        }
        catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed")
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces only the version read by the caller and, in the same transaction, reserves a scoped natural key owned by
    /// <paramref name="key"/>. Returns the new version, or null when another writer changed the record. A natural key
    /// that is already taken throws, so a pointer never silently resolves to another record.
    /// </summary>
    public async Task<string?> ReplaceClaimedAsync(string collection, string instanceId, string key, JsonElement record, string expectedVersion, string kind, string normalizedKey, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, key), StoreJsonContext.Default.UniqueClaim);
        try
        {
            var result = await database.TransactAsync(
            [
                TerraBaseTransactionOperation.Put(collection, NaturalKeyId(instanceId, collection, key), record, expectedVersion),
                TerraBaseTransactionOperation.Create(UniqueCollection, claim, NaturalKeyId(instanceId, kind, normalizedKey)),
            ], cancellationToken).ConfigureAwait(false);
            return result.Results[0].Version ?? throw new InvalidOperationException("TerraBase returned no version for a committed write.");
        }
        catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed" && error.OperationIndex == 0)
        {
            return null;
        }
    }

    /// <summary>
    /// Every document of the instance in <paramref name="collection"/> whose string field at the JSON Pointer
    /// <paramref name="field"/> equals <paramref name="value"/>. Callers recheck what they read.
    /// </summary>
    public async IAsyncEnumerable<TerraBaseDocument> QueryAsync(string collection, string instanceId, string field, string value, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var condition = new JsonObject
        {
            ["and"] = new JsonArray(new JsonObject { ["eq"] = new JsonArray("/instanceId", instanceId) }, new JsonObject { ["eq"] = new JsonArray(field, value) }),
        };
        using var parsed = JsonDocument.Parse(condition.ToJsonString());
        var filter = parsed.RootElement.Clone();
        string? cursor = null;
        do
        {
            var page = await database.QueryAsync(collection, new TerraBaseQuery { Filter = filter, Cursor = cursor, Limit = 100 }, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                yield return item;
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);
    }

    /// <summary>Deletes only the version read by the caller; false means absent or another writer won.</summary>
    public async Task<bool> CompareAndDeleteAsync(string collection, string instanceId, string key, string expectedVersion, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        try
        {
            await database.DeleteDocumentAsync(collection, NaturalKeyId(instanceId, collection, key), TerraBasePrecondition.Matches(expectedVersion), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TerraBaseException error) when (error is TerraBasePreconditionFailedException && error.ErrorCode == "precondition_failed"
            || error is TerraBaseNotFoundException && error.ErrorCode == "not_found")
        {
            return false;
        }
    }

    /// <summary>Reserves a scoped natural key atomically. Ambiguous failures propagate and leave the claim reserved.</summary>
    public async Task<bool> ClaimUniqueAsync(string instanceId, string kind, string normalizedKey, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var id = NaturalKeyId(instanceId, kind, normalizedKey);
        var record = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, ownerId), StoreJsonContext.Default.UniqueClaim);
        try
        {
            await database.PutDocumentAsync(UniqueCollection, id, record, TerraBasePrecondition.Absent, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed")
        {
            return false;
        }
    }

    /// <summary>
    /// Atomically reserves a scoped natural key for <paramref name="key"/> and creates the record that owns it, so a
    /// claim never exists without its owner. False means the natural key is taken and nothing was written.
    /// </summary>
    public async Task<bool> CreateClaimedAsync(string collection, string instanceId, string key, JsonElement record, string kind, string normalizedKey, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, key), StoreJsonContext.Default.UniqueClaim);
        try
        {
            await database.TransactAsync(
            [
                TerraBaseTransactionOperation.Create(UniqueCollection, claim, NaturalKeyId(instanceId, kind, normalizedKey)),
                TerraBaseTransactionOperation.Create(collection, record, NaturalKeyId(instanceId, collection, key)),
            ], cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed" && error.OperationIndex == 0)
        {
            return false;
        }
    }

    /// <summary>The owner of a scoped natural key, or <see langword="null"/> when it is unclaimed.</summary>
    public async Task<string?> FindUniqueOwnerAsync(string instanceId, string kind, string normalizedKey, CancellationToken cancellationToken = default)
    {
        var document = await database.GetDocumentAsync(UniqueCollection, NaturalKeyId(instanceId, kind, normalizedKey), cancellationToken).ConfigureAwait(false);
        var claim = document?.Document.Deserialize(StoreJsonContext.Default.UniqueClaim);
        return claim is not null && claim.InstanceId == instanceId && claim.Kind == kind ? claim.OwnerId : null;
    }

    /// <summary>Explicit owner-checked CAS release; never use compensation after an ambiguous write.</summary>
    public async Task<bool> ReleaseUniqueAsync(string instanceId, string kind, string normalizedKey, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var id = NaturalKeyId(instanceId, kind, normalizedKey);
        var document = await database.GetDocumentAsync(UniqueCollection, id, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return false;
        }

        var claim = document.Document.Deserialize(StoreJsonContext.Default.UniqueClaim);
        if (claim is null || claim.InstanceId != instanceId || claim.Kind != kind || claim.OwnerId != ownerId)
        {
            return false;
        }

        try
        {
            await database.DeleteDocumentAsync(UniqueCollection, id, TerraBasePrecondition.Matches(document.Version), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TerraBaseException error) when (error is TerraBasePreconditionFailedException && error.ErrorCode == "precondition_failed"
            || error is TerraBaseNotFoundException && error.ErrorCode == "not_found")
        {
            return false;
        }
    }

    /// <summary>Creates a bound challenge with a lowercase SHA-256 digest, never the bearer secret.</summary>
    public async Task CreateChallengeAsync(string instanceId, string purpose, string tokenHash, string subjectId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        ValidateTokenHash(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        if (expiresAt <= clock.GetUtcNow())
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A challenge must expire in the future.");
        }

        var challenge = new StoredChallenge(instanceId, purpose, tokenHash, subjectId, expiresAt);
        await database.PutDocumentAsync(ChallengeCollection, NaturalKeyId(instanceId, purpose, tokenHash),
            JsonSerializer.SerializeToElement(challenge, StoreJsonContext.Default.StoredChallenge), TerraBasePrecondition.Absent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Consumes exactly once with If-Match. Retains the consumed record to reject replay and reissue.</summary>
    public async Task<StoredChallenge?> ConsumeChallengeAsync(string instanceId, string purpose, string tokenHash, string subjectId, CancellationToken cancellationToken = default)
    {
        ValidateTokenHash(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var id = NaturalKeyId(instanceId, purpose, tokenHash);
        var document = await database.GetDocumentAsync(ChallengeCollection, id, cancellationToken).ConfigureAwait(false);
        var challenge = document?.Document.Deserialize(StoreJsonContext.Default.StoredChallenge);
        if (challenge is null || challenge.Consumed || challenge.ExpiresAt <= clock.GetUtcNow()
            || challenge.InstanceId != instanceId || challenge.Purpose != purpose || challenge.TokenHash != tokenHash || challenge.SubjectId != subjectId)
        {
            return null;
        }

        try
        {
            await database.PutDocumentAsync(ChallengeCollection, id,
                JsonSerializer.SerializeToElement(challenge with { Consumed = true }, StoreJsonContext.Default.StoredChallenge),
                TerraBasePrecondition.Matches(document!.Version), cancellationToken).ConfigureAwait(false);
            // A slow committed write must not return authorization after the challenge expired.
            return challenge.ExpiresAt > clock.GetUtcNow() ? challenge with { Consumed = true } : null;
        }
        catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed")
        {
            return null;
        }
    }

    private static void RequireEntityCollection(string collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        if (collection is UniqueCollection or ChallengeCollection or HealthCollection)
        {
            throw new ArgumentException("Infrastructure records require their dedicated operations.", nameof(collection));
        }
    }

    private static void ValidateTokenHash(string tokenHash)
    {
        if (tokenHash is not { Length: 64 } || tokenHash.Any(character => !char.IsAsciiHexDigitLower(character)))
        {
            throw new ArgumentException("A challenge requires a lowercase SHA-256 digest.", nameof(tokenHash));
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredChallenge))]
[JsonSerializable(typeof(UniqueClaim))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
