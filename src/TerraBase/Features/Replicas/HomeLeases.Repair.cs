using NATS.Client.KeyValueStore;
using TerraBase.Tenancy;

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
}
