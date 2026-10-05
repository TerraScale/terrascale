using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>GET …/organizations/{organization}/members</c>: lists the members of the organization.</summary>
public sealed class ListMembersEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Members);
        Name("listMembers");
        Summary("Lists the members of an organization");
        Description($"Needs member. Items are in user id order; query: after (user id cursor), limit (1 to 100, default 20). {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<ManagementPage<OrganizationMember>>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (after, limit) = ManagementRoute.Page(HttpContext);
        return ManagementRoute.Ok(await access.ListMembersAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), after, limit, cancellationToken));
    }
}
