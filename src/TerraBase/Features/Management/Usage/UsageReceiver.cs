using System.Diagnostics;
using System.Text.Json;
using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;

namespace TerraBase.Features.Management.Usage;

/// <summary>
/// A catalog primary's side of <c>CatalogUsage.Add</c>: adds the counts a follower sends to its own pending counts, which its
/// next flush saves. Each follower process (epoch) numbers its batches and sends the next only once the previous was
/// acknowledged, so a batch at or below the highest sequence added for its epoch is a retry, or an attempt that arrived late
/// (<see cref="UsageMeter.AcceptAsync"/>, which saves those sequences with the counts).
/// </summary>
internal sealed class UsageReceiver(TerraBaseOptions options, UsageMeter meter, SystemCatalog catalog, TimeProvider time, IMemoryBudget memory) : CatalogUsage.CatalogUsageBase
{
    /// <summary>Usage protocol version.</summary>
    internal const uint Protocol = 1;

    public override async Task<UsageAck> Add(UsageBatch request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (options.CatalogPrimary is not null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "this member is not a catalog primary"));
        }

        // A day of clock skew is tolerated; later hours could not be stored as a bucket.
        var latest = (time.GetUtcNow().ToUnixTimeSeconds() / 3600) + 24;
        if (request.Protocol != Protocol || request.Epoch.Length != 16 || request.Sequence is 0 or > long.MaxValue
            || request.Sender.Length is < 1 or > 128 || request.Counts.Count > UsageMeter.MaxBatch || request.Counts.Sum(count => (long)count.Measurements.Length) > UsageMeter.MaxBatchMeasurementBytes || !request.Counts.All(count => Valid(count, latest)))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid usage batch"));
        }

        using var reservation = memory.TryReserve(MemoryLane.Replication, Math.Max(4096, request.Counts.Sum(count => (long)count.Measurements.Length) * 4))
            ?? throw new RpcException(new Status(StatusCode.ResourceExhausted, "usage memory admission exhausted"));

        // An observation from the future would outrank every later one; a sender's clock may run at most MaxSkew ahead.
        var horizon = time.GetUtcNow() + MaxSkew;
        if (!request.Counts.All(count => ValidMeasurements(count, horizon)))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid usage measurements"));
        }

        // Only databases this catalog names get pending counts, as a flush would keep only theirs: so what followers send is
        // bounded by the databases that exist, whatever ids a batch carries. Each project is checked once (two point reads),
        // then each database of an existing one (one).
        var owners = request.Counts.Select(count => new DatabaseOwner(count.OrganizationId, count.ProjectId, count.DatabaseId)).ToHashSet();
        var read = await catalog.ReadAsync<KnownOwners>(async transaction =>
        {
            var found = new HashSet<DatabaseOwner>();
            foreach (var project in owners.GroupBy(owner => (owner.OrganizationId, owner.ProjectId)))
            {
                if (!await EngineManagementService.ScopeExistsUncheckedAsync(
                    transaction, project.Key.OrganizationId, project.Key.ProjectId, databaseId: null, context.CancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                foreach (var owner in project)
                {
                    if (await EngineManagementService.DatabaseUncheckedAsync(transaction, owner.ProjectId, owner.DatabaseId, context.CancellationToken)
                        .ConfigureAwait(false) is not null)
                    {
                        found.Add(owner);
                    }
                }
            }

            return found;
        }, static error => error, context.CancellationToken).ConfigureAwait(false);
        if (read is not HashSet<DatabaseOwner> known)
        {
            // The peer retries the batch later, as for any unavailable primary.
            throw read switch
            {
                Unavailable error => new RpcException(new Status(StatusCode.Unavailable, error.Message)),
                HashSet<DatabaseOwner> => new UnreachableException(),
            };
        }

        var counts = request.Counts
            .Select(count => KeyValuePair.Create(
                new UsageKey(new DatabaseOwner(count.OrganizationId, count.ProjectId, count.DatabaseId), count.Hour, (UsageScope)count.Scope, (int)count.Minute),
                new OpCounts((long)count.Reads, (long)count.Writes, Measurements(count))))
            .Where(entry => known.Contains(entry.Key.Database))
            .ToList();
        return await meter.AcceptAsync(request.Sender, request.Epoch.ToBase64(), (long)request.Sequence, counts, context.CancellationToken)
            .ConfigureAwait(false) switch
        {
            UsageAccept accepted => accepted == UsageAccept.TooManySenders
                ? throw new RpcException(new Status(StatusCode.ResourceExhausted, "too many usage senders"))
                : new UsageAck(),
            Unavailable refused => throw new RpcException(new Status(StatusCode.Unavailable, refused.Message)),
        };
    }

    // Ids are those of the catalog the follower replicates; a flush drops the counts of a database it does not name. Counts
    // are capped, so a whole batch adds without overflow and is checked before any of it is added.
    private static bool Valid(UsageCount count, long latest) =>
        count.OrganizationId.Length is > 0 and <= 128 && count.ProjectId.Length is > 0 and <= 128 && count.DatabaseId.Length is > 0 and <= 128
        && count.Hour >= 0 && count.Hour <= latest && count.Reads <= UsageMeter.MaxCount && count.Writes <= UsageMeter.MaxCount
        && count.Minute < 60 && (count.Scope == Replication.V1.UsageScope.Minute || count.Minute == 0)
        && (UsageScope)count.Scope switch
        {
            // As compaction files them: a day at its first hour, lifetime counts at hour 0.
            UsageScope.Hour or UsageScope.Minute => true,
            UsageScope.Day => count.Hour % 24 == 0,
            UsageScope.Lifetime => count.Hour == 0,
            _ => false,
        };
    private static IReadOnlyDictionary<string, UsageReading>? Measurements(UsageCount count) => count.Measurements.IsEmpty ? null
        : JsonSerializer.Deserialize(count.Measurements.Span, TerraBaseJsonContext.Default.OpCounts)?.Meters;

    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    private static bool ValidMeasurements(UsageCount count, DateTimeOffset horizon)
    {
        if (count.Measurements.Length > UsageMeter.MaxEntryMeasurementBytes)
        {
            return false;
        }

        try
        {
            if (count.Measurements.IsEmpty)
            {
                return true;
            }

            var payload = JsonSerializer.Deserialize(count.Measurements.Span, TerraBaseJsonContext.Default.OpCounts);
            return payload is { Reads: 0, Writes: 0, Meters: { } readings }
                && readings.All(entry => entry.Value is { Dimension: { } dimension } reading
                    && entry.Key == dimension.Key && UsageMeter.ValidDimension(dimension)
                    && (UsageMeter.ByteMeter(dimension.Meter) ? reading.Count >= 0 && reading.Value >= 0
                        : reading.Count is >= 0 and <= UsageMeter.MaxCount && reading.Value is >= 0 and <= UsageMeter.MaxCount)
                    && (reading.Latency is null || dimension.Meter == "requests" && reading.Latency.Length == UsageReading.LatencyBounds.Count
                        && reading.Latency.All(value => value is >= 0 and <= UsageMeter.MaxCount) && reading.Latency.Sum() == reading.Count)
                    && (reading.State is null || dimension.Meter is "replicated_ops" or "replication_status"
                        && double.IsFinite(reading.State.LagMs) && reading.State.LagMs >= 0 && reading.State.Remaining >= 0
                        && (reading.State.ConvergenceMs is null || double.IsFinite(reading.State.ConvergenceMs.Value) && reading.State.ConvergenceMs >= 0)
                        && reading.State.ObservedAt >= DateTimeOffset.UnixEpoch && reading.State.ObservedAt <= horizon
                        && (reading.State.LastApplied is null || reading.State.LastApplied >= DateTimeOffset.UnixEpoch && reading.State.LastApplied <= horizon)));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The databases of a batch that this catalog names, or why it cannot tell.
    private readonly union KnownOwners(HashSet<DatabaseOwner>, Unavailable);
}
