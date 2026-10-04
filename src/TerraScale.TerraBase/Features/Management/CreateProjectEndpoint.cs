using TerraScale.Endpoints;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>POST …/organizations/{organization}/projects</c>: creates a project.</summary>
public sealed class CreateProjectEndpoint(IManagementService management) : Endpoint<NameRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Projects);
        Name("createProject");
        Summary("Creates a project");
        Description("name: 1 to 128 characters with a letter or digit, unique within the organization ignoring case." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<Project>(StatusCodes.Status201Created);
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(DashboardAuthenticationHandler.PolicyName);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await management.CreateProjectAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.DisplayName(request), cancellationToken);
        return result.Value is { } value
            ? TypedResults.Created($"{ManagementRoute.Organizations}/{value.OrganizationId}/projects/{value.Id}", value)
            : ManagementRoute.Error(result.Error);
    }
}
