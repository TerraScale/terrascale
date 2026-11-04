using TerraBase.Engine.Revisions;

namespace TerraBase.Engine;

internal interface ICdcRetentionTransaction
{
    ValueTask SetCdcConsumerFloorAsync(string consumer, long? position, CancellationToken cancellationToken);
}

internal interface ICdcBackupRetention
{
    long Position { get; }

    ValueTask AcknowledgeAsync(long position, CancellationToken cancellationToken);
}

internal interface ICdcReplicaRetentionTransaction
{
    ValueTask SetReplicaCdcRegionsAsync(IReadOnlyList<string> regions, long version, CancellationToken cancellationToken);

    ValueTask AcknowledgeReplicaCdcAsync(string region, string? revision, CancellationToken cancellationToken);

    ValueTask OfferReplicaCdcAsync(string region, RevisionPullResult offered, CancellationToken cancellationToken);
}
