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
