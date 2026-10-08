using TerraBase.Features.CrossRegion.V1;
using TerraBase.Features.Management.Usage;

namespace TerraBase.Features.CrossRegion;

internal static class WanUsage
{
    public static string Kind(Replication.V1.ReplicationTrafficKind traffic) =>
        traffic == Replication.V1.ReplicationTrafficKind.Handoff ? "handoff" : "delta";

    // Discovery messages are shared: divide their exact encoded lengths across the advertised databases, conserving bytes.
    public static void Heads(IUsageMeter meter, string peerRegion, HeadsReply reply, int requestBytes, int replyBytes, bool responding)
    {
        var count = reply.Heads.Count;
        if (count == 0)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var head = reply.Heads[i];
            if (WanProtocol.Database(head.TenantId, head.ProjectId, head.DatabaseId) is not { } database)
            {
                continue;
            }

            meter.RecordReplicationBytes(database, peerRegion, "anti_entropy", responding, replyBytes / count + (i < replyBytes % count ? 1 : 0));
            meter.RecordReplicationBytes(database, peerRegion, "anti_entropy", !responding, requestBytes / count + (i < requestBytes % count ? 1 : 0));
        }
    }
}
