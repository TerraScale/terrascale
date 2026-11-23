using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Atlas;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
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
            return new QuotaExceeded("the organization's plan allows no more databases", "database_limit");
        }

        return regions.Check(input.Regions ?? input.ReplicaRegions) switch
        {
            IReadOnlyList<string> placed => await CreatePlacedAsync(project, input.Name, placed, input.AllRegions ?? false, mode, input.Strict ?? false,
                input.TtlSeconds, null, cancellationToken),
            Validation invalid => invalid,
        };
    }

    public async ValueTask<Result<DatabaseRecord, Validation, Forbidden, Conflict, QuotaExceeded, Unavailable>> CreatePlacedAsync(
        ProjectRecord project, string name, IReadOnlyList<string> placed, bool allRegions, ConsistencyMode mode, bool strict, long? ttlSeconds, Ulid? branchedFrom,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var id = Ulid.NewUlid(now);
        return await databases.CreateAsync(id, strict, cancellationToken) switch
        {
            OpenedDatabase opened => await InsertAsync(new DatabaseRecord(id, project.Id, name, placed, allRegions, mode, strict, ControlDatabaseState.Active, opened.KeyId,
                branchedFrom, null, now, ttlSeconds is { } ttl ? now.AddSeconds(ttl) : null, 1, 0), cancellationToken),
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<DatabaseRecord, Validation, Forbidden, Conflict, QuotaExceeded, Unavailable>> InsertAsync(DatabaseRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var inserted = await catalog.InsertDatabaseAsync(record, cancellationToken);
        if (inserted switch { DatabaseRecord => false, Conflict or Unavailable => true })
        {
            await databases.DeleteAsync(record.Id, CancellationToken.None);
        }

        return inserted switch
        {
            DatabaseRecord created => Published(created),
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

        var requested = input.Regions ?? input.ReplicaRegions ?? (input.Region is { } single ? [.. current.Regions, single] : null);
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

        return await SaveAsync(current, current with { ExpiresAt = ttlSeconds is { } ttl ? time.GetUtcNow().AddSeconds(ttl) : null, Version = current.Version + 1 },
            cancellationToken);
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

        var now = time.GetUtcNow();
        var branchId = Ulid.NewUlid(now);
        return await databases.OpenOrCreateAsync(source.Id, source.KeyId, source.Strict, cancellationToken) switch
        {
            ISqlDatabaseSession session => await databases.BranchAsync(session, branchId, at, cancellationToken) switch
            {
                OpenedDatabase opened => Widen(await InsertAsync(new DatabaseRecord(branchId, project.Id, name, source.Regions, source.AllRegions, source.DefaultConsistency,
                    source.Strict, ControlDatabaseState.Active, opened.KeyId, source.Id, null, now, ttlSeconds is { } ttl ? now.AddSeconds(ttl) : null, 1, source.StorageLimit),
                    cancellationToken)),
                NotFound missing => missing,
                Conflict conflict => conflict,
                Unavailable unavailable => unavailable,
            },
            Unavailable unavailable => unavailable,
        };
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

    private static Result<DatabaseRecord, Validation, Forbidden, NotFound, Conflict, QuotaExceeded, Unavailable> Widen(
        Result<DatabaseRecord, Validation, Forbidden, Conflict, QuotaExceeded, Unavailable> result) => result switch
        {
            DatabaseRecord record => record,
            Validation invalid => invalid,
            Forbidden forbidden => forbidden,
            Conflict conflict => conflict,
            QuotaExceeded quota => quota,
            Unavailable unavailable => unavailable,
        };

    private DatabaseRecord Changed(DatabaseRecord current, DatabaseInput input, IReadOnlyList<string> placed, ConsistencyMode mode) => current with
    {
        Name = input.Name ?? current.Name,
        PreviousName = input.Name is { } renamed && renamed != current.Name ? current.Name : current.PreviousName,
        Regions = placed,
        AllRegions = input.AllRegions ?? current.AllRegions,
        DefaultConsistency = mode,
        ExpiresAt = input.TtlSeconds is { } ttl ? time.GetUtcNow().AddSeconds(ttl) : current.ExpiresAt,
        Version = current.Version + 1,
    };

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
            Done => archived is null ? new Done() : archived,
            Unavailable unavailable => unavailable,
        };
    }

    private DatabaseRecord Published(DatabaseRecord database)
    {
        atlas.Queue(database.ProjectId, database.Name, database.PlacedIn(options.Region) ? options.Region : database.Regions.Count > 0 ? database.Regions[0] : null);
        return database;
    }
}
