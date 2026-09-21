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

internal sealed class RotateDirectoryTokenEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/directories/{directoryId}/rotate", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, string directoryId, HttpRequest request, InstanceResolver resolver, AuthStore store, TerraScaleClient client, CancellationToken ct)
    {
        var context = await ResolveInstanceAsync(request, slug, resolver, ct);
        if (context is null)
            return ApiResults.Unauthorized();

        var directory = await FindInstanceDirectoryAsync(store, context.Instance.Id, directoryId, ct);
        if (directory is null)
            return ApiResults.NotFound("directory_not_found", "no directory with this id in this instance");

        var token = "scim_" + SecureTokens.RandomToken(40);
        var tokenKey = SecureTokens.Sha256Hex(token);
        try
        {
            await store.ClaimUniqueAsync("scim-token", tokenKey, directory.Id, ct);
        }
        catch (TerraScaleApiException)
        {
            return ApiResults.Conflict("token_collision", "the generated token collided; retry the request");
        }
        var rotated = directory with { TokenKey = tokenKey };
        try
        {
            await client.Documents.PutAsync(store.Database, "scim_directories", rotated.Id, rotated, cancellationToken: ct);
        }
        catch
        {
            // The write failed: unwind the freshly claimed token so no orphan claim remains.
            await store.ReleaseUniqueAsync("scim-token", tokenKey, ct);
            throw;
        }
        await store.ReleaseUniqueAsync("scim-token", directory.TokenKey, ct);

        await AuditManagementAsync(store, context, "directory.token_rotated", new() { ["directory_id"] = directory.Id }, ct);
        return Results.Json(new { id = rotated.Id, org_id = rotated.OrgId, token });
    }
}
