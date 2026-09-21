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

internal sealed class ImpersonateUserEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/users/{userId}/impersonate", HandleAsync);

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
        if (user.Banned)
            return ApiResults.Forbidden("this user is banned");

        var userAgent = request.Headers.UserAgent.ToString();
        var (session, token) = await sessions.CreateSessionAsync(
            context.Instance.Id,
            user.Id,
            userAgent.Length == 0 ? null : userAgent,
            request.HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct);
        await AuditAsync(store, context, "user.impersonated", new() { ["user_id"] = user.Id, ["api_key_id"] = context.ApiKeyId! }, ct);
        return Results.Json(new { token, expires_at = session.ExpiresAt }, statusCode: 201);
    }
}
