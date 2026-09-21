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

internal sealed class CreateDirectoryEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/directories", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        var context = await ResolveInstanceAsync(request, slug, resolver, ct);
        if (context is null)
            return ApiResults.Unauthorized();

        var (body, bodyFailure) = await ReadBodyAsync<CreateDirectoryBody>(request, ct);
        if (body is null)
            return bodyFailure ?? ApiResults.BadRequest("invalid_request", "a JSON body is required");

        string? orgId = null;
        if (body.OrgId is { } rawOrgId)
        {
            var organization = await store.GetOrganizationAsync(rawOrgId, ct);
            if (organization is null || organization.InstanceId != context.Instance.Id)
                return ApiResults.NotFound("organization_not_found", "no organization with this id in this instance");
            orgId = organization.Id;
        }

        ScimDirectory directory;
        string token;
        try
        {
            (directory, token) = await CreateDirectoryAsync(store, context.Instance.Id, orgId, ct);
        }
        catch (TerraScaleApiException)
        {
            return ApiResults.Conflict("token_collision", "the generated token collided; retry the request");
        }
        await AuditManagementAsync(store, context, "directory.created", new() { ["directory_id"] = directory.Id }, ct);
        return Results.Json(new { id = directory.Id, org_id = directory.OrgId, token }, statusCode: 201);
    }

    /// <summary>Creates the directory and returns its bearer token — the only time the token exists in the clear.</summary>
    private static async Task<(ScimDirectory Directory, string Token)> CreateDirectoryAsync(AuthStore store, string instanceId, string? orgId, CancellationToken ct)
    {
        var token = "scim_" + SecureTokens.RandomToken(40);
        var directory = new ScimDirectory
        {
            Id = "dir_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            OrgId = orgId,
            TokenKey = SecureTokens.Sha256Hex(token),
            CreatedAt = Now(),
        };
        await store.CreateScimDirectoryAsync(directory, ct);
        return (directory, token);
    }

    private sealed record CreateDirectoryBody([property: JsonPropertyName("org_id")] string? OrgId);
}
