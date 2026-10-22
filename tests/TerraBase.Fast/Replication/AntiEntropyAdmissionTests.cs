using System.Net;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class AntiEntropyAdmissionTests
{
    [Test]
    public async Task An_excessive_state_page_is_rejected_without_changing_existing_catalog_values()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<TimeProvider>(new FakeTimeProvider()));
        var services = server.Services;
        var id = EntityIds.New().ToString();
        using var body = JsonDocument.Parse("""{"value":"kept"}""");
        var original = new RepairState("admission", id, DocumentState.Bottom.Put(new EntryVersion(new Hlc(1_000), ReplicaId.Of(99, 1, 0)), body.RootElement));
        await Assert.That(await services.GetRequiredService<IChangeGroupSink>().RepairAsync(ChangeGroups.System, [original], token)).IsEqualTo(1);
        using var replacement = JsonDocument.Parse("""{"value":"replaced"}""");
        var updated = original with { State = original.State.Put(new EntryVersion(new Hlc(2_000), ReplicaId.Of(99, 1, 0)), replacement.RootElement) };
        await using var peer = await AdmissionPeer.StartAsync();
        peer.Page = AntiEntropy.Encode([.. Enumerable.Repeat(updated, 1025)], peer.Incarnation);
        var states = services.GetRequiredService<IReplicaStates>();
        var before = (await states.DigestAsync(ChangeGroups.System, null, token))!;

        _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await services.GetRequiredService<AntiEntropy>().PullAsync(ChangeGroups.System, peer.Member, token));

        var after = (await states.DigestAsync(ChangeGroups.System, null, token))!;
        await Assert.That(after.Buckets.SequenceEqual(before.Buckets)).IsTrue();
        var stored = await states.ReadBucketsAsync(ChangeGroups.System, after.Incarnation, [.. Enumerable.Range(0, 4096)], null, 1024, ChangeGroups.MaxBatchBytes, token);
        await Assert.That(stored!.Value.States.Single(state => state.Collection == original.Collection && state.Id == id).State).IsEqualTo(original.State);
    }

    [Test]
    [Arguments(1, 0)]
    [Arguments(1024, 0)]
    [Arguments(1, 100)]
    public async Task A_valid_state_page_with_an_unknown_protobuf_field_repairs_the_catalog(int count, int unknownGroups)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<TimeProvider>(new FakeTimeProvider()));
        _ = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var peer = await AdmissionPeer.StartAsync();
        using var body = JsonDocument.Parse("""{"value":"repaired"}""");
        var repair = new RepairState("admission", EntityIds.New().ToString(), DocumentState.Bottom.Put(new EntryVersion(new Hlc(1_000), ReplicaId.Of(99, 1, 0)), body.RootElement));
        var repairs = Enumerable.Range(0, count).Select(_ => repair with { Id = EntityIds.New().ToString() }).ToArray();
        peer.Page = AntiEntropy.Encode(repairs, peer.Incarnation);
        var plain = ApplicationEnvelope.Decode(peer.Page.States.Span, ReplicationProtocol.MaxApplicationBytes);
        // Unknown field 2 contains a byte equal to the state-entry tag; only top-level field 1 counts as a state.
        peer.Page.States = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(
            [.. plain, 18, 1, 10, .. Enumerable.Repeat((byte)19, unknownGroups), .. Enumerable.Repeat((byte)20, unknownGroups)]));
        await Assert.That(StateList.Parser.ParseFrom(ApplicationEnvelope.Decode(peer.Page.States.Span, ReplicationProtocol.MaxApplicationBytes)).States.Count).IsEqualTo(count);

        await Assert.That(await server.Services.GetRequiredService<AntiEntropy>().PullAsync(ChangeGroups.System, peer.Member, token)).IsEqualTo(count);

        var states = server.Services.GetRequiredService<IReplicaStates>();
        var digest = (await states.DigestAsync(ChangeGroups.System, null, token))!;
        var stored = await states.ReadBucketsAsync(ChangeGroups.System, digest.Incarnation, [.. Enumerable.Range(0, 4096)], null, 1024, ChangeGroups.MaxBatchBytes, token);
        await Assert.That(stored!.Value.States.Count(state => state.Collection == repair.Collection)).IsEqualTo(count);
        await Assert.That(stored.Value.States.Single(state => state.Id == repairs[0].Id).State).IsEqualTo(repair.State);
    }

    [Test]
    public async Task A_digest_envelope_larger_than_the_fixed_bucket_array_is_rejected_before_bucket_comparison()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<TimeProvider>(new FakeTimeProvider()));
        _ = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var peer = await AdmissionPeer.StartAsync();
        peer.Digests = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(new byte[65537]));

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await server.Services.GetRequiredService<AntiEntropy>().PullAsync(ChangeGroups.System, peer.Member, token));

        await Assert.That(error!.Message).IsEqualTo("envelopes decode to 1 to 65536 bytes");
        await Assert.That(peer.PageReads).IsEqualTo(0);
        await Assert.That(await server.Services.GetRequiredService<AntiEntropy>().CatalogHeldWithinAsync(AntiEntropy.Round, token)).IsEmpty();
    }

    private sealed class AdmissionPeer : Features.Replication.V1.Replication.ReplicationBase, IAsyncDisposable
    {
        private WebApplication? _app;
        private readonly string _machine = EntityIds.New().ToString();
        public Ulid Incarnation { get; } = EntityIds.New();
        public string Address { get; private set; } = "";
        public ClusterMember Member => new(_machine, Address, "test-region", "");
        public ByteString Digests { get; set; } = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(Enumerable.Repeat((byte)1, 65536).ToArray()));
        public StatePage? Page { get; set; }
        public int PageReads { get; private set; }

        public static async Task<AdmissionPeer> StartAsync()
        {
            var peer = new AdmissionPeer();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(peer);
            peer._app = builder.Build();
            peer._app.MapGrpcService<AdmissionPeer>();
            await peer._app.StartAsync();
            peer.Address = peer._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return peer;
        }

        public override Task<DigestReply> Digest(DigestRequest request, ServerCallContext context) => Task.FromResult(new DigestReply
        {
            Held = true,
            Incarnation = ByteString.CopyFrom(Incarnation.ToByteArray()),
            Buckets = Digests,
        });

        public override async Task ReadBucket(BucketRequest request, IServerStreamWriter<StatePage> responseStream, ServerCallContext context)
        {
            PageReads++;
            if (Page is { } page)
            {
                await responseStream.WriteAsync(page, context.CancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }
}
