using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TerraBase.Features.HomePush;

namespace TerraBase.Fast.Sync;

public sealed class HomePushStreamReplicaTests
{
    [Test]
    public async Task A_stream_that_asks_for_more_replicas_than_the_cluster_stops_the_push_retry()
    {
        var exceeded = new NatsJSApiException(new ApiError
        {
            Code = 500,
            ErrCode = HomePush.StreamReplicasNotSupported,
            Description = "replicas > 1 not supported in non-clustered mode",
        });
        var unrelated = new NatsJSApiException(new ApiError
        {
            Code = 503,
            ErrCode = 10023,
            Description = "insufficient resources",
        });

        await Assert.That(HomePush.StreamReplicaCountExceedsCluster(exceeded)).IsTrue();
        await Assert.That(HomePush.StreamReplicaCountExceedsCluster(unrelated)).IsFalse();
    }
}
