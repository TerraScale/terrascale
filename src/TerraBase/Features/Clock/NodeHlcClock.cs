using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Clock;

internal sealed class NodeHlcClock(IStorageEngine engine, TerraBaseOptions options, TimeProvider time, ClockTrust trust, IMeterFactory meters, ILogger<NodeHlcClock> logger) : IHlcClock, IDisposable
{
    private const long ReservationMilliseconds = 5_000;
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private readonly Counter<long> _capped = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.clock.observe_capped", "{observation}");
    private readonly Counter<long> _stamps = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.clock.stamps", "{stamp}",
        "Stamps this node was asked to issue for its own writes, by outcome (issued or refused).");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HlcTimestamp _previous;
    private HlcTimestamp _reserved;
    private bool _initialized;
    private long _highWater;

    private long HighWater => Volatile.Read(ref _highWater);

    public void Dispose() => _gate.Dispose();

    public async Task SaveLastStampAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_initialized && _previous < _reserved)
            {
                await SaveAsync(_previous, CancellationToken.None).ConfigureAwait(false);
                _reserved = _previous;
                logger.ZLogInformation($"Saved the last HLC stamp {_previous.PhysicalMilliseconds:@Physical}.{_previous.Logical:@Logical} at shutdown");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<HlcTimestamp?> IssueAsync(CancellationToken cancellationToken)
    {
        if (!trust.Trusted)
        {
            return Refused();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await ReadPreviousAsync(cancellationToken).ConfigureAwait(false);
            var utc = time.GetUtcNow().ToUnixTimeMilliseconds();
            var stamp = Timestamp(Tick(previous, utc));
            if (!trust.Trusted || stamp.PhysicalMilliseconds > checked(utc + ClockTrust.BudgetMilliseconds))
            {
                return Refused();
            }

            Initialize(previous);
            await AdvanceLockedAsync(stamp, cancellationToken).ConfigureAwait(false);
            _stamps.Add(1, new KeyValuePair<string, object?>("outcome", "issued"));
            return stamp;
        }
        finally
        {
            _gate.Release();
        }
    }

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
            Initialize(await ReadPreviousAsync(cancellationToken).ConfigureAwait(false));
            return await AdvanceLockedAsync(Timestamp(Merge(_previous, Capped(remote))), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<HlcTimestamp> ReadPreviousAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return _previous;
        }

        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var record = await transaction.GetAsync("node_clocks", options.MachineId, cancellationToken).ConfigureAwait(false);
        return ReadState(record?.Json);
    }

    private void Initialize(HlcTimestamp previous)
    {
        if (_initialized)
        {
            return;
        }

        _previous = _reserved = previous;
        Remember(_previous.PhysicalMilliseconds);
        _ = _meter.CreateObservableGauge("terrabase.clock.hlc_ahead", () => Math.Max(0, HighWater - time.GetUtcNow().ToUnixTimeMilliseconds()), "ms");
        _initialized = true;
    }

    private HlcTimestamp Timestamp((long Physical, long Logical) candidate)
    {
        var (physical, logical) = candidate;
        if (logical > uint.MaxValue)
        {
            physical = checked(physical + 1);
            logical = 0;
        }

        return new HlcTimestamp(physical, (uint)logical, options.MachineId);
    }

    private async ValueTask<HlcTimestamp> AdvanceLockedAsync(HlcTimestamp timestamp, CancellationToken cancellationToken)
    {
        if (timestamp > _reserved)
        {
            var reserved = new HlcTimestamp(checked(timestamp.PhysicalMilliseconds + ReservationMilliseconds), uint.MaxValue, options.MachineId);
            await SaveAsync(reserved, cancellationToken).ConfigureAwait(false);
            _reserved = reserved;
        }

        _previous = timestamp;
        Remember(timestamp.PhysicalMilliseconds);
        return timestamp;
    }

    private static (long Physical, long Logical) Merge(HlcTimestamp previous, HlcTimestamp received) =>
        previous.PhysicalMilliseconds == received.PhysicalMilliseconds ? (previous.PhysicalMilliseconds, (long)Math.Max(previous.Logical, received.Logical) + 1)
        : previous.PhysicalMilliseconds > received.PhysicalMilliseconds ? (previous.PhysicalMilliseconds, (long)previous.Logical + 1)
        : (received.PhysicalMilliseconds, (long)received.Logical + 1);

    private static (long Physical, long Logical) Tick(HlcTimestamp previous, long utc) =>
        utc > previous.PhysicalMilliseconds ? (utc, 0) : (previous.PhysicalMilliseconds, (long)previous.Logical + 1);

    private HlcTimestamp Capped(HlcTimestamp remote)
    {
        var utc = time.GetUtcNow().ToUnixTimeMilliseconds();
        var ceiling = checked(utc + ClockTrust.BudgetMilliseconds);
        if (remote.PhysicalMilliseconds <= ceiling)
        {
            return remote;
        }

        _capped.Add(1);
        logger.ZLogWarning($"Remote clock from {remote.NodeId:@Node} is {remote.PhysicalMilliseconds - utc:@AheadMilliseconds} ms ahead; observing at {ceiling:@Ceiling}");
        return remote with { PhysicalMilliseconds = ceiling, Logical = 0 };
    }

    private async ValueTask SaveAsync(HlcTimestamp timestamp, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        _ = await transaction.PutAsync("node_clocks", options.MachineId,
            JsonSerializer.SerializeToUtf8Bytes(new HlcState(1, timestamp), ClockJsonContext.Default.HlcState), cancellationToken).ConfigureAwait(false);
        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private HlcTimestamp? Refused()
    {
        _stamps.Add(1, new KeyValuePair<string, object?>("outcome", "refused"));
        return null;
    }

    private HlcTimestamp ReadState(ReadOnlyMemory<byte>? json)
    {
        if (json is null)
        {
            return default;
        }

        var state = JsonSerializer.Deserialize(json.Value.Span, ClockJsonContext.Default.HlcState);
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
