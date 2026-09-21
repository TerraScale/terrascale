namespace TerraScale.Auth.Scim;

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TerraScale.Auth.Api;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Scim.ScimSupport;

internal sealed class ListUsersEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/scim/{directoryId}/Users", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, HttpRequest request, AuthStore store, CancellationToken ct,
            string? filter, int? count, int? startIndex)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;
        var directory = gate.Directory!;

        var (offset, pageSize) = Paginate(startIndex, count);

        List<User> matched;
        if (filter is null)
        {
            matched = (await ListInstanceUsersAsync(store, directory.InstanceId, ct)).Where(u => !u.Deleted).ToList();
        }
        else if (ParseFilter(filter) is not { } parsed)
        {
            return ScimError(400, "only simple filters of the form 'userName eq \"value\"' or 'externalId eq \"value\"' are supported", "invalidFilter");
        }
        else if (parsed.Attribute == "username")
        {
            var user = await store.FindUserByEmailAsync(directory.InstanceId, SessionService.NormalizeEmail(parsed.Value), ct);
            matched = user is null || user.Deleted ? [] : [user];
        }
        else
        {
            matched = (await ListInstanceUsersAsync(store, directory.InstanceId, ct))
                .Where(u => !u.Deleted && ExternalId(u) == parsed.Value)
                .ToList();
        }

        var ordered = matched.OrderBy(u => u.Id, StringComparer.Ordinal).ToList();
        var resources = ordered.Skip(offset - 1).Take(pageSize).Select(u => UserView(directory.Id, u)).ToList();
        return ScimJson(new ScimListResponse(
            Schemas: [ListResponseSchema],
            TotalResults: ordered.Count,
            StartIndex: offset,
            ItemsPerPage: resources.Count,
            Resources: resources));
    }

    /// <summary>
    /// All users of one instance, walking the shared users collection by the raw
    /// page cursor (the store filters in memory); the walk ends only when the
    /// engine returns an empty page, so foreign-user interleave cannot truncate it.
    /// </summary>
    private static async Task<List<User>> ListInstanceUsersAsync(AuthStore store, string instanceId, CancellationToken ct)
    {
        var users = new List<User>();
        string? after = null;
        while (true)
        {
            var (page, next) = await store.ListUsersAsync(instanceId, 1000, after, ct);
            users.AddRange(page);
            if (next is null)
                return users;
            after = next;
        }
    }

    private static (string Attribute, string Value)? ParseFilter(string filter)
    {
        var match = FilterRegex.Match(filter.Trim());
        return match.Success ? (match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value) : null;
    }

    /// <summary>Only the unescaped simple filter forms: userName/externalId eq "value".</summary>
    private static readonly Regex FilterRegex = new("""^(userName|externalId)\s+eq\s+"([^"]*)"$""", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
