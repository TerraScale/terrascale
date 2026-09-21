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

internal sealed class ListDirectoriesEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/directories", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        var context = await ResolveInstanceAsync(request, slug, resolver, ct);
        if (context is null)
            return ApiResults.Unauthorized();

        // Tokens are sha256-hashed at rest and never listed back.
        var directories = (await store.ListScimDirectoriesAsync(context.Instance.Id, ct))
            .Select(d => new { id = d.Id, org_id = d.OrgId, created_at = d.CreatedAt })
            .ToList();
        return Results.Json(new { data = directories });
    }
}
