using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Clock;
using TerraBase.Hosting;
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
                var reserved = await ReservationAsync(first, before.HlcNodeId);
                var issued = await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ => await client.NextAsync(new ClockRequest())));
                await Assert.That(issued.Select(stamp => (stamp.HlcPhysicalMilliseconds, stamp.HlcLogical)).Distinct().Count()).IsEqualTo(issued.Length);
                var newest = issued.MaxBy(stamp => (stamp.HlcPhysicalMilliseconds, stamp.HlcLogical))!;
                var observer = first.Services.GetRequiredService<IHlcClock>();
                var observed = await observer.ObserveAsync(new HlcTimestamp(newest.HlcPhysicalMilliseconds, newest.HlcLogical, "remote"), CancellationToken.None);
                await Assert.That(observed.CompareTo(new HlcTimestamp(newest.HlcPhysicalMilliseconds, newest.HlcLogical, newest.HlcNodeId))).IsGreaterThan(0);
                await Assert.That(await ReservationAsync(first, before.HlcNodeId)).IsEqualTo(reserved);
                await Assert.That(reserved.CompareTo(observed)).IsGreaterThan(0);
                before.HlcPhysicalMilliseconds = observed.PhysicalMilliseconds;
                before.HlcLogical = observed.Logical;
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
        var reserved = await ReservationAsync(peer, later.HlcNodeId);
        var future = new HlcTimestamp(reserved.PhysicalMilliseconds + 1, 0, "remote");
        var observed = await peer.Services.GetRequiredService<IHlcClock>().ObserveAsync(future, CancellationToken.None);
        await Assert.That(observed.CompareTo(future)).IsGreaterThan(0);
        await Assert.That((await ReservationAsync(peer, later.HlcNodeId)).CompareTo(observed)).IsGreaterThan(0);
        await Assert.That(peer.Services.GetRequiredService<ILocalWriteClock>().Allowed).IsFalse();
        var futureRefused = await Assert.ThrowsAsync<RpcException>(async () => await healthy.NextAsync(new ClockRequest()));
        await Assert.That(futureRefused!.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    private static GrpcChannel Channel(TerraBaseTestServer server) =>
        GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpClient = server.Client, DisposeHttpClient = false, CompressionProviders = [new ClockCompression()] });

    private static async Task<HlcTimestamp> ReservationAsync(TerraBaseTestServer server, string node)
    {
        var store = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var record = await transaction.GetAsync("node_clocks", node, CancellationToken.None);
        return JsonSerializer.Deserialize(record!.Json.Span, TerraBaseJsonContext.Default.HlcState)!.Timestamp;
    }

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
