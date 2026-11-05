using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Hosting;

namespace TerraBase.Features.Clock;

internal interface IHlcState
{
    long HighWater { get; }
    ValueTask RefreshAsync(CancellationToken cancellationToken);
}

internal sealed class NodeHlcClock(IStorageEngine engine, TerraBaseOptions options, TimeProvider time) : IHlcClock, IHlcState, IDisposable
{
    private const long ReservationMilliseconds = 5_000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HlcTimestamp _previous;
    private HlcTimestamp _reserved;
    private bool _initialized;
    private long _highWater;

    public long HighWater => Volatile.Read(ref _highWater);

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            return await AdvanceLockedAsync(remote, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var record = await transaction.GetAsync("node_clocks", options.MachineId, cancellationToken).ConfigureAwait(false);
        // A restart skips every timestamp that the previous process could have issued.
        _previous = _reserved = ReadState(record?.Json);
        Remember(_previous.PhysicalMilliseconds);
        _initialized = true;
    }

    private async ValueTask<HlcTimestamp> AdvanceLockedAsync(HlcTimestamp remote, CancellationToken cancellationToken)
    {
        var previous = _previous;
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
        if (timestamp > _reserved)
        {
            var reserved = new HlcTimestamp(checked(physical + ReservationMilliseconds), uint.MaxValue, options.MachineId);
            var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
            _ = await transaction.PutAsync("node_clocks", options.MachineId,
                JsonSerializer.SerializeToUtf8Bytes(new HlcState(1, reserved), TerraBaseJsonContext.Default.HlcState), cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            // Publish the reservation only after its durable commit succeeds.
            _reserved = reserved;
        }

        _previous = timestamp;
        // Admission tracks issued/observed time, not this process's unused reservation.
        Remember(timestamp.PhysicalMilliseconds);
        return timestamp;
    }

    private HlcTimestamp ReadState(ReadOnlyMemory<byte>? json)
    {
        if (json is null)
        {
            return default;
        }

        var state = JsonSerializer.Deserialize(json.Value.Span, TerraBaseJsonContext.Default.HlcState);
        if (state is not { Version: 1 } || state.Timestamp.PhysicalMilliseconds < 0 || state.Timestamp.NodeId != options.MachineId)
        {
            throw new InvalidDataException("the persisted HLC is invalid");
        }

        return state.Timestamp;
    }

    private void Remember(long physical)
    {
        var previous = HighWater;
        while (physical > previous)
        {
            var observed = Interlocked.CompareExchange(ref _highWater, physical, previous);
            if (observed == previous)
            {
                return;
            }

            previous = observed;
        }
    }

}

internal sealed record HlcState(int Version, HlcTimestamp Timestamp);
