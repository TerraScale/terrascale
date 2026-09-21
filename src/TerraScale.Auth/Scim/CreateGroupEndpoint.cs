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

internal sealed class CreateGroupEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/scim/{directoryId}/Groups", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;
        var directory = gate.Directory!;

        var (body, bodyFailure) = await ReadBodyAsync<ScimGroupBody>(request, ct);
        if (body is null)
            return bodyFailure ?? ScimError(400, "a JSON body is required");
        if (string.IsNullOrWhiteSpace(body.DisplayName))
            return ScimError(400, "displayName is required", "invalidValue");
        if (await ParseMembersAsync(store, directory, body.Members, ct) is not { } memberIds)
            return ScimError(400, "members must reference existing users by their id", "invalidValue");

        var now = Now();
        var group = new ScimGroup
        {
            Id = "grp_" + SecureTokens.RandomToken(20),
            InstanceId = directory.InstanceId,
            DirectoryId = directory.Id,
            DisplayName = body.DisplayName.Trim(),
            MemberUserIds = [.. memberIds],
            CreatedAt = now,
            UpdatedAt = now,
        };
        await new ScimGroups(client, store.Database).CreateAsync(group, ct);
        await AuditAsync(store, directory, "directory.group_provisioned", new() { ["group_id"] = group.Id }, ct);
        return ScimCreated(GroupView(directory.Id, group), $"/scim/{directory.Id}/Groups/{group.Id}");
    }
}
