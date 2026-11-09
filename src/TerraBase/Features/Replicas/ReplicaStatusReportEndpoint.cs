using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class ReplicaStatusReportEndpoint(IReplicaStatus status) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/replica-status");
        Name("reportReplicaStatus");
        Summary("Records a replica region's status of a database homed here");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. 204 once recorded; 404 when the database is not homed "
            + "here or its placement does not name the region.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(new RequestBodyMetadata(typeof(ReplicaStatusReport), "application/json"), ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (ReplicaRoutes.Database(HttpContext) is not { } database)
        {
            return ErrorResponse.From(new NotFound("the database does not exist"));
        }

        if (HttpContext.Request.ContentLength is not (> 0 and <= 4096))
        {
            return ErrorResponse.From(new Validation("the body is a replica status report of at most 4 KiB"));
        }

        ReplicaStatusReport? report;
        try
        {
            report = await HttpContext.Request.ReadFromJsonAsync(TerraBaseJsonContext.Default.ReplicaStatusReport, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            report = null;
        }

        if (report is null || !TerraBaseOptions.IsRegion(report.Region))
        {
            return ErrorResponse.From(new Validation("the body is a replica status report"));
        }

        return await status.RecordAsync(database, report, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : ErrorResponse.From(new NotFound("the database is not homed here with a replica in that region"));
    }
}
