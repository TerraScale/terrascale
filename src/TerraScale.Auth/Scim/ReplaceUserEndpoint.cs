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

internal sealed class ReplaceUserEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPut("/scim/{directoryId}/Users/{userId}", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, string userId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;
        var directory = gate.Directory!;

        var (body, bodyFailure) = await ReadBodyAsync<ScimUserBody>(request, ct);
        if (body is null)
            return bodyFailure ?? ScimError(400, "a JSON body is required");

        var user = await FindScimUserAsync(store, directory, userId, ct);
        if (user is null)
            return ScimError(404, $"no user with id '{userId}'");
        if (string.IsNullOrWhiteSpace(body.UserName) || !SessionService.IsValidEmail(body.UserName))
            return ScimError(400, "userName is required and must be a valid email address", "invalidValue");

        // Replace semantics: absent name/externalId clear, absent active keeps the current state.
        var externalId = NormalizeExternalId(body.ExternalId);
        var metadata = new Dictionary<string, object>(user.PrivateMetadata);
        if (externalId is null)
            metadata.Remove("scimExternalId");
        else
            metadata["scimExternalId"] = externalId;
        var updated = user with
        {
            Name = JoinName(body.Name),
            PrivateMetadata = metadata,
            UpdatedAt = Now(),
        };
        if (body.Active is { } active)
            updated = updated with { Banned = !active, BanReason = active ? null : "directory deprovisioned" };

        var (rekeyed, superseded, rekeyFailure) = await WithEmailAsync(store, directory.InstanceId, updated, body.UserName, ct);
        if (rekeyFailure is not null)
            return rekeyFailure;

        var (written, writeFailure) = await WriteScimUserAsync(store, client, directory, rekeyed, superseded is null ? [] : [superseded], ct);
        if (writeFailure is not null)
            return writeFailure;
        await AuditAsync(store, directory, AuditUpdateType(user, written), new() { ["user_id"] = written.Id }, ct);
        return ScimJson(UserView(directory.Id, written));
    }
}
