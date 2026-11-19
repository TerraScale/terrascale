using NATS.Client.KeyValueStore;
using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal sealed partial class HomeLeases
{
    internal async Task<IReadOnlySet<string>> ConfirmedUnservedElsewhereAsync(CancellationToken cancellationToken)
    {
        if (_kv is not { } kv || _unserved.IsEmpty)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var moving = await MovingAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> confirmed = new(StringComparer.Ordinal);
        foreach (var hint in _unserved.Values)
        {
            if (await StillUnservedElsewhereAsync(kv, hint, moving, cancellationToken).ConfigureAwait(false))
            {
                confirmed.Add(hint.DatabaseId);
            }
        }
        return confirmed;
    }

    private async Task<bool> StillUnservedElsewhereAsync(INatsKVStore kv, UnservedDatabase hint, IReadOnlySet<string> moving, CancellationToken cancellationToken)
    {
        if (await ReadAsync(kv, hint.DatabaseId, cancellationToken).ConfigureAwait(false) is not { Value: { } bytes } || Decode(bytes) is not { } lease
            || lease.Home == options.Region || lease.Home != hint.Region || lease.Node != hint.Node || lease.Epoch != hint.Epoch)
        {
            return false;
        }

        var database = new TenantContext(lease.OrganizationId, PlanTier.Hobby, lease.ProjectId, lease.DatabaseId);
        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || placement.Home == options.Region)
        {
            return false;
        }

        var live = await NodeLiveAsync(kv, lease.Node, cancellationToken).ConfigureAwait(false);
        return UnservedCause(lease, placement, live, owners.Confirmed(lease.Home), moving) == hint.Cause;
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
