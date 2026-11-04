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

public sealed record StoredChallenge(string InstanceId, string Purpose, string TokenHash, string SubjectId, DateTimeOffset ExpiresAt, bool Consumed = false, CredentialBinding? Binding = null);

internal sealed record UniqueClaim(string InstanceId, string Kind, string OwnerId);

internal sealed record ClosureMark(string Generation, DateTimeOffset? CommittedAt = null);

public sealed class AuthStore(TerraBaseSqlDatabase local, TimeProvider clock)
{
    private readonly SqlDocuments sql = new(local);

    public const int MaxCredentialBindings = 64;

    public const string UniqueCollection = "uniques";

    public const string ChallengeCollection = "verification_tokens";

    public const string HealthCollection = "storage_health";

    public const string UserCollection = "users";

    public const string SessionCollection = "sessions";

    public const string PasscodeCollection = "passcodes";

    public const string MfaCollection = "mfa_credentials";

    public const string OidcApplicationCollection = "oidc_applications";

    public const string OidcAuthorizationCollection = "oidc_authorizations";

    public const string OidcTokenCollection = "oidc_tokens";

    public const string OrganizationCollection = "organizations";

    public const string MembershipCollection = "memberships";

    public const string ProjectCollection = "projects";

    public const string ProjectMemberCollection = "project_members";

    public const string ApiKeyCollection = "api_keys";

    public const string ApiKeyUsageCollection = "api_key_usage";

    public const string InvitationCollection = "invitations";

    public const string SigningKeyCollection = "signing_keys";

    public const string DeletionJobCollection = "deletion_jobs";

    public const string DashboardDeletionCursorCollection = "dashboard_deletion_cursors";

    public const string AccountDeletionReceiptCollection = "account_deletion_receipts";

    public const string StepUpProofCollection = "step_up_proofs";

    public const string StepUpDeletionGrantCollection = "step_up_deletion_grants";

    public const string StepUpPasscodeCollection = "step_up_passcodes";

    public const string EmailChangeCollection = "email_changes";

    public const string ClosureJobCollection = "closure_jobs";

    public const string ClosureCancellationCollection = "closure_cancellations";

    public const string ClosureCommitCollection = "closure_commits";

    public const string AuditEventCollection = "audit_events";

    public const string ProjectAuthSettingsCollection = "project_auth_settings";

    public const string MailOutboxCollection = "auth_mail_outbox";

    public const string OrganizationSsoCollection = "organization_sso";

    public static IReadOnlyList<string> RequiredCollections { get; } =
    [
        UniqueCollection, ChallengeCollection, HealthCollection, UserCollection, SessionCollection, PasscodeCollection,
        SigningKeyCollection, OidcApplicationCollection, OidcAuthorizationCollection, OidcTokenCollection,
        "email_admission", "rate_limits", "email_configs", "email_config_versions", "email_allowance_deliveries", "email_alert_deliveries", "email_support_deliveries",
        OrganizationCollection, MembershipCollection, ProjectCollection, ProjectMemberCollection, ApiKeyCollection, ApiKeyUsageCollection,
        InvitationCollection, AuditEventCollection, MfaCollection, DeletionJobCollection, DashboardDeletionCursorCollection, ClosureJobCollection,
        ClosureCancellationCollection, ClosureCommitCollection, ProjectAuthSettingsCollection, OrganizationSsoCollection, AccountDeletionReceiptCollection,
        StepUpProofCollection, StepUpDeletionGrantCollection, StepUpPasscodeCollection, EmailChangeCollection, MailOutboxCollection,
    ];

    private static readonly TerraScaleIdGenerator Ids = new();

    public static string NewId() => Ids.NewId();

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task ProvisionAsync(CancellationToken cancellationToken = default) => this.sql.ProvisionAsync(RequiredCollections, cancellationToken);

    public static string NaturalKeyId(string instanceId, string kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var framed = string.Create(CultureInfo.InvariantCulture, $"{instanceId.Length}:{instanceId}{kind.Length}:{kind}{key.Length}:{key}");
        var hash = SHA256.HashData(StrictUtf8.GetBytes(framed));
        return new NUlid.Ulid(hash[..16]).ToString();
    }

    public static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return email.Trim().ToLowerInvariant();
    }

    public Task<StoredDocument?> GetAsync(string collection, string instanceId, string key, CancellationToken cancellationToken = default) =>
        AuthStorageTelemetry.ReadAsync(collection, "get", () => this.sql.GetAsync(collection, NaturalKeyId(instanceId, collection, key), cancellationToken));

    public Task<StoredDocument?> GetLocalAsync(string collection, string instanceId, string key, CancellationToken cancellationToken = default) =>
        this.GetAsync(collection, instanceId, key, cancellationToken);

    public async Task<string> CancelAccountClosureAsync(ClosureJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var generation = job.Generation ?? throw new InvalidOperationException("An account closure needs a generation.");
        var jobId = NaturalKeyId(job.InstanceId, ClosureJobCollection, ClosureJob.Key("account", job.SubjectId));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (await this.sql.GetAsync(ClosureCommitCollection, generation, cancellationToken).ConfigureAwait(false) is not null)
            {
                return "committed";
            }

            var current = await this.sql.GetAsync(ClosureJobCollection, jobId, cancellationToken).ConfigureAwait(false);
            var live = current?.Document.Deserialize(ClosureJson.Default.ClosureJob) is { } stored && stored.Generation == generation ? stored : null;
            if (live?.CommittedAt is not null)
            {
                return "committed";
            }

            List<SqlWrite> operations = [SqlWrite.AssertAbsent(ClosureCommitCollection, generation)];
            if (await this.sql.GetAsync(ClosureCancellationCollection, generation, cancellationToken).ConfigureAwait(false) is null)
            {
                operations.Add(SqlWrite.Create(ClosureCancellationCollection,
                    JsonSerializer.SerializeToElement(new ClosureMark(generation), StoreJsonContext.Default.ClosureMark), generation));
            }

            if (live is not null)
            {
                operations.Add(SqlWrite.Delete(ClosureJobCollection, jobId, current!.Version));
            }

            if (await this.sql.WriteAsync(operations, null, cancellationToken).ConfigureAwait(false) is not null)
            {
                return "cancelled";
            }
        }

        throw ApiException.Unavailable();
    }

    public async Task<bool> IsClosureCancellingAsync(ClosureJob job, CancellationToken cancellationToken = default) =>
        job.Generation is { } generation && await this.sql.GetAsync(ClosureCancellationCollection, generation, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<DateTimeOffset?> ClosureCommittedAtAsync(ClosureJob job, CancellationToken cancellationToken = default)
    {
        var mark = job.Generation is { } generation ? await this.sql.GetAsync(ClosureCommitCollection, generation, cancellationToken).ConfigureAwait(false) : null;
        return mark?.Document.Deserialize(StoreJsonContext.Default.ClosureMark)?.CommittedAt;
    }

    public async Task<string?> CommitAccountClosureAsync(ClosureJob job, string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Scope != "account" || job.Generation is not { } generation || job.CommittedAt is null)
        {
            throw new ArgumentException("A committed account generation is required.", nameof(job));
        }

        var versions = await this.sql.WriteAsync(
        [
            SqlWrite.Put(ClosureJobCollection, NaturalKeyId(job.InstanceId, ClosureJobCollection, ClosureJob.Key("account", job.SubjectId)),
                JsonSerializer.SerializeToElement(job, ClosureJson.Default.ClosureJob), version),
            SqlWrite.Create(ClosureCommitCollection, JsonSerializer.SerializeToElement(new ClosureMark(generation, job.CommittedAt), StoreJsonContext.Default.ClosureMark), generation),
            SqlWrite.AssertAbsent(ClosureCancellationCollection, generation),
        ], null, cancellationToken).ConfigureAwait(false);
        return versions?[0];
    }

    public Task<IReadOnlyList<StoredDocument>> QueryAsync(string collection, JsonElement filter, int limit, CancellationToken cancellationToken = default) =>
        this.QueryAsync(collection, filter, limit, null, cancellationToken);

    public async Task<IReadOnlyList<StoredDocument>> QueryAsync(string collection, JsonElement filter, int limit, IReadOnlyList<StoreSort>? sort, CancellationToken cancellationToken = default) =>
        (await AuthStorageTelemetry.ReadAsync(collection, "query", () => this.sql.QueryAsync(collection, null, filter, sort, limit, null, false, cancellationToken)).ConfigureAwait(false)).Items;

    public async Task<long> CountAsync(string collection, string instanceId, CancellationToken cancellationToken = default) =>
        (await this.sql.QueryAsync(collection, instanceId, null, null, 1, null, true, cancellationToken).ConfigureAwait(false)).Count ?? 0;

    public Task<StoreWrite> CreateAsync(string collection, string instanceId, string key, JsonElement record, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        return this.WriteFencedAsync(SqlWrite.Create(collection, record, NaturalKeyId(instanceId, collection, key)), cancellationToken);
    }

    public async Task<bool> CompareAndPutAsync(string collection, string instanceId, string key, JsonElement record, string expectedVersion, CancellationToken cancellationToken = default) =>
        await this.ReplaceAsync(collection, instanceId, key, record, expectedVersion, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<string?> ReplaceAsync(string collection, string instanceId, string key, JsonElement record, string expectedVersion, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        try
        {
            return (await this.WriteFencedAsync(SqlWrite.Put(collection, NaturalKeyId(instanceId, collection, key), record, expectedVersion), cancellationToken).ConfigureAwait(false)).Version;
        }
        catch (StorePreconditionFailedException)
        {
            return null;
        }
    }

    public async Task<string?> ReplaceClaimedAsync(string collection, string instanceId, string key, JsonElement record, string expectedVersion, string kind, string normalizedKey, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, key), StoreJsonContext.Default.UniqueClaim);
        try
        {
            return (await this.TransactFencedAsync(
            [
                SqlWrite.Put(collection, NaturalKeyId(instanceId, collection, key), record, expectedVersion),
                SqlWrite.Create(UniqueCollection, claim, NaturalKeyId(instanceId, kind, normalizedKey)),
            ], cancellationToken).ConfigureAwait(false))[0];
        }
        catch (StorePreconditionFailedException error) when (error.OperationIndex == 0)
        {
            return null;
        }
    }

    public async Task<bool> CommitVersionAsync(string collection, string instanceId, string key, JsonElement record, string? expectedVersion, string historyCollection, string historyKey, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        RequireEntityCollection(historyCollection);
        var id = NaturalKeyId(instanceId, collection, key);
        try
        {
            await this.TransactFencedAsync(
            [
                expectedVersion is null ? SqlWrite.Create(collection, record, id) : SqlWrite.Put(collection, id, record, expectedVersion),
                SqlWrite.Create(historyCollection, record, NaturalKeyId(instanceId, historyCollection, historyKey)),
            ], cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (StorePreconditionFailedException)
        {
            return false;
        }
    }

    public IAsyncEnumerable<StoredDocument> QueryAsync(string collection, string instanceId, string field, string value, CancellationToken cancellationToken = default) =>
        this.QueryAsync(collection, instanceId, [(field, value)], cancellationToken);

    public IAsyncEnumerable<StoredDocument> QueryAsync(string collection, string instanceId, IReadOnlyList<(string Field, string Value)> equals, CancellationToken cancellationToken = default) =>
        this.ScanAsync(collection, instanceId, equals, cancellationToken);

    public IAsyncEnumerable<StoredDocument> QueryLocalAsync(string collection, string instanceId, CancellationToken cancellationToken = default) =>
        this.ScanAsync(collection, instanceId, [], cancellationToken);

    private async IAsyncEnumerable<StoredDocument> ScanAsync(string collection, string instanceId, IReadOnlyList<(string Field, string Value)> equals, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(equals);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        JsonElement? filter = null;
        if (equals.Count > 0)
        {
            var conditions = new JsonArray();
            foreach (var (field, value) in equals)
            {
                conditions.Add(new JsonObject { ["eq"] = new JsonArray(field, value) });
            }

            filter = JsonSerializer.SerializeToElement(new JsonObject { ["and"] = conditions });
        }

        string? cursor = null;
        do
        {
            var page = await this.sql.QueryAsync(collection, instanceId, filter, null, 100, cursor, false, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                yield return item;
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);
    }

    public async Task<bool> CompareAndDeleteAsync(string collection, string instanceId, string key, string expectedVersion, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        return await this.sql.WriteAsync([SqlWrite.Delete(collection, NaturalKeyId(instanceId, collection, key), expectedVersion)], null, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<bool> RetireDeletionFenceAsync(DeletionJob job, string expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.InstanceId == "terrascale" && job.DashboardVerifiedAt is null)
        {
            return false;
        }

        return await this.sql.WriteAsync([SqlWrite.Delete(DeletionJobCollection, NaturalKeyId(job.InstanceId, DeletionJobCollection, job.Id), expectedVersion)], null, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<bool> ClaimUniqueAsync(string instanceId, string kind, string normalizedKey, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var record = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, ownerId), StoreJsonContext.Default.UniqueClaim);
        try
        {
            await this.WriteFencedAsync(SqlWrite.Create(UniqueCollection, record, NaturalKeyId(instanceId, kind, normalizedKey)), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (StorePreconditionFailedException)
        {
            return false;
        }
    }

    public async Task<bool> CreateClaimedAsync(string collection, string instanceId, string key, JsonElement record, string kind, string normalizedKey, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, key), StoreJsonContext.Default.UniqueClaim);
        try
        {
            await this.TransactFencedAsync(
            [
                SqlWrite.Create(UniqueCollection, claim, NaturalKeyId(instanceId, kind, normalizedKey)),
                SqlWrite.Create(collection, record, NaturalKeyId(instanceId, collection, key)),
            ], cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (StorePreconditionFailedException error) when (error.OperationIndex == 0)
        {
            return false;
        }
    }

    public async Task<string?> FindUniqueOwnerAsync(string instanceId, string kind, string normalizedKey, CancellationToken cancellationToken = default) =>
        await this.FindUniqueAsync(instanceId, kind, normalizedKey, cancellationToken).ConfigureAwait(false) is { } found ? found.OwnerId : null;

    public async Task<bool> ReleaseUniqueAsync(string instanceId, string kind, string normalizedKey, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (await this.FindUniqueAsync(instanceId, kind, normalizedKey, cancellationToken).ConfigureAwait(false) is not { } found || found.OwnerId != ownerId)
        {
            return false;
        }

        return await this.sql.WriteAsync([SqlWrite.Delete(UniqueCollection, NaturalKeyId(instanceId, kind, normalizedKey), found.Version)], null, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<int?> TransactAsync(IReadOnlyList<StoreOperation> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var pinned = operations.Where(operation => operation.Operation.IfMatch is not null)
            .ToDictionary(operation => (operation.Operation.Collection, operation.Operation.Id), operation => operation.Operation.IfMatch);
        var sent = new List<(SqlWrite Operation, int Index)>();
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Operation.Op == "assert" && pinned.TryGetValue((operation.Operation.Collection, operation.Operation.Id), out var version))
            {
                if (operation.CheckedVersion is null)
                {
                    throw new ArgumentException("An assertion on a record the transaction writes needs the version it was checked at.", nameof(operations));
                }

                if (operation.CheckedVersion != version)
                {
                    return index;
                }

                continue;
            }

            sent.Add((operation.Operation, index));
        }

        var fenceIds = new HashSet<(string, string)>();
        var count = sent.Count;
        for (var item = 0; item < count; item++)
        {
            var (operation, index) = sent[item];
            var revokes = operations[index].Revokes;
            foreach (var fence in DeletionFences(operation).Concat(revokes ? Enumerable.Empty<SqlWrite>() : ClosureFences(operation)))
            {
                if (fenceIds.Add((fence.Collection, fence.Id)))
                {
                    sent.Add((fence, index));
                }
            }
        }

        int? failed = null;
        var versions = await this.sql.WriteAsync([.. sent.Select(item => item.Operation)], at => failed = sent[at].Index, cancellationToken).ConfigureAwait(false);
        return versions is null ? failed : null;
    }

    public static StoreOperation Create(string collection, string instanceId, string key, JsonElement record)
    {
        RequireEntityCollection(collection);
        return new(SqlWrite.Create(collection, record, NaturalKeyId(instanceId, collection, key)));
    }

    public static StoreOperation Replace(string collection, string instanceId, string key, JsonElement record, string version)
    {
        RequireEntityCollection(collection);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return new(SqlWrite.Put(collection, NaturalKeyId(instanceId, collection, key), record, version));
    }

    public static StoreOperation Delete(string collection, string instanceId, string key, string version)
    {
        RequireEntityCollection(collection);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return new(SqlWrite.Delete(collection, NaturalKeyId(instanceId, collection, key), version));
    }

    public static StoreOperation Absent(string collection, string instanceId, string key)
    {
        RequireEntityCollection(collection);
        return new(SqlWrite.AssertAbsent(collection, NaturalKeyId(instanceId, collection, key)));
    }

    public static StoreOperation Require(string collection, string instanceId, string key, JsonElement filter, string? checkedVersion = null)
    {
        RequireEntityCollection(collection);
        return new(SqlWrite.Assert(collection, NaturalKeyId(instanceId, collection, key), filter)) { CheckedVersion = checkedVersion };
    }

    public Task<StoredPage> PageAsync(string collection, string instanceId, IReadOnlyList<JsonObject> conditions, IReadOnlyList<string> sort, int limit, string? cursor, bool descending = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        JsonElement? filter = conditions.Count == 0 ? null
            : JsonSerializer.SerializeToElement(new JsonObject { ["and"] = new JsonArray([.. conditions.Select(condition => condition.DeepClone())]) });
        return this.sql.QueryAsync(collection, instanceId, filter, [.. sort.Select(path => new StoreSort(path, descending ? "desc" : null))], limit, cursor, false, cancellationToken);
    }

    public Task<StoredPage> PageAsync(string collection, JsonElement filter, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        this.sql.QueryAsync(collection, null, filter, null, limit, cursor, false, cancellationToken);

    public static StoreOperation Claim(string instanceId, string kind, string normalizedKey, string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, ownerId), StoreJsonContext.Default.UniqueClaim);
        return new(SqlWrite.Create(UniqueCollection, claim, NaturalKeyId(instanceId, kind, normalizedKey)));
    }

    public static StoreOperation Reclaim(string instanceId, string kind, string normalizedKey, string ownerId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, ownerId), StoreJsonContext.Default.UniqueClaim);
        return new(SqlWrite.Put(UniqueCollection, NaturalKeyId(instanceId, kind, normalizedKey), claim, version));
    }

    public static StoreOperation Release(string instanceId, string kind, string normalizedKey, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        return new(SqlWrite.Delete(UniqueCollection, NaturalKeyId(instanceId, kind, normalizedKey), version));
    }

    public async Task<(string OwnerId, string Version)?> FindUniqueAsync(string instanceId, string kind, string normalizedKey, CancellationToken cancellationToken = default)
    {
        var document = await AuthStorageTelemetry.ReadAsync(UniqueCollection, "get", () => this.sql.GetAsync(UniqueCollection,
            NaturalKeyId(instanceId, kind, normalizedKey), cancellationToken)).ConfigureAwait(false);
        var claim = document?.Document.Deserialize(StoreJsonContext.Default.UniqueClaim);
        return claim is not null && claim.InstanceId == instanceId && claim.Kind == kind ? (claim.OwnerId, document!.Version) : null;
    }

    public static CredentialBinding NewCredentialBinding(string kind, string key) => new("credential:" + kind, key, NewId());

    public static StoreOperation CredentialClaim(string instanceId, CredentialBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.Kind.StartsWith("credential:", StringComparison.Ordinal))
        {
            throw new ArgumentException("A credential claim needs a credential kind.", nameof(binding));
        }

        return Claim(instanceId, binding.Kind, binding.Key, binding.OwnerId);
    }

    public static CredentialBinding UserStateBinding(string userId, string authStamp) => new("credential:user-state", userId, authStamp);

    public static CredentialBinding AuthorizationBinding(string authorizationId) => new("credential:authorization", authorizationId, authorizationId);

    public static IReadOnlyList<StoreOperation> CredentialRequirements(string instanceId, IReadOnlyList<CredentialBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count > MaxCredentialBindings)
        {
            throw new ArgumentException("Too many credential bindings.", nameof(bindings));
        }

        List<StoreOperation> operations = [];
        foreach (var binding in bindings.Distinct())
        {
            var (collection, id, field, value) = binding.Kind switch
            {
                "credential:user-state" => (UserCollection, NaturalKeyId(instanceId, UserCollection, binding.Key), "/authStamp", binding.OwnerId),
                "credential:authorization" when binding.OwnerId == binding.Key => (OidcAuthorizationCollection, NaturalKeyId(instanceId, OidcAuthorizationCollection, binding.Key), "/status", "valid"),
                _ when binding.Kind.StartsWith("credential:", StringComparison.Ordinal) => (UniqueCollection, NaturalKeyId(instanceId, binding.Kind, binding.Key), "/ownerId", binding.OwnerId),
                _ => throw new ArgumentException("Invalid credential binding.", nameof(bindings)),
            };
            var filter = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["and"] = new JsonArray(new JsonObject { ["eq"] = new JsonArray("/instanceId", instanceId) },
                    new JsonObject { ["eq"] = new JsonArray(field, value) }),
            });
            operations.Add(new StoreOperation(SqlWrite.Assert(collection, id, filter)));
        }

        return operations;
    }

    public async Task<bool> AreCredentialsCurrentAsync(string instanceId, IReadOnlyList<CredentialBinding> bindings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count > MaxCredentialBindings)
        {
            return false;
        }

        if (bindings.Any(binding => binding is null || string.IsNullOrEmpty(binding.Kind)
            || string.IsNullOrEmpty(binding.Key) || string.IsNullOrEmpty(binding.OwnerId)))
        {
            return false;
        }

        var checks = bindings.Distinct().Select(binding => this.IsCredentialCurrentAsync(instanceId, binding, cancellationToken));
        return (await Task.WhenAll(checks).ConfigureAwait(false)).All(current => current);
    }

    private async Task<bool> IsCredentialCurrentAsync(string instanceId, CredentialBinding binding, CancellationToken cancellationToken)
    {
        if (binding.Kind == "credential:user-state")
        {
            var user = await this.GetAsync(UserCollection, instanceId, binding.Key, cancellationToken).ConfigureAwait(false);
            return user is not null && user.Document.TryGetProperty("instanceId", out var scope) && scope.GetString() == instanceId
                && user.Document.TryGetProperty("id", out var id) && id.GetString() == binding.Key
                && user.Document.TryGetProperty("authStamp", out var stamp) && stamp.GetString() == binding.OwnerId
                && user.Document.TryGetProperty("email", out var email) && email.GetString() is { } address
                && await this.FindUniqueOwnerAsync(instanceId, "email", address, cancellationToken).ConfigureAwait(false) == binding.Key;
        }

        if (binding.Kind == "credential:authorization")
        {
            var authorization = await this.GetAsync(OidcAuthorizationCollection, instanceId, binding.Key, cancellationToken).ConfigureAwait(false);
            return binding.OwnerId == binding.Key && authorization is not null
                && authorization.Document.TryGetProperty("instanceId", out var scope) && scope.GetString() == instanceId
                && authorization.Document.TryGetProperty("id", out var id) && id.GetString() == binding.Key
                && authorization.Document.TryGetProperty("status", out var status) && status.GetString() == "valid";
        }

        return binding.Kind.StartsWith("credential:", StringComparison.Ordinal)
            && await this.FindUniqueOwnerAsync(instanceId, binding.Kind, binding.Key, cancellationToken).ConfigureAwait(false) == binding.OwnerId;
    }

    public async Task CreateChallengeAsync(string instanceId, string purpose, string tokenHash, string subjectId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        ValidateTokenHash(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        if (expiresAt <= clock.GetUtcNow())
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A challenge must expire in the future.");
        }

        var challenge = new StoredChallenge(instanceId, purpose, tokenHash, subjectId, expiresAt);
        await this.WriteFencedAsync(SqlWrite.Create(ChallengeCollection,
            JsonSerializer.SerializeToElement(challenge, StoreJsonContext.Default.StoredChallenge), NaturalKeyId(instanceId, purpose, tokenHash)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredChallenge?> ConsumeChallengeAsync(string instanceId, string purpose, string tokenHash, string subjectId, CancellationToken cancellationToken = default)
    {
        ValidateTokenHash(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var id = NaturalKeyId(instanceId, purpose, tokenHash);
        var document = await this.sql.GetAsync(ChallengeCollection, id, cancellationToken).ConfigureAwait(false);
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
                SqlWrite.Put(ChallengeCollection, id, JsonSerializer.SerializeToElement(consumed, StoreJsonContext.Default.StoredChallenge), document!.Version),
            ], cancellationToken).ConfigureAwait(false);
            return challenge.ExpiresAt > clock.GetUtcNow() ? consumed : null;
        }
        catch (StorePreconditionFailedException)
        {
            return null;
        }
    }

    private async Task<StoreWrite> WriteFencedAsync(SqlWrite operation, CancellationToken cancellationToken) =>
        new(operation.Id, (await this.TransactFencedAsync([operation], cancellationToken).ConfigureAwait(false))[0]);

    private async Task<IReadOnlyList<string>> TransactFencedAsync(IReadOnlyList<SqlWrite> operations, CancellationToken cancellationToken)
    {
        var fences = operations.SelectMany(operation => DeletionFences(operation).Concat(ClosureFences(operation))).DistinctBy(operation => (operation.Collection, operation.Id));
        int failed = -1;
        return await this.sql.WriteAsync([.. operations, .. fences], at => failed = at, cancellationToken).ConfigureAwait(false)
            ?? throw new StorePreconditionFailedException(failed);
    }

    private static IEnumerable<SqlWrite> DeletionFences(SqlWrite operation)
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
            SessionCollection or PasscodeCollection or MfaCollection or StepUpProofCollection or StepUpDeletionGrantCollection or StepUpPasscodeCollection or EmailChangeCollection => [("account", "userId")],
            OidcTokenCollection or OidcAuthorizationCollection => [("account", "subject")],
            UniqueCollection => [("account", "ownerId")],
            ChallengeCollection => [("account", "subjectId")],
            _ => [],
        };
        foreach (var (scope, field) in references)
        {
            if (document.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } subjectId)
            {
                yield return SqlWrite.AssertAbsent(DeletionJobCollection,
                    NaturalKeyId(instanceId, DeletionJobCollection, DeletionJob.Key(instanceId, scope, subjectId)));
            }
        }
    }

    private static IEnumerable<SqlWrite> ClosureFences(SqlWrite operation)
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
            yield return SqlWrite.AssertAbsent(ClosureCancellationCollection, creation);
        }

        (string Scope, string Field)[] references = operation.Collection switch
        {
            ProjectCollection => [("organization", "organizationId"), ("project", "id")],
            MembershipCollection => [("organization", "organizationId"), ("account", "userId")],
            ProjectMemberCollection => [("organization", "organizationId"), ("project", "projectId"), ("account", "userId")],
            ApiKeyCollection => [("organization", "organizationId"), ("project", "projectId")],
            InvitationCollection when operation.Op == "create" => [("organization", "organizationId"), ("project", "projectId")],
            _ => [],
        };
        foreach (var (scope, field) in references)
        {
            if (document.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } subjectId)
            {
                yield return SqlWrite.AssertAbsent(ClosureJobCollection,
                    NaturalKeyId(instanceId, ClosureJobCollection, ClosureJob.Key(scope, subjectId)));
            }
        }
    }

    public async Task<bool> DeleteWithClaimsAsync(string collection, string instanceId, string key, string version, string ownerId, CancellationToken cancellationToken = default)
    {
        RequireEntityCollection(collection);
        List<SqlWrite> operations = [SqlWrite.Delete(collection, NaturalKeyId(instanceId, collection, key), version)];
        await foreach (var claim in this.QueryAsync(UniqueCollection, instanceId, "/ownerId", ownerId, cancellationToken).ConfigureAwait(false))
        {
            if (claim.Document.Deserialize(StoreJsonContext.Default.UniqueClaim) is { } value && value.InstanceId == instanceId && value.OwnerId == ownerId)
            {
                if (operations.Count >= 100)
                {
                    throw new InvalidOperationException("A cleanup record has more owned claims than the bounded transaction permits.");
                }

                operations.Add(SqlWrite.Delete(UniqueCollection, claim.Id, claim.Version));
            }
        }

        return await this.sql.WriteAsync(operations, null, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<bool> DeleteOwnedInfrastructureAsync(string collection, string instanceId, string subjectId, StoredDocument document, CancellationToken cancellationToken = default)
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

        return await this.sql.WriteAsync([SqlWrite.Delete(collection, document.Id, document.Version)], null, cancellationToken).ConfigureAwait(false) is not null;
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

public sealed class StoreOperation
{
    internal StoreOperation(SqlWrite operation) => this.Operation = operation;

    internal SqlWrite Operation { get; }

    internal string? CheckedVersion { get; init; }

    internal bool Revokes { get; private init; }

    public StoreOperation AddsNoAccess() => new(this.Operation) { CheckedVersion = this.CheckedVersion, Revokes = true };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredChallenge))]
[JsonSerializable(typeof(UniqueClaim))]
[JsonSerializable(typeof(ClosureMark))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
