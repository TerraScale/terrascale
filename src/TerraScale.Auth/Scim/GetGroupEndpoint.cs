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

internal sealed class GetGroupEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/scim/{directoryId}/Groups/{groupId}", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, string groupId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;
        var directory = gate.Directory!;

        var group = await FindScimGroupAsync(new ScimGroups(client, store.Database), directory.Id, groupId, ct);
        return group is null
            ? ScimError(404, $"no group with id '{groupId}'")
            : ScimJson(GroupView(directory.Id, group));
    }
}
