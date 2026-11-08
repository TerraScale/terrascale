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
    private readonly SqlRecords sql = new(local);

    public const int MaxCredentialBindings = 64;

    public const string UniqueTable = "uniques";

    public const string ChallengeTable = "verification_tokens";

    public const string HealthTable = "storage_health";

    public const string UserTable = "users";

    public const string SessionTable = "sessions";

    public const string PasscodeTable = "passcodes";

    public const string MfaTable = "mfa_credentials";

    public const string PasskeyTable = "passkey_credentials";

    public const string OidcApplicationTable = "oidc_applications";

    public const string OidcAuthorizationTable = "oidc_authorizations";

    public const string OidcTokenTable = "oidc_tokens";

    public const string OrganizationTable = "organizations";

    public const string MembershipTable = "memberships";

    public const string ProjectTable = "projects";

    public const string ProjectMemberTable = "project_members";

    public const string ApiKeyTable = "api_keys";

    public const string ApiKeyUsageTable = "api_key_usage";

    public const string InvitationTable = "invitations";

    public const string SigningKeyTable = "signing_keys";

    public const string DeletionJobTable = "deletion_jobs";

    public const string DashboardDeletionCursorTable = "dashboard_deletion_cursors";

    public const string AccountDeletionReceiptTable = "account_deletion_receipts";

    public const string StepUpProofTable = "step_up_proofs";

    public const string StepUpDeletionGrantTable = "step_up_deletion_grants";

    public const string StepUpPasscodeTable = "step_up_passcodes";

    public const string EmailChangeTable = "email_changes";

    public const string ClosureJobTable = "closure_jobs";

    public const string ClosureCancellationTable = "closure_cancellations";

    public const string ClosureCommitTable = "closure_commits";

    public const string AuditEventTable = "audit_events";

    public const string ProjectAuthSettingsTable = "project_auth_settings";

    public const string MailOutboxTable = "auth_mail_outbox";

    public const string OrganizationSsoTable = "organization_sso";

    public static IReadOnlyList<string> RequiredTables { get; } =
    [
        UniqueTable, ChallengeTable, HealthTable, UserTable, SessionTable, PasscodeTable,
        SigningKeyTable, OidcApplicationTable, OidcAuthorizationTable, OidcTokenTable,
        "email_admission", "rate_limits", "email_configs", "email_config_versions", "email_allowance_deliveries", "email_alert_deliveries", "email_support_deliveries",
        OrganizationTable, MembershipTable, ProjectTable, ProjectMemberTable, ApiKeyTable, ApiKeyUsageTable,
        InvitationTable, AuditEventTable, MfaTable, PasskeyTable, DeletionJobTable, DashboardDeletionCursorTable, ClosureJobTable,
        ClosureCancellationTable, ClosureCommitTable, ProjectAuthSettingsTable, OrganizationSsoTable, AccountDeletionReceiptTable,
        StepUpProofTable, StepUpDeletionGrantTable, StepUpPasscodeTable, EmailChangeTable, MailOutboxTable,
    ];

    private static readonly TerraScaleIdGenerator Ids = new();

    public static string NewId() => Ids.NewId();

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task ProvisionAsync(CancellationToken cancellationToken = default) => this.sql.ProvisionAsync(RequiredTables, cancellationToken);

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

    public Task<StoredRow?> GetAsync(string table, string instanceId, string key, CancellationToken cancellationToken = default) =>
        AuthStorageTelemetry.ReadAsync(table, "get", () => this.sql.GetAsync(table, NaturalKeyId(instanceId, table, key), cancellationToken));

    public Task<StoredRow?> GetLocalAsync(string table, string instanceId, string key, CancellationToken cancellationToken = default) =>
        this.GetAsync(table, instanceId, key, cancellationToken);

    public async Task<string> CancelAccountClosureAsync(ClosureJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var generation = job.Generation ?? throw new InvalidOperationException("An account closure needs a generation.");
        var jobId = NaturalKeyId(job.InstanceId, ClosureJobTable, ClosureJob.Key("account", job.SubjectId));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (await this.sql.GetAsync(ClosureCommitTable, generation, cancellationToken).ConfigureAwait(false) is not null)
            {
                return "committed";
            }

            var current = await this.sql.GetAsync(ClosureJobTable, jobId, cancellationToken).ConfigureAwait(false);
            var live = current?.Body.Deserialize(ClosureJson.Default.ClosureJob) is { } stored && stored.Generation == generation ? stored : null;
            if (live?.CommittedAt is not null)
            {
                return "committed";
            }

            List<SqlWrite> operations = [SqlWrite.AssertAbsent(ClosureCommitTable, generation)];
            if (await this.sql.GetAsync(ClosureCancellationTable, generation, cancellationToken).ConfigureAwait(false) is null)
            {
                operations.Add(SqlWrite.Create(ClosureCancellationTable,
                    JsonSerializer.SerializeToElement(new ClosureMark(generation), StoreJsonContext.Default.ClosureMark), generation));
            }

            if (live is not null)
            {
                operations.Add(SqlWrite.Delete(ClosureJobTable, jobId, current!.Version));
            }

            if (await this.sql.WriteAsync(operations, null, cancellationToken).ConfigureAwait(false) is not null)
            {
                return "cancelled";
            }
        }

        throw ApiException.Unavailable();
    }

    public async Task<bool> IsClosureCancellingAsync(ClosureJob job, CancellationToken cancellationToken = default) =>
        job.Generation is { } generation && await this.sql.GetAsync(ClosureCancellationTable, generation, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<DateTimeOffset?> ClosureCommittedAtAsync(ClosureJob job, CancellationToken cancellationToken = default)
    {
        var mark = job.Generation is { } generation ? await this.sql.GetAsync(ClosureCommitTable, generation, cancellationToken).ConfigureAwait(false) : null;
        return mark?.Body.Deserialize(StoreJsonContext.Default.ClosureMark)?.CommittedAt;
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
            SqlWrite.Put(ClosureJobTable, NaturalKeyId(job.InstanceId, ClosureJobTable, ClosureJob.Key("account", job.SubjectId)),
                JsonSerializer.SerializeToElement(job, ClosureJson.Default.ClosureJob), version),
            SqlWrite.Create(ClosureCommitTable, JsonSerializer.SerializeToElement(new ClosureMark(generation, job.CommittedAt), StoreJsonContext.Default.ClosureMark), generation),
            SqlWrite.AssertAbsent(ClosureCancellationTable, generation),
        ], null, cancellationToken).ConfigureAwait(false);
        return versions?[0];
    }

    public Task<IReadOnlyList<StoredRow>> QueryAsync(string table, JsonElement filter, int limit, CancellationToken cancellationToken = default) =>
        this.QueryAsync(table, filter, limit, null, cancellationToken);

    public async Task<IReadOnlyList<StoredRow>> QueryAsync(string table, JsonElement filter, int limit, IReadOnlyList<StoreSort>? sort, CancellationToken cancellationToken = default) =>
        (await AuthStorageTelemetry.ReadAsync(table, "query", () => this.sql.QueryAsync(table, null, filter, sort, limit, null, false, cancellationToken)).ConfigureAwait(false)).Items;

    public async Task<long> CountAsync(string table, string instanceId, CancellationToken cancellationToken = default) =>
        (await this.sql.QueryAsync(table, instanceId, null, null, 1, null, true, cancellationToken).ConfigureAwait(false)).Count ?? 0;

    public Task<StoreWrite> CreateAsync(string table, string instanceId, string key, JsonElement record, CancellationToken cancellationToken = default)
    {
        RequireEntityTable(table);
        return this.WriteFencedAsync(SqlWrite.Create(table, record, NaturalKeyId(instanceId, table, key)), cancellationToken);
    }

    public async Task<bool> CompareAndPutAsync(string table, string instanceId, string key, JsonElement record, string expectedVersion, CancellationToken cancellationToken = default) =>
        await this.ReplaceAsync(table, instanceId, key, record, expectedVersion, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<string?> ReplaceAsync(string table, string instanceId, string key, JsonElement record, string expectedVersion, CancellationToken cancellationToken = default)
    {
        RequireEntityTable(table);
        try
        {
            return (await this.WriteFencedAsync(SqlWrite.Put(table, NaturalKeyId(instanceId, table, key), record, expectedVersion), cancellationToken).ConfigureAwait(false)).Version;
        }
        catch (StorePreconditionFailedException)
        {
            return null;
        }
    }

    public async Task<string?> ReplaceClaimedAsync(string table, string instanceId, string key, JsonElement record, string expectedVersion, string kind, string normalizedKey, CancellationToken cancellationToken = default)
    {
        RequireEntityTable(table);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, key), StoreJsonContext.Default.UniqueClaim);
        try
        {
            return (await this.TransactFencedAsync(
            [
                SqlWrite.Put(table, NaturalKeyId(instanceId, table, key), record, expectedVersion),
                SqlWrite.Create(UniqueTable, claim, NaturalKeyId(instanceId, kind, normalizedKey)),
            ], cancellationToken).ConfigureAwait(false))[0];
        }
        catch (StorePreconditionFailedException error) when (error.OperationIndex == 0)
        {
            return null;
        }
    }

    public async Task<bool> CommitVersionAsync(string table, string instanceId, string key, JsonElement record, string? expectedVersion, string historyTable, string historyKey, CancellationToken cancellationToken = default)
    {
        RequireEntityTable(table);
        RequireEntityTable(historyTable);
        var id = NaturalKeyId(instanceId, table, key);
        try
        {
            await this.TransactFencedAsync(
            [
                expectedVersion is null ? SqlWrite.Create(table, record, id) : SqlWrite.Put(table, id, record, expectedVersion),
                SqlWrite.Create(historyTable, record, NaturalKeyId(instanceId, historyTable, historyKey)),
            ], cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (StorePreconditionFailedException)
        {
            return false;
        }
    }

    public IAsyncEnumerable<StoredRow> QueryAsync(string table, string instanceId, string field, string value, CancellationToken cancellationToken = default) =>
        this.QueryAsync(table, instanceId, [(field, value)], cancellationToken);

    public IAsyncEnumerable<StoredRow> QueryAsync(string table, string instanceId, IReadOnlyList<(string Field, string Value)> equals, CancellationToken cancellationToken = default) =>
        this.ScanAsync(table, instanceId, equals, cancellationToken);

    public IAsyncEnumerable<StoredRow> QueryLocalAsync(string table, string instanceId, CancellationToken cancellationToken = default) =>
        this.ScanAsync(table, instanceId, [], cancellationToken);

    private async IAsyncEnumerable<StoredRow> ScanAsync(string table, string instanceId, IReadOnlyList<(string Field, string Value)> equals, [EnumeratorCancellation] CancellationToken cancellationToken)
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
            var page = await this.sql.QueryAsync(table, instanceId, filter, null, 100, cursor, false, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                yield return item;
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);
    }

    public async Task<bool> CompareAndDeleteAsync(string table, string instanceId, string key, string expectedVersion, CancellationToken cancellationToken = default)
    {
        RequireEntityTable(table);
        return await this.sql.WriteAsync([SqlWrite.Delete(table, NaturalKeyId(instanceId, table, key), expectedVersion)], null, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<bool> RetireDeletionFenceAsync(DeletionJob job, string expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.InstanceId == "terrascale" && job.DashboardVerifiedAt is null)
        {
            return false;
        }

        return await this.sql.WriteAsync([SqlWrite.Delete(DeletionJobTable, NaturalKeyId(job.InstanceId, DeletionJobTable, job.Id), expectedVersion)], null, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<bool> ClaimUniqueAsync(string instanceId, string kind, string normalizedKey, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var record = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, ownerId), StoreJsonContext.Default.UniqueClaim);
        try
        {
            await this.WriteFencedAsync(SqlWrite.Create(UniqueTable, record, NaturalKeyId(instanceId, kind, normalizedKey)), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (StorePreconditionFailedException)
        {
            return false;
        }
    }

    public async Task<bool> CreateClaimedAsync(string table, string instanceId, string key, JsonElement record, string kind, string normalizedKey, CancellationToken cancellationToken = default)
    {
        RequireEntityTable(table);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, key), StoreJsonContext.Default.UniqueClaim);
        try
        {
            await this.TransactFencedAsync(
            [
                SqlWrite.Create(UniqueTable, claim, NaturalKeyId(instanceId, kind, normalizedKey)),
                SqlWrite.Create(table, record, NaturalKeyId(instanceId, table, key)),
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

        return await this.sql.WriteAsync([SqlWrite.Delete(UniqueTable, NaturalKeyId(instanceId, kind, normalizedKey), found.Version)], null, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<int?> TransactAsync(IReadOnlyList<StoreOperation> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var pinned = operations.Where(operation => operation.Operation.IfMatch is not null)
            .ToDictionary(operation => (operation.Operation.Table, operation.Operation.Id), operation => operation.Operation.IfMatch);
        var sent = new List<(SqlWrite Operation, int Index)>();
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Operation.Op == "assert" && pinned.TryGetValue((operation.Operation.Table, operation.Operation.Id), out var version))
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
                if (fenceIds.Add((fence.Table, fence.Id)))
                {
                    sent.Add((fence, index));
                }
            }
        }

        int? failed = null;
        var versions = await this.sql.WriteAsync([.. sent.Select(item => item.Operation)], at => failed = sent[at].Index, cancellationToken).ConfigureAwait(false);
        return versions is null ? failed : null;
    }

    public static StoreOperation Create(string table, string instanceId, string key, JsonElement record)
    {
        RequireEntityTable(table);
        return new(SqlWrite.Create(table, record, NaturalKeyId(instanceId, table, key)));
    }

    public static StoreOperation Replace(string table, string instanceId, string key, JsonElement record, string version)
    {
        RequireEntityTable(table);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return new(SqlWrite.Put(table, NaturalKeyId(instanceId, table, key), record, version));
    }

    public static StoreOperation Delete(string table, string instanceId, string key, string version)
    {
        RequireEntityTable(table);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return new(SqlWrite.Delete(table, NaturalKeyId(instanceId, table, key), version));
    }

    public static StoreOperation Absent(string table, string instanceId, string key)
    {
        RequireEntityTable(table);
        return new(SqlWrite.AssertAbsent(table, NaturalKeyId(instanceId, table, key)));
    }

    public static StoreOperation Require(string table, string instanceId, string key, JsonElement filter, string? checkedVersion = null)
    {
        RequireEntityTable(table);
        return new(SqlWrite.Assert(table, NaturalKeyId(instanceId, table, key), filter)) { CheckedVersion = checkedVersion };
    }

    public Task<StoredPage> PageAsync(string table, string instanceId, IReadOnlyList<JsonObject> conditions, IReadOnlyList<string> sort, int limit, string? cursor, bool descending = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        JsonElement? filter = conditions.Count == 0 ? null
            : JsonSerializer.SerializeToElement(new JsonObject { ["and"] = new JsonArray([.. conditions.Select(condition => condition.DeepClone())]) });
        return this.sql.QueryAsync(table, instanceId, filter, [.. sort.Select(path => new StoreSort(path, descending ? "desc" : null))], limit, cursor, false, cancellationToken);
    }

    public Task<StoredPage> PageAsync(string table, JsonElement filter, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        this.sql.QueryAsync(table, null, filter, null, limit, cursor, false, cancellationToken);

    public static StoreOperation Claim(string instanceId, string kind, string normalizedKey, string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, ownerId), StoreJsonContext.Default.UniqueClaim);
        return new(SqlWrite.Create(UniqueTable, claim, NaturalKeyId(instanceId, kind, normalizedKey)));
    }

    public static StoreOperation Reclaim(string instanceId, string kind, string normalizedKey, string ownerId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        var claim = JsonSerializer.SerializeToElement(new UniqueClaim(instanceId, kind, ownerId), StoreJsonContext.Default.UniqueClaim);
        return new(SqlWrite.Put(UniqueTable, NaturalKeyId(instanceId, kind, normalizedKey), claim, version));
    }

    public static StoreOperation Release(string instanceId, string kind, string normalizedKey, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        return new(SqlWrite.Delete(UniqueTable, NaturalKeyId(instanceId, kind, normalizedKey), version));
    }

    public async Task<(string OwnerId, string Version)?> FindUniqueAsync(string instanceId, string kind, string normalizedKey, CancellationToken cancellationToken = default)
    {
        var rowData = await AuthStorageTelemetry.ReadAsync(UniqueTable, "get", () => this.sql.GetAsync(UniqueTable,
            NaturalKeyId(instanceId, kind, normalizedKey), cancellationToken)).ConfigureAwait(false);
        var claim = rowData?.Body.Deserialize(StoreJsonContext.Default.UniqueClaim);
        return claim is not null && claim.InstanceId == instanceId && claim.Kind == kind ? (claim.OwnerId, rowData!.Version) : null;
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
            var (table, id, field, value) = binding.Kind switch
            {
                "credential:user-state" => (UserTable, NaturalKeyId(instanceId, UserTable, binding.Key), "/authStamp", binding.OwnerId),
                "credential:authorization" when binding.OwnerId == binding.Key => (OidcAuthorizationTable, NaturalKeyId(instanceId, OidcAuthorizationTable, binding.Key), "/status", "valid"),
                _ when binding.Kind.StartsWith("credential:", StringComparison.Ordinal) => (UniqueTable, NaturalKeyId(instanceId, binding.Kind, binding.Key), "/ownerId", binding.OwnerId),
                _ => throw new ArgumentException("Invalid credential binding.", nameof(bindings)),
            };
            var filter = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["and"] = new JsonArray(new JsonObject { ["eq"] = new JsonArray("/instanceId", instanceId) },
                    new JsonObject { ["eq"] = new JsonArray(field, value) }),
            });
            operations.Add(new StoreOperation(SqlWrite.Assert(table, id, filter)));
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
            var user = await this.GetAsync(UserTable, instanceId, binding.Key, cancellationToken).ConfigureAwait(false);
            return user is not null && user.Body.TryGetProperty("instanceId", out var scope) && scope.GetString() == instanceId
                && user.Body.TryGetProperty("id", out var id) && id.GetString() == binding.Key
                && user.Body.TryGetProperty("authStamp", out var stamp) && stamp.GetString() == binding.OwnerId
                && user.Body.TryGetProperty("email", out var email) && email.GetString() is { } address
                && await this.FindUniqueOwnerAsync(instanceId, "email", address, cancellationToken).ConfigureAwait(false) == binding.Key;
        }

        if (binding.Kind == "credential:authorization")
        {
            var authorization = await this.GetAsync(OidcAuthorizationTable, instanceId, binding.Key, cancellationToken).ConfigureAwait(false);
            return binding.OwnerId == binding.Key && authorization is not null
                && authorization.Body.TryGetProperty("instanceId", out var scope) && scope.GetString() == instanceId
                && authorization.Body.TryGetProperty("id", out var id) && id.GetString() == binding.Key
                && authorization.Body.TryGetProperty("status", out var status) && status.GetString() == "valid";
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
        await this.WriteFencedAsync(SqlWrite.Create(ChallengeTable,
            JsonSerializer.SerializeToElement(challenge, StoreJsonContext.Default.StoredChallenge), NaturalKeyId(instanceId, purpose, tokenHash)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredChallenge?> ConsumeChallengeAsync(string instanceId, string purpose, string tokenHash, string subjectId, CancellationToken cancellationToken = default)
    {
        ValidateTokenHash(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var id = NaturalKeyId(instanceId, purpose, tokenHash);
        var rowData = await this.sql.GetAsync(ChallengeTable, id, cancellationToken).ConfigureAwait(false);
        var challenge = rowData?.Body.Deserialize(StoreJsonContext.Default.StoredChallenge);
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
                SqlWrite.Put(ChallengeTable, id, JsonSerializer.SerializeToElement(consumed, StoreJsonContext.Default.StoredChallenge), rowData!.Version),
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
        var fences = operations.SelectMany(operation => DeletionFences(operation).Concat(ClosureFences(operation))).DistinctBy(operation => (operation.Table, operation.Id));
        int failed = -1;
        return await this.sql.WriteAsync([.. operations, .. fences], at => failed = at, cancellationToken).ConfigureAwait(false)
            ?? throw new StorePreconditionFailedException(failed);
    }

    private static IEnumerable<SqlWrite> DeletionFences(SqlWrite operation)
    {
        if (operation.Body is not { ValueKind: JsonValueKind.Object } rowData
            || !rowData.TryGetProperty("instanceId", out var instance) || instance.GetString() is not { } instanceId)
        {
            yield break;
        }

        (string Scope, string Field)[] references = operation.Table switch
        {
            UserTable => [("account", "id")],
            OrganizationTable => [("organization", "id")],
            ProjectTable => [("organization", "organizationId"), ("project", "id")],
            MembershipTable => [("organization", "organizationId"), ("account", "userId")],
            ProjectMemberTable => [("organization", "organizationId"), ("project", "projectId"), ("account", "userId")],
            ApiKeyTable or ApiKeyUsageTable or InvitationTable => [("organization", "organizationId"), ("project", "projectId")],
            AuditEventTable => [("organization", "organizationId"), ("account", "actor")],
            SessionTable or PasscodeTable or MfaTable or StepUpProofTable or StepUpDeletionGrantTable or StepUpPasscodeTable or EmailChangeTable => [("account", "userId")],
            OidcTokenTable or OidcAuthorizationTable => [("account", "subject")],
            UniqueTable => [("account", "ownerId")],
            ChallengeTable => [("account", "subjectId")],
            _ => [],
        };
        foreach (var (scope, field) in references)
        {
            if (rowData.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } subjectId)
            {
                yield return SqlWrite.AssertAbsent(DeletionJobTable,
                    NaturalKeyId(instanceId, DeletionJobTable, DeletionJob.Key(instanceId, scope, subjectId)));
            }
        }
    }

    private static IEnumerable<SqlWrite> ClosureFences(SqlWrite operation)
    {
        if (operation.Body is not { ValueKind: JsonValueKind.Object } rowData
            || !rowData.TryGetProperty("instanceId", out var instance) || instance.GetString() is not { } instanceId)
        {
            yield break;
        }

        if (operation.Table == ClosureJobTable && rowData.TryGetProperty("scope", out var scopeField) && scopeField.GetString() == "account"
            && rowData.TryGetProperty("generation", out var generation) && generation.ValueKind == JsonValueKind.String && generation.GetString() is { } creation
            && (!rowData.TryGetProperty("committedAt", out var committed) || committed.ValueKind == JsonValueKind.Null))
        {
            yield return SqlWrite.AssertAbsent(ClosureCancellationTable, creation);
        }

        (string Scope, string Field)[] references = operation.Table switch
        {
            ProjectTable => [("organization", "organizationId"), ("project", "id")],
            MembershipTable => [("organization", "organizationId"), ("account", "userId")],
            ProjectMemberTable => [("organization", "organizationId"), ("project", "projectId"), ("account", "userId")],
            ApiKeyTable => [("organization", "organizationId"), ("project", "projectId")],
            InvitationTable when operation.Op == "create" => [("organization", "organizationId"), ("project", "projectId")],
            _ => [],
        };
        foreach (var (scope, field) in references)
        {
            if (rowData.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } subjectId)
            {
                yield return SqlWrite.AssertAbsent(ClosureJobTable,
                    NaturalKeyId(instanceId, ClosureJobTable, ClosureJob.Key(scope, subjectId)));
            }
        }
    }

    public async Task<bool> DeleteWithClaimsAsync(string table, string instanceId, string key, string version, string ownerId, CancellationToken cancellationToken = default)
    {
        RequireEntityTable(table);
        List<SqlWrite> operations = [SqlWrite.Delete(table, NaturalKeyId(instanceId, table, key), version)];
        await foreach (var claim in this.QueryAsync(UniqueTable, instanceId, "/ownerId", ownerId, cancellationToken).ConfigureAwait(false))
        {
            if (claim.Body.Deserialize(StoreJsonContext.Default.UniqueClaim) is { } value && value.InstanceId == instanceId && value.OwnerId == ownerId)
            {
                if (operations.Count >= 100)
                {
                    throw new InvalidOperationException("A cleanup record has more owned claims than the bounded transaction permits.");
                }

                operations.Add(SqlWrite.Delete(UniqueTable, claim.Id, claim.Version));
            }
        }

        return await this.sql.WriteAsync(operations, null, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<bool> DeleteOwnedInfrastructureAsync(string table, string instanceId, string subjectId, StoredRow rowData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rowData);
        var owned = table switch
        {
            UniqueTable => rowData.Body.Deserialize(StoreJsonContext.Default.UniqueClaim) is { } claim && claim.InstanceId == instanceId && claim.OwnerId == subjectId,
            ChallengeTable => rowData.Body.Deserialize(StoreJsonContext.Default.StoredChallenge) is { } challenge && challenge.InstanceId == instanceId && challenge.SubjectId == subjectId,
            _ => false,
        };
        if (!owned)
        {
            throw new ArgumentException("The infrastructure record is outside the deletion scope.", nameof(rowData));
        }

        return await this.sql.WriteAsync([SqlWrite.Delete(table, rowData.Id, rowData.Version)], null, cancellationToken).ConfigureAwait(false) is not null;
    }

    private static void RequireEntityTable(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (table is UniqueTable or ChallengeTable or HealthTable)
        {
            throw new ArgumentException("Infrastructure records require their dedicated operations.", nameof(table));
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
