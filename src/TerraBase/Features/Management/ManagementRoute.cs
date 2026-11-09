using System.Globalization;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

public sealed record NameRequest(string? Name);

public sealed record PlanRequest(string? Plan);

public sealed record EnrollmentRequest(string? Organization);

internal sealed record PageQuery(string? After, int Limit);

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

    public const string PageDescription = "Query: after (exclusive cursor from the previous page's next), limit (1 to 100, default 20). Items are in creation order.";

    private const int MaxDisplayNameLength = 128;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public static RouteIdCheck CheckRouteIds(HttpContext context)
    {
        foreach (var (name, value) in context.Request.RouteValues)
        {
            if (name is not ("databaseName" or "function" or "table")
                && !EntityIds.IsValid(value as string))
            {
                return new Validation(name + " must be a canonical uppercase ULID");
            }
        }

        return new Done();
    }

    public static string Value(HttpContext context, string name) => (string)context.Request.RouteValues[name]!;

    public static ValueTask<GetProjectResult> ProjectAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        management.GetProjectAsync(UserId(context), OrganizationId(context), Value(context, "project"), cancellationToken);

    public static ValueTask<DatabaseTenantResult> DatabaseAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        management.GetDatabaseTenantAsync(UserId(context), OrganizationId(context), Value(context, "project"), Value(context, "database"), cancellationToken);

    public static ValueTask<WritableDatabaseTenantResult> CheckWritableDatabaseAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        management.GetWritableDatabaseTenantAsync(UserId(context), OrganizationId(context), Value(context, "project"), Value(context, "database"), cancellationToken);

    public static ValueTask<WritableProjectResult> CheckWritableProjectAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        management.GetWritableProjectAsync(UserId(context), OrganizationId(context), Value(context, "project"), cancellationToken);

    public static IResult DatabaseNotFound(NotFound error) =>
        ErrorResponse.From(ProjectAccess.Current is not null ? new NotFound(Code: "unknown_route") : error);

    public static string ProjectPath(HttpContext context, Project project) =>
        context.Features.Get<LegacyManagementRoute>() is not null
            ? $"{Organizations}/{OrganizationId(context)}/projects/{project.Id}"
            : $"/v1/projects/{project.Id}";

    public static string DatabasePath(HttpContext context, TenantContext database) =>
        context.Features.Get<LegacyManagementRoute>() is not null
            ? $"{Organizations}/{database.OrganizationId}/projects/{database.ProjectId}/databases/{database.DatabaseId}"
            : $"/v1/projects/{database.ProjectId}/databases/{Value(context, "databaseName")}";

    public static string CreatedDatabasePath(HttpContext context, ManagedDatabase database) =>
        context.Features.Get<LegacyManagementRoute>() is not null
            ? $"{Organizations}/{OrganizationId(context)}/projects/{database.ProjectId}/databases/{database.Id}"
            : $"/v1/projects/{database.ProjectId}/databases/{database.Name}";

    public static string UserId(HttpContext context) => context.User.UserId();

    public static string OrganizationId(HttpContext context) => Value(context, "organization");

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

    public static Valid<string> CheckDatabaseName(NameRequest request) =>
        Engine.DatabaseName.TryParse(request.Name, out var name)
            ? name.Value
            : new Validation("name must be a lowercase database name of at most 63 characters");

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
