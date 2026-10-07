using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>DELETE …/projects/{project}</c>: deletes a project without databases or API keys.</summary>
public sealed class DeleteProjectEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ManagementRoute.Project);
        Name("deleteProject");
        Summary("Deletes a project");
        Description("Fails with 409 while the project still has databases or API keys." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.NoContent(await management.DeleteProjectAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), cancellationToken));
}
