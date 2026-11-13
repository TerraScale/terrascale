using TerraBase.Errors;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class DeleteProjectEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete(ManagementRoute.Project);
        Metadata(AnchoredRevocation.Instance);
        Name("deleteProject");
        Summary("Deletes a project");
        Description("Permanently erases the project's retained databases, ending their restore windows. Fails with 409 while the project still has live databases, API keys, file buckets or a customer backup destination. Remove those resources before deleting the project." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.DeleteProjectAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), HttpContext.RouteValue("project"), cancellationToken) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
