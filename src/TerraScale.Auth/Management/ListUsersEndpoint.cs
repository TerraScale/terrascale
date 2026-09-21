namespace TerraScale.Auth.Management;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Auth.Api;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;
using TerraScale.Endpoints;
using static TerraScale.Auth.Management.ManagementSupport;

internal sealed class ListUsersEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/users", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store,
            CancellationToken ct, int? limit, [FromQuery(Name = "starting_after")] string? startingAfter)
    {
        var gate = await RequireInstanceAsync(request, slug, resolver, ct);
        if (gate.Failure is { } failure)
            return failure;
        var context = gate.Context!;

        if (limit is < 1 or > 100)
            return ApiResults.BadRequest("invalid_request", "limit must be between 1 and 100");

        var page = (await ListInstanceUsersAsync(store, context.Instance.Id, ct))
            .Where(u => !u.Deleted)
            .OrderBy(u => u.Id, StringComparer.Ordinal)
            .Where(u => startingAfter is null || string.CompareOrdinal(u.Id, startingAfter) > 0)
            .Take(limit ?? 20)
            .Select(u => UserView(u, includePrivate: false))
            .ToList();
        return Results.Json(new { data = page });
    }

    /// <summary>
    /// All users of one instance. The engine lists the shared users collection and
    /// the store filters in memory, so the walk advances by the raw page's last
    /// document id and only ends when the engine returns an empty page.
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
}
