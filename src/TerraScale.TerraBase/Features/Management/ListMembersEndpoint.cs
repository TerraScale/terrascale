using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

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
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.CheckPage(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            PageQuery page => await access.ListMembersAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), page.After, page.Limit, cancellationToken) switch
            {
                ManagementPage<OrganizationMember> value => TypedResults.Ok(value),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
