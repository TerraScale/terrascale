using TerraBase.Configuration;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Import;

/// <summary><c>PATCH …/databases/{database}/import</c>: new options, analyzed again.</summary>
public sealed class UpdateImportEndpoint(IImportJobs jobs, ITenantDirectory tenants, TerraBaseOptions options) : Endpoint<UpdateImportRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch(ImportRoute.Path);
        Name("updateImport");
        Summary("Changes the import's options and analyzes the source again");
        Description("Body {options: {missingPrimaryKey, exclude}}, allowed while ready or rejected. The job moves to analyzing and gets a new preview id; the source cannot change." + ImportRoute.Reference);
        Tags(ImportRoute.Tag);
        Produces<ImportJobBody>(StatusCodes.Status202Accepted);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite, ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(UpdateImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ImportRoute.RunAsync(HttpContext, tenants, options, StatusCodes.Status202Accepted,
            destination => jobs.UpdateAsync(destination.Database, destination.Name, request, cancellationToken));
    }
}
