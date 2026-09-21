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

internal sealed class DeleteUserEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/users/{userId}", HandleAsync);

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

        // Soft delete: every uniqueness claim the user holds is released so the keys
        // free up for reuse — email, username, each passkey credential, and every
        // membership (SCIM deprovisioning behaves the same way).
        if (user.EmailKey is not null)
            await store.ReleaseUniqueAsync("user-email", user.EmailKey, ct);
        if (user.UsernameKey is not null)
            await store.ReleaseUniqueAsync("user-username", user.UsernameKey, ct);
        foreach (var passkey in await store.ListPasskeysAsync(user.Id, ct))
            await store.DeletePasskeyAsync(passkey.Id, ct);
        foreach (var membership in await store.ListMembershipsByUserAsync(user.Id, ct))
            if (membership.InstanceId == context.Instance.Id)
                await store.DeleteMembershipAsync(membership.Id, ct);
        await store.DeleteUserAuthenticationArtifactsAsync(context.Instance.Id, user.Id, ct);
        await store.ReleaseQuotaSeatAsync(user, ct);
        await store.UpsertUserAsync(user with
        {
            Deleted = true,
            EmailKey = null,
            UsernameKey = null,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        await AuditAsync(store, context, "user.deleted", new() { ["user_id"] = user.Id }, ct);
        return Results.NoContent();
    }
}
