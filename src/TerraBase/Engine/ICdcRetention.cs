namespace TerraBase.Engine;

internal sealed record CdcSchema(long RowId, string Type, string Name);

internal sealed record CdcCursor(long Position, string Incarnation, IReadOnlyList<CdcSchema> Schema);

internal readonly record struct CdcState(long End, bool Continuous, DateTimeOffset? Oldest);

internal interface ICdcBackupRetention
{
    long Position { get; }

    ValueTask<CdcCursor> CursorAsync(CancellationToken cancellationToken);

    ValueTask<CdcState> StateAsync(CdcCursor? stored, CancellationToken cancellationToken);

    ValueTask<bool> ResumeAsync(CdcCursor stored, CancellationToken cancellationToken);

    ValueTask SkipAsync(long end, CancellationToken cancellationToken);

    ValueTask SaveAccountingAsync(string id, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

    ValueTask<long> RetainedBytesAsync(CancellationToken cancellationToken);

    ValueTask AcknowledgeAsync(long position, CancellationToken cancellationToken);
}

internal interface ICdcReplicaRetentionTransaction
{
    ValueTask SetReplicaCdcRegionsAsync(IReadOnlyList<string> regions, long version, CancellationToken cancellationToken);

    ValueTask AcknowledgeReplicaCdcAsync(string region, long? position, CancellationToken cancellationToken);
}
