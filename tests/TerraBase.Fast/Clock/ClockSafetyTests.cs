using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Features.Clock;
using TerraBase.Testing;

namespace TerraBase.Fast.Clock;

public sealed class ClockSafetyTests
{
    [Test]
    public async Task IssuedTimestampsRemainMonotonicAfterRestartWithABackwardClock()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-hlc-").FullName;
        try
        {
            ClockSample before;
            await using (var first = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory))
            {
                using var channel = Channel(first);
                var client = new NodeClock.NodeClockClient(channel);
                await ReadyAsync(client);
                before = await client.NextAsync(new ClockRequest());
            }

            var time = new OffsetClock(TimeSpan.FromHours(-1));
            await using var second = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory,
                services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time)));
            using var reopened = Channel(second);
            var clock = new NodeClock.NodeClockClient(reopened);
            await WaitForAsync(clock, sample => double.IsFinite(sample.SkewMilliseconds));
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await clock.NextAsync(new ClockRequest()));
            await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.Unavailable);
            time.Reset();
            await ReadyAsync(clock);
            var after = await clock.NextAsync(new ClockRequest());
            await Assert.That(new HlcTimestamp(after.HlcPhysicalMilliseconds, after.HlcLogical, after.HlcNodeId)
                .CompareTo(new HlcTimestamp(before.HlcPhysicalMilliseconds, before.HlcLogical, before.HlcNodeId))).IsGreaterThan(0);
            await Assert.That(after.HlcNodeId).IsEqualTo(before.HlcNodeId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task AClockOneHundredMillisecondsAheadCannotIssueALocalWriteTimestamp()
    {
        await using var peer = await TerraBaseTestServer.StartAsync();
        using var peerChannel = Channel(peer);
        var healthy = new NodeClock.NodeClockClient(peerChannel);
        await ReadyAsync(healthy);
        var newest = await healthy.NextAsync(new ClockRequest());
        var peers = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(peers, "peer test-region 127.0.0.1\n");
            await using var skewed = await TerraBaseTestServer.StartAsync(services: services =>
            {
                var registered = services.Single(service => service.ServiceType == typeof(TerraBaseOptions));
                var options = (TerraBaseOptions)registered.ImplementationInstance!;
                services.Replace(ServiceDescriptor.Singleton(options with { PeersFile = peers, InternalPort = peer.BaseAddress.Port }));
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new OffsetClock(TimeSpan.FromMilliseconds(100))));
            });
            using var channel = Channel(skewed);
            var clock = new NodeClock.NodeClockClient(channel);
            await WaitForAsync(clock, sample => double.IsFinite(sample.SkewMilliseconds) && sample.SkewMilliseconds > 50);
            var sample = await clock.SampleAsync(new ClockRequest());
            await Assert.That(sample.LocalWritesAllowed).IsFalse();
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await clock.NextAsync(new ClockRequest()));
            await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.Unavailable);
            var later = await healthy.NextAsync(new ClockRequest());
            await Assert.That(later.HlcPhysicalMilliseconds).IsGreaterThanOrEqualTo(newest.HlcPhysicalMilliseconds);
        }
        finally
        {
            File.Delete(peers);
        }
    }

    private static GrpcChannel Channel(TerraBaseTestServer server) =>
        GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpClient = server.Client, DisposeHttpClient = false, CompressionProviders = [new ClockCompression()] });

    private static Task ReadyAsync(NodeClock.NodeClockClient clock) => WaitForAsync(clock, sample => sample.LocalWritesAllowed);

    private static async Task WaitForAsync(NodeClock.NodeClockClient clock, Func<ClockSample, bool> predicate)
    {
        ClockSample? sample = null;
        try
        {
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                sample = await clock.SampleAsync(new ClockRequest(), cancellationToken: token);
                return predicate(sample);
            }, timeout: TimeSpan.FromSeconds(15));
        }
        catch (OperationCanceledException error)
        {
            throw new InvalidOperationException($"clock admission did not converge: {sample}; kernel skew: {KernelClock.ErrorMilliseconds()}", error);
        }
    }

    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        private long _offsetTicks = offset.Ticks;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

        public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);
    }
}
