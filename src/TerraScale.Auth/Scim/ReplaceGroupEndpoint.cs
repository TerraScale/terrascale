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

internal sealed class ReplaceGroupEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPut("/scim/{directoryId}/Groups/{groupId}", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, string groupId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;
        var directory = gate.Directory!;

        var (body, bodyFailure) = await ReadBodyAsync<ScimGroupBody>(request, ct);
        if (body is null)
            return bodyFailure ?? ScimError(400, "a JSON body is required");

        var groups = new ScimGroups(client, store.Database);
        var group = await FindScimGroupAsync(groups, directory.Id, groupId, ct);
        if (group is null)
            return ScimError(404, $"no group with id '{groupId}'");
        if (string.IsNullOrWhiteSpace(body.DisplayName))
            return ScimError(400, "displayName is required", "invalidValue");
        if (await ParseMembersAsync(store, directory, body.Members, ct) is not { } memberIds)
            return ScimError(400, "members must reference existing users by their id", "invalidValue");

        var updated = group with
        {
            DisplayName = body.DisplayName.Trim(),
            MemberUserIds = [.. memberIds],
            UpdatedAt = Now(),
        };
        await groups.UpsertAsync(updated, ct);
        await AuditAsync(store, directory, "directory.group_updated", new() { ["group_id"] = group.Id }, ct);
        return ScimJson(GroupView(directory.Id, updated));
    }
}
