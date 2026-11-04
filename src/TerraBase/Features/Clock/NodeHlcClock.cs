using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Hosting;

namespace TerraBase.Features.Clock;

internal sealed class NodeHlcClock(IStorageEngine engine, TerraBaseOptions options, TimeProvider time) : IHlcClock
{
    public ValueTask<HlcTimestamp> NextAsync(CancellationToken cancellationToken) => AdvanceAsync(default, cancellationToken);

    public ValueTask<HlcTimestamp> ObserveAsync(HlcTimestamp remote, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(remote.PhysicalMilliseconds);
        if (!TerraBaseOptions.IsMachineId(remote.NodeId))
        {
            throw new ArgumentException("the remote HLC must identify a node", nameof(remote));
        }

        return AdvanceAsync(remote, cancellationToken);
    }

    private async ValueTask<HlcTimestamp> AdvanceAsync(HlcTimestamp remote, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        var record = await transaction.GetAsync("node_clocks", options.MachineId, cancellationToken).ConfigureAwait(false);
        var state = record is null ? null : JsonSerializer.Deserialize(record.Json.Span, TerraBaseJsonContext.Default.HlcState);
        if (record is not null && (state is not { Version: 1 } || state.Timestamp.PhysicalMilliseconds < 0 || state.Timestamp.NodeId != options.MachineId))
        {
            throw new InvalidDataException("the persisted HLC is invalid");
        }

        var previous = state?.Timestamp ?? default;
        var physical = Math.Max(time.GetUtcNow().ToUnixTimeMilliseconds(), Math.Max(previous.PhysicalMilliseconds, remote.PhysicalMilliseconds));
        var logical = physical == previous.PhysicalMilliseconds && physical == remote.PhysicalMilliseconds ? (long)Math.Max(previous.Logical, remote.Logical) + 1
            : physical == previous.PhysicalMilliseconds ? (long)previous.Logical + 1
            : physical == remote.PhysicalMilliseconds ? (long)remote.Logical + 1 : 0;
        if (logical > uint.MaxValue)
        {
            physical = checked(physical + 1);
            logical = 0;
        }

        var timestamp = new HlcTimestamp(physical, (uint)logical, options.MachineId);
        _ = await transaction.PutAsync("node_clocks", options.MachineId,
            JsonSerializer.SerializeToUtf8Bytes(new HlcState(1, timestamp), TerraBaseJsonContext.Default.HlcState), cancellationToken).ConfigureAwait(false);
        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return timestamp;
    }
}

internal sealed record HlcState(int Version, HlcTimestamp Timestamp);
