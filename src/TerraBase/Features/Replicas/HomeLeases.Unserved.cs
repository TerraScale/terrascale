using NATS.Client.KeyValueStore;
using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal sealed partial class HomeLeases
{
    internal async Task<IReadOnlyDictionary<string, ulong>> ConfirmUnservedElsewhereAsync(IEnumerable<string> databases, CancellationToken cancellationToken)
    {
        Dictionary<string, ulong> confirmed = new(StringComparer.Ordinal);
        if (_kv is not { } kv)
        {
            return confirmed;
        }

        IReadOnlySet<string>? moving = null;
        foreach (var id in databases.Distinct(StringComparer.Ordinal))
        {
            if (!_unserved.TryGetValue(id, out var hint))
            {
                continue;
            }
            moving ??= await MovingAsync(cancellationToken).ConfigureAwait(false);
            if (await UnservedRevisionAsync(kv, hint, moving, cancellationToken).ConfigureAwait(false) is { } revision)
            {
                confirmed[id] = revision;
            }
        }
        return confirmed;
    }

    internal async Task<bool> StillUnservedElsewhereAsync(string database, ulong revision, CancellationToken cancellationToken) =>
        _kv is { } kv && _unserved.TryGetValue(database, out var hint)
        && await UnservedRevisionAsync(kv, hint, await MovingAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false) == revision;

    private async Task<ulong?> UnservedRevisionAsync(INatsKVStore kv, UnservedDatabase hint, IReadOnlySet<string> moving, CancellationToken cancellationToken)
    {
        if (await ReadAsync(kv, hint.DatabaseId, cancellationToken).ConfigureAwait(false) is not { Value: { } bytes } read || Decode(bytes) is not { } lease
            || lease.Home == options.Region || lease.Home != hint.Region || lease.Node != hint.Node || lease.Epoch != hint.Epoch
            || owners.Confirmed(lease.Home) is not { } owner)
        {
            return null;
        }

        var database = new TenantContext(lease.OrganizationId, PlanTier.Hobby, lease.ProjectId, lease.DatabaseId);
        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || placement.Home == options.Region)
        {
            return null;
        }

        var live = await NodeLiveAsync(kv, lease.Node, cancellationToken).ConfigureAwait(false);
        if (UnservedCause(lease, placement, live, owner, moving) != hint.Cause || owners.Confirmed(lease.Home) != owner)
        {
            return null;
        }

        return await ReadAsync(kv, hint.DatabaseId, cancellationToken).ConfigureAwait(false) is { } again && again.Revision == read.Revision
            ? read.Revision : null;
    }

    private static async Task<bool> NodeLiveAsync(INatsKVStore kv, string node, CancellationToken cancellationToken)
    {
        try
        {
            return await kv.GetEntryAsync<byte[]>("node." + node, cancellationToken: cancellationToken).ConfigureAwait(false) is { Value: not null };
        }
        catch (Exception error) when (error is NatsKVKeyNotFoundException or NatsKVKeyDeletedException)
        {
            return false;
        }
    }

    private static string? UnservedCause(HomeLease lease, DatabasePlacement placement, bool nodeLive, RegionalOwner? homeOwner, IReadOnlySet<string> moving) =>
        !nodeLive ? "lapsed"
        : homeOwner is { } owner && owner.Node != lease.Node ? "not_writer"
        : lease.Home != placement.Home && lease.Epoch < placement.Version && !moving.Contains(lease.DatabaseId) ? "stale_lease"
        : null;
}
