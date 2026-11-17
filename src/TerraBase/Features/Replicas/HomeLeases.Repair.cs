using NATS.Client.KeyValueStore;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed record HomeLeaseRead(HomeLease? Lease, ulong? Revision);

internal sealed partial class HomeLeases
{
    internal async Task<HomeLeaseRead?> ReadLeaseAsync(string database, CancellationToken cancellationToken)
    {
        if (_kv is not { } kv || !Ready)
        {
            return null;
        }

        var entry = await ReadAsync(kv, database, cancellationToken).ConfigureAwait(false);
        return new HomeLeaseRead(entry?.Value is { } bytes ? Decode(bytes) : null, entry?.Revision);
    }

    internal async Task<string?> RepairBlockerAsync(string database, CancellationToken cancellationToken) =>
        !owners.Writer ? "not_writer"
        : owners.HandingOff ? "handing_off"
        : fences.Holds(database) ? "fenced"
        : (await MovingAsync(cancellationToken).ConfigureAwait(false)).Contains(database) ? "moving"
        : null;

    internal async Task<HomeLease?> RestampAsync(TenantContext database, DatabasePlacement placement, HomeLeaseRead read, CancellationToken cancellationToken)
    {
        if (_kv is not { } kv || !Ready)
        {
            return null;
        }

        var id = database.DatabaseId!;
        await _confirming.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await RepairBlockerAsync(id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return null;
            }

            var current = await ReadAsync(kv, id, cancellationToken).ConfigureAwait(false);
            if (current?.Revision != read.Revision)
            {
                return null;
            }

            var lease = new HomeLease(database.OrganizationId, database.ProjectId!, id, options.Region, options.MachineId, placement.Version, _session, read.Lease?.Adopted);
            var revision = current is { } entry
                ? await kv.UpdateAsync(HomeKey(id), Encode(lease), entry.Revision, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await kv.CreateAsync(HomeKey(id), Encode(lease), cancellationToken: cancellationToken).ConfigureAwait(false);
            _homes[id] = (lease, revision);
            _isolated.TryRemove(id, out _);
            _foreign.TryRemove(id, out _);
            leaseGate.Lease(id, held: true);
            return lease;
        }
        catch (Exception error) when (error is NatsKVWrongLastRevisionException or NatsKVCreateException)
        {
            return null;
        }
        finally
        {
            _confirming.Release();
        }
    }

    internal async Task<T> UnderHeldLeaseAsync<T>(
        TenantContext database, Func<CancellationToken, ValueTask<T>> bump, Func<T, long?> bumped, T refused, CancellationToken cancellationToken)
    {
        if (_js is null)
        {
            return await bump(cancellationToken).ConfigureAwait(false);
        }

        var id = database.DatabaseId!;
        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || placement.Home != options.Region || placement is { AllRegions: false, Replicas.Count: 0 })
        {
            return await bump(cancellationToken).ConfigureAwait(false);
        }

        if (_kv is not { } kv || !Ready)
        {
            return refused;
        }

        await _confirming.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isolated.ContainsKey(id) || !_homes.TryGetValue(id, out var known) || known.Lease.Node != options.MachineId
                || known.Lease.Session != _session || known.Lease.Epoch < placement.Version || !leaseGate.Writes(id))
            {
                return refused;
            }

            var result = await bump(cancellationToken).ConfigureAwait(false);
            if (bumped(result) is not { } version || version <= known.Lease.Epoch)
            {
                return result;
            }

            var stamped = known.Lease with { Epoch = version };
            try
            {
                var revision = await kv.UpdateAsync(HomeKey(id), Encode(stamped), known.Revision, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                _homes[id] = (stamped, revision);
            }
            catch (NatsKVWrongLastRevisionException)
            {
                leaseGate.Lease(id, held: false);
                logger.ZLogWarning($"The home lease of database {id:@Database} changed while its placement moved to version {version:@Version}; its writes wait until the lease is confirmed again");
            }

            return result;
        }
        finally
        {
            _confirming.Release();
        }
    }
}
