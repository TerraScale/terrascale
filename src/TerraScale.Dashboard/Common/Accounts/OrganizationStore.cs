using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Common.Accounts;

public sealed record StoredOrganization(OrganizationRecord Organization, string Version, bool Erased)
{
    internal OrganizationRows Rows { get; init; } = null!;
}

internal sealed record OrganizationRows(OrganizationRow Root, IReadOnlyList<OrganizationProjectRow> Projects, IReadOnlyList<ProjectDatabaseRow> Databases,
    IReadOnlyList<AuditEventRow> AuditEvents, IReadOnlyList<InvitationReservationRow> Reservations)
{
    public void Attach(DashboardDbContext db)
    {
        db.Attach(Root);
        db.AttachRange(Projects);
        db.AttachRange(Databases);
        db.AttachRange(AuditEvents);
        db.AttachRange(Reservations);
    }
}

public sealed class OrganizationStore(IDbContextFactory<DashboardDbContext> storage)
{
    public async Task<StoredOrganization?> FindAsync(string id, CancellationToken cancellationToken)
    {
        return (await LoadAsync(db => db.Organizations.Where(row => row.Id == id), null, cancellationToken)).GetValueOrDefault(id);
    }

    public async Task<IReadOnlyDictionary<string, OrganizationRecord?>> ReadManyAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        var keys = ids.Distinct(StringComparer.Ordinal).ToArray();
        return keys.Length == 0 ? new Dictionary<string, OrganizationRecord?>() :
            (await LoadAsync(db => db.Organizations.Where(row => keys.Contains(row.Id)), keys, cancellationToken))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Erased ? null : pair.Value.Organization, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<OrganizationRecord>> ListAsync(CancellationToken cancellationToken)
    {
        return [.. (await LoadAsync(db => db.Organizations.Where(row => row.ErasureId == null), [], cancellationToken)).Values.Select(stored => stored.Organization)];
    }

    public async Task<bool> IsErasedAsync(string id, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        return await db.Organizations.AnyAsync(row => row.Id == id && row.ErasureId != null, cancellationToken);
    }

    public async Task CreateAsync(OrganizationRecord organization, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(organization.Id))
        {
            throw new IOException("Organization identity is invalid.");
        }

        var rows = Write(organization, AccountService.NewId(), null);
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        db.Add(rows.Root);
        db.AddRange(rows.Projects);
        db.AddRange(rows.Databases);
        db.AddRange(rows.AuditEvents);
        db.AddRange(rows.Reservations);
        await RowSync.CreateAsync(db, cancellationToken);
    }

    public async Task ReplaceAsync(StoredOrganization stored, CancellationToken cancellationToken)
    {
        if (stored.Erased || stored.Organization.Id != stored.Rows.Root.Id)
        {
            throw new DbUpdateConcurrencyException("Dashboard storage changed concurrently.");
        }

        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var loaded = stored.Rows;
        loaded.Attach(db);
        var next = Write(stored.Organization, loaded.Root.Version, null);
        Apply(db, loaded, next);
        if (!db.ChangeTracker.HasChanges())
        {
            return;
        }

        loaded.Root.Version = AccountService.NewId();
        await RowSync.ReplaceAsync(db, cancellationToken);
    }

    public async Task FenceAsync(string id, string erasureId, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(id) || !TerraScaleIds.IsValid(erasureId))
        {
            throw new IOException("Organization erasure identity is invalid.");
        }

        for (var attempt = 0; ; attempt++)
        {
            var stored = await FindAsync(id, cancellationToken);
            if (stored is { Erased: true })
            {
                return;
            }

            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var erased = new OrganizationRecord { Id = id, Kind = stored?.Organization.Kind ?? "business", DeletedProjectIds = stored?.Organization.DeletedProjectIds ?? [] };
            var next = Write(erased, AccountService.NewId(), erasureId);
            if (stored is null)
            {
                db.Add(next.Root);
            }
            else
            {
                stored.Rows.Attach(db);
                Apply(db, stored.Rows, next);
            }

            try
            {
                await (stored is null ? RowSync.CreateAsync(db, cancellationToken) : RowSync.ReplaceAsync(db, cancellationToken));
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3) { }
        }
    }

    private static void Apply(DashboardDbContext db, OrganizationRows loaded, OrganizationRows next)
    {
        db.Entry(loaded.Root).CurrentValues.SetValues(next.Root);
        RowSync.Sync(db, loaded.Projects, next.Projects, row => row.Id);
        RowSync.Sync(db, loaded.Databases, next.Databases, row => row.Id);
        RowSync.Sync(db, loaded.AuditEvents, next.AuditEvents, row => row.Id);
        RowSync.Sync(db, loaded.Reservations, next.Reservations, row => row.Id);
    }

    private async Task<Dictionary<string, StoredOrganization>> LoadAsync(Func<DashboardDbContext, IQueryable<OrganizationRow>> roots, string[]? snapshot,
        CancellationToken cancellationToken)
    {
        var rootRows = RowSync.QueryAsync(storage, roots, cancellationToken);
        var scope = snapshot ?? [.. (await rootRows).Where(row => row.ErasureId is null).Select(row => row.Id)];
        var all = snapshot is [];
        var empty = !all && scope.Length == 0;
        var projects = empty ? Task.FromResult(new List<OrganizationProjectRow>()) :
            RowSync.QueryAsync(storage, db => db.OrganizationProjects.Where(row => all || scope.Contains(row.OrganizationId)), cancellationToken);
        var databases = empty ? Task.FromResult(new List<ProjectDatabaseRow>()) :
            RowSync.QueryAsync(storage, db => db.ProjectDatabases.Where(row => all || scope.Contains(row.OrganizationId)), cancellationToken);
        var audit = empty ? Task.FromResult(new List<AuditEventRow>()) :
            RowSync.QueryAsync(storage, db => db.AuditEvents.Where(row => all || scope.Contains(row.OrganizationId)).OrderBy(row => row.OccurredAt).ThenBy(row => row.Id), cancellationToken);
        var reservations = empty ? Task.FromResult(new List<InvitationReservationRow>()) :
            RowSync.QueryAsync(storage, db => db.InvitationReservations.Where(row => all || scope.Contains(row.OrganizationId)), cancellationToken);
        await Task.WhenAll(rootRows, projects, databases, audit, reservations);
        var projectRows = (await projects).ToLookup(item => item.OrganizationId, StringComparer.Ordinal);
        var databaseRows = (await databases).ToLookup(item => item.OrganizationId, StringComparer.Ordinal);
        var auditRows = (await audit).ToLookup(item => item.OrganizationId, StringComparer.Ordinal);
        var reservationRows = (await reservations).ToLookup(item => item.OrganizationId, StringComparer.Ordinal);
        return (await rootRows).ToDictionary(row => row.Id, row =>
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
                Region = project.Region,
                BackupRegion = project.BackupRegion,
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
            Region = pair.Value.Region,
            BackupRegion = pair.Value.BackupRegion,
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
