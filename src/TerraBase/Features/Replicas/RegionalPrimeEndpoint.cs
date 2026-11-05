using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class RegionalPrimeEndpoint(RegionalHandoff handoff) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/v1/rollout/prime");
        Name("RegionalPrimeEndpoint");
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
        var source = HttpContext.Request.Query["source"].ToString();
        return await handoff.PrimeAsync(source, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent() : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
