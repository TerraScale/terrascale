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

internal sealed class UpdateMembershipEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPatch("/v1/{slug}/organizations/{orgId}/memberships/{membershipId}", HandleAsync);

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
        var body = await httpRequest.ReadFromJsonAsync<UpdateMembershipRequest>(ct);
        if (body is null || !IsValidRole(body.Role))
            return ApiResults.BadRequest("invalid_role", "role must be 1-64 characters");

        // One atomic write: the memberKey claim stays held the whole time (no
        // delete+re-create window where the membership or its claim is released).
        membership = membership with { Role = body.Role!.Trim() };
        await store.UpsertMembershipAsync(membership, ct);
        await AuditAsync(store, context.Instance.Id, "membership.updated", context.ApiKeyId, new Dictionary<string, object>
        {
            ["organization_id"] = organization.Id,
            ["membership_id"] = membership.Id,
            ["user_id"] = membership.UserId,
            ["role"] = membership.Role,
        }, ct);
        return Results.Ok(Render(membership));
    }
}
