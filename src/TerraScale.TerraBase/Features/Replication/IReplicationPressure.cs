namespace TerraBase.Features.Replication;

/// <summary>Outgoing replication telemetry, never membership or local catch-up evidence.</summary>
public interface IReplicationPressure
{
    /// <summary>
    /// Committed local-origin change groups not durably acknowledged by configured peers, summed over peers and database
    /// incarnations. Null until all persisted journal heads are measured; zero when no peers are configured. The sum
    /// saturates at <see cref="long.MaxValue"/>. This does not measure incoming replication or authorize retirement.
    /// </summary>
    long? SenderDurableLagSeq { get; }
}
