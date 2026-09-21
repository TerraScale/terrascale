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

internal sealed class CreateMembershipEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/organizations/{orgId}/memberships", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string orgId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(httpRequest, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
        if (organization is null)
            return ApiResults.NotFound("organization_not_found", "no such organization");
        var body = await httpRequest.ReadFromJsonAsync<CreateMembershipRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.UserId) || !IsValidRole(body.Role))
            return ApiResults.BadRequest("invalid_request", "user_id and role (1-64 characters) are required");

        var user = await store.GetUserAsync(body.UserId.Trim(), ct);
        if (user is null || user.InstanceId != context.Instance.Id || user.Deleted)
            return ApiResults.NotFound("user_not_found", "no such user in this instance");

        var membership = new OrgMembership
        {
            Id = "mem_" + SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            OrgId = organization.Id,
            UserId = user.Id,
            MemberKey = $"{organization.Id}:{user.Id}",
            Role = body.Role!.Trim(),
            CreatedAt = Now(),
        };
        // Check-then-act ahead of the memberKey unique index (see organization creation).
        if ((await store.ListMembershipsByOrgAsync(organization.Id, ct)).Any(m => m.UserId == user.Id))
            return ApiResults.Conflict("already_a_member", "the user is already a member of this organization");
        try
        {
            await store.CreateMembershipAsync(membership, ct);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return ApiResults.Conflict("already_a_member", "the user is already a member of this organization");
        }
        await AuditAsync(store, context.Instance.Id, "membership.created", context.ApiKeyId, new Dictionary<string, object>
        {
            ["organization_id"] = organization.Id,
            ["membership_id"] = membership.Id,
            ["user_id"] = membership.UserId,
            ["role"] = membership.Role,
        }, ct);
        return Results.Ok(Render(membership));
    }
}
