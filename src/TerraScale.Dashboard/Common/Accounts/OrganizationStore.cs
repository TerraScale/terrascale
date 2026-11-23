using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Common.Accounts;

public sealed record StoredOrganization(OrganizationRecord Organization, string Version, bool Erased)
{
    public required OrganizationRows Rows { get; init; }
}

public sealed record OrganizationRows(OrganizationRow Root, IReadOnlyList<OrganizationProjectRow> Projects, IReadOnlyList<ProjectDatabaseRow> Databases,
    IReadOnlyList<AuditEventRow> AuditEvents, IReadOnlyList<InvitationReservationRow> Reservations);

public sealed class OrganizationStore(DashboardDb storage)
{
    private const string ById = DashboardSql.Organizations.Select + " WHERE id = @Id";
    private const string ByIds = DashboardSql.Organizations.Select + " WHERE id IN (SELECT value FROM json_each(@Ids))";
    private const string Live = DashboardSql.Organizations.Select + " WHERE erasure_id IS NULL";
    private const string Erased = "SELECT count(*) FROM organizations WHERE id = @Id AND erasure_id IS NOT NULL";
    private const string Scope = " WHERE @All OR organization_id IN (SELECT value FROM json_each(@Ids))";
    private const string Projects = DashboardSql.OrganizationProjects.Select + Scope;
    private const string Databases = DashboardSql.ProjectDatabases.Select + Scope;
    private const string Audit = DashboardSql.AuditEvents.Select + Scope + " ORDER BY occurred_at, id";
    private const string Reservations = DashboardSql.InvitationReservations.Select + Scope;
    private const string FenceVersion = "UPDATE organizations SET version = version WHERE id = @Id AND version = @OriginalVersion";
    private const string RemoveAudit = "DELETE FROM audit_events WHERE organization_id = @Id";
    private const string RemoveDrainJobs = "DELETE FROM background_jobs WHERE id LIKE @Prefix || '%' AND id LIKE '%' || @Suffix";
    private const string PendingCheckout = DashboardSql.PendingOrganizationCheckouts.Select + " WHERE draft_id = @DraftId";
    private const string BindingOf = DashboardSql.Billing.Select + " WHERE organization_id = @OrganizationId";
    private const string TouchRoot = """
        UPDATE organizations SET kind = @Kind, plan_id = @PlanId, billing_status = @BillingStatus, status = @Status,
            payment_method_validated = @PaymentMethodValidated, version = @Version
        WHERE id = @Id AND version = @OriginalVersion
        """;

    public async Task<StoredOrganization?> FindAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        var roots = (await connection.QueryAsync<OrganizationRow>(ById, new { Id = id })).AsList();
        return (await LoadAsync(connection, roots, null)).GetValueOrDefault(id);
    }

    public async Task<IReadOnlyDictionary<string, OrganizationRecord?>> ReadManyAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        var keys = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0)
        {
            return new Dictionary<string, OrganizationRecord?>();
        }

        await using var connection = await storage.OpenAsync(cancellationToken);
        var roots = (await connection.QueryAsync<OrganizationRow>(ByIds, new { Ids = Json(keys) })).AsList();
        return (await LoadAsync(connection, roots, keys))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Erased ? null : pair.Value.Organization, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<OrganizationRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        var roots = (await connection.QueryAsync<OrganizationRow>(Live)).AsList();
        return [.. (await LoadAsync(connection, roots, [])).Values.Select(stored => stored.Organization)];
    }

    public async Task<bool> IsErasedAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(Erased, new { Id = id }) > 0;
    }

    public async Task CreateAsync(OrganizationRecord organization, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(organization.Id))
        {
            throw new IOException("Organization identity is invalid.");
        }

        var rows = Write(organization, AccountService.NewId(), null);
        try
        {
            await storage.WriteAsync((connection, transaction) => InsertAsync(connection, transaction, rows), cancellationToken);
        }
        catch (Exception error) when (DashboardDb.IsDuplicate(error))
        {
            throw new DBConcurrencyException("Dashboard storage changed concurrently.", error);
        }
    }

    private static async Task InsertAsync(TerraBaseDbConnection connection, DbTransaction transaction, OrganizationRows rows)
    {
        await connection.ExecuteAsync(DashboardSql.Organizations.Insert, rows.Root, transaction);
        foreach (var row in rows.Projects)
        {
            await connection.ExecuteAsync(DashboardSql.OrganizationProjects.Insert, row, transaction);
        }

        foreach (var row in rows.Databases)
        {
            await connection.ExecuteAsync(DashboardSql.ProjectDatabases.Insert, row, transaction);
        }

        foreach (var row in rows.AuditEvents)
        {
            await connection.ExecuteAsync(DashboardSql.AuditEvents.Insert, row, transaction);
        }

        foreach (var row in rows.Reservations)
        {
            await connection.ExecuteAsync(DashboardSql.InvitationReservations.Insert, row, transaction);
        }
    }

    internal async Task<OrganizationRecord> CompleteCheckoutAsync(OrganizationRecord organization, PendingOrganizationCheckout draft, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await FindAsync(organization.Id, cancellationToken);
            if (stored is { Erased: true })
            {
                throw new AccountAccessException("Organization deletion is pending.", 409);
            }

            PendingOrganizationCheckout? pending;
            BillingBinding? binding;
            await using (var connection = await storage.OpenAsync(cancellationToken))
            {
                pending = await connection.QuerySingleOrDefaultAsync<PendingOrganizationCheckout>(PendingCheckout, new { draft.DraftId });
                binding = await connection.QuerySingleOrDefaultAsync<BillingBinding>(BindingOf, new { OrganizationId = organization.Id });
            }

            if (binding is not null)
            {
                if (stored is not { Erased: false } || binding.RequestId != draft.DraftId || binding.CheckoutId != draft.CheckoutId
                    || binding.CustomerId != draft.CustomerId || binding.SubscriptionId != draft.SubscriptionId)
                {
                    if (pending is { State: OrganizationCheckoutStates.Creating })
                    {
                        pending.State = OrganizationCheckoutStates.Canceling;
                        pending.RefusalReason = "This organization already has billing.";
                        if (!await storage.ReplaceAsync(pending, cancellationToken))
                        {
                            continue;
                        }
                    }

                    throw new OrganizationCheckoutRefusedException("This organization already has billing.");
                }

                if (pending is not null && !await storage.RemoveAsync(pending, cancellationToken))
                {
                    continue;
                }

                return stored.Organization;
            }

            if (pending is null || pending.State != OrganizationCheckoutStates.Creating || pending.UserId != draft.UserId || pending.OrganizationId != organization.Id)
            {
                throw new AccountAccessException("Organization is not ready to be created.", 409);
            }

            var billing = new BillingBinding
            {
                OrganizationId = organization.Id,
                Version = AccountService.NewId(),
                CustomerId = pending.CustomerId,
                RequestId = pending.DraftId,
                CheckoutId = pending.CheckoutId,
                Plan = pending.Plan,
                Interval = pending.Interval,
                StartedAt = RowSync.At(pending.CreatedAt),
                SubscriptionId = pending.SubscriptionId,
                EffectivePlan = pending.Plan,
                EffectiveInterval = pending.Interval,
                Status = pending.SubscriptionStatus,
                State = "active",
                TrialUsed = true,
                RenewalAt = pending.RenewalAt is { } renewal ? RowSync.At(renewal) : null,
                ReturnPath = "/organization?newOrganization=project&organizationId=" + Uri.EscapeDataString(organization.Id),
                OverageStartsAt = new(RowSync.At(pending.CreatedAt).Year, RowSync.At(pending.CreatedAt).Month, 1, 0, 0, 0, TimeSpan.Zero)
            };
            var customer = new StripeCustomerRow
            {
                Id = AccountService.NewId(),
                OrganizationId = organization.Id,
                CustomerId = pending.CustomerId,
                CreatedAt = pending.CreatedAt
            };
            var checkout = new StripeCheckoutRow
            {
                Id = AccountService.NewId(),
                OrganizationId = organization.Id,
                CustomerId = pending.CustomerId,
                CheckoutId = pending.CheckoutId,
                RequestId = pending.DraftId,
                ProjectId = "",
                ReturnPath = billing.ReturnPath,
                Plan = pending.Plan,
                Interval = pending.Interval,
                CreatedAt = pending.CreatedAt
            };
            OrganizationRows? created = null;
            OrganizationRow? touched = null;
            if (stored is null)
            {
                created = Write(organization, AccountService.NewId(), null);
            }
            else
            {
                touched = stored.Rows.Root with
                {
                    Kind = organization.Kind,
                    PlanId = organization.PlanId,
                    BillingStatus = organization.BillingStatus,
                    Status = organization.Status,
                    PaymentMethodValidated = organization.PaymentMethodValidated,
                    Version = AccountService.NewId(),
                    OriginalVersion = stored.Rows.Root.Version,
                };
                organization = Read(stored.Rows with { Root = touched });
            }

            pending.OriginalVersion = pending.Version;
            try
            {
                await storage.WriteAsync(async (connection, transaction) =>
                {
                    if (created is not null)
                    {
                        await InsertAsync(connection, transaction, created);
                    }

                    if (touched is not null)
                    {
                        DashboardDb.Expect(await connection.ExecuteAsync(TouchRoot, touched, transaction));
                    }

                    await connection.ExecuteAsync(DashboardSql.Billing.Insert, billing, transaction);
                    await connection.ExecuteAsync(DashboardSql.StripeCustomers.Insert, customer, transaction);
                    await connection.ExecuteAsync(DashboardSql.StripeCheckouts.Insert, checkout, transaction);
                    DashboardDb.Expect(await connection.ExecuteAsync(DashboardSql.PendingOrganizationCheckouts.Delete, pending, transaction));
                }, cancellationToken);
                return organization;
            }
            catch (Exception error) when (error is DBConcurrencyException || DashboardDb.IsDuplicate(error))
            {
                if (attempt == 2)
                {
                    throw new IOException("Organization completion changed concurrently. Retry organization setup.");
                }
            }
        }

        throw new IOException("Organization completion changed concurrently. Retry organization setup.");
    }

    public async Task ReplaceAsync(StoredOrganization stored, CancellationToken cancellationToken)
    {
        if (stored.Erased || stored.Organization.Id != stored.Rows.Root.Id)
        {
            throw new DBConcurrencyException("Dashboard storage changed concurrently.");
        }

        var loaded = stored.Rows;
        var next = Write(stored.Organization, loaded.Root.Version, null);
        var changes = Diff(loaded, next);
        if (!changes.Any)
        {
            return;
        }

        var root = next.Root;
        root.OriginalVersion = loaded.Root.Version;
        var auditOnly = root == loaded.Root with { OriginalVersion = loaded.Root.Version } && changes.AuditOnly;
        if (!auditOnly)
        {
            root.Version = AccountService.NewId();
        }

        try
        {
            await storage.WriteAsync(async (connection, transaction) =>
            {
                DashboardDb.Expect(auditOnly
                    ? await connection.ExecuteAsync(FenceVersion, root, transaction)
                    : await connection.ExecuteAsync(DashboardSql.Organizations.Update, root, transaction));
                await ApplyAsync(connection, transaction, changes);
                foreach (var removed in changes.Databases.Removed)
                {
                    await connection.ExecuteAsync(RemoveDrainJobs, new { Prefix = "log-drain/" + removed.ProjectId + "/", Suffix = "/" + removed.Id }, transaction);
                }
            }, cancellationToken);
        }
        catch (Exception error) when (DashboardDb.IsDuplicate(error))
        {
            throw new AccountAccessException("The change conflicts with an existing record.", StatusCodes.Status409Conflict);
        }

        loaded.Root.Version = root.Version;
    }

    public async Task FenceAsync(string id, string erasureId, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(id) || !TerraScaleIds.IsValid(erasureId))
        {
            throw new IOException("Organization erasure identity is invalid.");
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await FindAsync(id, cancellationToken);
            if (stored is { Erased: true })
            {
                await using var connection = await storage.OpenAsync(cancellationToken);
                await connection.ExecuteAsync(RemoveAudit, new { Id = id });
                return;
            }

            var erased = new OrganizationRecord { Id = id, Kind = stored?.Organization.Kind ?? "business", DeletedProjectIds = stored?.Organization.DeletedProjectIds ?? [] };
            var next = Write(erased, AccountService.NewId(), erasureId);
            try
            {
                if (stored is null)
                {
                    await storage.WriteAsync((connection, transaction) => connection.ExecuteAsync(DashboardSql.Organizations.Insert, next.Root, transaction), cancellationToken);
                }
                else
                {
                    next.Root.OriginalVersion = stored.Rows.Root.Version;
                    var changes = Diff(stored.Rows, next);
                    await storage.WriteAsync(async (connection, transaction) =>
                    {
                        DashboardDb.Expect(await connection.ExecuteAsync(DashboardSql.Organizations.Update, next.Root, transaction));
                        await ApplyAsync(connection, transaction, changes);
                    }, cancellationToken);
                }

                return;
            }
            catch (Exception error) when (error is DBConcurrencyException || DashboardDb.IsDuplicate(error))
            {
                if (attempt == 2)
                {
                    throw new IOException("Organization deletion changed concurrently. Retry deletion.");
                }
            }
        }
    }

    private sealed record OrganizationChanges(RowChanges<OrganizationProjectRow> Projects, RowChanges<ProjectDatabaseRow> Databases,
        RowChanges<AuditEventRow> AuditEvents, RowChanges<InvitationReservationRow> Reservations, bool RootChanged)
    {
        public bool Any => RootChanged || Projects.Any || Databases.Any || AuditEvents.Any || Reservations.Any;

        public bool AuditOnly => !RootChanged && !Projects.Any && !Databases.Any && !Reservations.Any
            && AuditEvents.Changed.Count == 0 && AuditEvents.Removed.Count == 0;
    }

    private static OrganizationChanges Diff(OrganizationRows loaded, OrganizationRows next) =>
        new(RowSync.Diff(loaded.Projects, next.Projects, static row => row.Id), RowSync.Diff(loaded.Databases, next.Databases, static row => row.Id),
            RowSync.Diff(loaded.AuditEvents, next.AuditEvents, static row => row.Id), RowSync.Diff(loaded.Reservations, next.Reservations, static row => row.Id),
            next.Root with { OriginalVersion = null, Version = loaded.Root.Version } != loaded.Root);

    private static async Task ApplyAsync(TerraBaseDbConnection connection, DbTransaction transaction, OrganizationChanges changes)
    {
        foreach (var row in changes.Reservations.Removed)
        {
            await connection.ExecuteAsync(DashboardSql.InvitationReservations.Delete, row, transaction);
        }

        foreach (var row in changes.AuditEvents.Removed)
        {
            await connection.ExecuteAsync(DashboardSql.AuditEvents.Delete, row, transaction);
        }

        foreach (var row in changes.Databases.Removed)
        {
            await connection.ExecuteAsync(DashboardSql.ProjectDatabases.Delete, row, transaction);
        }

        foreach (var row in changes.Projects.Removed)
        {
            await connection.ExecuteAsync(DashboardSql.OrganizationProjects.Delete, row, transaction);
        }

        foreach (var row in changes.Projects.Changed)
        {
            await connection.ExecuteAsync(DashboardSql.OrganizationProjects.Update, row, transaction);
        }

        foreach (var row in changes.Projects.Added)
        {
            await connection.ExecuteAsync(DashboardSql.OrganizationProjects.Insert, row, transaction);
        }

        foreach (var row in changes.Databases.Changed)
        {
            await connection.ExecuteAsync(DashboardSql.ProjectDatabases.Update, row, transaction);
        }

        foreach (var row in changes.Databases.Added)
        {
            await connection.ExecuteAsync(DashboardSql.ProjectDatabases.Insert, row, transaction);
        }

        foreach (var row in changes.AuditEvents.Changed)
        {
            await connection.ExecuteAsync(DashboardSql.AuditEvents.Update, row, transaction);
        }

        foreach (var row in changes.AuditEvents.Added)
        {
            await connection.ExecuteAsync(DashboardSql.AuditEvents.Insert, row, transaction);
        }

        foreach (var row in changes.Reservations.Changed)
        {
            await connection.ExecuteAsync(DashboardSql.InvitationReservations.Update, row, transaction);
        }

        foreach (var row in changes.Reservations.Added)
        {
            await connection.ExecuteAsync(DashboardSql.InvitationReservations.Insert, row, transaction);
        }
    }

    private static async Task<Dictionary<string, StoredOrganization>> LoadAsync(TerraBaseDbConnection connection, IReadOnlyList<OrganizationRow> roots, string[]? snapshot)
    {
        var scope = snapshot ?? [.. roots.Where(row => row.ErasureId is null).Select(row => row.Id)];
        var all = snapshot is [];
        var empty = !all && scope.Length == 0;
        var filter = new { All = all, Ids = Json(scope) };
        var projects = empty ? [] : (await connection.QueryAsync<OrganizationProjectRow>(Projects, filter)).AsList();
        var databases = empty ? [] : (await connection.QueryAsync<ProjectDatabaseRow>(Databases, filter)).AsList();
        var audit = empty ? [] : (await connection.QueryAsync<AuditEventRow>(Audit, filter)).AsList();
        var reservations = empty ? [] : (await connection.QueryAsync<InvitationReservationRow>(Reservations, filter)).AsList();
        var projectRows = projects.ToLookup(item => item.OrganizationId, StringComparer.Ordinal);
        var databaseRows = databases.ToLookup(item => item.OrganizationId, StringComparer.Ordinal);
        var auditRows = audit.ToLookup(item => item.OrganizationId, StringComparer.Ordinal);
        var reservationRows = reservations.ToLookup(item => item.OrganizationId, StringComparer.Ordinal);
        return roots.ToDictionary(row => row.Id, row =>
        {
            var rows = new OrganizationRows(row, [.. projectRows[row.Id]], [.. databaseRows[row.Id]], [.. auditRows[row.Id]], [.. reservationRows[row.Id]]);
            return row.ErasureId is null ? new StoredOrganization(Read(rows), row.Version, false) { Rows = rows } :
                new StoredOrganization(new OrganizationRecord { Id = row.Id }, row.Version, true) { Rows = rows };
        }, StringComparer.Ordinal);
    }

    private static OrganizationRecord Read(OrganizationRows rows)
    {
        var root = rows.Root;
        return new()
        {
            Id = root.Id,
            Kind = root.Kind,
            Status = root.Status,
            PlanId = root.PlanId,
            BillingStatus = root.BillingStatus,
            PaymentGraceUntil = root.PaymentGraceUntil is { } deadline ? RowSync.At(deadline) : null,
            PaymentMethodValidated = root.PaymentMethodValidated,
            SpendCapCents = root.SpendCapCents,
            PaidExcess = root.PaidExcess,
            Archived = root.Archived,
            DeletionRequested = root.DeletionRequested,
            InternalBilling = root.InternalBilling is null ? null : JsonSerializer.Deserialize(root.InternalBilling, AccountRowJson.Default.InternalBillingPolicy),
            InternalBillingAlerts = JsonSerializer.Deserialize(root.InternalBillingAlerts, AccountRowJson.Default.DictionaryStringInternalBillingAlert)!,
            AuditRetentionDays = root.AuditRetentionDays,
            PendingProjectDeletions = JsonSerializer.Deserialize(root.PendingProjectDeletions, AccountRowJson.Default.DictionaryStringString)!,
            DeletedProjectIds = [.. JsonSerializer.Deserialize(root.DeletedProjectIds, AccountRowJson.Default.StringArray)!],
            Projects = rows.Projects.ToDictionary(project => project.Id, project => new ProjectRecord
            {
                Id = project.Id,
                Regions = Strings(project.Regions),
                Status = project.Status,
                IsDefault = project.IsDefault,
                SpendCapCents = project.SpendCapCents,
                OperationLimits = new() { ReadLimit = project.ReadLimit, WriteLimit = project.WriteLimit, ReplicationLimit = project.ReplicationLimit },
                Databases = rows.Databases.Where(database => database.ProjectId == project.Id).ToDictionary(database => database.Id, database => new DatabaseRecord
                {
                    Id = database.Id,
                    ProjectId = database.ProjectId,
                    Name = database.Name,
                    PreviousName = database.PreviousName,
                    ConflictOf = database.ConflictOf,
                    Region = database.Region,
                    Regions = Strings(database.Regions),
                    AllRegions = database.AllRegions,
                    PlacementRevision = database.PlacementRevision,
                    Endpoint = database.Endpoint,
                    Status = database.Status,
                    SpendCapCents = database.SpendCapCents,
                    OperationLimits = new() { ReadLimit = database.ReadLimit, WriteLimit = database.WriteLimit, ReplicationLimit = database.ReplicationLimit },
                    BackupRetentionDays = database.BackupRetentionDays,
                    BranchedFrom = database.BranchedFrom,
                    CreatedAt = RowSync.At(database.CreatedAt),
                    ExpiresAt = database.ExpiresAt is { } expires ? RowSync.At(expires) : null
                }, StringComparer.Ordinal)
            }, StringComparer.Ordinal),
            AuditEvents = [.. rows.AuditEvents.Select(item => new AuditRecord(item.Id, item.Actor, item.Action, item.Target, item.TargetType, RowSync.At(item.OccurredAt), item.Source, item.Risk, item.Ip))],
            InvitationReservations = rows.Reservations.ToDictionary(item => item.Id, item => new InvitationReservation(item.Email, RowSync.At(item.ReservedAt), item.InvitationId)
            {
                ProjectId = item.ProjectId,
                Known = Strings(item.Known)
            }, StringComparer.Ordinal)
        };
    }

    private static OrganizationRows Write(OrganizationRecord organization, string version, string? erasureId)
    {
        var root = new OrganizationRow
        {
            Id = organization.Id,
            Kind = organization.Kind,
            Status = organization.Status,
            PlanId = organization.PlanId,
            BillingStatus = organization.BillingStatus,
            PaymentGraceUntil = organization.PaymentGraceUntil?.UtcDateTime,
            PaymentMethodValidated = organization.PaymentMethodValidated,
            SpendCapCents = organization.SpendCapCents,
            PaidExcess = organization.PaidExcess,
            Archived = organization.Archived,
            DeletionRequested = organization.DeletionRequested,
            InternalBilling = organization.InternalBilling is null ? null : JsonSerializer.Serialize(organization.InternalBilling, AccountRowJson.Default.InternalBillingPolicy),
            InternalBillingAlerts = JsonSerializer.Serialize(organization.InternalBillingAlerts, AccountRowJson.Default.DictionaryStringInternalBillingAlert),
            AuditRetentionDays = organization.AuditRetentionDays,
            PendingProjectDeletions = JsonSerializer.Serialize(organization.PendingProjectDeletions, AccountRowJson.Default.DictionaryStringString),
            DeletedProjectIds = Json([.. organization.DeletedProjectIds.Order(StringComparer.Ordinal)]),
            ErasureId = erasureId,
            Version = version
        };
        var projects = organization.Projects.Select(pair => new OrganizationProjectRow
        {
            Id = pair.Key,
            OrganizationId = organization.Id,
            Regions = Json([.. pair.Value.Regions]),
            Status = pair.Value.Status,
            IsDefault = pair.Value.IsDefault,
            SpendCapCents = pair.Value.SpendCapCents,
            ReadLimit = pair.Value.OperationLimits.ReadLimit,
            WriteLimit = pair.Value.OperationLimits.WriteLimit,
            ReplicationLimit = pair.Value.OperationLimits.ReplicationLimit
        }).ToArray();
        var databases = organization.Projects.SelectMany(project => project.Value.Databases.Select(pair => new ProjectDatabaseRow
        {
            Id = pair.Key,
            OrganizationId = organization.Id,
            ProjectId = project.Key,
            Name = pair.Value.Name,
            PreviousName = pair.Value.PreviousName,
            ConflictOf = pair.Value.ConflictOf,
            Region = pair.Value.Region,
            Regions = Json([.. pair.Value.Regions]),
            AllRegions = pair.Value.AllRegions,
            PlacementRevision = pair.Value.PlacementRevision,
            Endpoint = pair.Value.Endpoint,
            Status = pair.Value.Status,
            SpendCapCents = pair.Value.SpendCapCents,
            ReadLimit = pair.Value.OperationLimits.ReadLimit,
            WriteLimit = pair.Value.OperationLimits.WriteLimit,
            ReplicationLimit = pair.Value.OperationLimits.ReplicationLimit,
            BackupRetentionDays = pair.Value.BackupRetentionDays,
            BranchedFrom = pair.Value.BranchedFrom,
            CreatedAt = pair.Value.CreatedAt.UtcDateTime,
            ExpiresAt = pair.Value.ExpiresAt?.UtcDateTime
        })).ToArray();
        var audit = organization.AuditEvents.Select(item => new AuditEventRow
        {
            Id = item.Id,
            OrganizationId = organization.Id,
            Actor = item.Actor,
            Action = item.Action,
            Target = item.Target,
            TargetType = item.TargetType,
            OccurredAt = item.OccurredAt.UtcDateTime,
            Source = item.Source,
            Risk = item.Risk,
            Ip = item.Ip
        }).ToArray();
        var reservations = organization.InvitationReservations.Select(pair => new InvitationReservationRow
        {
            Id = pair.Key,
            OrganizationId = organization.Id,
            Email = pair.Value.Email,
            ReservedAt = pair.Value.ReservedAt.UtcDateTime,
            InvitationId = pair.Value.InvitationId,
            ProjectId = pair.Value.ProjectId,
            Known = Json([.. pair.Value.Known])
        }).ToArray();
        return new(root, projects, databases, audit, reservations);
    }

    private static string Json(string[] values)
    {
        return JsonSerializer.Serialize(values, AccountRowJson.Default.StringArray);
    }

    private static List<string> Strings(string json)
    {
        return [.. JsonSerializer.Deserialize(json, AccountRowJson.Default.StringArray)!];
    }
}
