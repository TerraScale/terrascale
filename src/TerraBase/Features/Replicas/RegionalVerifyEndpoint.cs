using TerraBase.Errors;
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
        Produces<DetailedErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        RegionalPrepareEndpoint.AnswerAsync(HttpContext, memory, async (request, token) => await handoff.VerifyAsync(request, token).ConfigureAwait(false) switch
        {
            Done => TypedResults.NoContent(),
            Unavailable error => ErrorResponse.From(error)
        }, cancellationToken);
}
