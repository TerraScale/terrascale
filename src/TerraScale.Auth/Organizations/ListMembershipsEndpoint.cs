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

internal sealed class ListMembershipsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/organizations/{orgId}/memberships", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string orgId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(httpRequest, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
        if (organization is null)
            return ApiResults.NotFound("organization_not_found", "no such organization");

        var items = new List<object>();
        foreach (var membership in await store.ListMembershipsByOrgAsync(organization.Id, ct))
        {
            var user = await store.GetUserAsync(membership.UserId, ct);
            object? summary = user is null ? null : new { id = user.Id, email = user.Email, name = user.Name };
            items.Add(new { id = membership.Id, organization_id = membership.OrgId, user_id = membership.UserId, role = membership.Role, created_at = membership.CreatedAt, user = summary });
        }
        return Results.Ok(items);
    }
}
