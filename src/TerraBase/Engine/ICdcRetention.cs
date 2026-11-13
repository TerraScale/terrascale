namespace TerraBase.Engine;

internal interface ICdcBackupRetention
{
    long Position { get; }

    ValueTask<long> RetainedBytesAsync(CancellationToken cancellationToken);

    ValueTask AcknowledgeAsync(long position, CancellationToken cancellationToken);
}

internal interface ICdcReplicaRetentionTransaction
{
    ValueTask SetReplicaCdcRegionsAsync(IReadOnlyList<string> regions, long version, CancellationToken cancellationToken);

    ValueTask AcknowledgeReplicaCdcAsync(string region, long? position, CancellationToken cancellationToken);
}
