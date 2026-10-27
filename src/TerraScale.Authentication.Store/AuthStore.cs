using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Store;

/// <summary>A durable single-use challenge. Only the digest of the bearer secret is persisted.</summary>
public sealed record StoredChallenge(string InstanceId, string Purpose, string TokenHash, string SubjectId, DateTimeOffset ExpiresAt, bool Consumed = false, CredentialBinding? Binding = null);

internal sealed record UniqueClaim(string InstanceId, string Kind, string OwnerId);

/// <summary>
/// Documents v1 persistence. Each entity uses its own collection; addresses include the instance.
/// No unconditional updates, automatic write retries, process-local coordination or memory fallback.
/// Callers must authorize the instance before using this internal persistence boundary.
/// Preconditions order requests only in this region. Replication resolves concurrent claims with TerraBase's
/// deterministic LWW metadata; credentials must retain and validate their winning claim on use.
/// </summary>
public sealed class AuthStore(TerraBaseDatabase local, TimeProvider clock)
{
    /// <summary>Maximum retained proof ancestors; reaching it requires a fresh sign-in, never dropping an ancestor.</summary>
    public const int MaxCredentialBindings = 64;

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

    /// <summary>TOTP and recovery credentials, addressed by instance and user id (TS-707).</summary>
    public const string MfaCollection = "mfa_credentials";

    /// <summary>OpenID Connect clients, addressed by instance and client id.</summary>
    public const string OidcApplicationCollection = "oidc_applications";

    /// <summary>OpenID Connect authorizations (one per sign-in), addressed by instance and id.</summary>
    public const string OidcAuthorizationCollection = "oidc_authorizations";

    /// <summary>OpenID Connect token entries, addressed by instance and id.</summary>
    public const string OidcTokenCollection = "oidc_tokens";

    /// <summary>Platform organizations, addressed by instance and id (TS-598).</summary>
    public const string OrganizationCollection = "organizations";

    /// <summary>Organization roles, addressed by instance and <c>{organization}/{user}</c>.</summary>
    public const string MembershipCollection = "memberships";

    /// <summary>Projects, addressed by instance and id.</summary>
    public const string ProjectCollection = "projects";

    /// <summary>Explicit project roles, addressed by instance and <c>{project}/{user}</c>.</summary>
    public const string ProjectMemberCollection = "project_members";

    /// <summary>API keys, addressed by instance and key id.</summary>
    public const string ApiKeyCollection = "api_keys";

    /// <summary>When each API key was last used, addressed by instance and key id.</summary>
    public const string ApiKeyUsageCollection = "api_key_usage";

    /// <summary>Invitations to an organization or project, addressed by instance and id.</summary>
    public const string InvitationCollection = "invitations";

    /// <summary>ES256 signing keys, one per key, addressed by instance and key id (<c>kid</c>).</summary>
    public const string SigningKeyCollection = "signing_keys";

    /// <summary>Durable retries for auth logical deletion; completion is not physical erasure.</summary>
    public const string DeletionJobCollection = "deletion_jobs";

    /// <summary>Durable SDK query positions for fair dashboard deletion proof delivery.</summary>
    public const string DashboardDeletionCursorCollection = "dashboard_deletion_cursors";

    /// <summary>Hashed, expiring status capabilities bound to an account closure generation.</summary>
    public const string AccountDeletionReceiptCollection = "account_deletion_receipts";

    /// <summary>Authorized closure intents (<see cref="ClosureJob"/>); while one exists it fences access-adding writes to its scope.</summary>
    public const string ClosureJobCollection = "closure_jobs";

    /// <summary>Permanent fences of cancelled intent generations, with all-holder receipts, in auth storage only.</summary>
    public const string ClosureCancellationCollection = "closure_cancellations";

    /// <summary>Immutable evidence that a generation began irreversible removals; ordered with cancellation by the local writer.</summary>
    public const string ClosureCommitCollection = "closure_commits";

    /// <summary>TerraBase's committed shared-history erasure markers (written by TerraBase holders, opaque ids only).</summary>
    public const string HistoryMarkCollection = "history_erasure_marks";

    /// <summary>Audit events of the platform's organizations, addressed by instance and id.</summary>
    public const string AuditEventCollection = "audit_events";

    /// <summary>One project tenant's sign-in methods and branding, addressed by the project id in its own instance.</summary>
    public const string ProjectAuthSettingsCollection = "project_auth_settings";

    /// <summary>Organizations' single sign-on connections, one per email domain.</summary>
    public const string OrganizationSsoCollection = "organization_sso";

    /// <summary>
    /// Every collection the service needs; provisioning creates them and readiness requires them. <c>rate_limits</c> belongs to the TS-488 attempt limiter,
    /// which every hosted sign-in call and email test send (TS-583) charges; and <c>email_configs</c> and
    /// <c>email_config_versions</c> to TS-583 email branding.
    /// </summary>
    public static IReadOnlyList<string> RequiredCollections { get; } =
    [
        UniqueCollection, ChallengeCollection, HealthCollection, UserCollection, SessionCollection, PasscodeCollection,
        SigningKeyCollection, OidcApplicationCollection, OidcAuthorizationCollection, OidcTokenCollection,
        "email_admission", "rate_limits", "email_configs", "email_config_versions", "email_allowance_deliveries", "email_alert_deliveries", "email_support_deliveries",
        OrganizationCollection, MembershipCollection, ProjectCollection, ProjectMemberCollection, ApiKeyCollection, ApiKeyUsageCollection,
        InvitationCollection, AuditEventCollection, MfaCollection, DeletionJobCollection, DashboardDeletionCursorCollection, ClosureJobCollection, HistoryMarkCollection,
        ClosureCancellationCollection, ClosureCommitCollection, ProjectAuthSettingsCollection, OrganizationSsoCollection, AccountDeletionReceiptCollection,
    ];

    private static readonly TerraScaleIdGenerator Ids = new();

    /// <summary>Generates a canonical ULID with monotonic ordering within a millisecond.</summary>
    public static string NewId() => Ids.NewId();

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Creates <see cref="RequiredCollections"/> in ts-auth's database, which TerraBase seeds. The host runs it at startup
    /// before the signing key bootstrap; the readiness probe never does, so a collection lost later fails closed.
    /// </summary>
    public async Task ProvisionAsync(CancellationToken cancellationToken = default)
    {
        foreach (var collection in RequiredCollections)
        {
            try
            {
                await local.CreateCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
            }
            catch (TerraBaseConflictException error) when (error.ErrorCode == "name_taken")
            {
                // Concurrent operator setup is idempotent; other failures propagate.
            }
        }
    }

    /// <summary>Stable name-based ULID: the first 16 bytes of a SHA-256 digest of unambiguously framed instance, kind and key.</summary>
    public static string NaturalKeyId(string instanceId, string kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var framed = string.Create(CultureInfo.InvariantCulture, $"{instanceId.Length}:{instanceId}{kind.Length}:{kind}{key.Length}:{key}");
        var hash = SHA256.HashData(StrictUtf8.GetBytes(framed));
        return new NUlid.Ulid(hash[..16]).ToString();
    }

    /// <summary>Canonical email key; email syntax validation belongs to the account boundary.</summary>
    public static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return email.Trim().ToLowerInvariant();
    }

    /// <summary>Reads a document and its opaque ETag version. Missing parents and outages propagate.</summary>
    public Task<TerraBaseDocument?> GetAsync(string collection, string instanceId, string key, CancellationToken cancellationToken = default) =>
        local.GetDocumentAsync(collection, NaturalKeyId(instanceId, collection, key), cancellationToken);

    /// <summary>Reads this region's replicated copy, without a remote fallback.</summary>
    public Task<TerraBaseDocument?> GetLocalAsync(string collection, string instanceId, string key, CancellationToken cancellationToken = default) =>
        this.GetAsync(collection, instanceId, key, cancellationToken);

    /// <summary>Fences an account intent everywhere; pending or irreversible work is never cancellation success.</summary>
    public Task<TerraBaseClosureCancellation> CancelAccountClosureAsync(ClosureJob job, CancellationToken cancellationToken = default) =>
        local.CancelClosureAsync(job.Generation ?? throw new InvalidOperationException("An account closure needs a generation."),
            NaturalKeyId(job.InstanceId, ClosureJobCollection, ClosureJob.Key("account", job.SubjectId)), cancellationToken);

    /// <summary>Whether cancellation is in progress for this exact creation.</summary>
    public async Task<bool> IsClosureCancellingAsync(ClosureJob job, CancellationToken cancellationToken = default) =>
        job.Generation is { } generation && await local.GetDocumentAsync(ClosureCancellationCollection, generation, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Retained local proof, even if a stale regional full-document write overwrote CommittedAt.</summary>
    public async Task<DateTimeOffset?> ClosureCommittedAtAsync(ClosureJob job, CancellationToken cancellationToken = default)
    {
        var mark = job.Generation is { } generation ? await local.GetDocumentAsync(ClosureCommitCollection, generation, cancellationToken).ConfigureAwait(false) : null;
        return mark is not null && mark.Document.TryGetProperty("committedAt", out var value) ? value.GetDateTimeOffset() : null;
    }

    /// <summary>The first irreversible step and its durable proof share the cancellation fence's writer.</summary>
    public async Task<string?> CommitAccountClosureAsync(ClosureJob job, string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Scope != "account" || job.Generation is not { } generation || job.CommittedAt is null)
        {
            throw new ArgumentException("A committed account generation is required.", nameof(job));
        }

        try
        {
            var proof = JsonSerializer.SerializeToElement(new JsonObject { ["generation"] = generation, ["committedAt"] = JsonValue.Create(job.CommittedAt) });
            var result = await local.TransactAsync(
            [
                TerraBaseTransactionOperation.Put(ClosureJobCollection, NaturalKeyId(job.InstanceId, ClosureJobCollection, ClosureJob.Key("account", job.SubjectId)),
                    JsonSerializer.SerializeToElement(job, ClosureJson.Default.ClosureJob), version),
                TerraBaseTransactionOperation.Put(ClosureCommitCollection, generation, proof),
                TerraBaseTransactionOperation.AssertAbsent(ClosureCancellationCollection, generation),
            ], cancellationToken).ConfigureAwait(false);
            return result.Results[0].Version;
        }
        catch (TerraBasePreconditionFailedException)
        {
            return null;
        }
    }

    /// <summary>Up to <paramref name="limit"/> documents matching a TerraBase query filter, in id order.</summary>
    public Task<IReadOnlyList<TerraBaseDocument>> QueryAsync(string collection, JsonElement filter, int limit, CancellationToken cancellationToken = default) =>
        this.QueryAsync(collection, filter, limit, null, cancellationToken);

    /// <summary>Up to <paramref name="limit"/> documents matching a TerraBase query filter, in <paramref name="sort"/> order, then id order.</summary>
    public async Task<IReadOnlyList<TerraBaseDocument>> QueryAsync(string collection, JsonElement filter, int limit, IReadOnlyList<TerraBaseSort>? sort, CancellationToken cancellationToken = default) =>
        (await local.QueryAsync(collection, new TerraBaseQuery { Filter = filter, Limit = limit, Sort = sort }, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>How many of the instance's documents <paramref name="collection"/> holds; a lower bound beyond 100000.</summary>
    public async Task<long> CountAsync(string collection, string instanceId, CancellationToken cancellationToken = default)
    {
        using var parsed = JsonDocument.Parse(new JsonObject { ["eq"] = new JsonArray("/instanceId", instanceId) }.ToJsonString());
        var page = await local.QueryAsync(collection, new TerraBaseQuery { Filter = parsed.RootElement.Clone(), Limit = 1, Count = true }, cancellationToken).ConfigureAwait(false);
        return page.Count?.Value ?? 0;
    }

    /// <summary>Creates only; a taken address throws a precondition failure.</summary>
    public Task<TerraBaseWriteResult> CreateAsync(string collection, string instanceId, string key, JsonElement record, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        return this.WriteFencedAsync(TerraBaseTransactionOperation.Create(collection, record, NaturalKeyId(instanceId, collection, key)), cancellationToken);
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
            return (await this.WriteFencedAsync(TerraBaseTransactionOperation.Put(collection, NaturalKeyId(instanceId, collection, key), record, expectedVersion), cancellationToken).ConfigureAwait(false)).Version;
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
            var result = await this.TransactFencedAsync(
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
    /// In one transaction, replaces the record at <paramref name="key"/> if it is still at <paramref name="expectedVersion"/>
    /// (or creates it when <paramref name="expectedVersion"/> is <see langword="null"/>), and creates the same record as the
    /// immutable history entry <paramref name="historyKey"/> in <paramref name="historyCollection"/>. False means another writer
    /// changed the record or already wrote that history entry, and nothing was written.
    /// </summary>
    public async Task<bool> CommitVersionAsync(string collection, string instanceId, string key, JsonElement record, string? expectedVersion, string historyCollection, string historyKey, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        RequireEntityCollection(historyCollection);
        var id = NaturalKeyId(instanceId, collection, key);
        try
        {
            await this.TransactFencedAsync(
            [
                expectedVersion is null ? TerraBaseTransactionOperation.Create(collection, record, id) : TerraBaseTransactionOperation.Put(collection, id, record, expectedVersion),
                TerraBaseTransactionOperation.Create(historyCollection, record, NaturalKeyId(instanceId, historyCollection, historyKey)),
            ], cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed")
        {
            return false;
        }
    }

    /// <summary>
    /// Every document of the instance in <paramref name="collection"/> whose string field at the JSON Pointer
    /// <paramref name="field"/> equals <paramref name="value"/>. Callers recheck what they read.
    /// </summary>
    public IAsyncEnumerable<TerraBaseDocument> QueryAsync(string collection, string instanceId, string field, string value, CancellationToken cancellationToken = default) =>
        this.QueryAsync(collection, instanceId, [(field, value)], cancellationToken);

    /// <summary>
    /// Every document of the instance in <paramref name="collection"/> whose string fields at the JSON Pointers in
    /// <paramref name="equals"/> equal their values. Callers recheck what they read.
    /// </summary>
    public IAsyncEnumerable<TerraBaseDocument> QueryAsync(string collection, string instanceId, IReadOnlyList<(string Field, string Value)> equals, CancellationToken cancellationToken = default) =>
        QueryAsync(local, collection, instanceId, equals, cancellationToken);

    /// <summary>
    /// Every document of the instance in <paramref name="collection"/>, from this region's copy. Only for the JWKS, which
    /// decides nothing about authorization (<c>KeyRing.GetPublishedAsync</c>).
    /// </summary>
    public IAsyncEnumerable<TerraBaseDocument> QueryLocalAsync(string collection, string instanceId, CancellationToken cancellationToken = default) =>
        QueryAsync(local, collection, instanceId, [], cancellationToken);

    private static async IAsyncEnumerable<TerraBaseDocument> QueryAsync(TerraBaseDatabase copy, string collection, string instanceId, IReadOnlyList<(string Field, string Value)> equals, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(equals);
        var conditions = new JsonArray(new JsonObject { ["eq"] = new JsonArray("/instanceId", instanceId) });
        foreach (var (field, value) in equals)
        {
            conditions.Add(new JsonObject { ["eq"] = new JsonArray(field, value) });
        }

        using var parsed = JsonDocument.Parse(new JsonObject { ["and"] = conditions }.ToJsonString());
        var filter = parsed.RootElement.Clone();
        string? cursor = null;
        do
        {
            var page = await copy.QueryAsync(collection, new TerraBaseQuery { Filter = filter, Cursor = cursor, Limit = 100 }, cancellationToken).ConfigureAwait(false);
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
            await local.DeleteDocumentAsync(collection, NaturalKeyId(instanceId, collection, key), TerraBasePrecondition.Matches(expectedVersion), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TerraBaseException error) when (error is TerraBasePreconditionFailedException && error.ErrorCode == "precondition_failed"
            || error is TerraBaseNotFoundException && error.ErrorCode == "not_found")
        {
            return false;
        }
    }

    /// <summary>
    /// Writes ts-auth's history erasure request (id = <see cref="DeletionJob.ErasureId"/>) naming the fence, unless it exists:
    /// TerraBase holders read the fence id from their copy of it; SYSTEM never stores it.
    /// </summary>
    public async Task EnsureHistoryRequestAsync(DeletionJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var request = JsonSerializer.SerializeToElement(new JsonObject { ["kind"] = "request", ["erasureId"] = job.ErasureId, ["fenceId"] = job.FenceId });
        try
        {
            await local.PutDocumentAsync(HistoryMarkCollection, job.ErasureId!, request, TerraBasePrecondition.Absent, cancellationToken).ConfigureAwait(false);
        }
        catch (TerraBasePreconditionFailedException)
        {
            // Written before: the request is immutable.
        }
    }

    /// <summary>
    /// Retires a deletion fence whose shared-history erasure TerraBase certified: the job at the version read, its holders' markers
    /// and its request document, atomically.
    /// False when another writer changed it or platform dashboard cleanup is unacknowledged.
    /// Callers check completion and <see cref="DeletionJob.FenceExpiresAt"/>.
    /// </summary>
    public async Task<bool> RetireDeletionFenceAsync(DeletionJob job, string expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.InstanceId == "terrascale" && job.DashboardVerifiedAt is null)
        {
            return false;
        }

        using var filter = JsonDocument.Parse(new JsonObject { ["eq"] = new JsonArray("/erasureId", job.ErasureId) }.ToJsonString());
        var marks = await this.QueryAsync(HistoryMarkCollection, filter.RootElement, 1000, cancellationToken).ConfigureAwait(false);
        // One transaction, so nothing is orphaned; the request document last, so its deletion stamp is the highest: a holder that
        // applied it applied the rest (TerraBase's retirement cut).
        List<TerraBaseTransactionOperation> operations = [TerraBaseTransactionOperation.Delete(DeletionJobCollection, NaturalKeyId(job.InstanceId, DeletionJobCollection, job.Id), expectedVersion)];
        operations.AddRange(marks.Where(mark => mark.Id != job.ErasureId).Select(mark => TerraBaseTransactionOperation.Delete(HistoryMarkCollection, mark.Id, mark.Version)));
        operations.AddRange(marks.Where(mark => mark.Id == job.ErasureId).Select(mark => TerraBaseTransactionOperation.Delete(HistoryMarkCollection, mark.Id, mark.Version)));
        try
        {
            await local.TransactAsync(operations, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TerraBaseException error) when (error is TerraBasePreconditionFailedException || error is TerraBaseNotFoundException && error.ErrorCode == "not_found")
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
            await this.WriteFencedAsync(TerraBaseTransactionOperation.Create(UniqueCollection, record, id), cancellationToken).ConfigureAwait(false);
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
            await this.TransactFencedAsync(
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
        var document = await local.GetDocumentAsync(UniqueCollection, NaturalKeyId(instanceId, kind, normalizedKey), cancellationToken).ConfigureAwait(false);
        var claim = document?.Document.Deserialize(StoreJsonContext.Default.UniqueClaim);
        return claim is not null && claim.InstanceId == instanceId && claim.Kind == kind ? claim.OwnerId : null;
    }

    /// <summary>Explicit owner-checked CAS release; never use compensation after an ambiguous write.</summary>
    public async Task<bool> ReleaseUniqueAsync(string instanceId, string kind, string normalizedKey, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var id = NaturalKeyId(instanceId, kind, normalizedKey);
        var document = await local.GetDocumentAsync(UniqueCollection, id, cancellationToken).ConfigureAwait(false);
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
            await local.DeleteDocumentAsync(UniqueCollection, id, TerraBasePrecondition.Matches(document.Version), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TerraBaseException error) when (error is TerraBasePreconditionFailedException && error.ErrorCode == "precondition_failed"
            || error is TerraBaseNotFoundException && error.ErrorCode == "not_found")
        {
            return false;
        }
    }

    /// <summary>
    /// Commits <paramref name="operations"/> in one TerraBase transaction: all of them or none. Returns null on success,
    /// or the index of a failed precondition, assertion or conditional delete of a missing document.
    /// Assertions see the state before the transaction's writes. TerraBase refuses two operations on one document, so an
    /// assertion made at a version (<see cref="Require"/>) on a document another operation writes at a version is left
    /// out when the two versions are the same (that write requires exactly what the caller checked), and otherwise fails
    /// the transaction at the assertion's index without sending it: the document changed between the caller's reads.
    /// </summary>
    public async Task<int?> TransactAsync(IReadOnlyList<StoreOperation> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var pinned = operations.Where(operation => operation.Operation.IfMatch is not null)
            .ToDictionary(operation => (operation.Operation.Collection, operation.Operation.Id), operation => operation.Operation.IfMatch);
        var sent = new List<(StoreOperation Operation, int Index)>();
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Operation.Op == "assert" && pinned.TryGetValue((operation.Operation.Collection, operation.Operation.Id), out var version))
            {
                if (operation.CheckedVersion is null)
                {
                    throw new ArgumentException("An assertion on a document the transaction writes needs the version it was checked at.", nameof(operations));
                }

                if (operation.CheckedVersion != version)
                {
                    return index;
                }

                continue;
            }

            sent.Add((operation, index));
        }

        var fenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in sent.ToArray())
        {
            foreach (var fence in DeletionFences(item.Operation.Operation).Concat(item.Operation.Revokes ? Enumerable.Empty<TerraBaseTransactionOperation>() : ClosureFences(item.Operation.Operation)))
            {
                if (fenceIds.Add(fence.Id!))
                {
                    sent.Add((new StoreOperation(fence), item.Index));
                }
            }
        }

        try
        {
            await local.TransactAsync([.. sent.Select(item => item.Operation.Operation)], cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (TerraBaseException error) when (error.OperationIndex is { } index && index >= 0 && index < sent.Count
            && (error is TerraBasePreconditionFailedException && error.ErrorCode == "precondition_failed"
                || error is TerraBaseNotFoundException && error.ErrorCode == "not_found" && sent[index].Operation.Operation.Op == "delete"))
        {
            return sent[index].Index;
        }
    }

    /// <summary>Create-only write of an entity record.</summary>
    public static StoreOperation Create(string collection, string instanceId, string key, JsonElement record)
    {
        RequireEntityCollection(collection);
        return new(TerraBaseTransactionOperation.Create(collection, record, NaturalKeyId(instanceId, collection, key)));
    }

    /// <summary>Replaces an entity record only at <paramref name="version"/>.</summary>
    public static StoreOperation Replace(string collection, string instanceId, string key, JsonElement record, string version)
    {
        RequireEntityCollection(collection);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return new(TerraBaseTransactionOperation.Put(collection, NaturalKeyId(instanceId, collection, key), record, version));
    }

    /// <summary>Deletes an entity record only at <paramref name="version"/>.</summary>
    public static StoreOperation Delete(string collection, string instanceId, string key, string version)
    {
        RequireEntityCollection(collection);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return new(TerraBaseTransactionOperation.Delete(collection, NaturalKeyId(instanceId, collection, key), version));
    }

    /// <summary>Asserts, inside the transaction, that an entity record does not exist.</summary>
    public static StoreOperation Absent(string collection, string instanceId, string key)
    {
        RequireEntityCollection(collection);
        return new(TerraBaseTransactionOperation.AssertAbsent(collection, NaturalKeyId(instanceId, collection, key)));
    }

    /// <summary>
    /// Asserts that an entity record exists and matches a TerraBase query <paramref name="filter"/>. Pass the
    /// <paramref name="checkedVersion"/> the caller read when it checked the same condition itself (see
    /// <see cref="TransactAsync"/>).
    /// </summary>
    public static StoreOperation Require(string collection, string instanceId, string key, JsonElement filter, string? checkedVersion = null)
    {
        RequireEntityCollection(collection);
        return new(TerraBaseTransactionOperation.Assert(collection, NaturalKeyId(instanceId, collection, key), filter)) { CheckedVersion = checkedVersion };
    }

    /// <summary>
    /// One page of the instance's documents in <paramref name="collection"/> matching every condition (TerraBase query
    /// filters), sorted by <paramref name="sort"/> then id, newest first when <paramref name="descending"/>.
    /// <paramref name="cursor"/> is a previous page's <c>NextCursor</c> for the same conditions and sort; TerraBase refuses
    /// any other (<c>invalid_cursor</c>).
    /// </summary>
    public Task<TerraBaseDocumentPage> PageAsync(string collection, string instanceId, IReadOnlyList<JsonObject> conditions, IReadOnlyList<string> sort, int limit, string? cursor, bool descending = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(sort);
        var all = new JsonArray(new JsonObject { ["eq"] = new JsonArray("/instanceId", instanceId) });
        foreach (var condition in conditions)
        {
            all.Add(condition.DeepClone());
        }

        using var parsed = JsonDocument.Parse(new JsonObject { ["and"] = all }.ToJsonString());
        return local.QueryAsync(collection, new TerraBaseQuery
        {
            Filter = parsed.RootElement.Clone(),
            Sort = [.. sort.Select(path => new TerraBaseSort(path, descending ? "desc" : null))],
            Cursor = cursor,
            Limit = limit,
        }, cancellationToken);
    }

    /// <summary>Reserves a scoped natural key for <paramref name="ownerId"/>; fails when it is taken.</summary>
    public static StoreOperation Claim(string instanceId, string kind, string normalizedKey, string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, ownerId), StoreJsonContext.Default.UniqueClaim);
        return new(TerraBaseTransactionOperation.Create(UniqueCollection, claim, NaturalKeyId(instanceId, kind, normalizedKey)));
    }

    /// <summary>Hands a scoped natural key held at <paramref name="version"/> to <paramref name="ownerId"/>.</summary>
    public static StoreOperation Reclaim(string instanceId, string kind, string normalizedKey, string ownerId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, ownerId), StoreJsonContext.Default.UniqueClaim);
        return new(TerraBaseTransactionOperation.Put(UniqueCollection, NaturalKeyId(instanceId, kind, normalizedKey), claim, version));
    }

    /// <summary>Releases a scoped natural key at the <paramref name="version"/> its owner read (<see cref="FindUniqueAsync"/>).</summary>
    public static StoreOperation Release(string instanceId, string kind, string normalizedKey, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        return new(TerraBaseTransactionOperation.Delete(UniqueCollection, NaturalKeyId(instanceId, kind, normalizedKey), version));
    }

    /// <summary>The owner and version of a scoped natural key, or <see langword="null"/> when it is unclaimed.</summary>
    public async Task<(string OwnerId, string Version)?> FindUniqueAsync(string instanceId, string kind, string normalizedKey, CancellationToken cancellationToken = default)
    {
        var document = await local.GetDocumentAsync(UniqueCollection, NaturalKeyId(instanceId, kind, normalizedKey), cancellationToken).ConfigureAwait(false);
        var claim = document?.Document.Deserialize(StoreJsonContext.Default.UniqueClaim);
        return claim is not null && claim.InstanceId == instanceId && claim.Kind == kind ? (claim.OwnerId, document!.Version) : null;
    }

    /// <summary>A new contender for a one-use credential slot; TerraBase, never the generated id, orders contenders.</summary>
    public static CredentialBinding NewCredentialBinding(string kind, string key) => new("credential:" + kind, key, NewId());

    /// <summary>Reserves a credential slot in the same transaction as its consumption.</summary>
    public static StoreOperation CredentialClaim(string instanceId, CredentialBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.Kind.StartsWith("credential:", StringComparison.Ordinal))
        {
            throw new ArgumentException("A credential claim needs a credential kind.", nameof(binding));
        }

        return Claim(instanceId, binding.Kind, binding.Key, binding.OwnerId);
    }

    /// <summary>Binds delegated credentials to the user's authoritative authentication-state winner.</summary>
    public static CredentialBinding UserStateBinding(string userId, string authStamp) => new("credential:user-state", userId, authStamp);

    /// <summary>Keeps a device approval and its descendants subject to the original device authorization's revocation.</summary>
    public static CredentialBinding AuthorizationBinding(string authorizationId) => new("credential:authorization", authorizationId, authorizationId);

    /// <summary>Checks every ancestor against the regional replicated winner; missing claims fail closed.</summary>
    public async Task<bool> AreCredentialsCurrentAsync(string instanceId, IReadOnlyList<CredentialBinding> bindings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count > MaxCredentialBindings)
        {
            return false;
        }

        // Reject malformed lineage before issuing reads. Independent ancestors have no ordering dependency;
        // bound the fan-out and retain every check, including user-state/email and device authorization checks.
        if (bindings.Any(binding => binding is null || string.IsNullOrEmpty(binding.Kind)
            || string.IsNullOrEmpty(binding.Key) || string.IsNullOrEmpty(binding.OwnerId)))
        {
            return false;
        }

        var distinct = bindings.Distinct().ToArray();
        for (var offset = 0; offset < distinct.Length; offset += 8)
        {
            var checks = distinct.Skip(offset).Take(8).Select(binding => this.IsCredentialCurrentAsync(instanceId, binding, cancellationToken));
            if ((await Task.WhenAll(checks).ConfigureAwait(false)).Any(current => !current))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> IsCredentialCurrentAsync(string instanceId, CredentialBinding binding, CancellationToken cancellationToken)
    {
        if (binding.Kind == "credential:user-state")
        {
            var user = await this.GetAsync(UserCollection, instanceId, binding.Key, cancellationToken).ConfigureAwait(false);
            if (user is null || !user.Document.TryGetProperty("instanceId", out var scope) || scope.GetString() != instanceId
                || !user.Document.TryGetProperty("id", out var id) || id.GetString() != binding.Key
                || !user.Document.TryGetProperty("authStamp", out var stamp) || stamp.GetString() != binding.OwnerId
                || !user.Document.TryGetProperty("email", out var email) || email.GetString() is not { } address
                || await this.FindUniqueOwnerAsync(instanceId, "email", address, cancellationToken).ConfigureAwait(false) != binding.Key)
            {
                return false;
            }

            return true;
        }

        if (binding.Kind == "credential:authorization")
        {
            var authorization = await this.GetAsync(OidcAuthorizationCollection, instanceId, binding.Key, cancellationToken).ConfigureAwait(false);
            if (binding.OwnerId != binding.Key || authorization is null
                || !authorization.Document.TryGetProperty("instanceId", out var scope) || scope.GetString() != instanceId
                || !authorization.Document.TryGetProperty("id", out var id) || id.GetString() != binding.Key
                || !authorization.Document.TryGetProperty("status", out var status) || status.GetString() != "valid")
            {
                return false;
            }

            return true;
        }

        if (!binding.Kind.StartsWith("credential:", StringComparison.Ordinal)
            || await this.FindUniqueOwnerAsync(instanceId, binding.Kind, binding.Key, cancellationToken).ConfigureAwait(false) != binding.OwnerId)
        {
            return false;
        }

        return true;
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
        await this.WriteFencedAsync(TerraBaseTransactionOperation.Create(ChallengeCollection,
            JsonSerializer.SerializeToElement(challenge, StoreJsonContext.Default.StoredChallenge), NaturalKeyId(instanceId, purpose, tokenHash)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Consumes exactly once with If-Match. Retains the consumed record to reject replay and reissue.</summary>
    public async Task<StoredChallenge?> ConsumeChallengeAsync(string instanceId, string purpose, string tokenHash, string subjectId, CancellationToken cancellationToken = default)
    {
        ValidateTokenHash(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var id = NaturalKeyId(instanceId, purpose, tokenHash);
        var document = await local.GetDocumentAsync(ChallengeCollection, id, cancellationToken).ConfigureAwait(false);
        var challenge = document?.Document.Deserialize(StoreJsonContext.Default.StoredChallenge);
        if (challenge is null || challenge.Consumed || challenge.ExpiresAt <= clock.GetUtcNow()
            || challenge.InstanceId != instanceId || challenge.Purpose != purpose || challenge.TokenHash != tokenHash || challenge.SubjectId != subjectId)
        {
            return null;
        }

        try
        {
            var binding = NewCredentialBinding("challenge:" + purpose, tokenHash);
            var consumed = challenge with { Consumed = true, Binding = binding };
            await this.TransactFencedAsync(
            [
                CredentialClaim(instanceId, binding).Operation,
                TerraBaseTransactionOperation.Put(ChallengeCollection, id,
                    JsonSerializer.SerializeToElement(consumed, StoreJsonContext.Default.StoredChallenge), document!.Version),
            ], cancellationToken).ConfigureAwait(false);
            // A slow committed write must not return authorization after the challenge expired.
            return challenge.ExpiresAt > clock.GetUtcNow() ? consumed : null;
        }
        catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed")
        {
            return null;
        }
    }

    private async Task<TerraBaseWriteResult> WriteFencedAsync(TerraBaseTransactionOperation operation, CancellationToken cancellationToken)
    {
        // A record outside every deletion scope stays a plain conditional write; only a fenced one needs a transaction.
        if (!DeletionFences(operation).Concat(ClosureFences(operation)).Any())
        {
            return await local.PutDocumentAsync(operation.Collection, operation.Id!, operation.Document!.Value,
                operation.Op == "create" ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(operation.IfMatch!), cancellationToken).ConfigureAwait(false);
        }

        var committed = await this.TransactFencedAsync([operation], cancellationToken).ConfigureAwait(false);
        // A committed create made the document; a version-matched put replaced one.
        return new TerraBaseWriteResult(operation.Id!, committed.Results[0].Version!, committed.Offset, committed.Durability) { Created = operation.Op == "create" };
    }

    private Task<TerraBaseTransactionResult> TransactFencedAsync(IReadOnlyList<TerraBaseTransactionOperation> operations, CancellationToken cancellationToken)
    {
        var fences = operations.SelectMany(operation => DeletionFences(operation).Concat(ClosureFences(operation))).DistinctBy(operation => operation.Id);
        return local.TransactAsync([.. operations, .. fences], cancellationToken);
    }

    // The retained job is also a permanent tombstone: writes started before deletion cannot repopulate an erased scope.
    private static IEnumerable<TerraBaseTransactionOperation> DeletionFences(TerraBaseTransactionOperation operation)
    {
        if (operation.Document is not { ValueKind: JsonValueKind.Object } document
            || !document.TryGetProperty("instanceId", out var instance) || instance.GetString() is not { } instanceId)
        {
            yield break;
        }

        (string Scope, string Field)[] references = operation.Collection switch
        {
            UserCollection => [("account", "id")],
            OrganizationCollection => [("organization", "id")],
            ProjectCollection => [("organization", "organizationId"), ("project", "id")],
            MembershipCollection => [("organization", "organizationId"), ("account", "userId")],
            ProjectMemberCollection => [("organization", "organizationId"), ("project", "projectId"), ("account", "userId")],
            ApiKeyCollection or ApiKeyUsageCollection or InvitationCollection => [("organization", "organizationId"), ("project", "projectId")],
            AuditEventCollection => [("organization", "organizationId"), ("account", "actor")],
            SessionCollection or PasscodeCollection or MfaCollection => [("account", "userId")],
            OidcTokenCollection or OidcAuthorizationCollection => [("account", "subject")],
            UniqueCollection => [("account", "ownerId")],
            ChallengeCollection => [("account", "subjectId")],
            _ => [],
        };
        foreach (var (scope, field) in references)
        {
            if (document.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } subjectId)
            {
                yield return TerraBaseTransactionOperation.AssertAbsent(DeletionJobCollection,
                    NaturalKeyId(instanceId, DeletionJobCollection, DeletionJob.Key(instanceId, scope, subjectId)));
            }
        }
    }

    // A pending closure refuses writes that would add or change access in its scope. Deletes, writes marked as revocations
    // (StoreOperation.AddsNoAccess), the organization record (whose project counter the closure itself rewrites) and audit
    // events stay writable, so access can still be taken away and the closure can finish.
    private static IEnumerable<TerraBaseTransactionOperation> ClosureFences(TerraBaseTransactionOperation operation)
    {
        if (operation.Document is not { ValueKind: JsonValueKind.Object } document
            || !document.TryGetProperty("instanceId", out var instance) || instance.GetString() is not { } instanceId)
        {
            yield break;
        }

        if (operation.Collection == ClosureJobCollection && document.TryGetProperty("scope", out var scopeField) && scopeField.GetString() == "account"
            && document.TryGetProperty("generation", out var generation) && generation.ValueKind == JsonValueKind.String && generation.GetString() is { } creation
            && (!document.TryGetProperty("committedAt", out var committed) || committed.ValueKind == JsonValueKind.Null))
        {
            yield return TerraBaseTransactionOperation.AssertAbsent(ClosureCancellationCollection, creation);
        }

        (string Scope, string Field)[] references = operation.Collection switch
        {
            ProjectCollection => [("organization", "organizationId"), ("project", "id")],
            MembershipCollection => [("organization", "organizationId"), ("account", "userId")],
            ProjectMemberCollection => [("organization", "organizationId"), ("project", "projectId"), ("account", "userId")],
            ApiKeyCollection => [("organization", "organizationId"), ("project", "projectId")],
            // Status changes (revocation, decline) only take access away; a new invitation could add it.
            InvitationCollection when operation.Op == "create" => [("organization", "organizationId"), ("project", "projectId")],
            _ => [],
        };
        foreach (var (scope, field) in references)
        {
            if (document.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } subjectId)
            {
                yield return TerraBaseTransactionOperation.AssertAbsent(ClosureJobCollection,
                    NaturalKeyId(instanceId, ClosureJobCollection, ClosureJob.Key(scope, subjectId)));
            }
        }
    }

    /// <summary>Deletes a cleanup record with every natural-key claim it owns, atomically at the observed versions.</summary>
    public async Task<bool> DeleteWithClaimsAsync(string collection, string instanceId, string key, string version, string ownerId, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        List<TerraBaseTransactionOperation> operations = [TerraBaseTransactionOperation.Delete(collection, NaturalKeyId(instanceId, collection, key), version)];
        await foreach (var claim in this.QueryAsync(UniqueCollection, instanceId, "/ownerId", ownerId, cancellationToken).ConfigureAwait(false))
        {
            if (claim.Document.Deserialize(StoreJsonContext.Default.UniqueClaim) is { } value && value.InstanceId == instanceId && value.OwnerId == ownerId)
            {
                if (operations.Count >= 100)
                {
                    throw new InvalidOperationException("A cleanup record has more owned claims than the bounded transaction permits.");
                }

                operations.Add(TerraBaseTransactionOperation.Delete(UniqueCollection, claim.Id, claim.Version));
            }
        }

        try
        {
            await this.TransactFencedAsync(operations, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TerraBaseException error) when (error is TerraBasePreconditionFailedException && error.ErrorCode == "precondition_failed"
            || error is TerraBaseNotFoundException && error.ErrorCode == "not_found")
        {
            return false;
        }
    }

    /// <summary>Deletes only a queried infrastructure record still owned by the erased subject at its observed version.</summary>
    public async Task<bool> DeleteOwnedInfrastructureAsync(string collection, string instanceId, string subjectId, TerraBaseDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var owned = collection switch
        {
            UniqueCollection => document.Document.Deserialize(StoreJsonContext.Default.UniqueClaim) is { } claim && claim.InstanceId == instanceId && claim.OwnerId == subjectId,
            ChallengeCollection => document.Document.Deserialize(StoreJsonContext.Default.StoredChallenge) is { } challenge && challenge.InstanceId == instanceId && challenge.SubjectId == subjectId,
            _ => false,
        };
        if (!owned)
        {
            throw new ArgumentException("The infrastructure record is outside the deletion scope.", nameof(document));
        }

        try
        {
            await local.DeleteDocumentAsync(collection, document.Id, TerraBasePrecondition.Matches(document.Version), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TerraBaseException error) when (error is TerraBasePreconditionFailedException && error.ErrorCode == "precondition_failed"
            || error is TerraBaseNotFoundException && error.ErrorCode == "not_found")
        {
            return false;
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

/// <summary>One operation of <see cref="AuthStore.TransactAsync"/>, built only by <see cref="AuthStore"/> so it addresses natural keys.</summary>
public sealed class StoreOperation
{
    internal StoreOperation(TerraBaseTransactionOperation operation) => this.Operation = operation;

    internal TerraBaseTransactionOperation Operation { get; }

    /// <summary>For an assertion, the version at which the caller checked the same condition.</summary>
    internal string? CheckedVersion { get; init; }

    /// <summary>Whether the write adds no access (a demotion, a removed role, closure bookkeeping); a pending closure never fences it.</summary>
    internal bool Revokes { get; private init; }

    /// <summary>
    /// Marks a write that adds no access (a membership or project-role rewrite that only lowers access, or closure
    /// bookkeeping on a project record), so a pending closure lets it through.
    /// </summary>
    public StoreOperation AddsNoAccess() => new(this.Operation) { CheckedVersion = this.CheckedVersion, Revokes = true };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredChallenge))]
[JsonSerializable(typeof(UniqueClaim))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
