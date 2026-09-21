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

internal sealed class GetResourceTypesEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/scim/{directoryId}/ResourceTypes", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, HttpRequest request, AuthStore store, CancellationToken ct)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;
        var directory = gate.Directory!;

        return ScimJson(new ScimListResponse(
            Schemas: [ListResponseSchema],
            TotalResults: 2,
            StartIndex: 1,
            ItemsPerPage: 2,
            Resources:
            [
                new { schemas = new[] { ResourceTypeSchema }, id = "User", name = "User", endpoint = $"/scim/{directory.Id}/Users", schema = UserSchema },
                new { schemas = new[] { ResourceTypeSchema }, id = "Group", name = "Group", endpoint = $"/scim/{directory.Id}/Groups", schema = GroupSchema },
            ]));
    }

    private const string ResourceTypeSchema = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";
}
