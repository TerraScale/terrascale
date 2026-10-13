using TerraBase.Configuration;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Import;

/// <summary><c>POST …/databases/{database}/import/resume</c>: resumes a failed or cancelled import.</summary>
public sealed class ResumeImportEndpoint(IImportJobs jobs, ITenantDirectory tenants, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ImportRoute.Path + "/resume");
        Name("resumeImport");
        Summary("Resumes a failed or cancelled import");
        Description("A job whose copy was confirmed continues after its last committed batch under the same preview; a job never confirmed is analyzed again and needs startImport with the new preview. 409 source_required when the staged source is gone: upload the same file again first." + ImportRoute.Reference);
        Tags(ImportRoute.Tag);
        Produces<ImportJobBody>(StatusCodes.Status202Accepted);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite, ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ImportRoute.RunAsync(HttpContext, tenants, options, StatusCodes.Status202Accepted, destination => jobs.ResumeAsync(destination.Database, destination.Name, cancellationToken));
}
