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

internal sealed class DeleteGroupEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/scim/{directoryId}/Groups/{groupId}", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, string groupId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;
        var directory = gate.Directory!;

        var groups = new ScimGroups(client, store.Database);
        var group = await FindScimGroupAsync(groups, directory.Id, groupId, ct);
        if (group is null)
            return ScimError(404, $"no group with id '{groupId}'");

        await groups.DeleteAsync(group.Id, ct);
        await AuditAsync(store, directory, "directory.group_deprovisioned", new() { ["group_id"] = group.Id }, ct);
        return Results.NoContent();
    }
}
