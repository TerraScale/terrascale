using TerraBase.Hosting;
using TerraBase.Configuration;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class RegionalStatusEndpoint(RegionalHandoff handoff) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/rollout/status");
        Name("RegionalStatusEndpoint");
        Summary("Coordinates a caught-up regional writer handoff");
        Description("Internal listener only, behind the existing peer credential. A readiness response alone never authorizes a replacement.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<RegionalStatus>();
        Produces(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var region = HttpContext.Request.Query["region"].ToString();
        if (region.Length > 0 && !TerraBaseOptions.IsRegion(region))
        {
            return TypedResults.BadRequest();
        }
        return TypedResults.Json(await handoff.StatusAsync(cancellationToken, region.Length > 0 ? region : null).ConfigureAwait(false), TerraBaseJsonContext.Default.RegionalStatus);
    }
}
