using System.Globalization;

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
    public const string CurrentOrganization = "/v1/management/users/{user}/current-organization";
    public const string PageDescription = "Query: after (exclusive id cursor from the previous page's next), limit (1 to 100, default 20). Items are in creation order.";

    private const int MaxDisplayNameLength = 128;
    private const int MaxUserIdLength = 256;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public static string Value(HttpContext context, string name) => (string)context.Request.RouteValues[name]!;

    public static string UserId(HttpContext context)
    {
        var user = Value(context, "user");
        return user.Length > MaxUserIdLength || user.Any(char.IsControl)
            ? throw new BadHttpRequestException("user must be 1 to 256 characters without control characters")
            : user;
    }

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
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    public static IResult Ok<T>(ManagementResult<T> result) => result.Error == ManagementError.None ? TypedResults.Ok(result.Value) : Error(result.Error);

    public static IResult NoContent(ManagementError error) => error == ManagementError.None ? TypedResults.NoContent() : Error(error);
}
