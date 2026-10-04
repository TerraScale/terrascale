using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>PATCH …/projects/{project}</c>: renames a project.</summary>
public sealed class RenameProjectEndpoint(IManagementService management) : Endpoint<NameRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch(ManagementRoute.Project);
        Name("renameProject");
        Summary("Renames a project");
        Tags(ManagementRoute.Tag);
        Produces<Project>();
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(ApiKeyScopes.Management);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ManagementRoute.Ok(await management.RenameProjectAsync(ManagementRoute.Value(HttpContext, "organization"), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.DisplayName(request), cancellationToken));
    }
}
