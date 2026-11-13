using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Clock;

internal sealed class ClockEndpoint(TimeProvider time) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/clock");
        Name("readNodeClock");
        Summary("Reads this node's wall clock");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Answers this node's wall clock in Unix "
            + "milliseconds. Other nodes compare it with their own to decide whether their clock is trusted to stamp writes.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<PeerClock>(StatusCodes.Status200OK);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.FromResult<IResult>(TypedResults.Json(new PeerClock(time.GetUtcNow().ToUnixTimeMilliseconds()), ClockJsonContext.Default.PeerClock));
    }
}
