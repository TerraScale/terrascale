using System.Globalization;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Import;

/// <summary><c>PUT …/databases/{database}/import/source?offset=</c>: stages one part (at most 4 MiB) of the source.</summary>
public sealed class UploadImportSourceEndpoint(IImportJobs jobs, ITenantDirectory tenants, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    private const string MediaType = "application/octet-stream";

    /// <inheritdoc />
    public override void Configure()
    {
        Put(ImportRoute.Path + "/source");
        Name("uploadImportSource");
        Summary("Uploads one part of the import's source");
        Description("Raw bytes (application/octet-stream, Content-Length 1 to 4194304) at query offset = source.receivedBytes. Parts arrive in order; a part that ends at or before receivedBytes is a replay (200, unchanged); any other offset answers 409 offset_mismatch. The part that completes sourceBytes moves the job to analyzing, which checks the SHA-256 first. 503 capacity when this node has no staging room now." + ImportRoute.Reference);
        Tags(ImportRoute.Tag);
        Metadata(new RequestBodyMetadata(typeof(byte[]), MediaType));
        Produces<ImportJobBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status415UnsupportedMediaType);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite, ApiKeyScopes.DocumentsWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var request = HttpContext.Request;
        if (!string.Equals(request.ContentType, MediaType, StringComparison.OrdinalIgnoreCase))
        {
            return ErrorResponse.From(new UnsupportedMediaType("the body must be application/octet-stream"));
        }

        if (request.Query.Count != 1 || request.Query["offset"] is not [{ } text]
            || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            return ErrorResponse.From(new Validation("query offset (bytes already received) is required"));
        }

        if (request.ContentLength is not { } length)
        {
            return ErrorResponse.From(new Validation("Content-Length is required"));
        }

        return await ImportRoute.RunAsync(HttpContext, tenants, options, StatusCodes.Status200OK,
            destination => jobs.UploadAsync(destination.Database, destination.Name, offset, length, request.Body, cancellationToken));
    }
}
