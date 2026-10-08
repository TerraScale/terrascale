using System.Globalization;
using System.Net;
using Bogus;
using Google.Protobuf;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class ReplicationStatusTests
{
    [Test]
    public async Task Disabled_replication_status_is_uncached_and_hidden_from_clients()
    {
        using var tls = new ClientTls();
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var response = await server.Client.GetAsync(new Uri("/replication", UriKind.Relative), token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        var status = await response.Content.ReadFromJsonAsync<ReplicationStatus>(token);
        await Assert.That(status!.Enabled).IsFalse();
        await Assert.That(status.Initialized).IsTrue();
        await Assert.That(status.DatabaseCount).IsEqualTo(0);
        await Assert.That(status.OmittedDatabases).IsEqualTo(0);
        await Assert.That(status.Peers.Count).IsEqualTo(0);

        using var client = tls.CreateClient(server.ClientAddress!);
        using var hidden = await client.GetAsync(new Uri("/replication", UriKind.Relative), token);
        await Assert.That(hidden.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_stalled_ack_exposes_durable_lag_and_catchup_then_records_convergence()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var token = TestContext.Current!.Execution.CancellationToken;
        var waiting = NewSignal();
        var release = NewSignal();
        var applied = 0UL;
        ByteString? digest = null;
        await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            await responses.WriteAsync(Welcome(requests.Current.Hello, applied, digest, out var member), context.CancellationToken);
            while (await requests.MoveNext(context.CancellationToken))
            {
                waiting.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                var batch = requests.Current.CommittedGroup;
                applied = batch.EventId.Sequence;
                digest = batch.JournalDigest;
                await responses.WriteAsync(FakePeer.Ack(member, batch, applied), context.CancellationToken);
            }
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
            settings["write-wait-ms"] = "0";
        });
        await TerraBaseTestServer.ReplicatedAsync(sender);
        var tenant = await ReplicationTenant.CreateAsync(sender, "backlog");
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
        await CreateCollectionAsync(tenant, writer, token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        await PutAsync(tenant, writer, token);
        var before = (await StatusAsync(sender, token)).Peers.Single().Databases.Single();
        await PutAsync(tenant, writer, token);

        var status = await StatusAsync(sender, token);
        await Assert.That(status.Enabled).IsTrue();
        await Assert.That(status.Initialized).IsTrue();
        await Assert.That(status.DatabaseCount).IsEqualTo(1);
        await Assert.That(status.OmittedDatabases).IsEqualTo(0);
        var outbound = status.Peers.Single();
        await Assert.That(outbound.Peer).IsEqualTo(0);
        await Assert.That(outbound.DurableLag).IsEqualTo(3);
        var database = outbound.Databases.Single();
        await Assert.That(database.Slot).IsBetween(0, 127);
        await Assert.That(database.OrganizationId).IsEqualTo(tenant.OrganizationId);
        await Assert.That(database.ProjectId).IsEqualTo(tenant.ProjectId);
        await Assert.That(database.DatabaseId).IsEqualTo(tenant.Database("backlog").DatabaseId);
        await Assert.That(database.Incarnation).IsNotEqualTo(Ulid.Empty);
        await Assert.That(database.Head).IsEqualTo(3);
        await Assert.That(database.Acknowledged).IsEqualTo(0);
        await Assert.That(database.DurableLag).IsEqualTo(3);
        await Assert.That(database.Diverged).IsFalse();
        await Assert.That(database.CatchupSeconds).IsGreaterThan(before.CatchupSeconds);
        await Assert.That(database.ConvergenceSeconds).IsNull();
        var label = $"database=\"{database.Slot.ToString(CultureInfo.InvariantCulture)}\"";
        await AssertLagAsync(sender, label, 3);
        await Assert.That(await sender.MetricAsync("terrabase_replication_database_diverged", label, token)).IsEqualTo(0);
        await Assert.That(await sender.MetricAsync("terrabase_replication_database_catchup_seconds", label, token)).IsGreaterThan(0);

        // Never-used slots do not add scrape work; allocated slots still have bounded numeric labels.
        var scrape = await sender.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token);
        var samples = scrape.Split('\n').Where(line => line.StartsWith("terrabase_replication_database_lag{", StringComparison.Ordinal)).ToArray();
        await Assert.That(samples.Length).IsEqualTo(1);
        await Assert.That(samples.Single().Contains(label, StringComparison.Ordinal)).IsTrue();
        await Assert.That(samples.Single().Contains("peer=\"0\"", StringComparison.Ordinal)).IsTrue();

        await Assert.That(scrape.Contains(tenant.OrganizationId, StringComparison.Ordinal)).IsFalse();
        await Assert.That(scrape.Contains(tenant.ProjectId, StringComparison.Ordinal)).IsFalse();
        await Assert.That(scrape.Contains(database.DatabaseId, StringComparison.Ordinal)).IsFalse();
        release.SetResult();
        await TerraBaseTestServer.ReplicatedAsync(sender);
        var recovered = (await StatusAsync(sender, token)).Peers.Single();
        await Assert.That(recovered.DurableLag).IsEqualTo(0);
        var caughtUp = recovered.Databases.Single();
        await Assert.That(caughtUp.Incarnation).IsEqualTo(database.Incarnation);
        await Assert.That(caughtUp.Slot).IsEqualTo(database.Slot);
        await Assert.That(caughtUp.Head).IsEqualTo(3);
        await Assert.That(caughtUp.Acknowledged).IsEqualTo(caughtUp.Head);
        await Assert.That(caughtUp.DurableLag).IsEqualTo(0);
        await Assert.That(caughtUp.CatchupSeconds).IsEqualTo(0);
        await Assert.That(caughtUp.Diverged).IsFalse();
        await Assert.That(caughtUp.ConvergenceSeconds).IsNotNull();
        var convergence = caughtUp.ConvergenceSeconds!.Value;
        await Assert.That(convergence).IsGreaterThanOrEqualTo(database.CatchupSeconds);
        await AssertLagAsync(sender, label, 0);
        await Assert.That(await sender.MetricAsync("terrabase_replication_database_catchup_seconds", label, token)).IsEqualTo(0);
        await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
            await sender.MetricAsync("terrabase_replication_convergence_seconds_count", label, cancellationToken) == 1);
        var seconds = await sender.MetricAsync("terrabase_replication_convergence_seconds_sum", label, token);
        await Assert.That(Math.Abs(seconds - convergence)).IsLessThan(0.000001);
    }

    [Test]
    public async Task A_conflicting_journal_ack_is_visible_until_valid_replication_recovers()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var token = TestContext.Current!.Execution.CancellationToken;
        var retrying = NewSignal();
        var recover = NewSignal();
        var corrupted = 0;
        var applied = 0UL;
        ByteString? digest = null;
        await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            if (Volatile.Read(ref corrupted) != 0)
            {
                retrying.TrySetResult();
                await recover.Task.WaitAsync(context.CancellationToken);
            }

            await responses.WriteAsync(Welcome(requests.Current.Hello, applied, digest, out var member), context.CancellationToken);
            while (await requests.MoveNext(context.CancellationToken))
            {
                var batch = requests.Current.CommittedGroup;
                var ack = FakePeer.Ack(member, batch, batch.EventId.Sequence);
                if (Interlocked.Exchange(ref corrupted, 1) == 0)
                {
                    ack.Durable.JournalDigest = ByteString.CopyFrom(new byte[JournalDigest.Length]);
                    await responses.WriteAsync(ack, context.CancellationToken);
                    return;
                }

                applied = batch.EventId.Sequence;
                digest = batch.JournalDigest;
                await responses.WriteAsync(ack, context.CancellationToken);
            }
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
            settings["write-wait-ms"] = "0";
        });
        await TerraBaseTestServer.ReplicatedAsync(sender);
        var tenant = await ReplicationTenant.CreateAsync(sender, "backlog");
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
        await CreateCollectionAsync(tenant, writer, token);
        await PutAsync(tenant, writer, token);
        await retrying.Task.WaitAsync(TimeSpan.FromSeconds(30), token);

        var database = (await StatusAsync(sender, token)).Peers.Single().Databases.Single();
        await Assert.That(database.Diverged).IsTrue();
        await Assert.That(database.Head).IsEqualTo(2);
        await Assert.That(database.Acknowledged).IsEqualTo(0);
        await Assert.That(database.DurableLag).IsEqualTo(2);
        await Assert.That(database.CatchupSeconds).IsGreaterThan(0);
        await Assert.That(database.ConvergenceSeconds).IsNull();
        var label = $"database=\"{database.Slot.ToString(CultureInfo.InvariantCulture)}\"";
        await AssertLagAsync(sender, label, 2);
        await Assert.That(await sender.MetricAsync("terrabase_replication_database_diverged", label, token)).IsEqualTo(1);

        recover.SetResult();
        await TerraBaseTestServer.ReplicatedAsync(sender);
        var recovered = (await StatusAsync(sender, token)).Peers.Single().Databases.Single();
        await Assert.That(recovered.Diverged).IsFalse();
        await Assert.That(recovered.Acknowledged).IsEqualTo(2);
        await Assert.That(recovered.DurableLag).IsEqualTo(0);
        await Assert.That(recovered.CatchupSeconds).IsEqualTo(0);
        await Assert.That(recovered.ConvergenceSeconds).IsNotNull();
        await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
            await sender.MetricAsync("terrabase_replication_database_diverged", label, cancellationToken) == 0);
        await AssertLagAsync(sender, label, 0);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SyncResponse Welcome(Hello hello, ulong applied, ByteString? digest, out PeerIdentity member)
    {
        var welcome = FakePeer.Welcome(hello, out member);
        var origin = hello.LocalIdentity.MemberIncarnation;
        welcome.Welcome.Frontiers.Add(new PeerFrontier
        {
            Origin = new OriginIdentity { ReplicaId = origin, Incarnation = origin },
            DurableSequence = applied,
            JournalDigest = digest ?? ByteString.CopyFrom(JournalDigest.Initial(origin.Span)),
        });
        return welcome;
    }

    private static async Task<ReplicationStatus> StatusAsync(TerraBaseTestServer server, CancellationToken token)
    {
        using var response = await server.Client.GetAsync(new Uri("/replication", UriKind.Relative), token);
        response.EnsureSuccessStatusCode();
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        return await response.Content.ReadFromJsonAsync<ReplicationStatus>(token)
            ?? throw new InvalidOperationException("the replication status response is empty");
    }

    private static async Task AssertLagAsync(TerraBaseTestServer server, string label, long expected)
    {
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await server.MetricAsync("terrabase_replication_database_lag", label, token) == expected
            && await server.MetricAsync("terrabase_replication_peer_lag", "peer=\"0\"", token) == expected
            && await server.MetricAsync("terrabase_replication_sender_durable_lag", "", token) == expected);
    }

    private static async Task CreateCollectionAsync(ReplicationTenant tenant, HttpClient writer, CancellationToken token)
    {
        using var response = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("backlog")}/collections", UriKind.Relative), new { name = "items" }, token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task PutAsync(ReplicationTenant tenant, HttpClient writer, CancellationToken token)
    {
        var faker = new Faker();
        using var response = await writer.PutAsJsonAsync(
            new Uri($"{tenant.Path("backlog")}/collections/items/documents/{EntityIds.New()}", UriKind.Relative),
            new { value = faker.Lorem.Sentence() }, token);
        response.EnsureSuccessStatusCode();
    }
}
