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

internal sealed class RevokeSessionEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/sessions/{sessionId}/revoke", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, string sessionId, HttpRequest request, InstanceResolver resolver, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var gate = await RequireInstanceAsync(request, slug, resolver, ct);
        if (gate.Failure is { } failure)
            return failure;
        var context = gate.Context!;

        var session = await store.GetSessionAsync(sessionId, ct);
        if (session is null || session.InstanceId != context.Instance.Id)
            return ApiResults.NotFound("session_not_found", "no session with this id in this instance");

        await sessions.RevokeAsync(context.Instance.Id, sessionId, ct);
        return Results.NoContent();
    }
}
