using System.Text.Json;
using Bogus;
using Google.Protobuf;
using TerraBase.Engine;
using TerraBase.Features.Replication.V1;
using TerraBase.Testing;

namespace TerraBase.Slow.Replication;

public sealed class ReplicationObservabilityTests
{
    [Test]
    public async Task Healthy_revalidation_overlapping_discovery_does_not_record_another_convergence()
    {
        using var tls = new PeerTls();
        using var clientTls = new ClientTls();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = 0UL;
        ByteString? digest = null;
        await using var peer = await FakePeer.StartAsync(tls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            if (applied > 0 && !release.Task.IsCompleted)
            {
                held.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
            }

            var hello = requests.Current.Hello;
            var welcome = FakePeer.Welcome(hello, out var member);
            welcome.Welcome.Frontiers.Add(new PeerFrontier
            {
                Origin = new OriginIdentity { ReplicaId = hello.LocalIdentity.MemberIncarnation, Incarnation = hello.LocalIdentity.MemberIncarnation },
                DurableSequence = applied,
                JournalDigest = digest ?? ByteString.CopyFrom(JournalDigest.Initial(hello.LocalIdentity.MemberIncarnation.Span)),
            });
            await responses.WriteAsync(welcome, context.CancellationToken);
            while (await requests.MoveNext(context.CancellationToken))
            {
                var batch = requests.Current.CommittedGroup;
                applied = batch.EventId.Sequence;
                digest = batch.JournalDigest;
                await responses.WriteAsync(FakePeer.Ack(member, batch, applied), context.CancellationToken);
            }
        });
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
            settings["write-wait-ms"] = "0";
        });
        var token = TestContext.Current!.Execution.CancellationToken;
        var tenant = await ReplicationTenant.CreateAsync(server, "shop");
        using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:write"));
        using var created = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("shop")}/collections", UriKind.Relative),
            new { name = new Faker().Random.String2(12, "abcdefghijklmnopqrstuvwxyz") }, token);
        created.EnsureSuccessStatusCode();
        await held.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        var convergences = await server.MetricAsync("terrabase_replication_convergence_seconds_count", "", token);
        var passes = await server.MetricAsync("terrabase_replication_sender_discovery_passes_total", "", token);
        // Observe a real subsequent discovery pass while the unchanged valid Welcome is held.
        await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
            await server.MetricAsync("terrabase_replication_sender_discovery_passes_total", "", cancellationToken) > passes);
        using var status = await server.Client.GetFromJsonAsync<JsonDocument>(new Uri("/replication", UriKind.Relative), token);
        var database = status!.RootElement.GetProperty("peers")[0].GetProperty("databases")[0];
        await Assert.That(database.GetProperty("durableLag").GetInt64()).IsEqualTo(1);
        await Assert.That(database.GetProperty("catchupSeconds").GetDouble()).IsEqualTo(0);
        await Assert.That(await server.MetricAsync("terrabase_replication_database_catchup_seconds", "", token)).IsEqualTo(0);
        await Assert.That(await server.MetricAsync("terrabase_replication_convergence_seconds_count", "", token)).IsEqualTo(convergences);
        release.TrySetResult();
        await TerraBaseTestServer.ReplicatedAsync(server);
        await Assert.That(await server.MetricAsync("terrabase_replication_convergence_seconds_count", "", token)).IsEqualTo(convergences);
    }

    [Test]
    public async Task Database_detail_is_bounded_and_omitted_databases_still_count_in_peer_lag()
    {
        using var tls = new PeerTls();
        using var clientTls = new ClientTls();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ByteString? omittedId = null;
        await using var peer = await FakePeer.StartAsync(tls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            await release.Task.WaitAsync(context.CancellationToken);
            var hello = requests.Current.Hello;
            var welcome = FakePeer.Welcome(hello, out var member);
            if (hello.LocalIdentity.DatabaseId == omittedId)
            {
                welcome.Welcome.Frontiers.Add(new PeerFrontier
                {
                    Origin = new OriginIdentity { ReplicaId = hello.LocalIdentity.MemberIncarnation, Incarnation = hello.LocalIdentity.MemberIncarnation },
                    DurableSequence = 2,
                    JournalDigest = ByteString.CopyFrom(JournalDigest.Initial(hello.LocalIdentity.MemberIncarnation.Span)),
                });
            }

            await responses.WriteAsync(welcome, context.CancellationToken);
            while (await requests.MoveNext(context.CancellationToken))
            {
                var batch = requests.Current.CommittedGroup;
                await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence), context.CancellationToken);
            }
        });
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
            settings["write-wait-ms"] = "0";
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        var faker = new Faker();
        for (var offset = 0; offset < 129; offset += 32)
        {
            var names = Enumerable.Range(offset, Math.Min(32, 129 - offset)).Select(index => $"db-{index}").ToArray();
            var tenant = await ReplicationTenant.CreateAsync(server, names);
            using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:write"));
            foreach (var name in names)
            {
                using var created = await writer.PostAsJsonAsync(new Uri($"{tenant.Path(name)}/collections", UriKind.Relative),
                    new { name = faker.Random.String2(12, "abcdefghijklmnopqrstuvwxyz") }, token);
                created.EnsureSuccessStatusCode();
                if (offset == 128)
                {
                    omittedId = ByteString.CopyFrom(Ulid.Parse(tenant.Database(name).DatabaseId!).ToByteArray());
                }
            }
        }

        using var status = await server.Client.GetFromJsonAsync<JsonDocument>(new Uri("/replication", UriKind.Relative), token);
        var body = status!.RootElement;
        await Assert.That(body.GetProperty("databaseCount").GetInt32()).IsEqualTo(129);
        await Assert.That(body.GetProperty("omittedDatabases").GetInt32()).IsEqualTo(1);
        var outbound = body.GetProperty("peers")[0];
        await Assert.That(outbound.GetProperty("databases").GetArrayLength()).IsEqualTo(128);
        await Assert.That(outbound.GetProperty("durableLag").GetInt64()).IsEqualTo(129);
        await Assert.That(await server.MetricAsync("terrabase_replication_observability_omitted", "", token)).IsEqualTo(1);
        await Assert.That(await server.MetricAsync("terrabase_replication_peer_lag", "", token)).IsEqualTo(129);
        await Assert.That(await server.MetricAsync("terrabase_replication_database_lag", "", token)).IsEqualTo(128);
        release.TrySetResult();
        await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
        {
            try
            {
                return await server.MetricAsync("terrabase_replication_divergence_detections_total", "reason=\"peer_ahead\"", cancellationToken) > 0;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });
        await Assert.That(await server.MetricAsync("terrabase_replication_database_diverged", "", token)).IsEqualTo(0);
    }
}
