using System.Globalization;
using System.Net.Mail;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>A body that carries only a name: create, rename and branch.</summary>
/// <param name="Name">Organization or project display name, or database name.</param>
public sealed record NameRequest(string? Name);

/// <summary>The plan to set.</summary>
/// <param name="Plan"><c>hobby</c>, <c>pro</c> or <c>enterprise</c>.</param>
public sealed record PlanRequest(string? Plan);

/// <summary>A database the acting user may change, or why not.</summary>
public readonly union WritableDatabase(TenantContext, NotFound, Forbidden);

/// <summary>A project the acting user may change, or why not.</summary>
public readonly union WritableProject(Project, NotFound, Forbidden);

/// <summary>A page request.</summary>
/// <param name="After">Exclusive cursor from the previous page's <c>next</c>.</param>
/// <param name="Limit">Page size, 1 to 100.</param>
internal sealed record PageQuery(string? After, int Limit);

/// <summary>Routes, validation and error mapping shared by the management endpoints.</summary>
internal static class ManagementRoute
{
    public const string Tag = "Management";
    public const string Organizations = "/v1/management/organizations";
    public const string Organization = Organizations + "/{organization}";
    public const string Projects = Organization + "/projects";
    public const string Project = Projects + "/{project}";
    public const string Databases = Project + "/databases";
    public const string Database = Databases + "/{database}";
    public const string CurrentOrganization = "/v1/management/current-organization";
    public const string AccessTag = "Access";
    public const string Members = Organization + "/members";
    public const string Member = Members + "/{user}";
    public const string Leave = Organization + "/leave";
    public const string Invitations = Organization + "/invitations";
    public const string AcceptInvitation = "/v1/management/invitations/accept";
    public const string LookupInvitation = "/v1/management/invitations/lookup";
    public const string Commits = "/v1/management/commits";
    public const string Versions = "/v1/management/versions";
    public const string Documents = "/v1/management/documents/{collection}";
    public const string OwnershipTransfer = Organization + "/ownership-transfer";
    public const string AuditRetention = Organization + "/audit-retention";
    public const string AuditEvents = Organization + "/audit-events";

    public const string IdentityDescription =
        "Authorization: Bearer <ts-auth access token>; its sub is the acting user. On the loopback administrative listener only, the "
        + "management service credential instead, with X-TerraBase-User-Id naming the acting user. Organizations the user is not a member of answer 404.";

    public const string ApiKeys = Project + "/api-keys";
    public const string ApiKey = ApiKeys + "/{key}";
    public const string Sql = Database + "/sql";
    public const string SavedQueries = Project + "/saved-queries";
    public const string SavedQuery = SavedQueries + "/{query}";
    public const string Indexes = Database + "/indexes";
    public const string Index = Indexes + "/{index}";
    public const string IndexRecommendations = Database + "/index-recommendations";
    public const string PageDescription = "Query: after (exclusive cursor from the previous page's next), limit (1 to 100, default 20). Items are in creation order.";

    private const int MaxDisplayNameLength = 128;
    private const int MaxUserIdLength = 256;
    private const int MaxEmailLength = 254;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public static string Value(HttpContext context, string name) => (string)context.Request.RouteValues[name]!;

    /// <summary>The project addressed by <c>{organization}</c> and <c>{project}</c>; <see langword="null"/> when it does not exist or the acting user is not a member of its organization.</summary>
    public static ValueTask<Project?> ProjectAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        management.GetProjectAsync(UserId(context), OrganizationId(context), Value(context, "project"), cancellationToken);

    /// <summary>
    /// The tenant of the database addressed by <c>{organization}</c>, <c>{project}</c> and <c>{database}</c>, resolved with
    /// the membership check; <see langword="null"/> when it does not exist or the acting user is not a member of its organization.
    /// </summary>
    public static ValueTask<TenantContext?> DatabaseAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        management.GetDatabaseTenantAsync(UserId(context), OrganizationId(context), Value(context, "project"), Value(context, "database"), cancellationToken);

    /// <summary>As <see cref="DatabaseAsync"/> for a change: a viewer, which is read-only, gets <see cref="ManagementError.Forbidden"/>.</summary>
    public static ValueTask<ManagementResult<TenantContext>> WritableDatabaseAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        management.GetWritableDatabaseTenantAsync(UserId(context), OrganizationId(context), Value(context, "project"), Value(context, "database"), cancellationToken);

    /// <summary>As <see cref="WritableDatabaseAsync"/>, as a union: a viewer gets <see cref="ManagementErrors.RoleTooLow"/>.</summary>
    public static async ValueTask<WritableDatabase> CheckWritableDatabaseAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        await WritableDatabaseAsync(context, management, cancellationToken).ConfigureAwait(false) switch
        {
            { Value: { } database } => database,
            { Error: ManagementError.Forbidden } => ManagementErrors.RoleTooLow,
            _ => new NotFound(),
        };

    /// <summary>As <see cref="WritableProjectAsync"/>, as a union: a viewer gets <see cref="ManagementErrors.RoleTooLow"/>.</summary>
    public static async ValueTask<WritableProject> CheckWritableProjectAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        await WritableProjectAsync(context, management, cancellationToken).ConfigureAwait(false) switch
        {
            { Value: { } project } => project,
            { Error: ManagementError.Forbidden } => ManagementErrors.RoleTooLow,
            _ => new NotFound(),
        };

    /// <summary>As <see cref="ProjectAsync"/> for a change: a viewer, which is read-only, gets <see cref="ManagementError.Forbidden"/>.</summary>
    public static ValueTask<ManagementResult<Project>> WritableProjectAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        management.GetWritableProjectAsync(UserId(context), OrganizationId(context), Value(context, "project"), cancellationToken);

    /// <summary>The canonical path of a project, from route values: request paths may differ in case or end in a slash.</summary>
    public static string ProjectPath(HttpContext context, Project project) => $"{Organizations}/{OrganizationId(context)}/projects/{project.Id}";

    /// <summary>The canonical path of a database, from its tenant (see <see cref="ProjectPath"/>).</summary>
    public static string DatabasePath(TenantContext database) =>
        $"{Organizations}/{database.OrganizationId}/projects/{database.ProjectId}/databases/{database.DatabaseId}";

    /// <summary>
    /// The acting user: the <c>sub</c> of the caller's ts-auth access token, or the user named with the management service
    /// credential on the administrative listener. A request header alone never names it.
    /// </summary>
    public static string UserId(HttpContext context) => context.User.UserId();

    /// <summary>The route's organization. Membership, checked by the service, decides whether the user reaches it.</summary>
    public static string OrganizationId(HttpContext context) => Value(context, "organization");

    /// <summary>The member addressed by the <c>{user}</c> route value.</summary>
    public static string MemberId(HttpContext context) => ValidUserId(Value(context, "user"), "user");

    /// <summary>Validates a user id from a route or body.</summary>
    public static string ValidUserId(string? user, string field) => OrThrow(CheckUserId(user, field));

    /// <summary>A user id from a route or body, or the rule it breaks.</summary>
    public static Valid<string> CheckUserId(string? user, string field) =>
        string.IsNullOrEmpty(user) || user.Length > MaxUserIdLength || user.Any(char.IsControl)
            ? new Validation($"{field} must be 1 to 256 characters without control characters")
            : user;

    /// <summary>Validates one email address and returns it trimmed.</summary>
    public static string Email(string? email) => OrThrow(CheckEmail(email));

    /// <summary>One email address, trimmed, or the rule it breaks.</summary>
    public static Valid<string> CheckEmail(string? email)
    {
        var trimmed = email?.Trim();
        return trimmed is { Length: <= MaxEmailLength } && MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed
            ? trimmed
            : new Validation("email must be one address of at most 254 characters");
    }

    // Callers not converted to unions yet (docs/error-handling.md) still throw; BadRequests answers the same 400.
    private static string OrThrow(Valid<string> value) => value switch
    {
        string valid => valid,
        Validation error => throw new BadHttpRequestException(error.Message),
    };

    private static PageQuery OrThrow(Valid<PageQuery> value) => value switch
    {
        PageQuery valid => valid,
        Validation error => throw new BadHttpRequestException(error.Message),
    };

    /// <summary>Validates a role. The enum converter also reads numbers, so undefined values are rejected here.</summary>
    public static OrganizationRole Role(OrganizationRole? role) =>
        role is { } value && Enum.IsDefined(value)
            ? value
            : throw new BadHttpRequestException("role must be owner, admin, member or viewer");

    /// <summary>Validates an organization or project name and returns it trimmed.</summary>
    public static string DisplayName(NameRequest request) => OrThrow(CheckDisplayName(request));

    /// <summary>An organization, project or key name, trimmed, or the rule it breaks.</summary>
    public static Valid<string> CheckDisplayName(NameRequest request)
    {
        var name = request.Name?.Trim();
        return string.IsNullOrEmpty(name) || name.Length > MaxDisplayNameLength || name.Any(char.IsControl) || !name.Any(char.IsLetterOrDigit)
            ? new Validation("name must be 1 to 128 characters, include a letter or digit and have no control characters")
            : name;
    }

    public static PlanTier Plan(PlanRequest request) => request.Plan switch
    {
        "hobby" => PlanTier.Hobby,
        "pro" => PlanTier.Pro,
        "enterprise" => PlanTier.Enterprise,
        _ => throw new BadHttpRequestException("plan must be hobby, pro or enterprise"),
    };

    public static string DatabaseName(NameRequest request) => OrThrow(CheckDatabaseName(request));

    /// <summary>A database name, or the rule it breaks.</summary>
    public static Valid<string> CheckDatabaseName(NameRequest request) =>
        Engine.DatabaseName.TryParse(request.Name, out var name)
            ? name.Value
            : new Validation("name must be a lowercase database name of at most 63 characters");

    public static (string? After, int Limit) Page(HttpContext context)
    {
        var page = OrThrow(CheckPage(context));
        return (page.After, page.Limit);
    }

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

    public static IResult Error(ManagementError error) => error switch
    {
        ManagementError.NotFound => TypedResults.NotFound(),
        ManagementError.NameTaken => TypedResults.Conflict("name is already taken"),
        ManagementError.NotEmpty => TypedResults.Conflict("delete what it contains first"),
        ManagementError.Forbidden => TypedResults.Json("your role in the organization does not allow this", statusCode: StatusCodes.Status403Forbidden),
        ManagementError.LastOwner => TypedResults.Conflict("the organization must keep an owner"),
        ManagementError.AlreadyMember => TypedResults.Conflict("the user is already a member"),
        ManagementError.InvitationPending => TypedResults.Conflict("an invitation for this email is pending"),
        ManagementError.AlreadyOwner => TypedResults.Conflict("the member is already an owner"),
        ManagementError.Expired => TypedResults.Conflict("the invitation expired"),
        ManagementError.IdTaken => TypedResults.Conflict("the id is already taken"),
        ManagementError.InvitationClosed => TypedResults.Conflict("the invitation is no longer pending"),
        ManagementError.TokenTaken => TypedResults.Conflict("another invitation has this token"),
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    public static IResult Ok<T>(ManagementResult<T> result) => result.Error == ManagementError.None ? TypedResults.Ok(result.Value) : Error(result.Error);

    public static IResult NoContent(ManagementError error) => error == ManagementError.None ? TypedResults.NoContent() : Error(error);
}
