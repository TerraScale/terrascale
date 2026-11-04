using System.Text.Json.Serialization;
using TerraBase.Engine.Revisions;

namespace TerraBase.Features.Replicas;

internal sealed record ReplicaChecksumPage(string Revision, bool Moved, IReadOnlyList<RangeChecksum> Ranges, ChecksumCursor? Next);

internal sealed record ReplicaStatusReport(string Region, string State, long? LagMs, DateTimeOffset? SyncedAt, DateTimeOffset? VerifiedAt);

public sealed record ReplicaStatusBody(
    string Region, string State, long? LagMs, DateTimeOffset? SyncedAt, DateTimeOffset? VerifiedAt, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ReportedAt = null);

internal static class ReplicaStates
{
    public const string Following = "following";
    public const string Bootstrapping = "bootstrapping";
    public const string Healing = "healing";
    public const string Stale = "stale";
    public const string Unknown = "unknown";

    public static bool IsReported(string state) => state is Following or Bootstrapping or Healing;
}
