using TerraBase.Configuration;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Import;

/// <summary><c>POST …/databases/{database}/import/start</c>: confirms the preview and starts the copy.</summary>
public sealed class StartImportEndpoint(IImportJobs jobs, ITenantDirectory tenants, TerraBaseOptions options) : Endpoint<StartImportRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ImportRoute.Path + "/start");
        Name("startImport");
        Summary("Confirms the preview and starts copying");
        Description("Body {previewId}: the preview.id the user confirmed. 409 preview_changed when it is not the current preview, 409 preview_blocked when the preview has blocking issues. Repeating the call while the copy is queued or running answers 202 with the current job." + ImportRoute.Reference);
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
    public override ValueTask<IResult> HandleAsync(StartImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ImportRoute.RunAsync(HttpContext, tenants, options, StatusCodes.Status202Accepted,
            destination => jobs.StartAsync(destination.Database, destination.Name, request, cancellationToken));
    }
}
