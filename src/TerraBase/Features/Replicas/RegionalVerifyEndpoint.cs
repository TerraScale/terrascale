using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraScale.Endpoints;
using TerraBase.Features.Callers;

namespace TerraBase.Features.Replicas;

internal sealed class RegionalVerifyEndpoint(RegionalHandoff handoff, IMemoryBudget memory) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Metadata(new Requires("rollout"));
        Post("/v1/rollout/verify");
        Name("RegionalVerifyEndpoint");
        Summary("Coordinates a caught-up regional writer handoff");
        Description("Internal listener only, behind the existing peer credential. A readiness response alone never authorizes a replacement.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces(StatusCodes.Status503ServiceUnavailable);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        RegionalPrepareEndpoint.AnswerAsync(HttpContext, handoff, memory, handoff.VerifyAsync, cancellationToken);
}
