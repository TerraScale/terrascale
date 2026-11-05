using TerraBase.Hosting;
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
        Produces(StatusCodes.Status204NoContent);
        Produces(StatusCodes.Status503ServiceUnavailable);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.FromResult<IResult>(TypedResults.Json(handoff.Status, TerraBaseJsonContext.Default.RegionalStatus));
    }
}
