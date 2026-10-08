using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>A committed commit as stored with its user: the request it answered and its response.</summary>
/// <param name="RequestHash">Lowercase hex SHA-256 of the request body.</param>
/// <param name="CommittedAt">Commit time; the record is kept for <c>CommitRetention</c> after it.</param>
/// <param name="Body">The response, UTF-8 JSON text.</param>
internal sealed record CommitRecord(string RequestHash, DateTimeOffset CommittedAt, string Body);

/// <summary>Which commit record a <c>commit_expiry</c> entry purges.</summary>
/// <param name="UserId">The committing user.</param>
/// <param name="CommitId">The commit id.</param>
internal sealed record CommitExpiry(string UserId, string CommitId);

/// <summary>A failed operation of a commit, before the commit names its index.</summary>
/// <param name="Error">The failure.</param>
internal sealed record OperationFailed(Failure Error);

/// <summary>One operation of a commit: its result, or why the commit stops.</summary>
internal readonly union CommitStep(CommitResultBody, OperationFailed);

/// <summary>
/// Commits (docs/management-commits.md): every operation runs in one write transaction of the system database. The
/// Management methods run unchanged inside it, because <see cref="CommitTransaction"/> makes their reads and writes
/// use the commit's transaction instead of their own. Database files are created under cleanup intents before the
/// transaction and removed after it, as the single-operation routes do. Expected failures are values: a rejected
/// commit leaves its transaction uncommitted, so nothing it wrote is published.
/// </summary>
internal sealed partial class EngineManagementService
{
    // Every organization and account has a version document whose engine version (a commit offset) is its version.
    private const string OrganizationVersions = "organization_versions";
    private const string AccountVersions = "account_versions";
    private const string CommitExpiries = "commit_expiry";

    // Collections of the dashboard document space are this prefix and a data API collection name.
    private const string DashboardSpace = "dashboard/";

    private const int CommitPurgeBatch = 100;

    private static readonly TimeSpan CommitRetention = TimeSpan.FromDays(7);
    private static readonly byte[] VersionDocument = "{}"u8.ToArray();

    /// <summary>The databases a commit created files for, or why it stopped before its transaction.</summary>
    private readonly union PreparedDatabases(Dictionary<int, AddedDatabase>, CommitRejected);

    // The transaction of the commit running on this async flow, if any.
    private static readonly AsyncLocal<IStorageTransaction?> CommitTransaction = new();

    // Inside a commit, a transaction of its own would wait for the writer the commit holds, or read around its writes.
    private static void RequireNoCommit()
    {
        if (CommitTransaction.Value is not null)
        {
            throw new InvalidOperationException("a commit operation must use the commit's transaction");
        }
    }

    public async ValueTask<CommitResult> CommitAsync(
        string userId, string? email, string commitId, string requestHash, IReadOnlyList<CommitOperation> operations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (await ReadCatalogAsync(transaction => RecordedAsync(transaction, userId, commitId, cancellationToken), cancellationToken).ConfigureAwait(false) is { } recorded)
        {
            return Replay(recorded, requestHash);
        }

        var prepared = await PrepareDatabasesAsync(userId, operations, cancellationToken).ConfigureAwait(false);
        if (prepared is CommitRejected rejected)
        {
            return rejected;
        }

        var created = prepared is Dictionary<int, AddedDatabase> databases ? databases : throw new UnreachableException();
        var deleted = new List<TenantContext>();
        CommitResult result;
        try
        {
            result = await WriteCatalogAsync(
                transaction => ApplyCommitAsync(transaction, userId, email, commitId, requestHash, operations, created, deleted, cancellationToken), cancellationToken,
                keep: applied => applied is CommitOutcome { Replayed: false })
                .ConfigureAwait(false);
        }
        catch
        {
            foreach (var database in created.Values)
            {
                await AbandonDatabaseAsync(database.Tenant).ConfigureAwait(false);
            }

            throw;
        }

        var committed = result is CommitOutcome { Replayed: false };
        foreach (var database in created.Values)
        {
            if (committed)
            {
                await engine.CompleteDatabaseIntentAsync(database.Tenant, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await AbandonDatabaseAsync(database.Tenant).ConfigureAwait(false);
            }
        }

        // The intents are committed: a failure here leaves them for the next start.
        if (committed)
        {
            foreach (var storage in deleted)
            {
                await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return result;
    }

    public ValueTask<Found<byte[]>> GetCommitAsync(string userId, string commitId, CancellationToken cancellationToken) =>
        ReadAsync<Found<byte[]>>(async transaction =>
            await RecordedAsync(transaction, userId, commitId, cancellationToken).ConfigureAwait(false) is { } recorded
                ? Encoding.UTF8.GetBytes(recorded.Body)
                : new NotFound("no committed commit has this id"),
            cancellationToken);

    public ValueTask<Found<VersionsBody>> GetVersionsAsync(string userId, IReadOnlyList<string> organizations, CancellationToken cancellationToken) =>
        ReadAsync<Found<VersionsBody>>(async transaction =>
        {
            ArgumentNullException.ThrowIfNull(organizations);
            var versions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var organizationId in organizations)
            {
                if (await GetMemberAsync(transaction, organizationId, userId, cancellationToken).ConfigureAwait(false) is null)
                {
                    return new NotFound(Message(ManagementError.NotFound));
                }

                versions[organizationId] = await VersionAsync(transaction, OrganizationVersions, organizationId, cancellationToken).ConfigureAwait(false);
            }

            return new VersionsBody(await VersionAsync(transaction, AccountVersions, userId, cancellationToken).ConfigureAwait(false), versions);
        }, cancellationToken);

    public ValueTask<Found<StoredDocument>> GetDocumentAsync(string collection, string id, CancellationToken cancellationToken) =>
        ReadAsync<Found<StoredDocument>>(async transaction =>
            await transaction.GetAsync(DashboardSpace + collection, id, cancellationToken).ConfigureAwait(false) is { } document
                ? document
                : new NotFound("the document does not exist"),
            cancellationToken);

    public async ValueTask<QueryResult> QueryDocumentsAsync(DocumentQuery query, long? countLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        string? shape;
        IMemoryReservation? retained = null;
        using (var scratch = memory.TryReserve(MemoryLane.Foreground, 64 * 1024))
        {
            shape = scratch is null ? null : QueryShape.Normalize(query);
            if (shape is not null)
            {
                retained = memory.TryReserve(MemoryLane.Foreground, 32L + 2L * shape.Length);
            }
        }

        using var reservation = retained;
        if (reservation is null)
        {
            shape = null;
        }
        var observation = new QueryObservation();
        var started = time.GetTimestamp();
        try
        {
            return await ReadAsync(transaction => documents.Value.QueryAsync(
                transaction, query with { Collection = DashboardSpace + query.Collection }, countLimit, cancellationToken, observation), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            shapes.Value.Record(null, query.Collection, shape ?? string.Empty, time.GetElapsedTime(started), observation.RowsScanned);
        }
    }

    /// <summary>The version of a dashboard document as the commit and read routes report it.</summary>
    internal static string DocumentVersion(StoredDocument document, string collection) =>
        ETags.Version(document.Scope, DashboardSpace + collection, document.Id, document.Version, document.Json, document.Deleted);

    /// <summary>Advances an organization's version, in the caller's write transaction; every Management change to it calls this.</summary>
    internal static async ValueTask TouchOrganizationAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken) =>
        await transaction.PutAsync(OrganizationVersions, organizationId, VersionDocument, cancellationToken).ConfigureAwait(false);

    // Advances a user's account version: membership, role and current organization changes.
    private static async ValueTask TouchAccountAsync(IStorageTransaction transaction, string userId, CancellationToken cancellationToken) =>
        await transaction.PutAsync(AccountVersions, userId, VersionDocument, cancellationToken).ConfigureAwait(false);

    // "0" until the first change.
    private static async ValueTask<string> VersionAsync(IStorageTransaction transaction, string collection, string id, CancellationToken cancellationToken) =>
        ((await transaction.GetVersionAsync(collection, id, cancellationToken).ConfigureAwait(false))?.Version ?? 0).ToString(CultureInfo.InvariantCulture);

    private async ValueTask<CommitResult> ApplyCommitAsync(
        IStorageTransaction transaction, string userId, string? email, string commitId, string requestHash, IReadOnlyList<CommitOperation> operations,
        IReadOnlyDictionary<int, AddedDatabase> created, List<TenantContext> deleted, CancellationToken cancellationToken)
    {
        // Duplicates serialize on the write transaction: only the first applies.
        if (await RecordedAsync(transaction, userId, commitId, cancellationToken).ConfigureAwait(false) is { } recorded)
        {
            return Replay(recorded, requestHash);
        }

        CommitTransaction.Value = transaction;
        var results = new CommitResultBody[operations.Count];
        var named = new List<string>();
        var before = new Dictionary<int, (long Count, bool Member)>();
        var fenced = new Dictionary<(string Collection, string Id), string>();
        // Fences, assertions and the counts limits compare with see the state before the commit. The first failure
        // returns, and the caller does not commit the transaction.
        foreach (var checks in new[] { true, false })
        {
            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i];
                var check = operation is DocumentCommitOperation { Operation: AssertOperation or AssertCountOperation }
                    or ManagementCommitOperation { Request.Op: "fence" or "limit" };
                if (check != checks)
                {
                    continue;
                }

                var step = operation switch
                {
                    DocumentCommitOperation document => await ApplyDocumentAsync(transaction, document.Operation, cancellationToken).ConfigureAwait(false),
                    ManagementCommitOperation { Request.Op: "fence" } fence => await FenceAsync(transaction, userId, fence.Request, fenced, cancellationToken).ConfigureAwait(false),
                    ManagementCommitOperation { Request.Op: "limit" } limit => await CountBeforeAsync(transaction, userId, limit.Request, i, before, cancellationToken).ConfigureAwait(false),
                    ManagementCommitOperation management => await ApplyManagementAsync(transaction, userId, email, management.Request, created.GetValueOrDefault(i), deleted, named, cancellationToken)
                        .ConfigureAwait(false),
                    _ => throw new ArgumentOutOfRangeException(nameof(operations)),
                };
                switch (step)
                {
                    case OperationFailed failed:
                        return new CommitRejected(failed.Error, i);
                    case CommitResultBody result:
                        results[i] = result;
                        break;
                }

                if (operation is ManagementCommitOperation { Request.Organization: { } organizationId })
                {
                    named.Add(organizationId);
                }
            }
        }

        // A limit needs membership before or after the commit: an operation may end it (leave) or start it (accept).
        foreach (var (i, (count, member)) in before)
        {
            var limit = ((ManagementCommitOperation)operations[i]).Request;
            if (!member && await GetMemberAsync(transaction, limit.Organization!, userId, cancellationToken).ConfigureAwait(false) is null)
            {
                return new CommitRejected(new NotFound(Message(ManagementError.NotFound)), i);
            }

            var after = await CountAsync(transaction, limit, cancellationToken).ConfigureAwait(false);
            if (after > (limit.Databases ?? limit.Members ?? limit.ActiveMembers) && after > count)
            {
                return new CommitRejected(new Conflict("the commit would exceed the limit", "limit_exceeded"), i);
            }

            results[i] = Counted(after);
        }

        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var organizationId in named)
        {
            if (await GetMemberAsync(transaction, organizationId, userId, cancellationToken).ConfigureAwait(false) is not null)
            {
                versions[organizationId] = await VersionAsync(transaction, OrganizationVersions, organizationId, cancellationToken).ConfigureAwait(false);
            }
        }

        var account = await VersionAsync(transaction, AccountVersions, userId, cancellationToken).ConfigureAwait(false);
        var body = await RecordCommitAsync(transaction, userId, commitId, requestHash, offset =>
            JsonSerializer.SerializeToUtf8Bytes(
                new CommitBody(commitId, offset.ToString(CultureInfo.InvariantCulture), results, new VersionsBody(account, versions)),
                TerraBaseJsonContext.Default.CommitBody),
            cancellationToken).ConfigureAwait(false);
        return new CommitOutcome(body, Replayed: false);
    }

    // Document operations keep the Documents API's failures, with their codes, as failures of the commit.
    private async ValueTask<CommitStep> ApplyDocumentAsync(IStorageTransaction transaction, DocumentOperation operation, CancellationToken cancellationToken)
    {
        var stored = operation with { Collection = DashboardSpace + operation.Collection };
        return await documents.Value.ApplyAsync(transaction, stored, cancellationToken).ConfigureAwait(false) switch
        {
            DocumentRejected rejected => new OperationFailed(rejected.Error),
            OperationResult { Version: not null, ETag: { } etag } result => new CommitResultBody(result.Id, etag),
            OperationResult { Count: { } count } => Counted(count),
            OperationResult => new CommitResultBody(Asserted: true),
        };
    }

    // A compare-and-set: the version must be current, and the fence advances it. Every fence compares with the version
    // before the commit, in fenced, even after an earlier fence of the commit advanced it.
    private static async ValueTask<CommitStep> FenceAsync(
        IStorageTransaction transaction, string userId, ManagementOpRequest fence, Dictionary<(string Collection, string Id), string> fenced,
        CancellationToken cancellationToken)
    {
        var (collection, id) = fence.Account == true ? (AccountVersions, userId) : (OrganizationVersions, fence.Organization!);
        if (fence.Account != true
            && (await AuthorizeAsync(transaction, userId, id, OrganizationRole.Viewer, cancellationToken).ConfigureAwait(false)).Error is var error and not ManagementError.None)
        {
            return Failed(error);
        }

        if (!fenced.TryGetValue((collection, id), out var original))
        {
            original = fenced[(collection, id)] = await VersionAsync(transaction, collection, id, cancellationToken).ConfigureAwait(false);
            await transaction.PutAsync(collection, id, VersionDocument, cancellationToken).ConfigureAwait(false);
        }

        return original == fence.Version
            ? new CommitResultBody(Asserted: true)
            : new OperationFailed(new PreconditionFailed("the version is stale"));
    }

    // Counts what a limit bounds before the commit, and whether the user was a member then; nothing is revealed
    // until the user's membership is checked after the commit.
    private async ValueTask<CommitStep> CountBeforeAsync(
        IStorageTransaction transaction, string userId, ManagementOpRequest limit, int index, Dictionary<int, (long Count, bool Member)> before,
        CancellationToken cancellationToken)
    {
        var member = await GetMemberAsync(transaction, limit.Organization!, userId, cancellationToken).ConfigureAwait(false) is not null;
        before[index] = (await CountAsync(transaction, limit, cancellationToken).ConfigureAwait(false), member);
        return new CommitResultBody(Asserted: true);
    }

    // The count a limit bounds; an organization that does not exist (yet, or any more) has none.
    private async ValueTask<long> CountAsync(IStorageTransaction transaction, ManagementOpRequest limit, CancellationToken cancellationToken)
    {
        var organizationId = limit.Organization!;
        if (await transaction.GetVersionAsync(Organizations, organizationId, cancellationToken).ConfigureAwait(false) is null)
        {
            return 0;
        }

        if (limit.Databases is not null)
        {
            long databases = 0;
            foreach (var project in await ListAllAsync(transaction, Projects(organizationId), TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false))
            {
                databases += await CountAllAsync(transaction, Databases(project.Id), cancellationToken).ConfigureAwait(false);
            }

            return databases;
        }

        var occupied = await CountAllAsync(transaction, Members(organizationId), cancellationToken).ConfigureAwait(false);
        if (limit.ActiveMembers is not null)
        {
            return occupied;
        }

        var now = time.GetUtcNow();
        foreach (var invitationId in await ListAllAsync(transaction, InvitationEmails(organizationId), Id, cancellationToken).ConfigureAwait(false))
        {
            if (await GetAsync(transaction, Invitations(organizationId), invitationId, TerraBaseJsonContext.Default.StoredInvitation, cancellationToken).ConfigureAwait(false)
                is { Invitation: { Status: InvitationStatus.Pending } invitation } && invitation.ExpiresAt > now)
            {
                occupied++;
            }
        }

        return occupied;
    }

    // ponytail: limits count by listing (organizations are bounded by their plans); keep counters if limits grow large.
    private static async ValueTask<long> CountAllAsync(IStorageTransaction transaction, string collection, CancellationToken cancellationToken) =>
        (await transaction.CountAsync(collection, filter: null, long.MaxValue - 1, cancellationToken).ConfigureAwait(false)).Count;

    private async ValueTask<CommitStep> ApplyManagementAsync(
        IStorageTransaction transaction, string userId, string? email, ManagementOpRequest op, AddedDatabase? created, List<TenantContext> deleted, List<string> named,
        CancellationToken cancellationToken)
    {
        var organization = op.Organization!;
        switch (op.Op)
        {
            case "create_organization":
                {
                    var result = await CreateOrganizationAsync(userId, op.Id ?? NewId(), op.Name!, cancellationToken).ConfigureAwait(false);
                    if (result.Value is { } createdOrganization)
                    {
                        named.Add(createdOrganization.Id);
                    }

                    return Body(result, TerraBaseJsonContext.Default.Organization);
                }

            case "rename_organization":
                return Body(await RenameOrganizationAsync(userId, organization, op.Name!, cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.Organization);
            case "set_plan":
                return Body(await SetPlanAsync(userId, organization, Plan(op.Plan!), cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.Organization);
            case "delete_organization":
                return Done(await DeleteOrganizationAsync(userId, organization, cancellationToken).ConfigureAwait(false));
            case "set_current_organization":
                return Body(await SetCurrentOrganizationAsync(userId, organization, cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.Organization);
            case "create_project":
                return Body(await CreateProjectAsync(userId, organization, op.Id ?? NewId(), op.Name!, cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.Project);
            case "rename_project":
                return Body(await RenameProjectAsync(userId, organization, op.Project!, op.Name!, cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.Project);
            case "delete_project":
                return Done(await DeleteProjectAsync(userId, organization, op.Project!, cancellationToken).ConfigureAwait(false));
            case "create_database" or "branch_database":
                {
                    var added = created!;
                    // The copy was taken before the commit: its source must still exist where the operation runs.
                    if (added.Database.BranchedFrom is { } source
                        && await transaction.GetVersionAsync(Databases(op.Project!), source, cancellationToken).ConfigureAwait(false) is not { Deleted: false })
                    {
                        return Failed(ManagementError.NotFound);
                    }

                    if (op.Id is not null
                        && await transaction.GetVersionAsync(EngineManagedBackupService.BackedUpDatabases, added.Database.Id, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        return Failed(ManagementError.IdTaken);
                    }

                    var registered = await RegisterDatabaseAsync(
                        transaction, project => MemberProjectAsync(project, userId, organization, op.Project!, cancellationToken), added.Database, cancellationToken).ConfigureAwait(false);
                    return registered == ManagementError.None
                        ? Body(ManagedDatabaseBody.From(added.Database, options.Region), TerraBaseJsonContext.Default.ManagedDatabaseBody)
                        : Failed(registered);
                }

            case "set_database_regions":
                {
                    if (DatabaseRegions.Check(options, options.Region, op.ReplicaRegions!) is { } invalid)
                    {
                        return new OperationFailed(invalid);
                    }

                    var placed = await SetDatabaseRegionsAsync(transaction, userId, organization, op.Project!, op.Database!, op.ReplicaRegions!, cancellationToken).ConfigureAwait(false);
                    return placed.Error == ManagementError.None
                        ? Body(ManagedDatabaseBody.From(placed.Value!, options.Region), TerraBaseJsonContext.Default.ManagedDatabaseBody)
                        : Failed(placed.Error);
                }

            case "delete_database":
                {
                    var unregistered = await UnregisterDatabaseAsync(transaction, userId, organization, op.Project!, op.Database!, options.Region, cancellationToken).ConfigureAwait(false);
                    if (unregistered.Value is not { } storage)
                    {
                        return Failed(unregistered.Error);
                    }

                    deleted.Add(storage);
                    return new CommitResultBody();
                }

            case "change_role":
                return Body(await ChangeRoleAsync(userId, organization, op.User!, op.Role!.Value, cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.OrganizationMember);
            case "remove_member":
                return Done(await RemoveMemberAsync(userId, organization, op.User!, cancellationToken).ConfigureAwait(false));
            case "leave":
                return Done(await LeaveAsync(userId, organization, cancellationToken).ConfigureAwait(false));
            case "invite":
                return Body(
                    await InviteAsync(userId, organization, NewId(), op.Email!, op.Role ?? OrganizationRole.Member, op.TokenHash!, secretHash: null, cancellationToken).ConfigureAwait(false),
                    TerraBaseJsonContext.Default.Invitation);
            case "resend_invitation":
                return Body(await ResendInvitationAsync(userId, organization, op.Invitation!, op.TokenHash!, cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.Invitation);
            case "revoke_invitation":
                return Done(await RevokeInvitationAsync(userId, organization, op.Invitation!, cancellationToken).ConfigureAwait(false));
            case "accept_invitation":
                {
                    // Redeeming proves the invitee by the access token's verified email, never by the request body.
                    if (email is null)
                    {
                        return new OperationFailed(new Forbidden("the access token has no verified email"));
                    }

                    var result = await AcceptInvitationByHashAsync(userId, email, op.TokenHash!, cancellationToken).ConfigureAwait(false);
                    if (result.Value is { } membership)
                    {
                        named.Add(membership.OrganizationId);
                    }

                    return Body(result, TerraBaseJsonContext.Default.Membership);
                }

            case "decline_invitation":
                return email is null
                    ? new OperationFailed(new Forbidden("the access token has no verified email"))
                    : Body(await DeclineInvitationAsync(userId, email, op.TokenHash!, cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.Invitation);
            case "start_transfer":
                return Body(await StartTransferAsync(userId, organization, op.User!, cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.OwnershipTransfer);
            case "confirm_transfer":
                return Body(await ConfirmTransferAsync(userId, organization, cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.OrganizationMember);
            case "cancel_transfer":
                return Done(await CancelTransferAsync(userId, organization, cancellationToken).ConfigureAwait(false));
            case "set_audit_retention":
                return Body(await SetAuditRetentionAsync(userId, organization, op.Days!.Value, cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.AuditRetention);
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op.Op, "unknown operation");
        }
    }

    // The plan of a set_plan operation, validated by the endpoint.
    private static PlanTier Plan(string plan) => plan switch
    {
        "pro" => PlanTier.Pro,
        "enterprise" => PlanTier.Enterprise,
        _ => PlanTier.Hobby,
    };

    // Creates the files of every created or branched database under its cleanup intent, after checking the user may
    // create in the project. An organization or project the commit itself creates (and that does not exist yet) is
    // checked only inside the transaction, where creating it makes the user its owner.
    private async ValueTask<PreparedDatabases> PrepareDatabasesAsync(
        string userId, IReadOnlyList<CommitOperation> operations, CancellationToken cancellationToken)
    {
        var prepared = new Dictionary<int, AddedDatabase>();
        try
        {
            for (var i = 0; i < operations.Count; i++)
            {
                if (operations[i] is not ManagementCommitOperation { Request: { Op: "create_database" or "branch_database" } op })
                {
                    continue;
                }

                var earlier = operations.Take(i).OfType<ManagementCommitOperation>().Select(operation => operation.Request).ToList();
                var createsOrganization = earlier.Any(create => create is { Op: "create_organization" } && create.Id == op.Organization);
                var createsProject = earlier.Any(create => create is { Op: "create_project" } && create.Id == op.Project);
                // Earlier operations that can make the user a member, or raise a viewer, before this one runs.
                var mayGrant = earlier.Any(grant => grant is { Op: "accept_invitation" } || (grant is { Op: "confirm_transfer" } && grant.Organization == op.Organization));
                var target = await ReadAsync<ManagementResult<(TenantContext Tenant, ManagedDatabase? Source)>>(async transaction =>
                {
                    // A chosen id registers a database another region holds under the same ids (docs/cross-region.md).
                    if (op.Id is { } id && (prepared.Values.Any(added => added.Database.Id == id)
                        || await transaction.GetVersionAsync(EngineManagedBackupService.BackedUpDatabases, id, cancellationToken).ConfigureAwait(false) is not null))
                    {
                        return ManagementError.IdTaken;
                    }

                    var organization = await GetAsync(transaction, Organizations, op.Organization!, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);
                    if (organization is null && createsOrganization)
                    {
                        // A new organization has no database to branch.
                        return op.Op == "create_database" ? (new TenantContext(op.Organization!, PlanTier.Hobby, op.Project), null) : ManagementError.NotFound;
                    }

                    // The transaction authorizes again, after the earlier operations; this only keeps files away from users
                    // who cannot become writers in this commit.
                    var writer = await AuthorizeAsync(transaction, userId, op.Organization!, OrganizationRole.Member, cancellationToken).ConfigureAwait(false);
                    if (writer.Error != ManagementError.None && !(mayGrant && organization is not null))
                    {
                        return writer.Error;
                    }

                    if (createsProject && await transaction.GetVersionAsync(ProjectOrganizations, op.Project!, cancellationToken).ConfigureAwait(false) is null)
                    {
                        return op.Op == "create_database" ? (new TenantContext(organization!.Id, organization.Plan, op.Project), null) : ManagementError.NotFound;
                    }

                    if (await OrganizationProjectAsync(transaction, op.Organization!, op.Project!, cancellationToken).ConfigureAwait(false) is not { } tenant)
                    {
                        return ManagementError.NotFound;
                    }

                    if (op.Op == "create_database")
                    {
                        return (tenant, null);
                    }

                    // A branch copies the home region's file and lives there only (docs/cross-region.md).
                    return await GetAsync(transaction, Databases(op.Project!), op.Database!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not { } source
                        ? ManagementError.NotFound
                        : source.Home(options.Region) != options.Region ? ManagementError.WrongRegion
                        : (tenant, source);
                }, cancellationToken).ConfigureAwait(false);
                if (target.Error != ManagementError.None)
                {
                    return await AbandonAsync(prepared, new CommitRejected(Failure(target.Error), i)).ConfigureAwait(false);
                }

                var (project, source) = target.Value;
                // A database is created in its home region, the one the commit is sent to (docs/cross-region.md).
                var replicas = op.ReplicaRegions ?? [];
                if ((op.HomeRegion ?? options.Region) != options.Region)
                {
                    return await AbandonAsync(prepared, new CommitRejected(Failure(ManagementError.WrongRegion), i)).ConfigureAwait(false);
                }

                if (DatabaseRegions.Check(options, options.Region, replicas) is { } invalid)
                {
                    return await AbandonAsync(prepared, new CommitRejected(invalid, i)).ConfigureAwait(false);
                }

                var database = new ManagedDatabase(
                    op.Id ?? NewId(), op.Project!, op.Name!, source?.Id, time.GetUtcNow(), options.Region, replicas.Count == 0 ? null : [.. replicas], 1);
                var storage = project with { DatabaseId = database.Id };
                if (!await PrepareDatabaseAsync(storage, source is null ? null : project with { DatabaseId = source.Id }, cancellationToken).ConfigureAwait(false))
                {
                    return await AbandonAsync(prepared, new CommitRejected(Failure(ManagementError.NotFound), i)).ConfigureAwait(false);
                }

                prepared[i] = new AddedDatabase(database, storage);
            }
        }
        catch
        {
            foreach (var database in prepared.Values)
            {
                await AbandonDatabaseAsync(database.Tenant).ConfigureAwait(false);
            }

            throw;
        }

        return prepared;
    }

    // Removes the files of databases prepared for a commit that stops before its transaction.
    private async ValueTask<CommitRejected> AbandonAsync(Dictionary<int, AddedDatabase> prepared, CommitRejected rejected)
    {
        foreach (var database in prepared.Values)
        {
            await AbandonDatabaseAsync(database.Tenant).ConfigureAwait(false);
        }

        return rejected;
    }

    // The user's record of a commit within the retention.
    private async ValueTask<CommitRecord?> RecordedAsync(IStorageTransaction transaction, string userId, string commitId, CancellationToken cancellationToken) =>
        await GetAsync(transaction, Commits(userId), commitId, TerraBaseJsonContext.Default.CommitRecord, cancellationToken).ConfigureAwait(false) is { } recorded
        && recorded.CommittedAt > time.GetUtcNow() - CommitRetention
            ? recorded
            : null;

    private static CommitResult Replay(CommitRecord recorded, string requestHash) =>
        Matches(recorded.RequestHash, requestHash)
            ? new CommitOutcome(Encoding.UTF8.GetBytes(recorded.Body), Replayed: true)
            : new CommitRejected(new Conflict("the commit id was used for a different request", "commit_id_reused"), Op: null);

    // Records the response in the commit's transaction and purges a batch of expired records, oldest first. The
    // expiry entry is written first: its write allocates this transaction's offset even when no operation wrote,
    // so the response reports the offset the commit actually has.
    private async ValueTask<byte[]> RecordCommitAsync(
        IStorageTransaction transaction, string userId, string commitId, string requestHash, Func<long, byte[]> response, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var offset = await transaction.PutAsync(CommitExpiries, TicksKey(now) + NewId(),
            JsonSerializer.SerializeToUtf8Bytes(new CommitExpiry(userId, commitId), TerraBaseJsonContext.Default.CommitExpiry), cancellationToken).ConfigureAwait(false);
        var body = response(offset);
        await PutAsync(transaction, Commits(userId), commitId, new CommitRecord(requestHash, now, Encoding.UTF8.GetString(body)), TerraBaseJsonContext.Default.CommitRecord, cancellationToken)
            .ConfigureAwait(false);

        var cutoff = TicksKey(now - CommitRetention);
        var oldest = await transaction.ListAsync(CommitExpiries, afterId: null, CommitPurgeBatch, MaxPageBytes, cancellationToken).ConfigureAwait(false);
        foreach (var entry in oldest.Documents.TakeWhile(entry => string.CompareOrdinal(entry.Id, cutoff) < 0))
        {
            var expired = JsonSerializer.Deserialize(entry.Json.Span, TerraBaseJsonContext.Default.CommitExpiry)!;
            // A record rewritten after it expired has a newer entry of its own.
            if (await GetAsync(transaction, Commits(expired.UserId), expired.CommitId, TerraBaseJsonContext.Default.CommitRecord, cancellationToken).ConfigureAwait(false) is { } record
                && record.CommittedAt <= now - CommitRetention)
            {
                await transaction.DeleteAsync(Commits(expired.UserId), expired.CommitId, cancellationToken).ConfigureAwait(false);
            }

            await transaction.DeleteAsync(CommitExpiries, entry.Id, cancellationToken).ConfigureAwait(false);
        }

        return body;
    }

    private static CommitResultBody Counted(long count) => new(Asserted: true, Count: count.ToString(CultureInfo.InvariantCulture));

    private static CommitStep Body<T>(T value, JsonTypeInfo<T> type) => new CommitResultBody(Value: JsonSerializer.SerializeToElement(value, type));

    private static CommitStep Body<T>(ManagementResult<T> result, JsonTypeInfo<T> type) =>
        result.Error == ManagementError.None ? Body(result.Value!, type) : Failed(result.Error);

    private static CommitStep Done(ManagementError error) => error == ManagementError.None ? new CommitResultBody() : Failed(error);

    private static CommitStep Failed(ManagementError error) => new OperationFailed(Failure(error));

    /// <summary>A Management failure as the failure kind it stands for, with its code.</summary>
    internal static Failure Failure(ManagementError error) => error switch
    {
        ManagementError.NotFound => new NotFound(Message(error)),
        ManagementError.Forbidden => new Forbidden(Message(error)),
        _ => new Conflict(Message(error), Code(error)),
    };

    private static string Code(ManagementError error) => error switch
    {
        ManagementError.NotFound => "not_found",
        ManagementError.NameTaken => "name_taken",
        ManagementError.NotEmpty => "not_empty",
        ManagementError.Forbidden => "forbidden",
        ManagementError.LastOwner => "last_owner",
        ManagementError.AlreadyMember => "already_member",
        ManagementError.InvitationPending => "invitation_pending",
        ManagementError.AlreadyOwner => "already_owner",
        ManagementError.Expired => "expired",
        ManagementError.IdTaken => "id_taken",
        ManagementError.InvitationClosed => "invitation_closed",
        ManagementError.TokenTaken => "token_taken",
        ManagementError.WrongRegion => "wrong_region",
        ManagementError.Replicated => "database_replicated",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    private static string Message(ManagementError error) => error switch
    {
        ManagementError.NotFound => "the resource does not exist",
        ManagementError.NameTaken => "name is already taken",
        ManagementError.NotEmpty => "delete what it contains first",
        ManagementError.Forbidden => "your role in the organization does not allow this",
        ManagementError.LastOwner => "the organization must keep an owner",
        ManagementError.AlreadyMember => "the user is already a member",
        ManagementError.InvitationPending => "an invitation for this email is pending",
        ManagementError.AlreadyOwner => "the member is already an owner",
        ManagementError.Expired => "the invitation expired",
        ManagementError.IdTaken => "the id is already taken",
        ManagementError.InvitationClosed => "the invitation is no longer pending",
        ManagementError.TokenTaken => "another invitation has this token",
        ManagementError.WrongRegion => "the database is managed in another region: send it to its home region",
        ManagementError.Replicated => "remove the database's replica regions first",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    private static string Commits(string userId) => $"users/{userId}/commits";
}
