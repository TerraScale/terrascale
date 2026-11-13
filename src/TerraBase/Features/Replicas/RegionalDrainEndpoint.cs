using TerraBase.Errors;
using TerraBase.Hosting;
using TerraScale.Endpoints;
using TerraBase.Features.Callers;

namespace TerraBase.Features.Replicas;

internal sealed class RegionalDrainEndpoint(RegionalHandoff handoff) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Metadata(new Requires("rollout"));
        Post("/v1/rollout/drain");
        Name("RegionalDrainEndpoint");
        Summary("Coordinates a caught-up regional writer handoff");
        Description("Internal listener only, behind the existing peer credential. A readiness response alone never authorizes a replacement.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var target = HttpContext.Request.Query["target"].ToString();
        return await handoff.DrainAsync(target, cancellationToken).ConfigureAwait(false) switch
        {
            Done => TypedResults.NoContent(),
            Unavailable error => ErrorResponse.From(error)
        };
    }
}
