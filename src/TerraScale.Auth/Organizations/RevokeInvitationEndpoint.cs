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

internal sealed class RevokeInvitationEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/organizations/{orgId}/invitations/{invitationId}/revoke", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string orgId, string invitationId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(httpRequest, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
        if (organization is null)
            return ApiResults.NotFound("organization_not_found", "no such organization");
        var invitation = await store.GetInvitationAsync(invitationId, ct);
        if (invitation is null || invitation.InstanceId != context.Instance.Id || invitation.OrgId != organization.Id)
            return ApiResults.NotFound("invitation_not_found", "no such invitation for this organization");
        if (invitation.Status != "pending")
            return ApiResults.Conflict("invitation_not_pending", "only pending invitations can be revoked");

        // Atomic pending→revoked: an accept that lands first wins the transition, so a
        // revoke can never overwrite an accepted invitation (and vice versa below).
        var revoked = await store.TryTransitionInvitationAsync(invitation.Id, "pending", "revoked", ct);
        if (revoked is null)
            return ApiResults.Conflict("invitation_not_pending", "only pending invitations can be revoked");
        return Results.Ok(Render(revoked));
    }
}
