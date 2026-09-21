namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Organizations.OrganizationSupport;

internal sealed class DeleteOrganizationEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/organizations/{orgId}", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string orgId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, TerraScaleClient client, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(httpRequest, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
        if (organization is null)
            return ApiResults.NotFound("organization_not_found", "no such organization");

        await store.DeleteOrganizationAsync(organization.Id, ct);
        await AuditAsync(store, context.Instance.Id, "organization.deleted", context.ApiKeyId, new Dictionary<string, object>
        {
            ["organization_id"] = organization.Id,
        }, ct);
        return Results.NoContent();
    }
}
