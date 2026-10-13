using TerraBase.Configuration;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Import;

/// <summary><c>POST …/databases/{database}/import/cancel</c>: cancels the import.</summary>
public sealed class CancelImportEndpoint(IImportJobs jobs, ITenantDirectory tenants, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ImportRoute.Path + "/cancel");
        Name("cancelImport");
        Summary("Cancels the import");
        Description("Sets cancelRequested; a running copy stops before its next batch and becomes cancelled. Documents already written stay; delete the database to discard them. Repeating the call answers 202 with the current job." + ImportRoute.Reference);
        Tags(ImportRoute.Tag);
        Produces<ImportJobBody>(StatusCodes.Status202Accepted);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ImportRoute.RunAsync(HttpContext, tenants, options, StatusCodes.Status202Accepted, destination => jobs.CancelAsync(destination.Database, destination.Name, cancellationToken));
}
