namespace TerraBase.Engine;

internal sealed record CdcSchema(long RowId, string Type, string Name);

internal sealed record CdcCursor(long Position, string Incarnation, IReadOnlyList<CdcSchema> Schema);

internal readonly record struct CdcRetained(string Incarnation, long Anchor, long Start, long? Oldest, DateTimeOffset? AnchorAt, DateTimeOffset? PendingAt);

internal readonly record struct CdcHistory(long Start, long? Oldest);

internal interface ICdcBackupRetention
{
    long Position { get; }

    ValueTask<CdcCursor> CursorAsync(CancellationToken cancellationToken);

    ValueTask<CdcRetained> RetainedAsync(CdcCursor? stored, CancellationToken cancellationToken);

    ValueTask<long> CommittedAsync(CancellationToken cancellationToken);

    ValueTask ResumeAsync(CdcCursor stored, CancellationToken cancellationToken);

    ValueTask<CdcHistory> DiscardAsync(CancellationToken cancellationToken);

    ValueTask SaveAccountingAsync(string id, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

    ValueTask<long> RetainedBytesAsync(CancellationToken cancellationToken);

    ValueTask AcknowledgeAsync(long position, CancellationToken cancellationToken);
}

internal interface ICdcReplicaRetentionTransaction
{
    ValueTask SetReplicaCdcRegionsAsync(IReadOnlyList<string> regions, long version, CancellationToken cancellationToken);

    ValueTask AcknowledgeReplicaCdcAsync(string region, long? position, CancellationToken cancellationToken);
}
