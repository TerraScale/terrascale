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

internal sealed class DeleteUserEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/scim/{directoryId}/Users/{userId}", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, string userId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;
        var directory = gate.Directory!;

        var user = await FindScimUserAsync(store, directory, userId, ct);
        if (user is null)
            return ScimError(404, $"no user with id '{userId}'");

        // Soft delete through the shared write path: same cleanup as deprovision,
        // plus the Deleted flag that hides the user from SCIM for good.
        var (_, writeFailure) = await WriteScimUserAsync(store, client, directory, user with { Deleted = true, UpdatedAt = Now() }, [], ct);
        if (writeFailure is not null)
            return writeFailure;
        await store.ReleaseQuotaSeatAsync(user, ct);
        await AuditAsync(store, directory, "directory.user_deprovisioned", new() { ["user_id"] = user.Id }, ct);
        return Results.NoContent();
    }
}
