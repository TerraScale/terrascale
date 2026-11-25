using TerraBase.Cluster;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Atlas;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Platform;
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Features.Management;

internal readonly union PageLimit(int, InvalidInput);

internal readonly union DisplayNameParse(string, InvalidInput);

internal readonly union OwnedOrganizationLookup(OrganizationRecord, CredentialRejected, ResourceNotFound, BackendUnavailable);

internal readonly union OrganizationCreation(OrganizationRecord, InvalidInput, CredentialRejected, CallerForbidden, StateConflict, BackendUnavailable);

internal readonly union DatabaseCreateOutcome(DatabaseRecord, InvalidInput, CallerForbidden, StateConflict, BackendUnavailable);

internal readonly union DatabaseCopyOutcome(RestoreOperation, ResourceNotFound, InvalidInput, StateConflict, BackendUnavailable);

internal readonly union DatabaseRemovalOutcome(Completed, BackendUnavailable);

internal readonly union DatabaseDeletionOutcome(Completed, StateConflict, BackendUnavailable);

internal readonly union ProjectCloseOutcome(Completed, StateConflict, BackendUnavailable);

internal readonly union DatabaseChangeOutcome(DatabaseRecord, InvalidInput, StateConflict, BackendUnavailable);

internal readonly union DatabaseAllowance(Completed, StateConflict, BackendUnavailable);

internal readonly union PlanLookup(string, BackendUnavailable);

internal sealed record TombstoneRetained;

internal readonly union TombstoneSweep(Completed, TombstoneRetained, BackendUnavailable);

internal readonly union DatabaseBranchOutcome(DatabaseRecord, InvalidInput, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable);

internal sealed class ManagementService(
    CatalogStore catalog,
    TenantDatabases databases,
    RegionDirectory regions,
    IBackupContinuity continuity,
    AntiEntropyService antiEntropy,
    AtlasPublisher atlas,
    TerraBaseOptions options,
    TimeProvider time)
{
    public const int DefaultLimit = 20;

    public const int MaxLimit = 100;

    private const long MinTtlSeconds = 60;

    private const int SweepPage = 64;

    private static readonly ResourceNotFound UnknownOrganization = new("the organization does not exist", "unknown_organization");

    private static readonly CallerForbidden PlatformOnly =
        new("organizations and projects are created and deleted through TerraScale accounts, not directly in TerraBase", "forbidden_caller");

    public static CallerForbidden? DirectUser(Caller caller) => caller switch
    {
        UserCaller { ViaOperator: false } => PlatformOnly,
        AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => null,
    };

    public static PageLimit Limit(int? limit) =>
        limit is null ? DefaultLimit : limit is >= 1 and <= MaxLimit ? limit.Value : new InvalidInput("limit must be an integer between 1 and 100", "invalid_request");

    public static DisplayNameParse DisplayName(string? raw)
    {
        var name = raw?.Trim();
        return string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(char.IsControl) || !name.Any(char.IsLetterOrDigit)
            ? new InvalidInput("name must be 1 to 128 characters, include a letter or digit and have no control characters", "invalid_name")
            : name;
    }

    public static Ulid? Id(string? raw) => raw is { Length: 26 } && Ulid.TryParse(raw, out var id) ? id : null;

    public async ValueTask<OwnedOrganizationLookup> OwnedOrganizationAsync(Caller caller, string? organizationId, CancellationToken cancellationToken)
    {
        if (Id(organizationId) is not { } id)
        {
            return UnknownOrganization;
        }

        var outcome = await catalog.GetOrganizationAsync(id, cancellationToken);
        return outcome switch
        {
            OrganizationRecord organization => caller switch
            {
                UserCaller user when string.Equals(user.UserId, organization.CreatedBy, StringComparison.Ordinal) => organization,
                PeerCaller => organization,
                ServiceCaller service when service.Grants.Contains("admin") => organization,
                AnonymousCaller => new CredentialRejected("management needs a ts-auth access token", "unauthorized"),
                UserCaller or ServiceCaller or ApiKeyCaller or ProjectCaller or EndUserCaller => UnknownOrganization,
            },
            ResourceNotFound => UnknownOrganization,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<OrganizationCreation> CreateOrganizationAsync(
        Caller caller, string? name, CancellationToken cancellationToken)
    {
        return caller switch
        {
            UserCaller { ViaOperator: false } => PlatformOnly,
            UserCaller user => await CreateOwnedAsync(user, name, cancellationToken),
            AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or ProjectCaller or EndUserCaller =>
                new CredentialRejected("creating an organization needs a ts-auth access token for a user", "unauthorized"),
        };
    }

    private async ValueTask<OrganizationCreation> CreateOwnedAsync(
        UserCaller user, string? name, CancellationToken cancellationToken)
    {
        return DisplayName(name) switch
        {
            string checkedName => await catalog.CreateOrganizationAsync(
                new OrganizationRecord(Ulid.NewUlid(Now()), checkedName, user.UserId, Now(), Plans.Hobby, false), cancellationToken) switch
            {
                OrganizationRecord created => created,
                StateConflict conflict => conflict,
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
        };
    }

    public async ValueTask<DatabaseCreateOutcome> CreateDatabaseAsync(
        ProjectRecord project, DatabaseInput input, CancellationToken cancellationToken)
    {
        if (!DatabaseNames.IsValid(input.Name))
        {
            return new InvalidInput("name must be a lowercase database name of at most 63 characters", "invalid_name");
        }

        if (input.TtlSeconds is < MinTtlSeconds)
        {
            return new InvalidInput("ttlSeconds is at least 60", "invalid_ttl");
        }

        var mode = ConsistencyMode.Fast;
        if (input.DefaultConsistency is { } consistency && !ConsistencyHeaders.TryParse(consistency, out mode))
        {
            return new InvalidInput("defaultConsistency is fast or strong", "invalid_consistency");
        }

        var allowance = await DatabaseAllowanceAsync(project, cancellationToken);
        if (allowance switch { Completed => null, StateConflict conflict => (DatabaseCreateOutcome?)conflict, BackendUnavailable unavailable => (DatabaseCreateOutcome?)unavailable } is { } refused)
        {
            return refused;
        }

        return regions.Check(input.Regions ?? (input.HomeRegion is { } home ? [home, .. input.ReplicaRegions ?? []] : input.ReplicaRegions)) switch
        {
            IReadOnlyList<string> placed => await CreatePlacedAsync(
                Record(project, input.Name, placed, input.AllRegions ?? false, mode, input.Strict ?? false, input.TtlSeconds, null, 0), cancellationToken),
            InvalidInput invalid => invalid,
        };
    }

    public async ValueTask<DatabaseCopyOutcome> CopyAsync(
        ProjectRecord project, DatabaseRecord source, string? name, long? ttlSeconds, RestoreTarget target, bool branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!DatabaseNames.IsValid(name))
        {
            return new InvalidInput("name must be a lowercase database name of at most 63 characters", "invalid_name");
        }

        if (ttlSeconds is < MinTtlSeconds)
        {
            return new InvalidInput("ttlSeconds is at least 60", "invalid_ttl");
        }

        var allowance = await DatabaseAllowanceAsync(project, cancellationToken);
        if (allowance switch { Completed => null, StateConflict conflict => (DatabaseCopyOutcome?)conflict, BackendUnavailable unavailable => (DatabaseCopyOutcome?)unavailable } is { } refused)
        {
            return refused;
        }

        var record = Record(project, name, source.Regions, source.AllRegions, source.DefaultConsistency, source.Strict, ttlSeconds, source.Id, source.StorageLimit);
        var outcome = await catalog.InsertDatabaseAsync(record, cancellationToken);
        return outcome switch
        {
            DatabaseRecord reserved => await (branch
                ? continuity.BranchAsync(new BranchRequest(source.Id, reserved.Id, target), cancellationToken)
                : continuity.RestoreAsync(new PointInTimeRestoreRequest(source.Id, reserved.Id, target), cancellationToken)) switch
            {
                RestoreOperation operation => Started(reserved, operation),
                ContinuityNotFound missing => await DiscardAsync(reserved, new ResourceNotFound(missing.Message, missing.Code)),
                ContinuityInvalidTarget invalid => await DiscardAsync(reserved, new InvalidInput(invalid.Message, invalid.Code)),
                ContinuityConflict conflict => await DiscardAsync(reserved, new StateConflict(conflict.Message, conflict.Code)),
                ContinuityUnavailable unavailable => await DiscardAsync(reserved, new BackendUnavailable(unavailable.Message, unavailable.Code)),
            },
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DatabaseRemovalOutcome> DiscardCopyAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var outcome = await catalog.GetDatabaseAsync(databaseId, cancellationToken);
        return outcome switch
        {
            DatabaseRecord record => await DeleteDatabaseAsync(record, cancellationToken) switch
            {
                Completed done => done,
                StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
                BackendUnavailable unavailable => unavailable,
            },
            ResourceNotFound => new Completed(),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ProjectCloseOutcome> CloseProjectAsync(Ulid projectId, Ulid authorityOrganizationId, bool erase, CancellationToken cancellationToken)
    {
        if (PlatformCatalog.IsPlatformProject(projectId))
        {
            return new StateConflict("TerraScale's own projects hold its services' databases and cannot be closed", "platform_project_protected");
        }

        _ = await antiEntropy.SyncAsync(SystemDatabase.Id, TimeSpan.FromSeconds(2), cancellationToken);
        var found = await catalog.GetProjectAsync(projectId, cancellationToken);
        return found switch
        {
            ProjectRecord project when project.OrganizationId != authorityOrganizationId =>
                new StateConflict("the project belongs to another organization in TerraBase", "binding_conflict"),
            ProjectRecord project => await CloseFoundProjectAsync(project, erase, cancellationToken),
            ResourceNotFound => new Completed(),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ProjectCloseOutcome> CloseFoundProjectAsync(ProjectRecord project, bool erase, CancellationToken cancellationToken)
    {
        if (erase)
        {
            string? after = null;
            while (true)
            {
                var pageOutcome = await catalog.ListDatabasesAsync(project.Id, after, MaxLimit, cancellationToken);
                if (pageOutcome switch { CatalogPage<DatabaseRecord> => null, BackendUnavailable unavailable => unavailable } is { } listFailure)
                {
                    return listFailure;
                }

                var page = pageOutcome switch { CatalogPage<DatabaseRecord> listed => listed, BackendUnavailable => new CatalogPage<DatabaseRecord>([], null) };
                foreach (var database in page.Items)
                {
                    var deleted = await DeleteDatabaseAsync(database, cancellationToken);
                    if (deleted switch { Completed => null, StateConflict conflict => (ProjectCloseOutcome?)conflict, BackendUnavailable unavailable => unavailable } is { } deleteFailure)
                    {
                        return deleteFailure;
                    }
                }

                if (page.Next is not { } next)
                {
                    break;
                }

                after = next;
            }
        }

        var removed = await catalog.DeleteProjectAsync(project.Id, cancellationToken);
        return removed switch
        {
            Completed done => done,
            StateConflict { Code: "project_not_empty" } => new StateConflict("delete the project's databases first", "not_empty"),
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DatabaseChangeOutcome> UpdateDatabaseAsync(
        DatabaseRecord current, DatabaseInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Name is { } name && !DatabaseNames.IsValid(name))
        {
            return new InvalidInput("name must be a lowercase database name of at most 63 characters", "invalid_name");
        }

        var mode = current.DefaultConsistency;
        if (input.DefaultConsistency is { } consistency && !ConsistencyHeaders.TryParse(consistency, out mode))
        {
            return new InvalidInput("defaultConsistency is fast or strong", "invalid_consistency");
        }

        if (input.TtlSeconds is < MinTtlSeconds)
        {
            return new InvalidInput("ttlSeconds is at least 60", "invalid_ttl");
        }

        if (input.HasRegions)
        {
            return input.HomeRegion is not null || input.ReplicaRegions is not null || input.AllRegions is not null || input.Region is not null
                ? new InvalidInput("send regions or homeRegion, replicaRegions and allRegions, not both")
                : regions.CheckSet(input.Regions) switch
                {
                    IReadOnlyList<string> placed => await SaveAsync(current, Changed(current, input, placed, mode), cancellationToken),
                    InvalidInput invalid => invalid,
                };
        }

        var requested = input.ReplicaRegions ?? (input.Region is { } single ? [.. current.Regions, single] : null);
        return requested is null
            ? await SaveAsync(current, Changed(current, input, current.Regions, mode), cancellationToken)
            : regions.Check(requested) switch
            {
                IReadOnlyList<string> placed => await SaveAsync(current, Changed(current, input, placed, mode), cancellationToken),
                InvalidInput invalid => invalid,
            };
    }

    public async ValueTask<DatabaseChangeOutcome> SetTtlAsync(DatabaseRecord current, long? ttlSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (ttlSeconds is < MinTtlSeconds)
        {
            return new InvalidInput("ttlSeconds is at least 60", "invalid_ttl");
        }

        return await SaveAsync(current, current with { ExpiresAt = ttlSeconds is { } ttl ? Now().AddSeconds(ttl) : null }, cancellationToken);
    }

    public async ValueTask<DatabaseDeletionOutcome> DeleteDatabaseAsync(DatabaseRecord database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var outcome = await catalog.DeleteDatabaseAsync(database, Now() + CatalogStore.TombstoneRetention, cancellationToken);
        return outcome switch
        {
            DatabaseTombstoned tombstoned => await RetireAsync(database, tombstoned.NameReused, cancellationToken) switch
            {
                Completed done => done,
                BackendUnavailable unavailable => unavailable,
            },
            ResourceNotFound => await ShredAsync(database, cancellationToken) switch
            {
                Completed done => done,
                BackendUnavailable unavailable => unavailable,
            },
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DatabaseBranchOutcome> BranchAsync(
        ProjectRecord project, DatabaseRecord source, ISqlDatabaseSession session, string? name, long? ttlSeconds, CausalBookmark at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(session);
        if (!DatabaseNames.IsValid(name))
        {
            return new InvalidInput("name must be a lowercase database name of at most 63 characters", "invalid_name");
        }

        if (ttlSeconds is < MinTtlSeconds)
        {
            return new InvalidInput("ttlSeconds is at least 60", "invalid_ttl");
        }

        var allowance = await DatabaseAllowanceAsync(project, cancellationToken);
        if (allowance switch { Completed => null, StateConflict conflict => (DatabaseBranchOutcome?)conflict, BackendUnavailable unavailable => (DatabaseBranchOutcome?)unavailable } is { } refused)
        {
            return refused;
        }

        var record = Record(project, name, source.Regions, source.AllRegions, source.DefaultConsistency, source.Strict, ttlSeconds, source.Id, source.StorageLimit);
        var branched = await databases.BranchAsync(session, record.Id, record.KeyId, at, cancellationToken);
        return branched switch
        {
            ISqlDatabaseSession => await PublishBranchAsync(record, cancellationToken),
            ResourceNotFound missing => missing,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<DatabaseBranchOutcome> PublishBranchAsync(DatabaseRecord record, CancellationToken cancellationToken)
    {
        var published = await catalog.InsertDatabaseAsync(record, cancellationToken);
        return published switch
        {
            DatabaseRecord branch => Published(branch),
            StateConflict conflict => await DiscardUnpublishedBranchAsync(record.Id, conflict),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<DatabaseBranchOutcome> DiscardUnpublishedBranchAsync(Ulid databaseId, StateConflict conflict)
    {
        var deleted = await databases.DeleteAsync(databaseId, CancellationToken.None);
        return deleted switch
        {
            Completed => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<int> SweepDeletedAsync(CancellationToken cancellationToken)
    {
        var purged = 0;
        string? after = null;
        do
        {
            var pageOutcome = await catalog.DeletedDatabasesAsync(after, SweepPage, cancellationToken);
            var page = pageOutcome switch
            {
                CatalogPage<DatabaseRecord> listed => listed,
                BackendUnavailable => new CatalogPage<DatabaseRecord>([], null),
            };
            foreach (var database in page.Items)
            {
                purged += await SweepTombstoneAsync(database, cancellationToken) switch
                {
                    Completed => 1,
                    TombstoneRetained or BackendUnavailable => 0,
                };
            }

            after = page.Next;
        }
        while (after is not null);

        return purged;
    }

    public async ValueTask<int> SweepExpiredAsync(CancellationToken cancellationToken)
    {
        var now = Now();
        var expiredOutcome = await catalog.ExpiredDatabasesAsync(now, SweepPage, cancellationToken);
        var expired = expiredOutcome switch
        {
            IReadOnlyList<DatabaseRecord> records => records,
            BackendUnavailable => [],
        };
        var deleted = 0;
        foreach (var database in expired)
        {
            var claimed = await catalog.ExpireDatabaseAsync(database, now, now + CatalogStore.TombstoneRetention, cancellationToken);
            deleted += claimed switch
            {
                DatabaseTombstoned tombstoned => await RetireAsync(database, tombstoned.NameReused, cancellationToken) switch
                {
                    Completed or BackendUnavailable => 1,
                },
                StateConflict or BackendUnavailable => 0,
            };
        }

        return deleted;
    }

    private async ValueTask<TombstoneSweep> SweepTombstoneAsync(DatabaseRecord database, CancellationToken cancellationToken)
    {
        var now = Now();
        if (database.ExpiresAt is { } purgeAfter && purgeAfter > now)
        {
            var local = await databases.DeleteAsync(database.Id, cancellationToken);
            return local switch
            {
                Completed => new TombstoneRetained(),
                BackendUnavailable unavailable => unavailable,
            };
        }

        var shredded = await ShredAsync(database, cancellationToken);
        return shredded switch
        {
            Completed => await catalog.CleanupDatabaseAsync(database.Id, purged: true, cancellationToken) switch
            {
                Completed => await catalog.PurgeDatabaseAsync(database, now, cancellationToken) switch
                {
                    TombstonePurged => new Completed(),
                    ResourceNotFound => new TombstoneRetained(),
                    BackendUnavailable unavailable => unavailable,
                },
                BackendUnavailable unavailable => unavailable,
            },
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<PlanLookup> PlanAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var organizationOutcome = await catalog.GetOrganizationAsync(project.OrganizationId, cancellationToken);
        return organizationOutcome switch
        {
            OrganizationRecord organization => project.Plan ?? organization.Plan,
            ResourceNotFound => project.Plan ?? Plans.Hobby,
            BackendUnavailable unavailable => new BackendUnavailable(string.Concat("the organization's plan could not be read; retry: ", unavailable.Message), "plan_unavailable"),
        };
    }

    private async ValueTask<DatabaseAllowance> DatabaseAllowanceAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        if (project.OrganizationId == PlatformCatalog.OrganizationId)
        {
            return new Completed();
        }

        var planOutcome = await PlanAsync(project, cancellationToken);
        return planOutcome switch
        {
            string plan => await catalog.CountOrganizationDatabasesAsync(project.OrganizationId, cancellationToken) switch
            {
                long count when count >= Plans.DatabaseLimit(plan) => new StateConflict("the organization's plan allows no more databases", "database_limit"),
                long => new Completed(),
                BackendUnavailable unavailable => unavailable,
            },
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<DatabaseCreateOutcome> CreatePlacedAsync(
        DatabaseRecord record, CancellationToken cancellationToken)
    {
        var outcome = await catalog.InsertDatabaseAsync(record, cancellationToken);
        return outcome switch
        {
            DatabaseRecord reserved => await databases.CreateAsync(reserved.Id, reserved.KeyId, reserved.Strict, cancellationToken) switch
            {
                ISqlDatabaseSession => Published(reserved),
                StateConflict conflict => await DiscardAsync(reserved, conflict),
                BackendUnavailable unavailable => await DiscardAsync(reserved, unavailable),
            },
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private static bool SamePlacement(DatabaseRecord current, IReadOnlyList<string> placed, bool allRegions) =>
        current.AllRegions == allRegions
        && current.Regions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).SequenceEqual(placed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());

    private DatabaseRecord Record(
        ProjectRecord project, string name, IReadOnlyList<string> placed, bool allRegions, ConsistencyMode mode, bool strict, long? ttlSeconds, Ulid? branchedFrom, long storageLimit)
    {
        var now = Now();
        return new DatabaseRecord(Ulid.NewUlid(now), project.Id, name, placed, allRegions, mode, strict, ControlDatabaseState.Active, databases.ActiveKeyId, branchedFrom, null, now,
            ttlSeconds is { } ttl ? now.AddSeconds(ttl) : null, 1, storageLimit);
    }

    private RestoreOperation Started(DatabaseRecord reserved, RestoreOperation operation)
    {
        _ = Published(reserved);
        return operation;
    }

    private async ValueTask<TFailure> DiscardAsync<TFailure>(DatabaseRecord reserved, TFailure failure)
    {
        var outcome = await DeleteDatabaseAsync(reserved, CancellationToken.None);
        return outcome switch
        {
            Completed or StateConflict or BackendUnavailable => failure,
        };
    }

    private DatabaseRecord Changed(DatabaseRecord current, DatabaseInput input, IReadOnlyList<string> placed, ConsistencyMode mode)
    {
        var allRegions = input.AllRegions ?? current.AllRegions;
        var preferred = input.HomeRegion ?? (current.Regions.Count > 0 ? current.Regions[0] : null);
        IReadOnlyList<string> ordered = preferred is { } home && placed.Contains(home, StringComparer.Ordinal)
            ? [home, .. placed.Where(region => !string.Equals(region, home, StringComparison.Ordinal))]
            : placed;
        return current with
        {
            Name = input.Name ?? current.Name,
            PreviousName = input.Name is { } renamed && renamed != current.Name ? current.Name : current.PreviousName,
            Regions = ordered,
            AllRegions = allRegions,
            DefaultConsistency = mode,
            ExpiresAt = input.TtlSeconds is { } ttl ? Now().AddSeconds(ttl) : current.ExpiresAt,
            Version = SamePlacement(current, ordered, allRegions) && (input.Name is null || input.Name == current.Name) ? current.Version : current.Version + 1,
        };
    }

    private async ValueTask<DatabaseChangeOutcome> SaveAsync(DatabaseRecord current, DatabaseRecord updated, CancellationToken cancellationToken)
    {
        var savedOutcome = await catalog.UpdateDatabaseAsync(current, updated, cancellationToken);
        return savedOutcome switch
        {
            DatabaseRecord record => Renamed(current, Published(record)),
            ResourceNotFound missing => new StateConflict(missing.Message, "database_changed"),
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private DatabaseRecord Renamed(DatabaseRecord current, DatabaseRecord saved)
    {
        if (!string.Equals(current.Name, saved.Name, StringComparison.Ordinal))
        {
            atlas.Queue(current.ProjectId, current.Name, null);
        }

        return saved;
    }

    private async ValueTask<DatabaseRemovalOutcome> RetireAsync(DatabaseRecord database, bool nameReused, CancellationToken cancellationToken)
    {
        Withdraw(database, nameReused);
        var shredded = await ShredAsync(database, cancellationToken);
        return shredded switch
        {
            Completed => await catalog.CleanupDatabaseAsync(database.Id, purged: false, cancellationToken) switch
            {
                Completed done => done,
                BackendUnavailable unavailable => unavailable,
            },
            BackendUnavailable unavailable => unavailable,
        };
    }

    private void Withdraw(DatabaseRecord database, bool nameReused)
    {
        if (!nameReused)
        {
            atlas.Queue(database.ProjectId, database.Name, null);
        }
    }

    private async ValueTask<DatabaseRemovalOutcome> ShredAsync(DatabaseRecord database, CancellationToken cancellationToken)
    {
        await databases.EvictAsync(database.Id);
        var archivedOutcome = await continuity.CryptoShredAsync(database.Id, cancellationToken);
        var local = await databases.DeleteAsync(database.Id, cancellationToken);
        var archived = archivedOutcome switch
        {
            ArchiveShredded or ContinuityNotFound => null,
            ContinuityUnavailable error when error.Code == "backup_disabled" => null,
            ContinuityUnavailable error => new BackendUnavailable(error.Message, error.Code),
        };
        return local switch
        {
            Completed when archived is { } failure => failure,
            Completed done => done,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private DatabaseRecord Published(DatabaseRecord database)
    {
        atlas.Queue(database.ProjectId, database.Name, database.PlacedIn(options.Region) ? options.Region : database.Regions.Count > 0 ? database.Regions[0] : null);
        return database;
    }
}
