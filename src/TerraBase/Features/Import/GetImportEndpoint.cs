using TerraBase.Configuration;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Import;

/// <summary><c>GET …/databases/{database}/import</c>: reads the destination's import.</summary>
public sealed class GetImportEndpoint(IImportJobs jobs, ITenantDirectory tenants, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ImportRoute.Path);
        Name("getImport");
        Summary("Reads a database's import");
        Description("The import job attached to this destination: state, source upload, preview (once analyzed), progress, validation and error. Poll it while the job is analyzing, queued, running or validating." + ImportRoute.Reference);
        Tags(ImportRoute.Tag);
        Produces<ImportJobBody>();
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(ListenerMetadata.Client, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ImportRoute.RunAsync(HttpContext, tenants, options, StatusCodes.Status200OK, destination => jobs.GetAsync(destination.Database, destination.Name, cancellationToken));
}
