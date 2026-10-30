using MessagePack;

namespace TerraScale.TerraBase.Contracts.Backups
{
    /// <summary>The inclusive continuous-backup restore window.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseRestoreWindow(DateTimeOffset Earliest, DateTimeOffset Latest);

    /// <summary>Advisory restore metadata, not proof of backup object integrity.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBasePointInTimePlan(TerraBaseRestoreWindow Window, DateTimeOffset PointInTime, DateTimeOffset SnapshotAt, DateTimeOffset? LatestSegmentAt, int SegmentCount, string HistoryCheck);

    /// <summary>A restore or advisory plan request.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBasePointInTimeRequest(DateTimeOffset PointInTime, string? Name, string? Destination);


}
