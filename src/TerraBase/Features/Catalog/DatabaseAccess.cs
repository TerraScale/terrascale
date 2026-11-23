using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Platform;
using TerraBase.Features.Rules;

namespace TerraBase.Features.Catalog;

public sealed record ResolvedProject(ProjectRecord Project, Caller Caller);

public sealed record ResolvedDatabase(ProjectRecord Project, DatabaseRecord Database, Caller Caller, bool CanWrite, RulesAuth? EndUser);

internal sealed class DatabaseAccess(CatalogStore catalog, TenantDatabases databases, TimeProvider time)
{
    public static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");

    private static readonly NotFound UnknownProject = new("the project does not exist", "unknown_project");

    private static readonly Forbidden ServiceDenied = new("the caller holds no grant for this project", "forbidden_caller");

    public async ValueTask<Result<ResolvedDatabase, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> DataAsync(
        Caller caller, string? projectReference, string? databaseReference, CancellationToken cancellationToken)
    {
        if (caller is AnonymousCaller)
        {
            return new Unauthorized("SQL needs a bearer credential", "unauthorized");
        }

        if (ParseProject(projectReference) is not { } projectId)
        {
            return new Validation("project must be a canonical uppercase ULID", "invalid_project");
        }

        if (databaseReference is null || (!DatabaseNames.IsValid(databaseReference) && !IsId(databaseReference)))
        {
            return new Validation("database must be a canonical uppercase ULID or a lowercase name of at most 63 characters", "invalid_database");
        }

        if (Foreign(caller, projectId) is { } refusal)
        {
            return refusal;
        }

        if (caller is ServiceCaller service && !ServiceAllows(service, projectId, Scopes.SqlRead))
        {
            return ServiceDenied;
        }

        return await catalog.GetProjectAsync(projectId, cancellationToken) switch
        {
            ProjectRecord project => await DataInProjectAsync(caller, project, databaseReference, cancellationToken),
            NotFound => UnknownDatabase,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<ResolvedDatabase, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> DataByIdAsync(
        Caller caller, Ulid databaseId, CancellationToken cancellationToken) =>
        await catalog.GetDatabaseAsync(databaseId, cancellationToken) switch
        {
            DatabaseRecord database => await DataAsync(caller, database.ProjectId.ToString(), database.Id.ToString(), cancellationToken),
            NotFound => UnknownDatabase,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<ResolvedProject, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> ProjectAsync(
        Caller caller, string? projectReference, string scope, CancellationToken cancellationToken)
    {
        if (caller is AnonymousCaller)
        {
            return new Unauthorized("database management needs a bearer credential", "unauthorized");
        }

        if (ParseProject(projectReference) is not { } projectId)
        {
            return new Validation("project must be a canonical uppercase ULID", "invalid_project");
        }

        if (Foreign(caller, projectId) is { } refusal)
        {
            return refusal;
        }

        if (caller is ServiceCaller service && !ServiceAllows(service, projectId, scope))
        {
            return ServiceDenied;
        }

        return await catalog.GetProjectAsync(projectId, cancellationToken) switch
        {
            ProjectRecord project => await AuthorizeProjectAsync(caller, project, null, scope, cancellationToken) switch
            {
                Done => new ResolvedProject(project, caller),
                Forbidden forbidden => forbidden,
                NotFound => UnknownProject,
                Unavailable unavailable => unavailable,
            },
            NotFound => await EnrollProjectAsync(caller, projectId, scope, cancellationToken),
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<ResolvedProject, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> EnrollProjectAsync(
        Caller caller, Ulid projectId, string scope, CancellationToken cancellationToken)
    {
        if (caller is not ProjectCaller { OrganizationId: { } organizationId } token || token.ProjectId != projectId
            || PlatformCatalog.IsPlatformProject(projectId) || organizationId == PlatformCatalog.OrganizationId)
        {
            return UnknownProject;
        }

        if (!token.Scopes.Contains(scope))
        {
            return MissingScope(scope);
        }

        return await catalog.EnrollProjectAsync(projectId, organizationId, time.GetUtcNow(), cancellationToken) switch
        {
            ProjectRecord enrolled => new ResolvedProject(enrolled, caller),
            NotFound => UnknownProject,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<ResolvedDatabase, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> ManagedAsync(
        Caller caller, string? projectReference, string? databaseReference, string scope, CancellationToken cancellationToken) =>
        await ProjectAsync(caller, projectReference, scope, cancellationToken) switch
        {
            ResolvedProject when databaseReference is null => UnknownDatabase,
            ResolvedProject project => await catalog.FindDatabaseAsync(project.Project.Id, databaseReference, cancellationToken) switch
            {
                DatabaseRecord database when Reaches(caller, database) => new ResolvedDatabase(project.Project, database, caller, CanWrite(caller), EndUser(caller)),
                DatabaseRecord or NotFound => UnknownDatabase,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    public ValueTask<Result<ISqlDatabaseSession, Unavailable>> OpenAsync(DatabaseRecord database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return databases.OpenOrCreateAsync(database.Id, database.KeyId, database.Strict, cancellationToken);
    }

    public async ValueTask<Result<Done, Forbidden, NotFound, Unavailable>> AuthorizeProjectAsync(
        Caller caller, ProjectRecord project, DatabaseRecord? database, string scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        return caller switch
        {
            AnonymousCaller => UnknownProject,
            PeerCaller => new Done(),
            ServiceCaller service => Service(service, project, scope),
            ApiKeyCaller key when !key.Key.Reaches(project, database) => UnknownProject,
            ApiKeyCaller key => key.Key.Grants(scope) ? new Done() : MissingScope(scope),
            UserCaller user => await catalog.GetOrganizationAsync(project.OrganizationId, cancellationToken) switch
            {
                OrganizationRecord organization when string.Equals(organization.CreatedBy, user.UserId, StringComparison.Ordinal) => new Done(),
                OrganizationRecord or NotFound => UnknownProject,
                Unavailable unavailable => unavailable,
            },
            ProjectCaller token when token.ProjectId != project.Id || (token.OrganizationId is { } org && org != project.OrganizationId) => UnknownProject,
            ProjectCaller token => token.Scopes.Contains(scope) ? new Done() : MissingScope(scope),
            EndUserCaller endUser when endUser.ProjectId != project.Id => UnknownProject,
            EndUserCaller => scope is Scopes.SqlRead or Scopes.SqlWrite ? new Done() : MissingScope(scope),
        };
    }

    private async ValueTask<Result<ResolvedDatabase, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> DataInProjectAsync(
        Caller caller, ProjectRecord project, string databaseReference, CancellationToken cancellationToken) =>
        await catalog.FindDatabaseAsync(project.Id, databaseReference, cancellationToken) switch
        {
            DatabaseRecord database when Reaches(caller, database) => await AuthorizeProjectAsync(caller, project, database, Scopes.SqlRead, cancellationToken) switch
            {
                Done => new ResolvedDatabase(project, database, caller, CanWrite(caller, project), EndUser(caller)),
                Forbidden forbidden => forbidden,
                NotFound => UnknownDatabase,
                Unavailable unavailable => unavailable,
            },
            DatabaseRecord or NotFound => UnknownDatabase,
            Unavailable unavailable => unavailable,
        };

    private static bool Reaches(Caller caller, DatabaseRecord database) => caller switch
    {
        ProjectCaller { Database: { } narrowed } => Matches(narrowed, database),
        EndUserCaller { Database: { } narrowed } => Matches(narrowed, database),
        ApiKeyCaller { Key.DatabaseId: { } narrowed } => narrowed == database.Id,
        AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => true,
    };

    private static bool Matches(string narrowed, DatabaseRecord database) =>
        string.Equals(narrowed, database.Id.ToString(), StringComparison.Ordinal) || string.Equals(narrowed, database.Name, StringComparison.Ordinal);

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

    private static Result<Done, Forbidden, NotFound, Unavailable> Service(ServiceCaller service, ProjectRecord project, string scope) =>
        ServiceAllows(service, project.Id, scope) ? new Done() : ServiceDenied;

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

    private static Unauthorized? Foreign(Caller caller, Ulid projectId) => caller switch
    {
        EndUserCaller endUser when endUser.ProjectId != projectId =>
            new Unauthorized("a project tenant token only reaches its own project's routes", "invalid_token"),
        AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => null,
    };

    private static Forbidden MissingScope(string scope) =>
        new(string.Concat("the credential lacks the ", scope, " scope"), "forbidden_scope", new Dictionary<string, string> { ["scope"] = scope });

    private static bool IsId(string reference) => reference.Length == 26 && Ulid.TryParse(reference, out _);

    private static Ulid? ParseProject(string? reference) => reference is { Length: 26 } && Ulid.TryParse(reference, out var id) ? id : null;
}
