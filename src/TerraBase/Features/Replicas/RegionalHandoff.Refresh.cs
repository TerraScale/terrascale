using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using NATS.Client.KeyValueStore;
using TerraBase.Hosting;

namespace TerraBase.Features.Replicas;

internal sealed partial class RegionalHandoff
{
    private readonly Counter<long> _refreshErrors = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.ownership.refresh_errors",
        description: "Failed remote regional ownership observations.");
    private readonly Dictionary<string, (Type Type, string Message)> _refreshFailures = new(StringComparer.Ordinal);

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

    private async Task ObserveRegionsAsync(INatsKVStore kv, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
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
        var state = (error.GetType(), error.Message);
        if (!_refreshFailures.TryGetValue(region, out var previous) || previous != state)
        {
            _refreshFailures[region] = state;
            LogRegionFailed(logger, error, region);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Regional ownership observation failed for {Region}; retaining the last observed owner")]
    private static partial void LogRegionFailed(ILogger logger, Exception exception, string region);

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
            if (owner.Region != region || !nodes.Any(node => node.Region == region && node.Name == owner.Node))
            {
                throw new InvalidDataException("regional owner is outside admitted peers");
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
            throw new InvalidDataException("regional owner record is missing");
        }
    }
}
