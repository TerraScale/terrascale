using TerraBase.Errors;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

public sealed class RemoveMemberEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete(ManagementRoute.Member);
        Metadata(AnchoredRevocation.Instance);
        Name("removeMember");
        Summary("Removes a member from an organization");
        Description($"Needs admin, and owner to remove an owner. 409 for the last owner. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.CheckUserId(ManagementRoute.Value(HttpContext, "user"), "user") switch
        {
            Validation error => ErrorResponse.From(error),
            string member => await access.RemoveMemberAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), member, cancellationToken) switch
            {
                Done => TypedResults.NoContent(),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
