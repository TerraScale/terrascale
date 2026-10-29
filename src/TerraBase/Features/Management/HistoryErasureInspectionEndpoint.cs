using System.Text.Json.Serialization;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Features.Catalog;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class HistoryErasureInspectionEndpoint(HistoryErasures erasures, ICatalogGate gate) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/admin/history-erasures/{erasure}");
        Name("inspectHistoryErasure");
        Summary("Read one existing history erasure's metadata");
        Description("Internal listener and peer credential only. Reads one catalog snapshot: current status, holder receipts, final cuts, completed rewrite lineage and backup barrier metadata. Never creates authority, settles, issues tokens, rewrites or verifies backups. Contains no subject identifiers, document content, credentials or settlement tokens. 404 is absence on this node, never proof of completion. A generation object must still be authenticated and matched to a completed rewrite; metadata alone is not a physical-erasure certificate.");
        Tags("Administration");
        Produces<HistoryErasureInspection>();
        foreach (var status in new[] { 400, 404, 503 })
        {
            Produces<ErrorBody>(status);
        }

        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var erasure = HttpContext.Request.RouteValues["erasure"] as string;
        if (!EntityIds.IsValid(erasure))
        {
            return ErrorResponse.From(new Validation("erasure must be a canonical uppercase ULID"));
        }

        if (!gate.IsOpen)
        {
            return ErrorResponse.From(new Unavailable("the catalog gate is not open and trusted", "catalog_unavailable"));
        }

        return await erasures.InspectAsync(erasure!, cancellationToken).ConfigureAwait(false) switch
        {
            HistoryErasureInspection inspection => TypedResults.Json(inspection, HistoryInspectionJson.Default.HistoryErasureInspection),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HistoryErasureInspection))]
internal sealed partial class HistoryInspectionJson : JsonSerializerContext;
