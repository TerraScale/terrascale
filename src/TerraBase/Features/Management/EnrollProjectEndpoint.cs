using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class EnrollProjectEndpoint(IProjectCatalog catalog) : Endpoint<EnrollmentRequest, IResult>
{
    internal const string Actor = "terrabase-enrollment";

    public override void Configure()
    {
        Put("/admin/projects/{project}/enrollment");
        Name("enrollProject");
        Summary("Applies a project's enrollment in this region");
        Description("Internal peer listener only, behind the shared peer credential; 404 elsewhere. Writes the same project→organization "
            + "binding the project's first database create wrote in the region that served it, so every region resolves the project instead of "
            + "answering unknown_database until its catalog converges. Idempotent for the same organization; 404 not_found when the project is "
            + "enrolled to another organization, retired or closed, writing nothing.");
        Tags("Administration");
        Produces(StatusCodes.Status200OK);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<IResult> HandleAsync(EnrollmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = DatabaseRoute.Project(HttpContext);
        if (!EntityIds.IsValid(project) || !EntityIds.IsValid(request.Organization!))
        {
            return ErrorResponse.From(new Validation("project and organization must be canonical uppercase ULIDs"));
        }

        return await catalog.ResolveProjectAsync(project, Actor, request.Organization!, cancellationToken).ConfigureAwait(false) switch
        {
            string => TypedResults.Ok(),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
