using System.Text.Json;

namespace TerraBase.Replication;

public readonly record struct ReplicationDot(string Origin, ulong Sequence);

public sealed record ReplicationRecord(
    DocumentKey Key,
    ReplicationDot Dot,
    IReadOnlyDictionary<string, ulong> Context,
    long Hlc,
    bool Deleted,
    JsonElement? Body)
{
    /// <summary>Canonical UTF-8 body retained on the replication hot path.</summary>
    public ReadOnlyMemory<byte>? CanonicalBody { get; init; }

    public static ReplicationRecord FromCanonicalBytes(DocumentKey key, ReplicationDot dot,
        IReadOnlyDictionary<string, ulong> context, long hlc, bool deleted, ReadOnlyMemory<byte> body) =>
        new(key, dot, context, hlc, deleted, null) { CanonicalBody = body };
}

public enum ReplicationApplyOutcome { Applied, Merged, Duplicate, Deferred }

public sealed record ReplicationApplyResult(ReplicationApplyOutcome Outcome, JsonElement? Body, IReadOnlyDictionary<string, ulong> Frontier)
{
    /// <summary>Canonical bytes, when available, avoiding a deserialize/serialize round trip.</summary>
    public ReadOnlyMemory<byte>? BodyBytes { get; init; }
}

/// <summary>
/// A deterministic leaderless document replica. Causal dependencies are checked
/// before apply; concurrent object fields use remove-wins plus HLC/origin tie-breaks.
/// </summary>
public sealed class LeaderlessReplicationEngine
{
    private sealed record Version(ReplicationRecord Record);
    private readonly Dictionary<DocumentKey, Version> documents = new();
    private readonly Dictionary<string, ulong> frontier = new(StringComparer.Ordinal);
    private readonly List<ReplicationRecord> operationLog = [];
    private readonly object gate = new();

    public ReplicationApplyResult Apply(ReplicationRecord incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        lock (gate)
        {
            if (incoming.Dot.Sequence == 0 || string.IsNullOrWhiteSpace(incoming.Dot.Origin)) throw new ArgumentException("A replication dot must be non-zero.", nameof(incoming));
            if (frontier.TryGetValue(incoming.Dot.Origin, out var seen) && seen >= incoming.Dot.Sequence)
                return Result(ReplicationApplyOutcome.Duplicate, documents.GetValueOrDefault(incoming.Key)?.Record);
            foreach (var dependency in incoming.Context)
                if (!frontier.TryGetValue(dependency.Key, out var value) || value < dependency.Value)
                    return Result(ReplicationApplyOutcome.Deferred, documents.GetValueOrDefault(incoming.Key)?.Record);

            documents.TryGetValue(incoming.Key, out var current);
            var outcome = ReplicationApplyOutcome.Applied;
            if (current is not null)
            {
                var local = current.Record;
                if (Contains(incoming.Context, local.Dot))
                {
                    // incoming supersedes the local version without parsing either body
                }
                else if (Contains(local.Context, incoming.Dot))
                {
                    Advance(incoming.Dot); return Result(ReplicationApplyOutcome.Duplicate, local);
                }
                else
                {
                    var incomingDot = incoming.Dot;
                    incoming = Merge(local, incoming);
                    Advance(incomingDot);
                    outcome = ReplicationApplyOutcome.Merged;
                }
            }
            documents[incoming.Key] = new Version(incoming);
            operationLog.Add(incoming);
            Advance(incoming.Dot);
            return Result(outcome, incoming);
        }
    }

    public IReadOnlyDictionary<string, ulong> Frontier
    { get { lock (gate) return new Dictionary<string, ulong>(frontier, StringComparer.Ordinal); } }

    public JsonElement? Read(DocumentKey key)
    {
        lock (gate)
        {
            var row = documents.GetValueOrDefault(key)?.Record;
            if (row is null || row.Deleted) return null;
            return row.Body ?? (row.CanonicalBody is { } bytes ? JsonDocument.Parse(bytes.ToArray()).RootElement.Clone() : null);
        }
    }

    /// <summary>Returns operation deltas newer than a peer's version vector.</summary>
    public IReadOnlyList<ReplicationRecord> ExportDelta(IReadOnlyDictionary<string, ulong> peerFrontier)
    {
        ArgumentNullException.ThrowIfNull(peerFrontier);
        lock (gate)
        {
            return operationLog.Where(record => !peerFrontier.TryGetValue(record.Dot.Origin, out var seen) || seen < record.Dot.Sequence).ToArray();
        }
    }

    /// <summary>Imports a delta batch; deferred records are returned for repair.</summary>
    public IReadOnlyList<ReplicationRecord> ImportDelta(IEnumerable<ReplicationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var deferred = new List<ReplicationRecord>();
        foreach (var record in records)
            if (Apply(record).Outcome == ReplicationApplyOutcome.Deferred) deferred.Add(record);
        return deferred;
    }

    /// <summary>Compacts acknowledged history while retaining current materialized state.</summary>
    public int Compact(IReadOnlyDictionary<string, ulong> stableFrontier)
    {
        ArgumentNullException.ThrowIfNull(stableFrontier);
        lock (gate)
        {
            var before = operationLog.Count;
            operationLog.RemoveAll(record => stableFrontier.TryGetValue(record.Dot.Origin, out var through)
                && record.Dot.Sequence <= through
                && documents.TryGetValue(record.Key, out var current)
                && current.Record.Dot != record.Dot);
            return before - operationLog.Count;
        }
    }

    private void Advance(ReplicationDot dot)
    { if (!frontier.TryGetValue(dot.Origin, out var current) || current < dot.Sequence) frontier[dot.Origin] = dot.Sequence; }

    private ReplicationApplyResult Result(ReplicationApplyOutcome outcome, ReplicationRecord? record)
    {
        var body = record is { Deleted: false } ? record.Body?.Clone() : null;
        return new ReplicationApplyResult(outcome, body, new Dictionary<string, ulong>(frontier, StringComparer.Ordinal))
        { BodyBytes = record is { Deleted: false } ? record.CanonicalBody : null };
    }

    private static bool Contains(IReadOnlyDictionary<string, ulong> context, ReplicationDot dot) => context.TryGetValue(dot.Origin, out var seq) && seq >= dot.Sequence;

    private static ReplicationRecord Merge(ReplicationRecord left, ReplicationRecord right)
    {
        var winner = Compare(left, right) >= 0 ? left : right;
        var context = new Dictionary<string, ulong>(left.Context, StringComparer.Ordinal);
        foreach (var pair in right.Context) context[pair.Key] = Math.Max(context.GetValueOrDefault(pair.Key), pair.Value);
        context[left.Dot.Origin] = Math.Max(context.GetValueOrDefault(left.Dot.Origin), left.Dot.Sequence);
        context[right.Dot.Origin] = Math.Max(context.GetValueOrDefault(right.Dot.Origin), right.Dot.Sequence);
        // Tombstones win concurrent updates so a delete cannot be resurrected
        // by a replica that happened to receive an older body later.
        var deleted = left.Deleted || right.Deleted;
        var body = deleted ? null : MergeBodies(left, right);
        return winner with { Context = context, Body = body, CanonicalBody = null, Deleted = deleted };
    }

    private static int Compare(ReplicationRecord left, ReplicationRecord right) =>
        left.Hlc != right.Hlc ? left.Hlc.CompareTo(right.Hlc) : string.CompareOrdinal(left.Dot.Origin, right.Dot.Origin);

    private static JsonElement? MergeBodies(ReplicationRecord leftRecord, ReplicationRecord rightRecord)
    {
        var left = leftRecord.Body ?? (leftRecord.CanonicalBody is { } leftBytes ? JsonDocument.Parse(leftBytes.ToArray()).RootElement.Clone() : null);
        var right = rightRecord.Body ?? (rightRecord.CanonicalBody is { } rightBytes ? JsonDocument.Parse(rightBytes.ToArray()).RootElement.Clone() : null);
        if (left is null) return right?.Clone();
        if (right is null) return left?.Clone();
        if (left.Value.ValueKind != JsonValueKind.Object || right.Value.ValueKind != JsonValueKind.Object)
            return (Compare(leftRecord, rightRecord) >= 0 ? left : right)?.Clone();
        var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in left.Value.EnumerateObject()) values[property.Name] = property.Value.Clone();
        foreach (var property in right.Value.EnumerateObject())
            if (!values.ContainsKey(property.Name) || Compare(leftRecord, rightRecord) < 0) values[property.Name] = property.Value.Clone();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); foreach (var pair in values) { writer.WritePropertyName(pair.Key); pair.Value.WriteTo(writer); } writer.WriteEndObject(); }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
