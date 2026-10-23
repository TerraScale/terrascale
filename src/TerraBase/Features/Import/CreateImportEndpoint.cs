using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Import;

/// <summary><c>POST …/databases/{database}/import</c>: attaches an import to a new, empty destination.</summary>
public sealed class CreateImportEndpoint(IImportJobs jobs, ITenantDirectory tenants, TerraBaseOptions options) : Endpoint<CreateImportRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ImportRoute.Path);
        Name("createImport");
        Summary("Attaches an import to a new, empty database");
        Description("Body {format: postgres_plain | sqlite | ndjson, sourceBytes (1 to 1073741824), sourceSha256 (64 lowercase hex), options?: {missingPrimaryKey: reject | generate, exclude: [\"schema.table\" | \"schema.table.column\"], collection?: ndjson collection for lines that name none}}. The database must have been created for this import with createDatabase and hold no collection (409 destination_not_empty). The job starts in awaiting_source; upload the file with uploadImportSource. An identical replay answers 200 with the same job; a different request answers 409 import_exists." + ImportRoute.Reference);
        Tags(ImportRoute.Tag);
        Produces<ImportJobBody>(StatusCodes.Status201Created);
        Produces<ImportJobBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite, ApiKeyScopes.DocumentsWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CreateImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ImportRoute.ResolveAsync(HttpContext, tenants, options, cancellationToken) switch
        {
            // Create-only writes and the record's revision protect a destination served by this region alone.
            ImportDestination { Replicated: true } => ErrorResponse.From(new Conflict(
                "an import needs a database without replica regions; add replicas after it completes", "destination_replicated")),
            ImportDestination destination => await jobs.CreateAsync(destination.Database, destination.Name, request, cancellationToken) switch
            {
                ImportCreated created => TypedResults.Created(HttpContext.Request.Path.Value, created.Job),
                ImportJobBody job => TypedResults.Ok(job),
                NotFound error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Validation error => ErrorResponse.From(error),
                PayloadTooLarge error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
            ImportElsewhere elsewhere => elsewhere.Result,
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
