using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Atlas;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Platform;
using TerraBase.Features.Regions;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Features.Management;

internal sealed class ManagementService(
    CatalogStore catalog,
    TenantDatabases databases,
    RegionDirectory regions,
    IBackupContinuity continuity,
    AtlasPublisher atlas,
    TerraBaseOptions options,
    TimeProvider time)
{
    public const int DefaultLimit = 20;

    public const int MaxLimit = 100;

    private const long MinTtlSeconds = 60;

    private static readonly NotFound UnknownOrganization = new("the organization does not exist", "unknown_organization");

    public static Result<int, Validation> Limit(int? limit) =>
        limit is null ? DefaultLimit : limit is >= 1 and <= MaxLimit ? limit.Value : new Validation("limit must be an integer between 1 and 100", "invalid_request");

    public static Result<string, Validation> DisplayName(string? raw)
    {
        var name = raw?.Trim();
        return string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(char.IsControl) || !name.Any(char.IsLetterOrDigit)
            ? new Validation("name must be 1 to 128 characters, include a letter or digit and have no control characters", "invalid_name")
            : name;
    }

    public static Ulid? Id(string? raw) => raw is { Length: 26 } && Ulid.TryParse(raw, out var id) ? id : null;

    public async ValueTask<Result<OrganizationRecord, Unauthorized, NotFound, Unavailable>> OwnedOrganizationAsync(Caller caller, string? organizationId, CancellationToken cancellationToken)
    {
        if (Id(organizationId) is not { } id)
        {
            return UnknownOrganization;
        }

        return await catalog.GetOrganizationAsync(id, cancellationToken) switch
        {
            OrganizationRecord organization => caller switch
            {
                UserCaller user when string.Equals(user.UserId, organization.CreatedBy, StringComparison.Ordinal) => organization,
                PeerCaller => organization,
                ServiceCaller service when service.Grants.Contains("admin") => organization,
                AnonymousCaller => new Unauthorized("management needs a ts-auth access token", "unauthorized"),
                UserCaller or ServiceCaller or ApiKeyCaller or ProjectCaller or EndUserCaller => UnknownOrganization,
            },
            NotFound => UnknownOrganization,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<OrganizationRecord, Validation, Unauthorized, Conflict, Unavailable>> CreateOrganizationAsync(
        Caller caller, string? name, CancellationToken cancellationToken)
    {
        return caller switch
        {
            UserCaller user => await CreateOwnedAsync(user, name, cancellationToken),
            AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or ProjectCaller or EndUserCaller =>
                new Unauthorized("creating an organization needs a ts-auth access token for a user", "unauthorized"),
        };
    }

    private async ValueTask<Result<OrganizationRecord, Validation, Unauthorized, Conflict, Unavailable>> CreateOwnedAsync(
        UserCaller user, string? name, CancellationToken cancellationToken)
    {
        return DisplayName(name) switch
        {
            string checkedName => await catalog.CreateOrganizationAsync(
                new OrganizationRecord(Ulid.NewUlid(time.GetUtcNow()), checkedName, user.UserId, time.GetUtcNow(), Plans.Hobby, false), cancellationToken) switch
            {
                OrganizationRecord created => created,
                Conflict conflict => conflict,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
        };
    }

    public async ValueTask<Result<DatabaseRecord, Validation, Forbidden, Conflict, QuotaExceeded, Unavailable>> CreateDatabaseAsync(
        ProjectRecord project, DatabaseInput input, CancellationToken cancellationToken)
    {
        if (!DatabaseNames.IsValid(input.Name))
        {
            return new Validation("name must be a lowercase database name of at most 63 characters", "invalid_name");
        }

        if (input.TtlSeconds is < MinTtlSeconds)
        {
            return new Validation("ttlSeconds is at least 60", "invalid_ttl");
        }

        var mode = ConsistencyMode.Fast;
        if (input.DefaultConsistency is { } consistency && !ConsistencyHeaders.TryParse(consistency, out mode))
        {
            return new Validation("defaultConsistency is fast or strong", "invalid_consistency");
        }

        if (project.OrganizationId != PlatformCatalog.OrganizationId)
        {
            var limit = await catalog.GetOrganizationAsync(project.OrganizationId, cancellationToken) switch
            {
                OrganizationRecord organization => Plans.DatabaseLimit(project.Plan ?? organization.Plan),
                NotFound or Unavailable => Plans.DatabaseLimit(project.Plan ?? Plans.Hobby),
            };
            var count = await catalog.CountOrganizationDatabasesAsync(project.OrganizationId, cancellationToken) switch
            {
                long value => value,
                Unavailable => 0,
            };
            if (count >= limit)
            {
                return new Conflict("the organization's plan allows no more databases", "database_limit");
            }
        }

        return regions.Check(input.Regions ?? (input.HomeRegion is { } home ? [home, .. input.ReplicaRegions ?? []] : input.ReplicaRegions)) switch
        {
            IReadOnlyList<string> placed => await CreatePlacedAsync(
                Record(project, input.Name, placed, input.AllRegions ?? false, mode, input.Strict ?? false, input.TtlSeconds, null, 0), cancellationToken),
            Validation invalid => invalid,
        };
    }

    public async ValueTask<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>> CopyAsync(
        ProjectRecord project, DatabaseRecord source, string? name, long? ttlSeconds, RestoreTarget target, bool branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!DatabaseNames.IsValid(name))
        {
            return new Validation("name must be a lowercase database name of at most 63 characters", "invalid_name");
        }

        var record = Record(project, name, source.Regions, source.AllRegions, source.DefaultConsistency, source.Strict, ttlSeconds, source.Id, source.StorageLimit);
        return await catalog.InsertDatabaseAsync(record, cancellationToken) switch
        {
            DatabaseRecord reserved => await (branch
                ? continuity.BranchAsync(new BranchRequest(source.Id, reserved.Id, target), cancellationToken)
                : continuity.RestoreAsync(new PointInTimeRestoreRequest(source.Id, reserved.Id, target), cancellationToken)) switch
            {
                RestoreOperation operation => Started(reserved, operation),
                NotFound missing => await DiscardAsync(reserved, missing),
                Validation invalid => await DiscardAsync(reserved, invalid),
                Conflict conflict => await DiscardAsync(reserved, conflict),
                Unavailable unavailable => await DiscardAsync(reserved, unavailable),
            },
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<Done, Unavailable>> DiscardCopyAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        await catalog.GetDatabaseAsync(databaseId, cancellationToken) switch
        {
            DatabaseRecord record => await DeleteDatabaseAsync(record, cancellationToken),
            NotFound => new Done(),
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<Done, Conflict, Unavailable>> CloseProjectAsync(Ulid projectId, Ulid authorityOrganizationId, bool erase, CancellationToken cancellationToken)
    {
        if (PlatformCatalog.IsPlatformProject(projectId))
        {
            return new Conflict("TerraScale's own projects hold its services' databases and cannot be closed", "platform_project_protected");
        }

        var found = await catalog.GetProjectAsync(projectId, cancellationToken);
        return found switch
        {
            ProjectRecord project when project.OrganizationId != authorityOrganizationId =>
                new Conflict("the project belongs to another organization in TerraBase", "binding_conflict"),
            ProjectRecord project => await CloseFoundProjectAsync(project, erase, cancellationToken),
            NotFound => new Done(),
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<Done, Conflict, Unavailable>> CloseFoundProjectAsync(ProjectRecord project, bool erase, CancellationToken cancellationToken)
    {
        if (erase)
        {
            string? after = null;
            while (true)
            {
                var pageOutcome = await catalog.ListDatabasesAsync(project.Id, after, MaxLimit, cancellationToken);
                if (pageOutcome switch { CatalogPage<DatabaseRecord> => null, Unavailable unavailable => unavailable } is { } listFailure)
                {
                    return listFailure;
                }

                var page = pageOutcome switch { CatalogPage<DatabaseRecord> listed => listed, Unavailable => new CatalogPage<DatabaseRecord>([], null) };
                foreach (var database in page.Items)
                {
                    var deleted = await DeleteDatabaseAsync(database, cancellationToken);
                    if (deleted switch { Done => null, Unavailable unavailable => unavailable } is { } deleteFailure)
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
            Done done => done,
            Conflict { Code: "project_not_empty" } => new Conflict("delete the project's databases first", "not_empty"),
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<DatabaseRecord, Validation, Conflict, Unavailable>> UpdateDatabaseAsync(
        DatabaseRecord current, DatabaseInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Name is { } name && !DatabaseNames.IsValid(name))
        {
            return new Validation("name must be a lowercase database name of at most 63 characters", "invalid_name");
        }

        var mode = current.DefaultConsistency;
        if (input.DefaultConsistency is { } consistency && !ConsistencyHeaders.TryParse(consistency, out mode))
        {
            return new Validation("defaultConsistency is fast or strong", "invalid_consistency");
        }

        if (input.TtlSeconds is < MinTtlSeconds)
        {
            return new Validation("ttlSeconds is at least 60", "invalid_ttl");
        }

        if (input.HasRegions)
        {
            return input.HomeRegion is not null || input.ReplicaRegions is not null || input.AllRegions is not null || input.Region is not null
                ? new Validation("send regions or homeRegion, replicaRegions and allRegions, not both")
                : regions.CheckSet(input.Regions) switch
                {
                    IReadOnlyList<string> placed => await SaveAsync(current, Changed(current, input, placed, mode), cancellationToken),
                    Validation invalid => invalid,
                };
        }

        var requested = input.ReplicaRegions ?? (input.Region is { } single ? [.. current.Regions, single] : null);
        return requested is null
            ? await SaveAsync(current, Changed(current, input, current.Regions, mode), cancellationToken)
            : regions.Check(requested) switch
            {
                IReadOnlyList<string> placed => await SaveAsync(current, Changed(current, input, placed, mode), cancellationToken),
                Validation invalid => invalid,
            };
    }

    public async ValueTask<Result<DatabaseRecord, Validation, Conflict, Unavailable>> SetTtlAsync(DatabaseRecord current, long? ttlSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (ttlSeconds is < MinTtlSeconds)
        {
            return new Validation("ttlSeconds is at least 60", "invalid_ttl");
        }

        return await SaveAsync(current, current with { ExpiresAt = ttlSeconds is { } ttl ? time.GetUtcNow().AddSeconds(ttl) : null }, cancellationToken);
    }

    public async ValueTask<Result<Done, Unavailable>> DeleteDatabaseAsync(DatabaseRecord database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await catalog.DeleteDatabaseAsync(database, cancellationToken) switch
        {
            Done => await ShredAsync(database, cancellationToken),
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<DatabaseRecord, Validation, Forbidden, NotFound, Conflict, QuotaExceeded, Unavailable>> BranchAsync(
        ProjectRecord project, DatabaseRecord source, string? name, long? ttlSeconds, CausalBookmark at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!DatabaseNames.IsValid(name))
        {
            return new Validation("name must be a lowercase database name of at most 63 characters", "invalid_name");
        }

        if (ttlSeconds is < MinTtlSeconds)
        {
            return new Validation("ttlSeconds is at least 60", "invalid_ttl");
        }

        var record = Record(project, name, source.Regions, source.AllRegions, source.DefaultConsistency, source.Strict, ttlSeconds, source.Id, source.StorageLimit);
        return await databases.OpenOrCreateAsync(source.Id, source.KeyId, source.Strict, cancellationToken) switch
        {
            ISqlDatabaseSession session => await catalog.InsertDatabaseAsync(record, cancellationToken) switch
            {
                DatabaseRecord reserved => await databases.BranchAsync(session, reserved.Id, reserved.KeyId, at, cancellationToken) switch
                {
                    ISqlDatabaseSession => Published(reserved),
                    NotFound missing => await DiscardAsync(reserved, missing),
                    Conflict conflict => await DiscardAsync(reserved, conflict),
                    Unavailable unavailable => await DiscardAsync(reserved, unavailable),
                },
                Conflict conflict => conflict,
                Unavailable unavailable => unavailable,
            },
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<int> SweepDeletedAsync(CancellationToken cancellationToken)
    {
        var deleted = await catalog.DeletedDatabasesAsync(64, cancellationToken) switch
        {
            IReadOnlyList<DatabaseRecord> records => records,
            Unavailable => [],
        };
        var shredded = 0;
        foreach (var database in deleted)
        {
            shredded += await ShredAsync(database, cancellationToken) switch
            {
                Done => 1,
                Unavailable => 0,
            };
        }

        return shredded;
    }

    public async ValueTask<int> SweepExpiredAsync(CancellationToken cancellationToken)
    {
        var expired = await catalog.ExpiredDatabasesAsync(time.GetUtcNow(), 64, cancellationToken) switch
        {
            IReadOnlyList<DatabaseRecord> records => records,
            Unavailable => [],
        };
        var deleted = 0;
        foreach (var database in expired)
        {
            deleted += await DeleteDatabaseAsync(database, cancellationToken) switch
            {
                Done => 1,
                Unavailable => 0,
            };
        }

        return deleted;
    }

    private async ValueTask<Result<DatabaseRecord, Validation, Forbidden, Conflict, QuotaExceeded, Unavailable>> CreatePlacedAsync(
        DatabaseRecord record, CancellationToken cancellationToken) =>
        await catalog.InsertDatabaseAsync(record, cancellationToken) switch
        {
            DatabaseRecord reserved => await databases.CreateAsync(reserved.Id, reserved.KeyId, reserved.Strict, cancellationToken) switch
            {
                ISqlDatabaseSession => Published(reserved),
                Conflict conflict => await DiscardAsync(reserved, conflict),
                Unavailable unavailable => await DiscardAsync(reserved, unavailable),
            },
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };

    private static bool SamePlacement(DatabaseRecord current, IReadOnlyList<string> placed, bool allRegions) =>
        current.AllRegions == allRegions
        && current.Regions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).SequenceEqual(placed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private DatabaseRecord Record(
        ProjectRecord project, string name, IReadOnlyList<string> placed, bool allRegions, ConsistencyMode mode, bool strict, long? ttlSeconds, Ulid? branchedFrom, long storageLimit)
    {
        var now = time.GetUtcNow();
        return new DatabaseRecord(Ulid.NewUlid(now), project.Id, name, placed, allRegions, mode, strict, ControlDatabaseState.Active, databases.ActiveKeyId, branchedFrom, null, now,
            ttlSeconds is { } ttl ? now.AddSeconds(ttl) : null, 1, storageLimit);
    }

    private RestoreOperation Started(DatabaseRecord reserved, RestoreOperation operation)
    {
        _ = Published(reserved);
        return operation;
    }

    private async ValueTask<TFailure> DiscardAsync<TFailure>(DatabaseRecord reserved, TFailure failure) =>
        await DeleteDatabaseAsync(reserved, CancellationToken.None) switch
        {
            Done or Unavailable => failure,
        };

    private DatabaseRecord Changed(DatabaseRecord current, DatabaseInput input, IReadOnlyList<string> placed, ConsistencyMode mode)
    {
        var allRegions = input.AllRegions ?? current.AllRegions;
        return current with
        {
            Name = input.Name ?? current.Name,
            PreviousName = input.Name is { } renamed && renamed != current.Name ? current.Name : current.PreviousName,
            Regions = placed,
            AllRegions = allRegions,
            DefaultConsistency = mode,
            ExpiresAt = input.TtlSeconds is { } ttl ? time.GetUtcNow().AddSeconds(ttl) : current.ExpiresAt,
            Version = SamePlacement(current, placed, allRegions) ? current.Version : current.Version + 1,
        };
    }

    private async ValueTask<Result<DatabaseRecord, Validation, Conflict, Unavailable>> SaveAsync(DatabaseRecord current, DatabaseRecord updated, CancellationToken cancellationToken)
    {
        var saved = await catalog.UpdateDatabaseAsync(current, updated, cancellationToken) switch
        {
            DatabaseRecord record => (Result<DatabaseRecord, Validation, Conflict, Unavailable>)Published(record),
            NotFound missing => new Conflict(missing.Message ?? "the database no longer exists", "database_changed"),
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
        if (current.Name != updated.Name)
        {
            atlas.Queue(current.ProjectId, current.Name, null);
        }

        return saved;
    }

    private async ValueTask<Result<Done, Unavailable>> ShredAsync(DatabaseRecord database, CancellationToken cancellationToken)
    {
        atlas.Queue(database.ProjectId, database.Name, null);
        var local = await databases.DeleteAsync(database.Id, cancellationToken);
        var archived = await continuity.CryptoShredAsync(database.Id, cancellationToken) switch
        {
            Done or NotFound => null,
            Unavailable error when error.Code == "backup_disabled" => null,
            Unavailable error => error,
        };
        return local switch
        {
            Done when archived is { } failure => failure,
            Done => await catalog.PurgeDatabaseAsync(database.Id, cancellationToken),
            Unavailable unavailable => unavailable,
        };
    }

    private DatabaseRecord Published(DatabaseRecord database)
    {
        atlas.Queue(database.ProjectId, database.Name, database.PlacedIn(options.Region) ? options.Region : database.Regions.Count > 0 ? database.Regions[0] : null);
        return database;
    }
}
