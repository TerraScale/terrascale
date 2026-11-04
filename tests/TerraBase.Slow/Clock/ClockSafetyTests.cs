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
        var peers = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(peers, "wan remote-region 192.0.2.1\n");
            ClockSample before;
            await using (var first = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory,
                services =>
                {
                    var registered = services.Single(service => service.ServiceType == typeof(TerraBaseOptions));
                    var options = (TerraBaseOptions)registered.ImplementationInstance!;
                    services.Replace(ServiceDescriptor.Singleton(options with { PeersFile = peers }));
                }))
            {
                using var channel = Channel(first);
                var client = new NodeClock.NodeClockClient(channel);
                await ReadyAsync(client);
                before = await client.NextAsync(new ClockRequest());
            }

            var persisted = new HlcTimestamp(before.HlcPhysicalMilliseconds, before.HlcLogical, before.HlcNodeId);
            var encoded = new byte[HlcTimestamp.EncodedLength];
            persisted.WriteTo(encoded);
            await Assert.That(HlcTimestamp.ReadFrom(encoded)).IsEqualTo(persisted);
            var firstNode = new byte[HlcTimestamp.EncodedLength];
            var secondNode = new byte[HlcTimestamp.EncodedLength];
            (persisted with { NodeId = "a" }).WriteTo(firstNode);
            (persisted with { NodeId = "aa" }).WriteTo(secondNode);
            await Assert.That(firstNode.AsSpan().SequenceCompareTo(secondNode)).IsLessThan(0);

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
            var resumed = new HlcTimestamp(after.HlcPhysicalMilliseconds, after.HlcLogical, after.HlcNodeId);
            await Assert.That(resumed.CompareTo(persisted)).IsGreaterThan(0);
            var afterEncoded = new byte[HlcTimestamp.EncodedLength];
            resumed.WriteTo(afterEncoded);
            await Assert.That(afterEncoded.AsSpan().SequenceCompareTo(encoded)).IsGreaterThan(0);
            await Assert.That(HlcTimestamp.ReadFrom(afterEncoded)).IsEqualTo(resumed);
            await Assert.That(after.HlcNodeId).IsEqualTo(before.HlcNodeId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            File.Delete(peers);
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
        await using var skewed = await TerraBaseTestServer.StartAsync(services: services =>
            services.Replace(ServiceDescriptor.Singleton<IClockErrorSource>(new TestClockErrorSource(-100))));
        using var channel = Channel(skewed);
        var clock = new NodeClock.NodeClockClient(channel);
        await WaitForAsync(clock, sample => double.IsFinite(sample.SkewMilliseconds) && sample.SkewMilliseconds > 50);
        var sample = await clock.SampleAsync(new ClockRequest());
        await Assert.That(sample.LocalWritesAllowed).IsFalse();
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await clock.NextAsync(new ClockRequest()));
        await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.Unavailable);
        await ReadyAsync(healthy);
        var later = await healthy.NextAsync(new ClockRequest());
        await Assert.That(later.HlcPhysicalMilliseconds).IsGreaterThanOrEqualTo(newest.HlcPhysicalMilliseconds);
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
            throw new InvalidOperationException($"clock admission did not converge: {sample}", error);
        }
    }

    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        private long _offsetTicks = offset.Ticks;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

        public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);
    }
}
