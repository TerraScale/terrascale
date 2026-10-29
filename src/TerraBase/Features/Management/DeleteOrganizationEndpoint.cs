using TerraBase.Errors;
using TerraBase.Features.Replication;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>DELETE /v1/management/organizations/{organization}</c>: deletes an organization without projects.</summary>
public sealed class DeleteOrganizationEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ManagementRoute.Organization);
        Metadata(AnchoredRevocation.Instance);
        Name("deleteOrganization");
        Summary("Deletes an organization");
        Description("Needs the organization creator. Fails with 409 while the organization still has projects." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.DeleteOrganizationAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), cancellationToken) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
