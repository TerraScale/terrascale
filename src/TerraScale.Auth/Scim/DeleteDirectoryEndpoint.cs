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

internal sealed class DeleteDirectoryEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/directories/{directoryId}", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, string directoryId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        var context = await ResolveInstanceAsync(request, slug, resolver, ct);
        if (context is null)
            return ApiResults.Unauthorized();

        var directory = await FindInstanceDirectoryAsync(store, context.Instance.Id, directoryId, ct);
        if (directory is null)
            return ApiResults.NotFound("directory_not_found", "no directory with this id in this instance");

        await store.DeleteScimDirectoryAsync(directory.Id, ct);
        await AuditManagementAsync(store, context, "directory.deleted", new() { ["directory_id"] = directory.Id }, ct);
        return Results.NoContent();
    }
}
