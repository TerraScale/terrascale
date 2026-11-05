using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class RegionalDrainEndpoint(RegionalHandoff handoff) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/v1/rollout/drain");
        Name("RegionalDrainEndpoint");
        Summary("Coordinates a caught-up regional writer handoff");
        Description("Internal listener only, behind the existing peer credential. A readiness response alone never authorizes a replacement.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var target = HttpContext.Request.Query["target"].ToString();
        return await handoff.DrainAsync(target, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent() : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
