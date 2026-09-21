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

internal sealed class ListUserSessionsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/users/{userId}/sessions", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        var gate = await RequireInstanceAsync(request, slug, resolver, ct);
        if (gate.Failure is { } failure)
            return failure;
        var context = gate.Context!;

        var user = await FindLiveUserAsync(store, context.Instance.Id, userId, ct);
        if (user is null)
            return ApiResults.NotFound("user_not_found", "no user with this id in this instance");

        var sessions = (await store.ListSessionsByUserAsync(user.Id, ct))
            .Where(s => s.InstanceId == context.Instance.Id)
            .Select(SessionView)
            .ToList();
        return Results.Json(new { data = sessions });
    }

    private static Dictionary<string, object?> SessionView(Session session) => new()
    {
        ["id"] = session.Id,
        ["status"] = session.Status,
        ["created_at"] = session.CreatedAt,
        ["expires_at"] = session.ExpiresAt,
        ["last_active_at"] = session.LastActiveAt,
        ["user_agent"] = session.UserAgent,
        ["ip_address"] = session.IpAddress,
    };
}
