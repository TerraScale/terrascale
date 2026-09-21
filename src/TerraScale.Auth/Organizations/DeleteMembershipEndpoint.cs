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

internal sealed class DeleteMembershipEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/organizations/{orgId}/memberships/{membershipId}", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string orgId, string membershipId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(httpRequest, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
        if (organization is null)
            return ApiResults.NotFound("organization_not_found", "no such organization");
        var membership = await FindMembershipAsync(store, context.Instance.Id, organization.Id, membershipId, ct);
        if (membership is null)
            return ApiResults.NotFound("membership_not_found", "no such membership in this organization");

        await store.DeleteMembershipAsync(membership.Id, ct);
        await AuditAsync(store, context.Instance.Id, "membership.deleted", context.ApiKeyId, new Dictionary<string, object>
        {
            ["organization_id"] = organization.Id,
            ["membership_id"] = membership.Id,
            ["user_id"] = membership.UserId,
        }, ct);
        return Results.NoContent();
    }
}
