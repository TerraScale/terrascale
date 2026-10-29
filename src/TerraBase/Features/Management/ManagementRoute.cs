using System.Globalization;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>A body that carries only a name: create, rename and branch.</summary>
/// <param name="Name">Organization or project display name, or database name.</param>
public sealed record NameRequest(string? Name);

/// <summary>The plan to set.</summary>
/// <param name="Plan"><c>hobby</c>, <c>pro</c> or <c>enterprise</c>.</param>
public sealed record PlanRequest(string? Plan);

/// <summary>A database the acting user may change, or why not.</summary>
public readonly union WritableDatabase(TenantContext, NotFound, Forbidden, Unavailable);

/// <summary>A project the acting user may change, or why not.</summary>
public readonly union WritableProject(Project, NotFound, Forbidden, Unavailable);

/// <summary>A page request.</summary>
/// <param name="After">Exclusive cursor from the previous page's <c>next</c>.</param>
/// <param name="Limit">Page size, 1 to 100.</param>
internal sealed record PageQuery(string? After, int Limit);

/// <summary>Routes, validation and error mapping shared by the management endpoints.</summary>
internal static class ManagementRoute
{
    internal readonly union RouteIdCheck(Done, Validation);

    public const string Tag = "Management";
    public const string Prefix = "/v1/management";
    public const string Organizations = Prefix + "/organizations";
    public const string Organization = Organizations + "/{organization}";
    public const string Projects = Organization + "/projects";
    public const string Project = Projects + "/{project}";
    public const string Databases = Project + "/databases";
    public const string Database = Databases + "/{database}";
    public const string IdentityDescription =
        "Authorization: Bearer <ts-auth access token>; its sub is the acting user. On the internal listener only, the "
        + "platform operator instead, with X-TerraBase-User-Id naming the acting user. Organizations the user did not create answer 404.";

    public const string Sql = Database + "/sql";
    public const string SavedQueries = Project + "/saved-queries";
    public const string SavedQuery = SavedQueries + "/{query}";
    public const string Indexes = Database + "/indexes";
    public const string Index = Indexes + "/{index}";
    public const string IndexRecommendations = Database + "/index-recommendations";
    public const string PageDescription = "Query: after (exclusive cursor from the previous page's next), limit (1 to 100, default 20). Items are in creation order.";

    private const int MaxDisplayNameLength = 128;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public static RouteIdCheck CheckRouteIds(HttpContext context)
    {
        foreach (var (name, value) in context.Request.RouteValues)
        {
            if (name is not ("index" or "collection" or "databaseName" or "recommendation" or "function")
                && !EntityIds.IsValid(value as string))
            {
                return new Validation(name + " must be a canonical uppercase ULID");
            }
        }

        return new Done();
    }

    public static string Value(HttpContext context, string name) => (string)context.Request.RouteValues[name]!;

    /// <summary>The addressed project, or why it cannot be read.</summary>
    public static async ValueTask<GetProjectResult> ProjectAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        await management.GetProjectAsync(UserId(context), OrganizationId(context), Value(context, "project"), cancellationToken).ConfigureAwait(false) switch
        {
            Project project => project,
            NotFound => new NotFound(),
            Unavailable error => error,
        };

    public static async ValueTask<DatabaseTenantResult> DatabaseAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        await management.GetDatabaseTenantAsync(UserId(context), OrganizationId(context), Value(context, "project"), Value(context, "database"), cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext database => database,
            NotFound => new NotFound(),
            Unavailable error => error,
        };

    public static async ValueTask<WritableDatabase> CheckWritableDatabaseAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        await management.GetWritableDatabaseTenantAsync(UserId(context), OrganizationId(context), Value(context, "project"), Value(context, "database"), cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext database => database,
            NotFound => new NotFound(),
            Forbidden error => error,
            Unavailable error => error,
        };

    public static async ValueTask<WritableProject> CheckWritableProjectAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        await management.GetWritableProjectAsync(UserId(context), OrganizationId(context), Value(context, "project"), cancellationToken).ConfigureAwait(false) switch
        {
            Project project => project,
            NotFound => new NotFound(),
            Forbidden error => error,
            Unavailable error => error,
        };

    public static IResult DatabaseNotFound(NotFound error) =>
        ErrorResponse.From(ProjectAccess.Current is not null ? new NotFound(Code: "unknown_route") : error);

    /// <summary>The canonical path of a project, from route values: request paths may differ in case or end in a slash.</summary>
    public static string ProjectPath(HttpContext context, Project project) =>
        context.Features.Get<LegacyManagementRoute>() is not null
            ? $"{Organizations}/{OrganizationId(context)}/projects/{project.Id}"
            : $"/v1/projects/{project.Id}";

    /// <summary>The canonical path of a database, from its tenant (see <see cref="ProjectPath"/>).</summary>
    public static string DatabasePath(HttpContext context, TenantContext database) =>
        context.Features.Get<LegacyManagementRoute>() is not null
            ? $"{Organizations}/{database.OrganizationId}/projects/{database.ProjectId}/databases/{database.DatabaseId}"
            : $"/v1/projects/{database.ProjectId}/databases/{Value(context, "databaseName")}";

    /// <summary>The path of a database just created or branched by a canonical route or its hidden legacy alias.</summary>
    public static string CreatedDatabasePath(HttpContext context, ManagedDatabase database) =>
        context.Features.Get<LegacyManagementRoute>() is not null
            ? $"{Organizations}/{OrganizationId(context)}/projects/{database.ProjectId}/databases/{database.Id}"
            : $"/v1/projects/{database.ProjectId}/databases/{database.Name}";

    /// <summary>
    /// The acting user: the <c>sub</c> of the caller's ts-auth access token, or the user named with the management service
    /// credential on the administrative listener. A request header alone never names it.
    /// </summary>
    public static string UserId(HttpContext context) => context.User.UserId();

    /// <summary>The route's organization. Membership, checked by the service, decides whether the user reaches it.</summary>
    public static string OrganizationId(HttpContext context) => Value(context, "organization");

    /// <summary>An organization, project or key name, trimmed, or the rule it breaks.</summary>
    public static Valid<string> CheckDisplayName(NameRequest request)
    {
        var name = request.Name?.Trim();
        return string.IsNullOrEmpty(name) || name.Length > MaxDisplayNameLength || name.Any(char.IsControl) || !name.Any(char.IsLetterOrDigit)
            ? new Validation("name must be 1 to 128 characters, include a letter or digit and have no control characters")
            : name;
    }

    public static Valid<PlanTier> CheckPlan(PlanRequest request) => request.Plan switch
    {
        "hobby" => PlanTier.Hobby,
        "starter" => PlanTier.Starter,
        "pro" => PlanTier.Pro,
        "team" => PlanTier.Team,
        "enterprise" => PlanTier.Enterprise,
        _ => new Validation("plan must be hobby, starter, pro, team or enterprise"),
    };

    /// <summary>A database name, or the rule it breaks.</summary>
    public static Valid<string> CheckDatabaseName(NameRequest request) =>
        Engine.DatabaseName.TryParse(request.Name, out var name)
            ? name.Value
            : new Validation("name must be a lowercase database name of at most 63 characters");

    /// <summary>The <c>after</c> and <c>limit</c> query parameters, or the rule they break.</summary>
    public static Valid<PageQuery> CheckPage(HttpContext context)
    {
        var query = context.Request.Query;
        var limit = DefaultLimit;
        if (query.TryGetValue("limit", out var rawLimit)
            && (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaxLimit))
        {
            return new Validation("limit must be an integer between 1 and 100");
        }

        return new PageQuery(query.TryGetValue("after", out var after) ? after.ToString() : null, limit);
    }

}
