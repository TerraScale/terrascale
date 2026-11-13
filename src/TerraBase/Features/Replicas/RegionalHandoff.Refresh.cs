using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using TerraBase.Hosting;

namespace TerraBase.Features.Replicas;

internal sealed class OwnershipRecordException(string kind, int? length = null, string? owner = null) : IOException(kind)
{
    internal string Kind { get; } = kind;
    internal int? Length { get; } = length;
    internal string? Owner { get; } = owner;
}

internal sealed partial class RegionalHandoff
{
    private readonly Counter<long> _refreshErrors = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.ownership.refresh_errors",
        description: "Failed remote regional ownership observations.");
    private sealed record OwnershipFailure(string Kind, int? Length, string? Owner, string Error);
    private readonly Dictionary<string, OwnershipFailure> _refreshFailures = new(StringComparer.Ordinal);
    private OwnershipFailure? _renewalFailure;

    private async Task RefreshAsync(INatsKVStore kv, CancellationToken cancellationToken)
    {
        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshRegionAsync(kv, nodes, options.Region, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _changing.Release();
        }
    }

    private async Task ObserveRegionsAsync(NatsOpts configuration, CancellationToken cancellationToken)
    {
        await using var connection = new NatsConnection(configuration with
        {
            Name = $"terrabase-regional-observer-{options.MachineId}",
        });
        var context = new NatsKVContext(new NatsJSContext(connection));
        INatsKVStore? kv = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (kv is null)
                {
                    await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                    kv = await context.GetStoreAsync(Bucket, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var region in nodes.Select(node => node.Region).Concat(owners.Regions).Distinct(StringComparer.Ordinal).Where(region => region != options.Region))
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attempt.CancelAfter(AttemptBudget);
                    try
                    {
                        await RefreshRegionAsync(kv, nodes, region, attempt.Token).ConfigureAwait(false);
                        if (_refreshFailures.Remove(region))
                        {
                            LogRegionRecovered(logger, region);
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        RegionFailed(region, error);
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                foreach (var region in owners.Regions.Where(region => region != options.Region))
                {
                    RegionFailed(region, error);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private void RegionFailed(string region, Exception error)
    {
        owners.Unconfirm(region);
        _refreshErrors.Add(1, new TagList { { "region", region } });
        var state = Failure(error);
        if (!_refreshFailures.TryGetValue(region, out var previous) || previous != state)
        {
            _refreshFailures[region] = state;
            LogRegionFailed(logger, region, state.Kind, state.Length, state.Owner, state.Error);
        }
    }

    private static OwnershipFailure Failure(Exception error) => error switch
    {
        OwnershipRecordException record => new(record.Kind, record.Length, record.Owner, nameof(OwnershipRecordException)),
        OperationCanceledException or TimeoutException or NatsTimeoutException => new("timed_out", null, null, error.GetType().Name),
        _ => new("unavailable", null, null, error.GetType().Name),
    };

    private void RenewalFailed(Exception error)
    {
        var state = Failure(error);
        if (_renewalFailure != state)
        {
            _renewalFailure = state;
            LogRenewalFailed(logger, options.Region, state.Kind, state.Length, state.Owner, state.Error);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Regional ownership observation failed for {Region}; phase remote_observation, cause {Cause}, record length {Length}, owner {Owner}, error {Error}; retaining the last observed owner")]
    private static partial void LogRegionFailed(ILogger logger, string region, string cause, int? length, string? owner, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Regional ownership renewal failed for {Region}; phase own_renewal, cause {Cause}, record length {Length}, owner {Owner}, error {Error}; writes remain fenced after lease expiry")]
    private static partial void LogRenewalFailed(ILogger logger, string region, string cause, int? length, string? owner, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Regional ownership renewal recovered for {Region}")]
    private static partial void LogRenewalRecovered(ILogger logger, string region);

    [LoggerMessage(Level = LogLevel.Information, Message = "Regional ownership observation recovered for {Region}")]
    private static partial void LogRegionRecovered(ILogger logger, string region);

    private async Task RefreshRegionAsync(INatsKVStore kv, ImmutableArray<ServingNode> nodes, string region, CancellationToken cancellationToken)
    {
        var entry = await ReadAsync(kv, region, cancellationToken).ConfigureAwait(false);
        if (entry is null && region == options.Region)
        {
            var first = new RegionalOwner(region, nodes.Where(node => node.Region == region).MinBy(node => node.Name, StringComparer.Ordinal)!.Name, 1, Version: ProtocolVersion);
            try
            {
                _ = await kv.CreateAsync(region, Encode(first), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (NatsKVCreateException)
            {
            }

            entry = await ReadAsync(kv, region, cancellationToken).ConfigureAwait(false);
        }

        if (entry is { } observed)
        {
            var owner = Decode(observed.Value!);
            if (owner.Region != region)
            {
                throw new OwnershipRecordException("record_invalid", observed.Value!.Length);
            }
            if (!nodes.Any(node => node.Region == region && node.Name == owner.Node))
            {
                throw new OwnershipRecordException("owner_not_admitted", owner: owner.Node);
            }

            if (region == options.Region)
            {
                _startupOwner ??= owner;
            }

            if (region == options.Region && await RecoverTransferAsync(kv, observed, owner, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            Observe(owner);
            if (region == options.Region && owner.Node == options.MachineId)
            {
                await RenewWriterAsync(kv, observed, owner, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (region != options.Region)
        {
            throw new OwnershipRecordException("record_absent");
        }
    }
}
