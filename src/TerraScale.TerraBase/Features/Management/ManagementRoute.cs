using System.Globalization;
using System.Net.Mail;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>A body that carries only a name: create, rename and branch.</summary>
/// <param name="Name">Organization or project display name, or database name.</param>
public sealed record NameRequest(string? Name);

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
    public const string OwnershipTransfer = Organization + "/ownership-transfer";
    public const string AuditRetention = Organization + "/audit-retention";
    public const string AuditEvents = Organization + "/audit-events";

    /// <summary>The acting user's id from ts-auth, sent by the dashboard on every request.</summary>
    public const string UserHeader = "X-TerraBase-User-Id";

    /// <summary>The organization the user acts in; required on, and equal to, every <c>/organizations/{organization}/…</c> route.</summary>
    public const string OrganizationHeader = "X-TerraBase-Organization-Id";

    public const string IdentityDescription =
        "Authorization: Bearer <dashboard credential>. " + UserHeader + " names the acting user; " + OrganizationHeader
        + " must equal {organization} on organization routes. Organizations the user is not a member of answer 404.";
    public const string PageDescription = "Query: after (exclusive id cursor from the previous page's next), limit (1 to 100, default 20). Items are in creation order.";

    private const int MaxDisplayNameLength = 128;
    private const int MaxUserIdLength = 256;
    private const int MaxEmailLength = 254;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public static string Value(HttpContext context, string name) => (string)context.Request.RouteValues[name]!;

    public static string UserId(HttpContext context)
    {
        var values = context.Request.Headers[UserHeader];
        var user = values.Count == 1 ? values[0] : null;
        return string.IsNullOrEmpty(user) || user.Length > MaxUserIdLength || user.Any(char.IsControl)
            ? throw new BadHttpRequestException(UserHeader + " must be one value of 1 to 256 characters without control characters")
            : user;
    }

    /// <summary>The route's organization, which must match <see cref="OrganizationHeader"/>.</summary>
    public static string OrganizationId(HttpContext context)
    {
        var organization = Value(context, "organization");
        var values = context.Request.Headers[OrganizationHeader];
        return values.Count == 1 && string.Equals(values[0], organization, StringComparison.Ordinal)
            ? organization
            : throw new BadHttpRequestException(OrganizationHeader + " must be one value equal to the organization in the path");
    }

    /// <summary>The member addressed by the <c>{user}</c> route value.</summary>
    public static string MemberId(HttpContext context) => ValidUserId(Value(context, "user"), "user");

    /// <summary>Validates a user id from a route or body.</summary>
    public static string ValidUserId(string? user, string field) =>
        string.IsNullOrEmpty(user) || user.Length > MaxUserIdLength || user.Any(char.IsControl)
            ? throw new BadHttpRequestException($"{field} must be 1 to 256 characters without control characters")
            : user;

    /// <summary>Validates one email address and returns it trimmed.</summary>
    public static string Email(string? email)
    {
        var trimmed = email?.Trim();
        return trimmed is { Length: <= MaxEmailLength } && MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed
            ? trimmed
            : throw new BadHttpRequestException("email must be one address of at most 254 characters");
    }

    /// <summary>Validates a role. The enum converter also reads numbers, so undefined values are rejected here.</summary>
    public static OrganizationRole Role(OrganizationRole? role) =>
        role is { } value && Enum.IsDefined(value)
            ? value
            : throw new BadHttpRequestException("role must be owner, admin or member");

    /// <summary>Validates an organization or project name and returns it trimmed.</summary>
    public static string DisplayName(NameRequest request)
    {
        var name = request.Name?.Trim();
        return string.IsNullOrEmpty(name) || name.Length > MaxDisplayNameLength || name.Any(char.IsControl) || !name.Any(char.IsLetterOrDigit)
            ? throw new BadHttpRequestException("name must be 1 to 128 characters, include a letter or digit and have no control characters")
            : name;
    }

    public static string DatabaseName(NameRequest request) =>
        Engine.DatabaseName.TryParse(request.Name, out var name)
            ? name.Value
            : throw new BadHttpRequestException("name must be a lowercase database name of at most 63 characters");

    public static (string? After, int Limit) Page(HttpContext context)
    {
        var query = context.Request.Query;
        var limit = DefaultLimit;
        if (query.TryGetValue("limit", out var rawLimit)
            && (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaxLimit))
        {
            throw new BadHttpRequestException("limit must be an integer between 1 and 100");
        }

        return (query.TryGetValue("after", out var after) ? after.ToString() : null, limit);
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
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    public static IResult Ok<T>(ManagementResult<T> result) => result.Error == ManagementError.None ? TypedResults.Ok(result.Value) : Error(result.Error);

    public static IResult NoContent(ManagementError error) => error == ManagementError.None ? TypedResults.NoContent() : Error(error);
}
