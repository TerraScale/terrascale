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

internal sealed class RevokeAllUserSessionsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/users/{userId}/sessions/revoke-all", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var gate = await RequireInstanceAsync(request, slug, resolver, ct);
        if (gate.Failure is { } failure)
            return failure;
        var context = gate.Context!;

        var user = await FindLiveUserAsync(store, context.Instance.Id, userId, ct);
        if (user is null)
            return ApiResults.NotFound("user_not_found", "no user with this id in this instance");

        foreach (var session in await store.ListSessionsByUserAsync(user.Id, ct))
            if (session.InstanceId == context.Instance.Id && session.Status == "active")
                await sessions.RevokeAsync(context.Instance.Id, session.Id, ct);
        return Results.NoContent();
    }
}
