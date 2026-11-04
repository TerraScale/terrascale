using Grpc.Core;

namespace TerraBase.Features.Clock;

internal sealed class NodeClockService(IHlcClock clock, ILocalWriteClock guard, TimeProvider time) : NodeClock.NodeClockBase
{
    public override Task<ClockSample> Sample(ClockRequest request, ServerCallContext context) => Task.FromResult(Sample());

    public override async Task<ClockSample> Next(ClockRequest request, ServerCallContext context)
    {
        try
        {
            if (!guard.Allowed)
            {
                throw new ClockSkewException();
            }

            var timestamp = await clock.NextAsync(context.CancellationToken).ConfigureAwait(false);
            if (!guard.Allowed || timestamp.PhysicalMilliseconds > time.GetUtcNow().ToUnixTimeMilliseconds() + 50)
            {
                throw new ClockSkewException();
            }

            var sample = Sample();
            sample.HlcPhysicalMilliseconds = timestamp.PhysicalMilliseconds;
            sample.HlcLogical = timestamp.Logical;
            sample.HlcNodeId = timestamp.NodeId;
            return sample;
        }
        catch (ClockSkewException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "clock skew prevents local writes; forward to the database home"));
        }
    }

    private ClockSample Sample() => new()
    {
        UtcMilliseconds = time.GetUtcNow().ToUnixTimeMilliseconds(),
        SkewMilliseconds = guard.SkewMilliseconds,
        LocalWritesAllowed = guard.Allowed,
    };
}
