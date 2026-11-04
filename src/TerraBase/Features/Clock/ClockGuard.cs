using System.Diagnostics.Metrics;
using Grpc.Core;
using Grpc.Net.Client;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;

namespace TerraBase.Features.Clock;

internal sealed record ClockReport(string Node, string Region, double? SkewMilliseconds, bool LocalWritesAllowed, DateTimeOffset SampledAt);

internal sealed class ClockGuard(
    TerraBaseOptions options, InternalCredential credential, TimeProvider time, IMeterFactory meters, IHlcState state) : BackgroundService, ILocalWriteClock
{
    private const double LimitMilliseconds = 50;
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(15);
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly TimeProvider _elapsed = TimeProvider.System;
    private Reading? _reading;
    private int _reported;

    public bool Allowed => SkewMilliseconds <= LimitMilliseconds;

    public double SkewMilliseconds
    {
        get
        {
            var reading = Volatile.Read(ref _reading);
            if (reading is null || _elapsed.GetElapsedTime(reading.At) is var age && age > FreshFor)
            {
                return double.PositiveInfinity;
            }

            var before = _elapsed.GetTimestamp();
            var utc = time.GetUtcNow().ToUnixTimeMilliseconds();
            var after = _elapsed.GetTimestamp();
            var lower = utc - reading.Utc - _elapsed.GetElapsedTime(reading.At, after).TotalMilliseconds - 1;
            var upper = utc - reading.Utc - _elapsed.GetElapsedTime(reading.After, before).TotalMilliseconds + 1;
            var jump = Math.Max(Math.Abs(lower), Math.Abs(upper));
            return Math.Max(reading.Skew + jump, state.HighWater - utc);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var meter = meters.Create(Telemetry.MeterName);
        _ = meter.CreateObservableGauge("terrabase.clock.skew", () => SkewMilliseconds, "ms");
        _ = meter.CreateObservableGauge("terrabase.clock.local_writes_allowed", () => Allowed ? 1 : 0);
        _ = meter.CreateObservableGauge("terrabase.clock.reported", () => Volatile.Read(ref _reported));
        await using var reports = new ClockReports(options);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            var sampleAt = _elapsed.GetTimestamp();
            var sampleUtc = time.GetUtcNow().ToUnixTimeMilliseconds();
            var sampleAfter = _elapsed.GetTimestamp();
            var skew = KernelClock.ErrorMilliseconds();
            try
            {
                await state.RefreshAsync(stoppingToken).ConfigureAwait(false);
                var nodes = options.PeersFile is null ? [] : await new PeersFile(options).ListAsync(stoppingToken).ConfigureAwait(false);
                var others = nodes.Where(node => node.Name != options.MachineId).ToList();
                var regional = others.Where(node => node.Region == options.Region).ToList();
                var peers = (regional.Count > 0 ? regional : others).Take(64).ToList();
                var lower = new List<double> { 0 };
                var upper = new List<double> { 0 };
                foreach (var peer in peers)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        using var channel = GrpcChannel.ForAddress(new UriBuilder(Uri.UriSchemeHttp, peer.Address.ToString(), options.InternalPort).Uri,
                            new GrpcChannelOptions { HttpClient = _http, DisposeHttpClient = false, MaxReceiveMessageSize = 4096, CompressionProviders = [new ClockCompression()] });
                        var at = _elapsed.GetTimestamp();
                        var start = time.GetUtcNow().ToUnixTimeMilliseconds();
                        var sample = await new NodeClock.NodeClockClient(channel).SampleAsync(new ClockRequest(), cancellationToken: timeout.Token)
                            .ResponseAsync.ConfigureAwait(false);
                        var duration = _elapsed.GetElapsedTime(at).TotalMilliseconds;
                        var end = time.GetUtcNow().ToUnixTimeMilliseconds();
                        if (Math.Abs(end - start - duration) > LimitMilliseconds)
                        {
                            skew = double.PositiveInfinity;
                        }

                        lower.Add(sample.UtcMilliseconds - start - duration - 1);
                        upper.Add(sample.UtcMilliseconds - start + 1);
                    }
                    catch (Exception error) when (error is RpcException or HttpRequestException or OperationCanceledException && !stoppingToken.IsCancellationRequested)
                    {
                    }
                }

                skew = lower.Count <= (peers.Count + 1) / 2 || peers.Count == 64
                    ? double.PositiveInfinity : Math.Max(skew, Math.Max(Math.Abs(Median(lower, upper: false)), Math.Abs(Median(upper, upper: true))));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageBusyException)
            {
                skew = double.PositiveInfinity;
            }

            Volatile.Write(ref _reading, new Reading(skew, sampleAt, sampleAfter, sampleUtc));
            var value = SkewMilliseconds;
            var stored = await reports.PublishAsync(new ClockReport(options.MachineId, options.Region, double.IsFinite(value) ? value : null, value <= LimitMilliseconds, time.GetUtcNow()), stoppingToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _reported, stored ? 1 : 0);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private static double Median(List<double> values, bool upper)
    {
        values.Sort();
        return values[upper ? values.Count / 2 : (values.Count - 1) / 2];
    }

    private sealed record Reading(double Skew, long At, long After, long Utc);
}
