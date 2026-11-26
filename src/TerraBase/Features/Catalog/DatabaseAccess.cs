using TerraBase.Cluster;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Platform;
using TerraBase.Features.Rules;
using TerraBase.Hosting;
using TerraBase.Storage;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Features.Catalog;

internal readonly union DatabaseResolution(ResolvedDatabase, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal readonly union ProjectResolution(ResolvedProject, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal readonly union ProjectAuthorization(Completed, CallerForbidden, ResourceNotFound, BackendUnavailable);

public sealed record ResolvedProject(ProjectRecord Project, Caller Caller);

public sealed record ResolvedDatabase(ProjectRecord Project, DatabaseRecord Database, Caller Caller, bool CanWrite, RulesAuth? EndUser);

internal sealed class DatabaseAccess(
    CatalogStore catalog,
    TenantDatabases databases,
    ClusterOptions cluster,
    StorageEngineOptions storage,
    AntiEntropyService antiEntropy,
    PeerHealthMonitor health,
    TimeProvider time)
{
    private static readonly TimeSpan FirstOpenSyncBudget = TimeSpan.FromSeconds(5);

    private static readonly BackendUnavailable SourceUnsynced =
        new("the database could not be caught up from its home region; retry when the region is reachable", "source_unsynced");

    public static readonly ResourceNotFound UnknownDatabase = new("the database does not exist", "unknown_database");

    private static readonly ResourceNotFound UnknownProject = new("the project does not exist", "unknown_project");

    private static readonly CallerForbidden ServiceDenied = new("the caller holds no grant for this project", "forbidden_caller");

    public async ValueTask<DatabaseResolution> DataAsync(
        Caller caller, string? projectReference, string? databaseReference, CancellationToken cancellationToken)
    {
        if (caller is AnonymousCaller)
        {
            return new CredentialRejected("SQL needs a bearer credential", "unauthorized");
        }

        if (ParseProject(projectReference) is not { } projectId)
        {
            return new InvalidInput("project must be a canonical uppercase ULID", "invalid_project");
        }

        if (databaseReference is null || (!DatabaseNames.IsValid(databaseReference) && !IsId(databaseReference)))
        {
            return new InvalidInput("database must be a canonical uppercase ULID or a lowercase name of at most 63 characters", "invalid_database");
        }

        if (Foreign(caller, projectId) is { } refusal)
        {
            return refusal;
        }

        if (PlatformCatalog.IsPlatformProject(projectId) && IsCustomer(caller))
        {
            return UnknownDatabase;
        }

        if (caller is ServiceCaller service && !ServiceAllows(service, projectId, Scopes.SqlRead))
        {
            return ServiceDenied;
        }

        var outcome = await catalog.GetProjectAsync(projectId, cancellationToken);
        return outcome switch
        {
            ProjectRecord project => await DataInProjectAsync(caller, project, databaseReference, cancellationToken),
            ResourceNotFound => UnknownDatabase,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DatabaseResolution> DataByIdAsync(
        Caller caller, Ulid databaseId, CancellationToken cancellationToken)
    {
        var outcome = await catalog.GetDatabaseAsync(databaseId, cancellationToken);
        return outcome switch
        {
            DatabaseRecord database => await DataAsync(caller, database.ProjectId.ToString(), database.Id.ToString(), cancellationToken),
            ResourceNotFound => UnknownDatabase,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ProjectResolution> ProjectAsync(
        Caller caller, string? projectReference, string scope, CancellationToken cancellationToken)
    {
        if (caller is AnonymousCaller)
        {
            return new CredentialRejected("database management needs a bearer credential", "unauthorized");
        }

        if (ParseProject(projectReference) is not { } projectId)
        {
            return new InvalidInput("project must be a canonical uppercase ULID", "invalid_project");
        }

        if (Foreign(caller, projectId) is { } refusal)
        {
            return refusal;
        }

        if (PlatformCatalog.IsPlatformProject(projectId) && IsCustomer(caller))
        {
            return UnknownProject;
        }

        if (caller is ServiceCaller service && !ServiceAllows(service, projectId, scope))
        {
            return ServiceDenied;
        }

        var outcome = await catalog.GetProjectAsync(projectId, cancellationToken);
        return outcome switch
        {
            ProjectRecord project => await AuthorizeProjectAsync(caller, project, null, scope, cancellationToken) switch
            {
                Completed => new ResolvedProject(project, caller),
                CallerForbidden forbidden => forbidden,
                ResourceNotFound => UnknownProject,
                BackendUnavailable unavailable => unavailable,
            },
            ResourceNotFound => await EnrollProjectAsync(caller, projectId, scope, cancellationToken),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ProjectResolution> EnrollProjectAsync(
        Caller caller, Ulid projectId, string scope, CancellationToken cancellationToken)
    {
        if (caller is not ProjectCaller { OrganizationId: { } organizationId } token || token.ProjectId != projectId
            || PlatformCatalog.IsPlatformProject(projectId))
        {
            return UnknownProject;
        }

        if (!token.Scopes.Contains(scope))
        {
            return MissingScope(scope);
        }

        var outcome = await catalog.EnrollProjectAsync(projectId, organizationId, time.GetUtcNow(), cancellationToken);
        return outcome switch
        {
            ProjectRecord enrolled => new ResolvedProject(enrolled, caller),
            ResourceNotFound => UnknownProject,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DatabaseResolution> ManagedAsync(
        Caller caller, string? projectReference, string? databaseReference, string scope, CancellationToken cancellationToken)
    {
        var outcome = await ProjectAsync(caller, projectReference, scope, cancellationToken);
        return outcome switch
        {
            ResolvedProject when databaseReference is null => UnknownDatabase,
            ResolvedProject project => await catalog.FindDatabaseAsync(project.Project.Id, databaseReference, cancellationToken) switch
            {
                DatabaseRecord database when Reaches(caller, database) => new ResolvedDatabase(project.Project, database, caller, CanWrite(caller), EndUser(caller)),
                DatabaseRecord or ResourceNotFound => UnknownDatabase,
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ProjectResolution> CreatorAsync(
        Caller caller, string? projectReference, string? databaseName, CancellationToken cancellationToken)
    {
        var outcome = await ProjectAsync(caller, projectReference, Scopes.DatabasesWrite, cancellationToken);
        return outcome switch
        {
            ResolvedProject when !MayCreate(caller, databaseName) => NarrowedCreate,
            ResolvedProject project => project,
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<TenantReadyOutcome> CaughtUpAsync(DatabaseRecord database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var home = database.Regions.Count > 0 ? database.Regions[0] : cluster.Region;
        PeerNodeInfo[] homePeers = [.. cluster.Peers.Where(peer => string.Equals(peer.Region, home, StringComparison.Ordinal))];
        if (!homePeers.Any(peer => health.IsReachable(peer.NodeId)))
        {
            return string.Equals(home, cluster.Region, StringComparison.Ordinal) ? await LocalHomeAsync(database, cancellationToken) : SourceUnsynced;
        }

        var outcome = await databases.OpenOrCreateAsync(database.Id, database.KeyId, database.Strict, cancellationToken);
        return outcome switch
        {
            ISqlDatabaseSession session => await CaughtUpFromPeersAsync(database, session, homePeers, cancellationToken),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<TenantReadyOutcome> LocalHomeAsync(DatabaseRecord database, CancellationToken cancellationToken)
    {
        var opened = await databases.OpenAsync(database.Id, database.KeyId, cancellationToken);
        return opened switch
        {
            ISqlDatabaseSession session => await ProvenAsync(database, session, cancellationToken),
            ResourceNotFound => SourceUnsynced,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<TenantReadyOutcome> ProvenAsync(DatabaseRecord database, ISqlDatabaseSession session, CancellationToken cancellationToken)
    {
        PeerNodeInfo[] placedPeers = [.. cluster.Peers.Where(peer => database.PlacedIn(peer.Region))];
        var report = await antiEntropy.SyncSessionAsync(session, placedPeers, FirstOpenSyncBudget, cancellationToken);
        return !session.CurrentBookmark.IsEmpty || (report.Peers.Count == placedPeers.Length && report.Peers.All(static peer => peer.Complete))
            ? new TenantReadyOutcome(session)
            : new TenantReadyOutcome(SourceUnsynced);
    }

    private async ValueTask<TenantReadyOutcome> CaughtUpFromPeersAsync(
        DatabaseRecord database, ISqlDatabaseSession session, PeerNodeInfo[] homePeers, CancellationToken cancellationToken)
    {
        PeerNodeInfo[] placedPeers = [.. cluster.Peers.Where(peer => database.PlacedIn(peer.Region) || homePeers.Contains(peer))];
        var report = await antiEntropy.SyncSessionAsync(session, placedPeers, FirstOpenSyncBudget, cancellationToken);
        var homeAnswered = report.Peers.Any(peer => peer is { Complete: true, HeadEmpty: false } && homePeers.Any(home => string.Equals(home.NodeId, peer.NodeId, StringComparison.Ordinal)));
        var everyPlacedPeer = report.Peers.Count == placedPeers.Length && report.Peers.All(static peer => peer.Complete);
        var localHomeCopy = string.Equals(database.Regions.Count > 0 ? database.Regions[0] : cluster.Region, cluster.Region, StringComparison.Ordinal)
            && !session.CurrentBookmark.IsEmpty;
        return homeAnswered || everyPlacedPeer || localHomeCopy ? new TenantReadyOutcome(session) : new TenantReadyOutcome(SourceUnsynced);
    }

    public async ValueTask<TenantReadyOutcome> OpenAsync(DatabaseRecord database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var existed = File.Exists(LocalPath(database));
        var outcome = await databases.OpenOrCreateAsync(database.Id, database.KeyId, database.Strict, cancellationToken);
        var opened = outcome switch
        {
            ISqlDatabaseSession => true,
            BackendUnavailable => false,
        };
        if (!existed && opened && cluster.Peers.Count > 0)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(FirstOpenSyncBudget);
            try
            {
                _ = await antiEntropy.SyncAsync(database.Id, budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return outcome;
    }

    public async ValueTask<ProjectAuthorization> AuthorizeProjectAsync(
        Caller caller, ProjectRecord project, DatabaseRecord? database, string scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (PlatformCatalog.IsPlatformProject(project.Id) && IsCustomer(caller))
        {
            return UnknownProject;
        }

        return caller switch
        {
            AnonymousCaller => UnknownProject,
            PeerCaller => new Completed(),
            ServiceCaller service => Service(service, project, scope),
            ApiKeyCaller key when !key.Key.Reaches(project, database) => UnknownProject,
            ApiKeyCaller key => key.Key.Grants(scope) ? new Completed() : MissingScope(scope),
            UserCaller user => await catalog.GetOrganizationAsync(project.OrganizationId, cancellationToken) switch
            {
                OrganizationRecord organization when string.Equals(organization.CreatedBy, user.UserId, StringComparison.Ordinal) => new Completed(),
                OrganizationRecord or ResourceNotFound => UnknownProject,
                BackendUnavailable unavailable => unavailable,
            },
            ProjectCaller token when token.ProjectId != project.Id || (token.OrganizationId is { } org && org != project.OrganizationId) => UnknownProject,
            ProjectCaller token => token.Scopes.Contains(scope) ? new Completed() : MissingScope(scope),
            EndUserCaller endUser when endUser.ProjectId != project.Id => UnknownProject,
            EndUserCaller => scope is Scopes.SqlRead or Scopes.SqlWrite ? new Completed() : MissingScope(scope),
        };
    }

    private async ValueTask<DatabaseResolution> DataInProjectAsync(
        Caller caller, ProjectRecord project, string databaseReference, CancellationToken cancellationToken)
    {
        var outcome = await catalog.FindDatabaseAsync(project.Id, databaseReference, cancellationToken);
        return outcome switch
        {
            DatabaseRecord database when Reaches(caller, database) => await AuthorizeProjectAsync(caller, project, database, Scopes.SqlRead, cancellationToken) switch
            {
                Completed => new ResolvedDatabase(project, database, caller, CanWrite(caller, project), EndUser(caller)),
                CallerForbidden forbidden => forbidden,
                ResourceNotFound => UnknownDatabase,
                BackendUnavailable unavailable => unavailable,
            },
            DatabaseRecord or ResourceNotFound => UnknownDatabase,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public static bool Reaches(Caller caller, DatabaseRecord database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return caller switch
        {
            ProjectCaller { Database: { } narrowed } => Matches(narrowed, database),
            EndUserCaller { Database: { } narrowed } => Matches(narrowed, database),
            ApiKeyCaller { Key.DatabaseId: { } narrowed } => narrowed == database.Id,
            AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => true,
        };
    }

    public static bool IsNarrowed(Caller caller) => caller switch
    {
        ProjectCaller { Database: not null } or EndUserCaller { Database: not null } or ApiKeyCaller { Key.DatabaseId: not null } => true,
        AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => false,
    };

    public static bool MayRename(Caller caller, DatabaseRecord database, string? name)
    {
        ArgumentNullException.ThrowIfNull(database);
        return name is null || string.Equals(name, database.Name, StringComparison.Ordinal) || caller switch
        {
            ProjectCaller { Database: { } narrowed } => IsId(narrowed),
            EndUserCaller { Database: { } narrowed } => IsId(narrowed),
            AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => true,
        };
    }

    public static CallerForbidden NarrowedCreate { get; } =
        new("the credential is narrowed to one database and cannot create another", "database_narrowed");

    public static bool MayCreate(Caller caller, string? name) => caller switch
    {
        ProjectCaller { Database: { } narrowed } => !IsId(narrowed) && string.Equals(narrowed, name, StringComparison.Ordinal),
        EndUserCaller { Database: not null } or ApiKeyCaller { Key.DatabaseId: not null } => false,
        AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => true,
    };

    private static bool Matches(string narrowed, DatabaseRecord database) =>
        IsId(narrowed) ? Ulid.Parse(narrowed) == database.Id : string.Equals(narrowed, database.Name, StringComparison.Ordinal);

    private static bool IsCustomer(Caller caller) => caller switch
    {
        ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => true,
        AnonymousCaller or PeerCaller or ServiceCaller => false,
    };

    private string LocalPath(DatabaseRecord database) =>
        Path.Combine(storage.RootDirectory, database.Id.ToString(), string.Concat(database.Id.ToString(), ".db"));

    private static bool CanWrite(Caller caller, ProjectRecord? project = null) => caller switch
    {
        AnonymousCaller => false,
        PeerCaller or UserCaller or EndUserCaller => true,
        ServiceCaller service => service.Grants.Contains("admin") || service.Grants.Contains("platform.write")
            || (project is not null && service.Grants.Contains(string.Concat("data:", project.Id.ToString()))),
        ApiKeyCaller key => key.Key.Grants(Scopes.SqlWrite),
        ProjectCaller token => token.Scopes.Contains(Scopes.SqlWrite),
    };

    private static RulesAuth? EndUser(Caller caller) => caller switch
    {
        EndUserCaller endUser => endUser.Auth,
        AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller => null,
    };

    private static ProjectAuthorization Service(ServiceCaller service, ProjectRecord project, string scope) =>
        ServiceAllows(service, project.Id, scope) ? new Completed() : ServiceDenied;

    private static bool ServiceAllows(ServiceCaller service, Ulid projectId, string scope)
    {
        if (service.Grants.Contains("admin"))
        {
            return true;
        }

        var data = service.Grants.Contains(string.Concat("data:", projectId.ToString()));
        var platform = PlatformCatalog.IsPlatformProject(projectId);
        return scope switch
        {
            Scopes.SqlRead => data || (platform && (service.Grants.Contains("platform.read") || service.Grants.Contains("platform.write"))),
            Scopes.SqlWrite => data || (platform && service.Grants.Contains("platform.write")),
            Scopes.DatabasesRead => service.Grants.Contains("catalog.read") || data,
            _ => false,
        };
    }

    private static CredentialRejected? Foreign(Caller caller, Ulid projectId) => caller switch
    {
        EndUserCaller endUser when endUser.ProjectId != projectId =>
            new CredentialRejected("a project tenant token only reaches its own project's routes", "invalid_token"),
        AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => null,
    };

    private static CallerForbidden MissingScope(string scope) =>
        new(string.Concat("the credential lacks the ", scope, " scope"), "forbidden_scope", new Dictionary<string, string> { ["scope"] = scope });

    private static bool IsId(string reference) => reference.Length == 26 && Ulid.TryParse(reference, out _);

    private static Ulid? ParseProject(string? reference) => reference is { Length: 26 } && Ulid.TryParse(reference, out var id) ? id : null;
}
