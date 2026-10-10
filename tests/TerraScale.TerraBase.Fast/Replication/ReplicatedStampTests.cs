using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Bogus;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

// Replicated writes join with their own stamps and dots (docs/design/anna-core.md, Identities and dots): a stamp far ahead
// of this member's clock is applied but moves the clock at most the 1 s skew bound ahead, and a dot that arrives with a
// second value is refused before anything changes.
public sealed class ReplicatedStampTests
{
    [Test]
    public async Task A_stamp_far_ahead_is_applied_and_counted_but_moves_the_clock_only_the_skew_ahead()
    {
        await using var node = await NodeAsync();
        var meters = node.Server.Services.GetRequiredService<IMeterFactory>();
        var capped = 0L;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Meter.Scope == meters && instrument.Name == "terrabase.clock.skew_capped")
                {
                    observer.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref capped, value));
        listener.Start();

        var id = Guid.CreateVersion7().ToString("D");
        var ahead = new Hlc((ulong)DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() << 16);
        var far = Deltas.Write("items", id, "{\"value\":\"ahead\"}", stamp: ahead);
        await node.ApplyAsync(1, far);
        await Assert.That(Interlocked.Read(ref capped)).IsEqualTo(1);
        await Assert.That(await node.VersionAsync(id)).IsEqualTo(new DocumentVersion(checked((long)ahead.Value), Deleted: false));

        // A local write after it stamps within the skew of the physical clock, behind the stamp it observed.
        var other = Guid.CreateVersion7().ToString("D");
        using (var put = await node.Client.PutAsJsonAsync(node.Document(other), new { value = new Faker().Lorem.Word() }))
        {
            put.EnsureSuccessStatusCode();
        }

        var local = new Hlc((ulong)(await node.VersionAsync(other))!.Value.Version);
        await Assert.That(local.Value).IsLessThan(ahead.Value);
        await Assert.That(local.Milliseconds).IsLessThanOrEqualTo(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)Engine.MemberClock.MaxSkew.TotalMilliseconds);

        // A local write over the stamp far ahead still replaces it: it saw that write, whatever the stamps say.
        using (var put = await node.Client.PutAsJsonAsync(node.Document(id), new { value = "local" }))
        {
            put.EnsureSuccessStatusCode();
        }

        using var read = await node.Client.GetAsync(node.Document(id));
        await Assert.That(await read.Content.ReadAsStringAsync()).Contains("\"value\":\"local\"");
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, false, false)]
    [Arguments(false, true, true)]
    [Arguments(true, true, true)]
    [Arguments(false, false, true)]
    public async Task A_dot_that_arrives_with_a_second_value_is_refused_and_changes_nothing(bool tombstone, bool sameStamp, bool conflictLast)
    {
        await using var node = await NodeAsync();
        var id = Guid.CreateVersion7().ToString("D");
        var replica = Deltas.NewReplica();
        var stamp = new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16);
        await node.ApplyAsync(1, Deltas.Write("items", id, "{\"value\":\"first\"}", replica, stamp));
        var before = await node.VersionAsync(id);
        var offset = await node.OffsetAsync();

        // The same replica's first dot of the document again, with another value (a replica that minted it twice), under
        // another stamp or the same one. Sent after a valid delta, the refusal also rolls that delta back.
        var reused = Deltas.Write("items", id, tombstone ? null : "{\"value\":\"second\"}", replica, sameStamp ? stamp : new Hlc(stamp.Value + 1));
        var added = Deltas.Write("items", Guid.CreateVersion7().ToString("D"), "{}");
        var refused = conflictLast ? await node.SendAsync(2, added, reused) : await node.SendAsync(2, reused, added);
        await Assert.That(refused.FrameCase).IsEqualTo(SyncResponse.FrameOneofCase.Error);
        await Assert.That(refused.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorInvalidBatch);
        await Assert.That(await node.VersionAsync(id)).IsEqualTo(before);
        await Assert.That(await node.VersionAsync(added.Id)).IsNull();
        await Assert.That(await node.OffsetAsync()).IsEqualTo(offset);
        await Assert.That(await node.AppliedAsync()).IsEqualTo(1);
        using var read = await node.Client.GetAsync(node.Document(id));
        await Assert.That(await read.Content.ReadAsStringAsync()).Contains("\"value\":\"first\"");
    }

    private static async Task<Node> NodeAsync()
    {
        var peerTls = new PeerTls();
        var clientTls = new ClientTls();
        var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
        });
        var tenant = await ReplicationTenant.CreateAsync(server, "db");
        var client = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:read", "documents:write"));
        using (var collection = await client.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name = "items" }))
        {
            collection.EnsureSuccessStatusCode();
        }

        return new Node(server, tenant, client, peerTls, clientTls);
    }

    // One server with a fake peer's Sync stream into database db.
    private sealed class Node(TerraBaseTestServer server, ReplicationTenant tenant, HttpClient client, PeerTls peerTls, ClientTls clientTls) : IAsyncDisposable
    {
        private readonly byte[] _origin = RandomNumberGenerator.GetBytes(16);
        private Grpc.Net.Client.GrpcChannel? _channel;
        private AsyncDuplexStreamingCall<SyncRequest, SyncResponse>? _call;

        public TerraBaseTestServer Server => server;

        public HttpClient Client => client;

        public Uri Document(string id) => new($"{tenant.Path("db")}/collections/items/documents/by-key?id={id}", UriKind.Relative);

        public async Task ApplyAsync(long sequence, params DocumentDelta[] changes)
        {
            var durable = await SendAsync(sequence, changes);
            await Assert.That(durable.Durable?.DurablePrefix).IsEqualTo((ulong)sequence);
            await Assert.That((await NextAsync()).Applied?.AppliedPrefix).IsEqualTo((ulong)sequence);
        }

        public async Task<SyncResponse> SendAsync(long sequence, params DocumentDelta[] changes)
        {
            if (_call is null)
            {
                _channel = peerTls.CreateChannel(server.PeerAddress!);
                _call = new Features.Replication.V1.Replication.ReplicationClient(_channel).Sync();
                await _call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(tenant.Database("db"), _origin) });
                await Assert.That((await NextAsync()).FrameCase).IsEqualTo(SyncResponse.FrameOneofCase.Welcome);
            }

            await _call.RequestStream.WriteAsync(new SyncRequest
            {
                CommittedGroup = ReplicationProtocol.CreateBatch(tenant.Database("db"), _origin, sequence, Deltas.Group(changes)),
            });
            return await NextAsync();
        }

        public async Task<DocumentVersion?> VersionAsync(string id)
        {
            await using var read = await StoreAsync();
            return await read.GetVersionAsync("items", id, CancellationToken.None);
        }

        public async Task<long> OffsetAsync()
        {
            await using var read = await StoreAsync();
            return await read.GetOffsetAsync(CancellationToken.None);
        }

        public async Task<long> AppliedAsync()
        {
            await using var read = await StoreAsync();
            return await read.GetAppliedSequenceAsync(_origin, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            _call?.Dispose();
            _channel?.Dispose();
            client.Dispose();
            await server.DisposeAsync();
            peerTls.Dispose();
            clientTls.Dispose();
        }

        private async Task<IStorageTransaction> StoreAsync()
        {
            var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), CancellationToken.None))!;
            return await store.BeginTransactionAsync(write: false, CancellationToken.None);
        }

        private async Task<SyncResponse> NextAsync() =>
            await _call!.ResponseStream.MoveNext(CancellationToken.None) ? _call.ResponseStream.Current : throw new InvalidOperationException("the Sync stream ended");
    }
}
