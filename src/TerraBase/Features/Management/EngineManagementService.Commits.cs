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
using TerraBase.Features.Qos;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed record CommitRecord(string RequestHash, DateTimeOffset CommittedAt, string Body);

internal sealed record CommitExpiry(string UserId, string CommitId);

internal sealed record OperationFailed(Failure Error);

internal readonly union CommitStep(CommitResultBody, OperationFailed);

internal sealed partial class EngineManagementService
{
    private const string OrganizationVersions = "organization_versions";
    private const string AccountVersions = "account_versions";
    private const string CommitExpiries = "commit_expiry";

    private const string DashboardSpace = "dashboard/";

    private const int CommitPurgeBatch = 100;

    private static readonly TimeSpan CommitRetention = TimeSpan.FromDays(7);
    private static readonly byte[] VersionDocument = "{}"u8.ToArray();

    private readonly union PreparedDatabases(Dictionary<int, AddedDatabase>, CommitRejected);

    private readonly union PreparedDatabaseTarget((TenantContext Tenant, ManagedDatabase? Source), AddedDatabase, NotFound, Forbidden, Conflict, Unavailable);

    private readonly union RecordedCommit(CommitRecord, NotFound, Unavailable);

    private static readonly AsyncLocal<IStorageTransaction?> CommitTransaction = new();

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
        var replay = await ReadAsync<RecordedCommit>(async transaction =>
            await RecordedAsync(transaction, userId, commitId, cancellationToken).ConfigureAwait(false) switch
            {
                CommitRecord recorded => recorded,
                NotFound error => error,
            }, static error => error, cancellationToken).ConfigureAwait(false);
        if (replay is not NotFound)
        {
            return replay switch
            {
                CommitRecord recorded => Replay(recorded, requestHash),
                Unavailable error => new CommitRejected(error, Op: null),
                NotFound => throw new UnreachableException(),
            };
        }

        string[] organizations = [.. operations.OfType<ManagementCommitOperation>()
            .Where(operation => operation.Request.Op == "branch_database")
            .Select(operation => operation.Request.Organization!).Distinct(StringComparer.Ordinal)];
        var entered = await quota.Value.EnterOrganizationsAsync(organizations, cancellationToken).ConfigureAwait(false);
        if (entered is not StorageQuotaGroupLease granted)
        {
            return entered switch
            {
                Unavailable error => new CommitRejected(error, Op: null),
                StorageQuotaGroupLease => throw new UnreachableException(),
            };
        }

        using var admission = granted;
        if (organizations.Length > 0)
        {
            var afterAdmission = await ReadAsync<RecordedCommit>(async transaction =>
                await RecordedAsync(transaction, userId, commitId, cancellationToken).ConfigureAwait(false) switch
                {
                    CommitRecord recorded => recorded,
                    NotFound error => error,
                }, static error => error, cancellationToken).ConfigureAwait(false);
            if (afterAdmission is not NotFound)
            {
                return afterAdmission switch
                {
                    CommitRecord recorded => Replay(recorded, requestHash),
                    Unavailable error => new CommitRejected(error, Op: null),
                    NotFound => throw new UnreachableException(),
                };
            }
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
            if (await CheckPreparedQuotaAsync(userId, operations, created, admission, cancellationToken).ConfigureAwait(false) is { } exceeded)
            {
                return await AbandonAsync(created, exceeded).ConfigureAwait(false);
            }

            await AcquireCommitUnitsAsync(created.Values, cancellationToken).ConfigureAwait(false);

            result = await WriteCatalogAsync(
                transaction => ApplyCommitAsync(transaction, userId, email, commitId, requestHash, operations, created, deleted, cancellationToken), cancellationToken,
                keep: applied => applied is CommitOutcome { Replayed: false })
                .ConfigureAwait(false);
        }
        catch (QueryCapacityException) when (created.Values.Any(database => database.Database.BranchedFrom is not null))
        {
            return await AbandonAsync(created, new CommitRejected(StorageQuota.AccountingUnavailable,
                created.First(pair => pair.Value.Database.BranchedFrom is not null).Key)).ConfigureAwait(false);
        }
        catch
        {
            foreach (var database in created.Values.Where(database => !database.Existing))
            {
                await AbandonDatabaseAsync(database.Tenant).ConfigureAwait(false);
            }

            throw;
        }

        var committed = result is CommitOutcome { Replayed: false };
        foreach (var database in created.Values.Where(database => !database.Existing))
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

        if (committed)
        {
            foreach (var storage in deleted)
            {
                _ = await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (result is CommitOutcome)
        {
            foreach (var (index, database) in created.Where(pair => pair.Value.Existing))
            {
                var initialized = await InitializeLocalAsync(database.Tenant, cancellationToken).ConfigureAwait(false);
                if (initialized is not Done)
                {
                    return initialized switch
                    {
                        NotFound error => new CommitRejected(error, index),
                        Unavailable error => new CommitRejected(error, index),
                        Done => throw new UnreachableException(),
                    };
                }
            }
        }

        return result;
    }

    private async ValueTask<CommitRejected?> CheckPreparedQuotaAsync(
        string userId, IReadOnlyList<CommitOperation> operations, IReadOnlyDictionary<int, AddedDatabase> created,
        StorageQuotaGroupLease admission, CancellationToken cancellationToken)
    {
        var growth = new Dictionary<string, (long Bytes, int Op)>(StringComparer.Ordinal);
        foreach (var (i, added) in created)
        {
            if (added.Database.BranchedFrom is null || !admission.RequiresAccounting(added.Tenant.OrganizationId))
            {
                continue;
            }

            var organization = added.Tenant.OrganizationId;
            var bytes = await quota.Value.ReadBytesAsync(added.Tenant, cancellationToken).ConfigureAwait(false);
            growth[organization] = (checked(growth.GetValueOrDefault(organization).Bytes + bytes), i);
        }

        var removed = new HashSet<(string Organization, string Project, string Database)>();
        foreach (var operation in operations.OfType<ManagementCommitOperation>())
        {
            var request = operation.Request;
            if (request.Op != "delete_database" || !growth.ContainsKey(request.Organization!)
                || !removed.Add((request.Organization!, request.Project!, request.Database!)))
            {
                continue;
            }

            var found = await GetDatabaseAsync(userId, request.Organization!, request.Project!, request.Database!, cancellationToken).ConfigureAwait(false);
            if (found is Unavailable refused)
            {
                return new CommitRejected(refused, growth[request.Organization!].Op);
            }

            if (found is ManagedDatabase database && CheckDeletionPlacement(database, options.Region) is Done)
            {
                var bytes = await quota.Value.ReadBytesAsync(new TenantContext(request.Organization!, PlanTier.Hobby, request.Project, request.Database), cancellationToken)
                    .ConfigureAwait(false);
                var (currentBytes, currentOp) = growth[request.Organization!];
                growth[request.Organization!] = (checked(currentBytes - bytes), currentOp);
            }
        }

        foreach (var (organization, (bytes, i)) in growth)
        {
            var check = await admission.CheckAsync(organization, bytes, cancellationToken).ConfigureAwait(false);
            if (check is not Done)
            {
                return check switch
                {
                    QuotaExceeded error => new CommitRejected(error, i),
                    Unavailable error => new CommitRejected(error, i),
                    Done => throw new UnreachableException(),
                };
            }
        }

        return null;
    }

    public ValueTask<GetCommitResult> GetCommitAsync(string userId, string commitId, CancellationToken cancellationToken) =>
        ReadAsync<GetCommitResult>(async transaction =>
            await RecordedAsync(transaction, userId, commitId, cancellationToken).ConfigureAwait(false) switch
            {
                CommitRecord recorded => Encoding.UTF8.GetBytes(recorded.Body),
                NotFound error => error,
            }, static error => error, cancellationToken);

    public ValueTask<GetVersionsResult> GetVersionsAsync(string userId, IReadOnlyList<string> organizations, CancellationToken cancellationToken) =>
        ReadAsync<GetVersionsResult>(async transaction =>
        {
            ArgumentNullException.ThrowIfNull(organizations);
            var versions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var organizationId in organizations)
            {
                if (await GetMemberAsync(transaction, organizationId, userId, cancellationToken).ConfigureAwait(false) is null)
                {
                    return ManagementErrors.NotFound;
                }

                versions[organizationId] = await VersionAsync(transaction, OrganizationVersions, organizationId, cancellationToken).ConfigureAwait(false);
            }

            return new VersionsBody(await VersionAsync(transaction, AccountVersions, userId, cancellationToken).ConfigureAwait(false), versions);
        }, static error => error, cancellationToken);

    public ValueTask<GetDashboardDocumentResult> GetDocumentAsync(string collection, string id, CancellationToken cancellationToken) =>
        ReadAsync<GetDashboardDocumentResult>(async transaction =>
            await transaction.GetAsync(DashboardSpace + collection, id, cancellationToken).ConfigureAwait(false) is { } document
                ? document
                : new NotFound("the document does not exist"),
            static error => error, cancellationToken);

    public async ValueTask<QueryDashboardDocumentsResult> QueryDocumentsAsync(DocumentQuery query, long? countLimit, CancellationToken cancellationToken)
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
            return await ReadAsync<QueryDashboardDocumentsResult>(async transaction =>
                await documents.Value.QueryAsync(transaction, query with { Collection = DashboardSpace + query.Collection }, countLimit, cancellationToken, observation).ConfigureAwait(false) switch
                {
                    QueryOutcome outcome => outcome,
                    Forbidden error => new NotFound(error.Message, error.Code),
                    NotFound error => error,
                    Validation error => error,
                }, static error => error, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            shapes.Value.Record(null, query.Collection, shape ?? string.Empty, time.GetElapsedTime(started), observation.RowsScanned);
        }
    }

    internal static string DocumentVersion(StoredDocument document, string collection) =>
        ETags.Version(document.Scope, DashboardSpace + collection, document.Id, document.Version, document.Json, document.Deleted);

    internal static async ValueTask TouchOrganizationAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken) =>
        await transaction.PutAsync(OrganizationVersions, organizationId, VersionDocument, cancellationToken).ConfigureAwait(false);

    private static async ValueTask TouchAccountAsync(IStorageTransaction transaction, string userId, CancellationToken cancellationToken) =>
        await transaction.PutAsync(AccountVersions, userId, VersionDocument, cancellationToken).ConfigureAwait(false);

    private static async ValueTask<string> VersionAsync(IStorageTransaction transaction, string collection, string id, CancellationToken cancellationToken) =>
        ((await transaction.GetVersionAsync(collection, id, cancellationToken).ConfigureAwait(false))?.Version ?? 0).ToString(CultureInfo.InvariantCulture);

    private async ValueTask<CommitResult> ApplyCommitAsync(
        IStorageTransaction transaction, string userId, string? email, string commitId, string requestHash, IReadOnlyList<CommitOperation> operations,
        IReadOnlyDictionary<int, AddedDatabase> created, List<TenantContext> deleted, CancellationToken cancellationToken)
    {
        if (await CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false) is Unavailable refused)
        {
            return new CommitRejected(refused, Op: null);
        }

        var existing = await RecordedAsync(transaction, userId, commitId, cancellationToken).ConfigureAwait(false);
        if (existing is not NotFound)
        {
            return existing switch
            {
                CommitRecord recorded => Replay(recorded, requestHash),
                NotFound => throw new UnreachableException(),
            };
        }

        CommitTransaction.Value = transaction;
        var results = new CommitResultBody[operations.Count];
        var named = new List<string>();
        var before = new Dictionary<int, (long Count, bool Member)>();
        var fenced = new Dictionary<(string Collection, string Id), string>();
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
                if (step is not CommitResultBody result)
                {
                    return step switch
                    {
                        OperationFailed failed => new CommitRejected(failed.Error, i),
                        CommitResultBody => throw new UnreachableException(),
                    };
                }

                results[i] = result;

                if (operation is ManagementCommitOperation { Request.Organization: { } organizationId })
                {
                    named.Add(organizationId);
                }
            }
        }

        foreach (var (i, (count, member)) in before)
        {
            var limit = ((ManagementCommitOperation)operations[i]).Request;
            if (!member && await GetMemberAsync(transaction, limit.Organization!, userId, cancellationToken).ConfigureAwait(false) is null)
            {
                return new CommitRejected(ManagementErrors.NotFound, i);
            }

            var after = await CountAsync(transaction, limit, cancellationToken).ConfigureAwait(false);
            if (after > (limit.Databases ?? limit.Members ?? limit.ActiveMembers) && after > count)
            {
                return new CommitRejected(new Conflict("the commit would exceed the limit", "limit_exceeded"), i);
            }

            results[i] = Counted(after);
        }

        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var organizationId in named.Distinct(StringComparer.Ordinal))
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

    private static async ValueTask<CommitStep> FenceAsync(
        IStorageTransaction transaction, string userId, ManagementOpRequest fence, Dictionary<(string Collection, string Id), string> fenced,
        CancellationToken cancellationToken)
    {
        var (collection, id) = fence.Account == true ? (AccountVersions, userId) : (OrganizationVersions, fence.Organization!);
        if (fence.Account != true)
        {
            var actor = await AuthorizeAsync(transaction, userId, id, OrganizationRole.Viewer, cancellationToken).ConfigureAwait(false);
            if (actor is not OrganizationMember)
            {
                return actor switch
                {
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    OrganizationMember => throw new UnreachableException(),
                };
            }
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

    private async ValueTask<CommitStep> CountBeforeAsync(
        IStorageTransaction transaction, string userId, ManagementOpRequest limit, int index, Dictionary<int, (long Count, bool Member)> before,
        CancellationToken cancellationToken)
    {
        var member = await GetMemberAsync(transaction, limit.Organization!, userId, cancellationToken).ConfigureAwait(false) is not null;
        before[index] = (await CountAsync(transaction, limit, cancellationToken).ConfigureAwait(false), member);
        return new CommitResultBody(Asserted: true);
    }

    private async ValueTask<long> CountAsync(IStorageTransaction transaction, ManagementOpRequest limit, CancellationToken cancellationToken)
    {
        var organizationId = limit.Organization!;
        if (await transaction.GetVersionAsync(Organizations, organizationId, cancellationToken).ConfigureAwait(false) is not { Deleted: false })
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
                    return await CreateOrganizationAsync(userId, op.Id ?? NewId(), op.Name!, cancellationToken).ConfigureAwait(false) switch
                    {
                        Organization createdOrganization => NamedOrganization(createdOrganization, named),
                        Conflict error => new OperationFailed(error),
                        Unavailable error => new OperationFailed(error),
                    };
                }

            case "rename_organization":
                return await RenameOrganizationAsync(userId, organization, op.Name!, cancellationToken).ConfigureAwait(false) switch
                {
                    Organization value => Body(value, TerraBaseJsonContext.Default.Organization),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "set_plan":
                return await SetPlanAsync(userId, organization, Plan(op.Plan!), cancellationToken).ConfigureAwait(false) switch
                {
                    Organization value => Body(value, TerraBaseJsonContext.Default.Organization),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "delete_organization":
                return await DeleteOrganizationAsync(userId, organization, cancellationToken).ConfigureAwait(false) switch
                {
                    Done => new CommitResultBody(),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "set_current_organization":
                return await SetCurrentOrganizationAsync(userId, organization, cancellationToken).ConfigureAwait(false) switch
                {
                    Organization value => Body(value, TerraBaseJsonContext.Default.Organization),
                    NotFound error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "create_project":
                return await CreateProjectAsync(userId, organization, op.Id ?? NewId(), op.Name!, cancellationToken).ConfigureAwait(false) switch
                {
                    Project value => Body(value, TerraBaseJsonContext.Default.Project),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "rename_project":
                return await RenameProjectAsync(userId, organization, op.Project!, op.Name!, cancellationToken).ConfigureAwait(false) switch
                {
                    Project value => Body(value, TerraBaseJsonContext.Default.Project),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "delete_project":
                return await DeleteProjectAsync(userId, organization, op.Project!, cancellationToken).ConfigureAwait(false) switch
                {
                    Done => new CommitResultBody(),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "create_database" or "branch_database":
                {
                    var added = created!;
                    if (added.Existing)
                    {
                        var access = await MemberProjectAsync(transaction, userId, organization, op.Project!, cancellationToken).ConfigureAwait(false);
                        return access switch
                        {
                            TenantContext when await GetAsync(transaction, Databases(op.Project!), added.Database.Id,
                                TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is { } known
                                && SameDatabaseCreation(known, op) => Body(ManagedDatabaseBody.From(known, options.Region), TerraBaseJsonContext.Default.ManagedDatabaseBody),
                            TenantContext => new OperationFailed(ManagementErrors.IdTaken),
                            NotFound error => new OperationFailed(error),
                            Forbidden error => new OperationFailed(error),
                        };
                    }

                    if (added.Database.BranchedFrom is { } source
                        && await GetAsync(transaction, Databases(op.Project!), source, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is null)
                    {
                        return new OperationFailed(ManagementErrors.NotFound);
                    }

                    if (op.Id is not null
                        && await transaction.GetVersionAsync(EngineManagedBackupService.BackedUpDatabases, added.Database.Id, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        return new OperationFailed(ManagementErrors.IdTaken);
                    }

                    var registered = await RegisterDatabaseAsync(
                        transaction, project => MemberProjectAsync(project, userId, organization, op.Project!, cancellationToken), added.Database,
                        new AllowanceCheck(databaseAllowance.Limit, options.Region, _serving, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    return registered switch
                    {
                        TenantContext => Body(ManagedDatabaseBody.From(added.Database, options.Region), TerraBaseJsonContext.Default.ManagedDatabaseBody),
                        NotFound error => new OperationFailed(error),
                        Forbidden error => new OperationFailed(error),
                        Conflict error => new OperationFailed(error),
                    };
                }

            case "set_database_regions":
                {
                    if (DatabaseRegions.Check(options.Region, op.ReplicaRegions!, created: false) is Validation invalid)
                    {
                        return new OperationFailed(invalid);
                    }

                    return await SetDatabaseRegionsAsync(transaction, userId, organization, op.Project!, op.Database!, op.ReplicaRegions!, cancellationToken).ConfigureAwait(false) switch
                    {
                        ManagedDatabase value => Body(ManagedDatabaseBody.From(value, options.Region), TerraBaseJsonContext.Default.ManagedDatabaseBody),
                        NotFound error => new OperationFailed(error),
                        Forbidden error => new OperationFailed(error),
                        Conflict error => new OperationFailed(error),
                        Unavailable error => new OperationFailed(error),
                    };
                }

            case "delete_database":
                {
                    return await UnregisterDatabaseAsync(transaction, userId, organization, op.Project!, op.Database!, options.Region, cancellationToken).ConfigureAwait(false) switch
                    {
                        TenantContext storage => DeletedDatabase(storage, deleted),
                        NotFound error => new OperationFailed(error),
                        Forbidden error => new OperationFailed(error),
                        Conflict error => new OperationFailed(error),
                    };
                }

            case "change_role":
                return await ChangeRoleAsync(userId, organization, op.User!, op.Role!.Value, cancellationToken).ConfigureAwait(false) switch
                {
                    OrganizationMember value => Body(value, TerraBaseJsonContext.Default.OrganizationMember),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "remove_member":
                return await RemoveMemberAsync(userId, organization, op.User!, cancellationToken).ConfigureAwait(false) switch
                {
                    Done => new CommitResultBody(),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "leave":
                return await LeaveAsync(userId, organization, cancellationToken).ConfigureAwait(false) switch
                {
                    Done => new CommitResultBody(),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "invite":
                return await InviteAsync(userId, organization, NewId(), op.Email!, op.Role ?? OrganizationRole.Member, op.TokenHash!, secretHash: null, cancellationToken).ConfigureAwait(false) switch
                {
                    Invitation value => Body(value, TerraBaseJsonContext.Default.Invitation),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "resend_invitation":
                return await ResendInvitationAsync(userId, organization, op.Invitation!, op.TokenHash!, cancellationToken).ConfigureAwait(false) switch
                {
                    Invitation value => Body(value, TerraBaseJsonContext.Default.Invitation),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "revoke_invitation":
                return await RevokeInvitationAsync(userId, organization, op.Invitation!, cancellationToken).ConfigureAwait(false) switch
                {
                    Done => new CommitResultBody(),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "accept_invitation":
                {
                    if (email is null)
                    {
                        return new OperationFailed(new Forbidden("the access token has no verified email"));
                    }

                    return await AcceptInvitationByHashAsync(userId, email, op.TokenHash!, cancellationToken).ConfigureAwait(false) switch
                    {
                        Membership membership => NamedMembership(membership, named),
                        NotFound error => new OperationFailed(error),
                        Forbidden error => new OperationFailed(error),
                        Conflict error => new OperationFailed(error),
                        Unavailable error => new OperationFailed(error),
                    };
                }

            case "decline_invitation":
                return email is null
                    ? new OperationFailed(new Forbidden("the access token has no verified email"))
                    : await DeclineInvitationAsync(userId, email, op.TokenHash!, cancellationToken).ConfigureAwait(false) switch
                    {
                        Invitation value => Body(value, TerraBaseJsonContext.Default.Invitation),
                        NotFound error => new OperationFailed(error),
                        Forbidden error => new OperationFailed(error),
                        Conflict error => new OperationFailed(error),
                        Unavailable error => new OperationFailed(error),
                    };
            case "start_transfer":
                return await StartTransferAsync(userId, organization, op.User!, cancellationToken).ConfigureAwait(false) switch
                {
                    OwnershipTransfer value => Body(value, TerraBaseJsonContext.Default.OwnershipTransfer),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Conflict error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "confirm_transfer":
                return await ConfirmTransferAsync(userId, organization, cancellationToken).ConfigureAwait(false) switch
                {
                    OrganizationMember value => Body(value, TerraBaseJsonContext.Default.OrganizationMember),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "cancel_transfer":
                return await CancelTransferAsync(userId, organization, cancellationToken).ConfigureAwait(false) switch
                {
                    Done => new CommitResultBody(),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            case "set_audit_retention":
                return await SetAuditRetentionAsync(userId, organization, op.Days!.Value, cancellationToken).ConfigureAwait(false) switch
                {
                    AuditRetention value => Body(value, TerraBaseJsonContext.Default.AuditRetention),
                    NotFound error => new OperationFailed(error),
                    Forbidden error => new OperationFailed(error),
                    Unavailable error => new OperationFailed(error),
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op.Op, "unknown operation");
        }
    }

    private static CommitStep NamedOrganization(Organization organization, List<string> named)
    {
        named.Add(organization.Id);
        return Body(organization, TerraBaseJsonContext.Default.Organization);
    }

    private static CommitStep NamedMembership(Membership membership, List<string> named)
    {
        named.Add(membership.OrganizationId);
        return Body(membership, TerraBaseJsonContext.Default.Membership);
    }

    private static CommitStep DeletedDatabase(TenantContext storage, List<TenantContext> deleted)
    {
        deleted.Add(storage);
        return new CommitResultBody();
    }

    private static PlanTier Plan(string plan) => plan switch
    {
        "starter" => PlanTier.Starter,
        "pro" => PlanTier.Pro,
        "team" => PlanTier.Team,
        "enterprise" => PlanTier.Enterprise,
        _ => PlanTier.Hobby,
    };

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
                var mayGrant = earlier.Any(grant => grant is { Op: "accept_invitation" } || (grant is { Op: "confirm_transfer" } && grant.Organization == op.Organization));
                var target = await ReadAsync<PreparedDatabaseTarget>(async transaction =>
                {
                    if (op.Id is { } id && prepared.Values.Any(added => added.Database.Id == id))
                    {
                        return ManagementErrors.IdTaken;
                    }

                    if (op.Id is { } purgeId && await DatabasePurge.IsDeletedAsync(transaction, purgeId, cancellationToken).ConfigureAwait(false))
                    {
                        return ManagementErrors.IdTaken;
                    }

                    ManagedDatabase? registered = null;
                    if (op.Id is { } existingId
                        && await transaction.GetVersionAsync(EngineManagedBackupService.BackedUpDatabases, existingId, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        var owner = await GetAsync(transaction, EngineManagedBackupService.BackedUpDatabases, existingId,
                            TerraBaseJsonContext.Default.DatabaseOwner, cancellationToken).ConfigureAwait(false);
                        registered = await GetAsync(transaction, Databases(op.Project!), existingId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false);
                        if (owner is null || owner.OrganizationId != op.Organization || owner.ProjectId != op.Project || registered is null || !SameDatabaseCreation(registered, op))
                        {
                            return ManagementErrors.IdTaken;
                        }
                    }

                    var organization = await GetAsync(transaction, Organizations, op.Organization!, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);
                    if (organization is null && createsOrganization)
                    {
                        return op.Op == "create_database" ? (new TenantContext(op.Organization!, PlanTier.Hobby, op.Project), null) : ManagementErrors.NotFound;
                    }

                    if (!(mayGrant && organization is not null))
                    {
                        var actor = await AuthorizeAsync(transaction, userId, op.Organization!, OrganizationRole.Member, cancellationToken).ConfigureAwait(false);
                        if (actor is not OrganizationMember)
                        {
                            return actor switch
                            {
                                NotFound error => error,
                                Forbidden error => error,
                                OrganizationMember => throw new UnreachableException(),
                            };
                        }
                    }

                    if (createsProject && await transaction.GetVersionAsync(ProjectOrganizations, op.Project!, cancellationToken).ConfigureAwait(false) is null)
                    {
                        return op.Op == "create_database" ? (new TenantContext(organization!.Id, organization.Plan, op.Project), null) : ManagementErrors.NotFound;
                    }

                    if (await ProjectTenantAsync(transaction, organization, op.Project!, cancellationToken).ConfigureAwait(false) is not { } tenant)
                    {
                        return ManagementErrors.NotFound;
                    }

                    if (registered is not null)
                    {
                        return new AddedDatabase(registered, tenant with { DatabaseId = registered.Id }, Existing: true);
                    }

                    if (op.Op == "create_database")
                    {
                        return (tenant, null);
                    }

                    return await GetAsync(transaction, Databases(op.Project!), op.Database!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not { } source
                        ? ManagementErrors.NotFound
                        : source.Home(options.Region) != options.Region ? ManagementErrors.WrongRegion
                        : (tenant, source);
                }, static error => error, cancellationToken).ConfigureAwait(false);
                if (target is AddedDatabase existing)
                {
                    prepared[i] = existing;
                    continue;
                }

                if (target is not ValueTuple<TenantContext, ManagedDatabase?> resolved)
                {
                    var rejected = target switch
                    {
                        NotFound error => new CommitRejected(error, i),
                        Forbidden error => new CommitRejected(error, i),
                        Conflict error => new CommitRejected(error, i),
                        Unavailable error => new CommitRejected(error, i),
                        ValueTuple<TenantContext, ManagedDatabase?> => throw new UnreachableException(),
                        AddedDatabase => throw new UnreachableException(),
                    };
                    return await AbandonAsync(prepared, rejected).ConfigureAwait(false);
                }

                var (project, source) = resolved;
                if ((op.HomeRegion ?? options.Region) != options.Region)
                {
                    return await AbandonAsync(prepared, new CommitRejected(ManagementErrors.WrongRegion, i)).ConfigureAwait(false);
                }

                IReadOnlyList<string> replicas = op.ReplicaRegions ?? [];

                if (DatabaseRegions.Check(options.Region, replicas, false) is Validation invalid)
                {
                    return await AbandonAsync(prepared, new CommitRejected(invalid, i)).ConfigureAwait(false);
                }

                var database = new ManagedDatabase(
                    op.Id ?? NewId(), op.Project!, op.Name!, source?.Id, time.GetUtcNow(), options.Region, replicas.Count == 0 ? null : [.. replicas], 1);
                var storage = project with { DatabaseId = database.Id };
                var preparation = await PrepareDatabaseAsync(storage, source is null ? null : project with { DatabaseId = source.Id }, cancellationToken).ConfigureAwait(false);
                if (preparation is not bool created || !created)
                {
                    var failure = preparation switch
                    {
                        bool => new CommitRejected(ManagementErrors.NotFound, i),
                        Unavailable error => new CommitRejected(error, i),
                    };
                    return await AbandonAsync(prepared, failure).ConfigureAwait(false);
                }

                prepared[i] = new AddedDatabase(database, storage);
            }
        }
        catch
        {
            foreach (var database in prepared.Values.Where(database => !database.Existing))
            {
                await AbandonDatabaseAsync(database.Tenant).ConfigureAwait(false);
            }

            throw;
        }

        return prepared;
    }

    private async ValueTask<CommitRejected> AbandonAsync(Dictionary<int, AddedDatabase> prepared, CommitRejected rejected)
    {
        foreach (var database in prepared.Values.Where(database => !database.Existing))
        {
            await AbandonDatabaseAsync(database.Tenant).ConfigureAwait(false);
        }

        return rejected;
    }

    private bool SameDatabaseCreation(ManagedDatabase database, ManagementOpRequest op) =>
        op.Op == "create_database" && database.BranchedFrom is null && database.ProjectId == op.Project
        && (database.Name == op.Name || (database.ConflictOf is not null && database.PreviousName == op.Name))
        && database.PlacedIn(options.Region) && (op.HomeRegion is null || op.HomeRegion == database.Home(options.Region))
        && !database.AllRegions && (op.ReplicaRegions is null || database.Placement(options.Region).Regions.Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { options.Region }.Concat(op.ReplicaRegions).Order(StringComparer.Ordinal), StringComparer.Ordinal));

    private async ValueTask<Found<CommitRecord>> RecordedAsync(IStorageTransaction transaction, string userId, string commitId, CancellationToken cancellationToken) =>
        await GetAsync(transaction, Commits(userId), commitId, TerraBaseJsonContext.Default.CommitRecord, cancellationToken).ConfigureAwait(false) is { } recorded
        && recorded.CommittedAt > time.GetUtcNow() - CommitRetention
            ? recorded
            : new NotFound("no committed commit has this id");

    private static CommitResult Replay(CommitRecord recorded, string requestHash) =>
        Matches(recorded.RequestHash, requestHash)
            ? new CommitOutcome(Encoding.UTF8.GetBytes(recorded.Body), Replayed: true)
            : new CommitRejected(new Conflict("the commit id was used for a different request", "commit_id_reused"), Op: null);

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

    private static string Commits(string userId) => $"users/{userId}/commits";
}
