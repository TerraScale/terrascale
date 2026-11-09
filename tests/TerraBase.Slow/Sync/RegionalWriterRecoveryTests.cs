using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Slow.Sync;

public sealed partial class RegionalWriterCrashTests
{
    [Test]
    [Timeout(120_000)]
    public async Task A_target_killed_after_the_owner_CAS_before_acquiring_admission_recovers_the_source_within_the_bound(CancellationToken token)
    {
        await using var drill = await RecoveryDrill.StartAsync(token);
        var blocked = drill.TargetNats.Drop(static (subject, _) => subject == "$KV." + RegionalWriterLease.Bucket + ".gru");
        var elapsed = Stopwatch.StartNew();
        var draining = drill.SourceHttp.PostAsync(ManagementApi.Uri("/v1/rollout/drain?target=gru-2"), null, token);
        await blocked.WaitAsync(TimeSpan.FromSeconds(20), token);
        var pending = await drill.OwnerAsync(token);
        await Assert.That(pending is { Node: "gru-2", Activated: false, Transfer.Phase: "activating" }).IsTrue();
        var writers = await new NatsKVContext(new NatsJSContext(drill.Connection)).GetStoreAsync(RegionalWriterLease.Bucket, token);
        try
        {
            _ = await writers.GetEntryAsync<byte[]>("gru", cancellationToken: token);
            throw new InvalidOperationException("target acquired admission before the injected failure");
        }
        catch (Exception error) when (error is NatsKVKeyNotFoundException or NatsKVKeyDeletedException)
        {
        }
        var restart = Stopwatch.StartNew();
        await drill.Source.KillAsync();
        await IgnoreControllerDisconnectAsync(draining);
        await drill.Source.StartAsync(token);
        await TerraBaseTestServer.EventuallyAsync(async cancellation =>
            (await RecoveryDrill.StatusAsync(drill.SourceHttp, cancellation)) is { Owner: "gru-2", Ready: true }, TimeSpan.FromSeconds(5));
        restart.Stop();
        await Assert.That(await drill.OwnerAsync(token) is { Node: "gru-2", Activated: false, Transfer.Phase: "activating" }).IsTrue();
        using (var followed = await drill.SourceHttp.PostAsync(ManagementApi.Uri("/v1/rollout/prime?source=gru-2"), null, token))
        {
            await Assert.That(followed.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await followed.Content.ReadAsStringAsync(token));
        }
        await drill.Target.KillAsync();
        await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
        await Assert.That(elapsed.Elapsed - restart.Elapsed < RegionalHandoff.RecoveryBound).IsTrue();
        Console.WriteLine($"DRILL target activation failed: recovered_ms={(elapsed.Elapsed - restart.Elapsed).TotalMilliseconds:F3}, restart_ms={restart.Elapsed.TotalMilliseconds:F3}, end_to_end_ms={elapsed.Elapsed.TotalMilliseconds:F3}, bound_ms={RegionalHandoff.RecoveryBound.TotalMilliseconds:F0}");
        await Assert.That((await drill.OwnerAsync(token)).Node).IsEqualTo("gru-1");
        await drill.AssertRowsAsync(drill.SourceHttp, ["ack-0", "ack-1", "ack-2"], token);
        await drill.ExecuteAsync(drill.SourceHttp, "INSERT INTO acked VALUES ('recovered')", token);
        await drill.AssertRowsAsync(drill.SourceHttp, ["ack-0", "ack-1", "ack-2", "recovered"], token);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Quorum_loss_during_rollback_retries_from_the_persisted_phase_after_healing(CancellationToken token)
    {
        await using var drill = await RecoveryDrill.StartAsync(token);
        var blocked = drill.SourceNats.Drop(static (subject, payload) => OwnerPhase(subject, payload, "activating"));
        using var controller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var draining = drill.SourceHttp.PostAsync(ManagementApi.Uri("/v1/rollout/drain?target=gru-2"), null, controller.Token);
        await blocked.WaitAsync(TimeSpan.FromSeconds(20), token);
        drill.SourceNats.Partition();
        await controller.CancelAsync();
        await IgnoreControllerDisconnectAsync(draining);
        await drill.Source.RecoveryFailed.WaitAsync(TimeSpan.FromSeconds(10), token);
        await Assert.That((await RecoveryDrill.StatusAsync(drill.SourceHttp, token)).Writer).IsFalse();
        await Assert.That((await drill.OwnerAsync(token)).Transfer is not null).IsTrue();
        drill.SourceNats.Allow();
        drill.SourceNats.Heal();
        await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
        var restored = await drill.OwnerAsync(token);
        await Assert.That(restored.Transfer is null && restored.Epoch > 1).IsTrue();
        await drill.ExecuteAsync(drill.SourceHttp, "INSERT INTO acked VALUES ('healed')", token);
        await drill.AssertRowsAsync(drill.SourceHttp, ["ack-0", "ack-1", "ack-2", "healed"], token);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Controller_disconnect_before_reservation_before_CAS_after_CAS_and_after_activation_never_strands_or_stops_both_nodes(CancellationToken token)
    {
        await using var drill = await RecoveryDrill.StartAsync(token);
        foreach (var phase in new[] { "preparing", "activating" })
        {
            var blocked = drill.SourceNats.Drop((subject, payload) => OwnerPhase(subject, payload, phase));
            using var controller = CancellationTokenSource.CreateLinkedTokenSource(token);
            var draining = drill.SourceHttp.PostAsync(ManagementApi.Uri("/v1/rollout/drain?target=gru-2"), null, controller.Token);
            await blocked.WaitAsync(TimeSpan.FromSeconds(20), token);
            await controller.CancelAsync();
            await IgnoreControllerDisconnectAsync(draining);
            drill.SourceNats.Allow();
            await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
            await TerraBaseTestServer.EventuallyAsync(async cancellation => (await drill.OwnerAsync(cancellation)).Transfer is null,
                RegionalHandoff.RecoveryBound);
            await drill.AssertRowsAsync(drill.SourceHttp, ["ack-0", "ack-1", "ack-2"], token);
            Console.WriteLine("DRILL controller disconnected before " + phase + ": source recovered");
        }

        var admission = drill.TargetNats.Drop(static (subject, _) => subject == "$KV." + RegionalWriterLease.Bucket + ".gru");
        using (var controller = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var draining = drill.SourceHttp.PostAsync(ManagementApi.Uri("/v1/rollout/drain?target=gru-2"), null, controller.Token);
            await admission.WaitAsync(TimeSpan.FromSeconds(20), token);
            await Assert.That((await drill.OwnerAsync(token)).Node).IsEqualTo("gru-2");
            await controller.CancelAsync();
            await IgnoreControllerDisconnectAsync(draining);
            await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
        }
        drill.TargetNats.Allow();
        await drill.AssertRowsAsync(drill.SourceHttp, ["ack-0", "ack-1", "ack-2"], token);
        Console.WriteLine("DRILL controller disconnected after CAS: source recovered");

        using (var completed = await drill.SourceHttp.PostAsync(ManagementApi.Uri("/v1/rollout/drain?target=gru-2"), null, token))
        {
            await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await completed.Content.ReadAsStringAsync(token)
                + "\nSOURCE: " + drill.Source.Diagnostics + "\nTARGET: " + drill.Target.Diagnostics);
        }
        await RecoveryDrill.WaitWriterAsync(drill.TargetHttp, RegionalHandoff.RecoveryBound, token);
        await Assert.That(await drill.OwnerAsync(token) is { Node: "gru-2", Activated: true, Transfer.Phase: "retiring" }).IsTrue();
        using (var conflicting = await drill.TargetHttp.PostAsync(ManagementApi.Uri("/v1/rollout/drain?target=gru-1"), null, token))
        {
            await Assert.That(conflicting.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }
        await drill.ExecuteAsync(drill.TargetHttp, "INSERT INTO acked VALUES ('survivor')", token);
        await drill.AssertRowsAsync(drill.TargetHttp, ["ack-0", "ack-1", "ack-2", "survivor"], token);
        Console.WriteLine("DRILL controller lost after activation: durable intent rejects the other stop");
    }

    [Test]
    [Timeout(120_000)]
    public async Task A_certified_manual_owner_CAS_with_a_connected_source_holding_admission_never_allows_dual_commit(CancellationToken token)
    {
        await using var drill = await RecoveryDrill.StartAsync(token);
        var snapshot = await drill.Source.SnapshotAsync(token);
        var context = new NatsKVContext(new NatsJSContext(drill.Connection));
        var owners = await context.GetStoreAsync(RegionalHandoff.Bucket, token);
        var current = await owners.GetEntryAsync<byte[]>("gru", cancellationToken: token);
        var previous = JsonSerializer.Deserialize(current.Value!, ReplicasJsonContext.Default.RegionalOwner)!;
        var writers = await context.GetStoreAsync(RegionalWriterLease.Bucket, token);
        var recoverable = previous with
        {
            Transfer = new RegionalTransfer("manual-cas-proof", "gru-1", "gru-2",
                (await RecoveryDrill.StatusAsync(drill.SourceHttp, token)).Session, "recovering", DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30)),
        };
        var revision = await owners.UpdateAsync("gru", JsonSerializer.SerializeToUtf8Bytes(recoverable, ReplicasJsonContext.Default.RegionalOwner), current.Revision, cancellationToken: token);
        using var payload = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(snapshot with { Epoch = previous.Epoch }, ReplicasJsonContext.Default.RegionalPrepare));
        payload.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using (var prepared = await drill.TargetHttp.PostAsync(ManagementApi.Uri("/v1/rollout/prepare"), payload, token))
        {
            await Assert.That(prepared.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await prepared.Content.ReadAsStringAsync(token)
                + "\nSOURCE: " + drill.Source.Diagnostics + "\nTARGET: " + drill.Target.Diagnostics);
        }
        var certificate = await owners.GetEntryAsync<byte[]>("prepared.gru.gru-2", cancellationToken: token);
        await Assert.That(JsonSerializer.Deserialize(certificate.Value!, ReplicasJsonContext.Default.RegionalPrepared))
            .IsEqualTo(new RegionalPrepared("gru-1", "gru-2", previous.Epoch, null));
        var blocked = drill.SourceNats.Drop(static (subject, payload) =>
            subject.StartsWith("$JS.API.", StringComparison.Ordinal)
            && subject.Contains("KV_" + RegionalHandoff.Bucket, StringComparison.Ordinal)
            && (subject.EndsWith(".gru", StringComparison.Ordinal)
                || Encoding.UTF8.GetString(payload.Span).Contains("$KV." + RegionalHandoff.Bucket + ".gru", StringComparison.Ordinal)));
        var held = await writers.GetEntryAsync<byte[]>("gru", cancellationToken: token);
        await Assert.That(JsonSerializer.Deserialize(held.Value!, ReplicasJsonContext.Default.RegionalWriter)!.Node).IsEqualTo("gru-1");
        await Assert.That((await RecoveryDrill.StatusAsync(drill.SourceHttp, token)).Writer).IsTrue();
        await File.WriteAllTextAsync(drill.Source.Arm, drill.Database, token);
        var writing = drill.SourceHttp.PostAsJsonAsync(ManagementApi.Uri(drill.Sql),
            new { statements = new[] { new { sql = "INSERT INTO acked VALUES ('source-overlap')" } } }, token);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(File.Exists(drill.Source.MidWrite)), TimeSpan.FromSeconds(5));
        var manual = new RegionalOwner("gru", "gru-2", previous.Epoch + 1, "gru-1", Version: 2);
        _ = await owners.UpdateAsync("gru", JsonSerializer.SerializeToUtf8Bytes(manual, ReplicasJsonContext.Default.RegionalOwner), revision, cancellationToken: token);
        await blocked.WaitAsync(TimeSpan.FromSeconds(5), token);
        using (var refused = await drill.TargetHttp.PostAsJsonAsync(ManagementApi.Uri(drill.Sql),
            new { statements = new[] { new { sql = "INSERT INTO acked VALUES ('target-too-early')" } } }, token))
        {
            await Assert.That(refused.IsSuccessStatusCode).IsFalse();
        }
        await RecoveryDrill.WaitWriterAsync(drill.TargetHttp, TimeSpan.FromSeconds(10), token);
        await drill.ExecuteAsync(drill.TargetHttp, "INSERT INTO acked VALUES ('target-committed')", token);
        File.Delete(drill.Source.Arm);
        using (var refused = await writing)
        {
            await Assert.That(refused.IsSuccessStatusCode).IsFalse();
        }
        var sourceRows = await drill.Source.NativeRowsAsync(new TerraBase.Tenancy.TenantContext(drill.Organization,
            TerraBase.Tenancy.PlanTier.Hobby, drill.Project, drill.Database), token);
        await Assert.That(sourceRows).IsEquivalentTo(["ack-0", "ack-1", "ack-2"]);
        drill.SourceNats.Allow();
        await drill.AssertRowsAsync(drill.TargetHttp, ["ack-0", "ack-1", "ack-2", "target-committed"], token);
        Console.WriteLine("DRILL certified manual CAS: source TCP connected, source held lease at CAS, target commit succeeded, source overlap commit refused");
    }

    [Test]
    [Timeout(120_000)]
    public async Task A_target_that_acquired_admission_then_failed_activation_recovers_without_losing_acked_rows(CancellationToken token)
    {
        await using var drill = await RecoveryDrill.StartAsync(token);
        var blocked = drill.TargetNats.Drop((subject, _) => subject == "$KV." + HomeLeases.Bucket + ".home." + drill.Database);
        var elapsed = Stopwatch.StartNew();
        var draining = drill.SourceHttp.PostAsync(ManagementApi.Uri("/v1/rollout/drain?target=gru-2"), null, token);
        await blocked.WaitAsync(TimeSpan.FromSeconds(20), token);
        await Assert.That(await drill.OwnerAsync(token) is { Node: "gru-2", Activated: false, Transfer.Phase: "activating" }).IsTrue();
        var writers = await new NatsKVContext(new NatsJSContext(drill.Connection)).GetStoreAsync(RegionalWriterLease.Bucket, token);
        var held = await writers.GetEntryAsync<byte[]>("gru", cancellationToken: token);
        await Assert.That(JsonSerializer.Deserialize(held.Value!, ReplicasJsonContext.Default.RegionalWriter)!.Node).IsEqualTo("gru-2");
        await drill.Source.KillAsync();
        await drill.Target.KillAsync();
        await IgnoreControllerDisconnectAsync(draining);
        await drill.Source.StartAsync(token);
        await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
        await Assert.That(elapsed.Elapsed < RegionalHandoff.RecoveryBound).IsTrue();
        await drill.ExecuteAsync(drill.SourceHttp, "INSERT INTO acked VALUES ('after-acquired-failure')", token);
        await drill.AssertRowsAsync(drill.SourceHttp, ["ack-0", "ack-1", "ack-2", "after-acquired-failure"], token);
        Console.WriteLine($"DRILL target failed after acquiring admission: recovered_ms={elapsed.Elapsed.TotalMilliseconds:F3}");
    }

    [Test]
    [Timeout(300_000)]
    public async Task Foreign_and_third_node_database_leases_are_isolated_without_stranding_the_regional_writer(CancellationToken token)
    {
        await using var drill = await RecoveryDrill.StartAsync(token);
        var foreignDb = await drill.CreateDatabaseAsync("foreign-conflict", token);
        var thirdDb = await drill.CreateDatabaseAsync("third-conflict", token);
        var mismatchDb = await drill.CreateDatabaseAsync("identity-conflict", token);
        await drill.Source.KillAsync();
        await drill.Target.KillAsync();
        var homes = await new NatsKVContext(new NatsJSContext(drill.Connection)).GetStoreAsync(HomeLeases.Bucket, token);
        var foreign = new HomeLease(drill.Organization, drill.Project, foreignDb, "sjc", "sjc-foreign", 0, "foreign-session");
        var third = new HomeLease(drill.Organization, drill.Project, thirdDb, "gru", "gru-third", 100, "third-session");
        var mismatch = new HomeLease(EntityIds.New().ToString(), drill.Project, mismatchDb, "gru", "gru-1", 100, "mismatch-session");
        foreach (var conflict in new[] { foreign, third, mismatch })
        {
            _ = await homes.PutAsync("node." + conflict.Node, JsonSerializer.SerializeToUtf8Bytes(new NodeLease(conflict.Node, conflict.Home, conflict.Session), ReplicasJsonContext.Default.NodeLease), cancellationToken: token);
            _ = await homes.PutAsync("home." + conflict.DatabaseId, JsonSerializer.SerializeToUtf8Bytes(conflict, ReplicasJsonContext.Default.HomeLease), cancellationToken: token);
        }
        var elapsed = Stopwatch.StartNew();
        await Task.WhenAll(drill.Source.StartAsync(token), drill.Target.StartAsync(token));
        await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
        await Assert.That(elapsed.Elapsed < RegionalHandoff.RecoveryBound).IsTrue();
        Console.WriteLine($"DRILL isolated foreign and third-node DBs: healthy writer recovered_ms={elapsed.Elapsed.TotalMilliseconds:F3}");
        await drill.ExecuteAsync(drill.SourceHttp, "INSERT INTO acked VALUES ('healthy-after-conflict')", token);
        foreach (var conflict in new[] { foreign, third, mismatch })
        {
            using var refused = await drill.SourceHttp.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{drill.Project}/databases/{conflict.DatabaseId}/sql/execute"),
                new { statements = new[] { new { sql = "CREATE TABLE must_not_commit(id TEXT)" } } }, token);
            await Assert.That(refused.IsSuccessStatusCode).IsFalse();
            var retained = await homes.GetEntryAsync<byte[]>("home." + conflict.DatabaseId, cancellationToken: token);
            await Assert.That(JsonSerializer.Deserialize(retained.Value!, ReplicasJsonContext.Default.HomeLease)).IsEqualTo(conflict);
        }
        await drill.AssertRowsAsync(drill.SourceHttp, ["ack-0", "ack-1", "ack-2", "healthy-after-conflict"], token);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Recovery_retries_the_certified_live_target_instead_of_returning_ownership_to_a_dead_source(CancellationToken token)
    {
        await using var drill = await RecoveryDrill.StartAsync(token);
        var blocked = drill.TargetNats.Drop((subject, _) => subject == "$KV." + HomeLeases.Bucket + ".home." + drill.Database);
        var draining = drill.SourceHttp.PostAsync(ManagementApi.Uri("/v1/rollout/drain?target=gru-2"), null, token);
        await blocked.WaitAsync(TimeSpan.FromSeconds(20), token);
        var pending = await drill.OwnerAsync(token);
        await Assert.That(pending is { Node: "gru-2", Activated: false }).IsTrue();
        await drill.Source.KillAsync();
        await IgnoreControllerDisconnectAsync(draining);
        await TerraBaseTestServer.EventuallyAsync(async cancellation =>
            (await drill.OwnerAsync(cancellation)).Transfer!.Deadline > pending.Transfer!.Deadline,
            RegionalHandoff.RecoveryBound);
        await Assert.That((await drill.OwnerAsync(token)).Node).IsEqualTo("gru-2");
        drill.TargetNats.Allow();
        await RecoveryDrill.WaitWriterAsync(drill.TargetHttp, RegionalHandoff.RecoveryBound, token);
        await drill.ExecuteAsync(drill.TargetHttp, "INSERT INTO acked VALUES ('live-target-recovered')", token);
        await drill.AssertRowsAsync(drill.TargetHttp, ["ack-0", "ack-1", "ack-2", "live-target-recovered"], token);
    }

    [Test]
    [Timeout(120_000)]
    public async Task An_upgraded_follower_cannot_claim_its_own_regions_database_during_owner_restart(CancellationToken token)
    {
        await using var drill = await RecoveryDrill.StartAsync(token);
        var homes = await new NatsKVContext(new NatsJSContext(drill.Connection)).GetStoreAsync(HomeLeases.Bucket, token);
        var before = await homes.GetEntryAsync<byte[]>("home." + drill.Database, cancellationToken: token);
        var retained = JsonSerializer.Deserialize(before.Value!, ReplicasJsonContext.Default.HomeLease)!;
        await Assert.That(retained.Node).IsEqualTo("gru-1");
        await drill.Source.KillAsync();
        await Task.Delay(HomeLeases.Ttl + TimeSpan.FromSeconds(22), token);
        var after = await homes.GetEntryAsync<byte[]>("home." + drill.Database, cancellationToken: token);
        await Assert.That(JsonSerializer.Deserialize(after.Value!, ReplicasJsonContext.Default.HomeLease)).IsEqualTo(retained);
        await Assert.That(after.Revision).IsEqualTo(before.Revision);
        await Assert.That((await RecoveryDrill.StatusAsync(drill.TargetHttp, token)).Writer).IsFalse();
        await drill.Source.StartAsync(token);
        await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
        await drill.ExecuteAsync(drill.SourceHttp, "INSERT INTO acked VALUES ('after-owner-restart')", token);
        await drill.AssertRowsAsync(drill.SourceHttp, ["ack-0", "ack-1", "ack-2", "after-owner-restart"], token);
        Console.WriteLine("DRILL same-region failover: owner absent 32s, home revision unchanged, follower never claimed; restart writer recovered");
    }

    private static bool OwnerPhase(string subject, ReadOnlyMemory<byte> payload, string phase)
    {
        if (subject != "$KV." + RegionalHandoff.Bucket + ".gru" || payload.IsEmpty)
        {
            return false;
        }
        var owner = JsonSerializer.Deserialize(payload.Span, ReplicasJsonContext.Default.RegionalOwner);
        return owner?.Transfer?.Phase == phase;
    }

    private static async Task IgnoreControllerDisconnectAsync(Task<HttpResponseMessage> request)
    {
        try
        {
            using var response = await request;
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException)
        {
        }
    }

    private sealed class RecoveryDrill(
        JetStreamServer nats, string root, RegionalNatsFault sourceNats, RegionalNatsFault targetNats,
        Node source, Node target, NatsConnection connection) : IAsyncDisposable
    {
        internal RegionalNatsFault SourceNats => sourceNats;
        internal RegionalNatsFault TargetNats => targetNats;
        internal Node Source => source;
        internal Node Target => target;
        internal NatsConnection Connection => connection;
        internal HttpClient SourceHttp { get; } = PeerTls.CreateClient(source.Address);
        internal HttpClient TargetHttp { get; } = PeerTls.CreateClient(target.Address);
        internal string Sql { get; private set; } = "";
        internal string Database { get; private set; } = "";
        internal string Organization { get; private set; } = "";
        internal string Project { get; private set; } = "";
        private string User { get; set; } = "";

        internal async Task<string> CreateDatabaseAsync(string name, CancellationToken token)
        {
            using var management = PeerTls.CreateClient(source.Address);
            management.DefaultRequestHeaders.Add("X-TerraBase-User-Id", User);
            using var response = await management.PostAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{Organization}/projects/{Project}/databases"), new { name, replicaRegions = ReplicaRegions }, token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync(token));
            return (await response.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
        }

        internal static async Task<RecoveryDrill> StartAsync(CancellationToken token)
        {
            var nats = await JetStreamServer.StartAsync(token);
            var root = Directory.CreateTempSubdirectory("regional-recovery-").FullName;
            var master = Path.Combine(root, "master-key");
            await File.WriteAllTextAsync(master, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), token);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(master, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            using var reservation = new TcpListener(IPAddress.Any, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var peers = Path.Combine(root, "peers");
            await File.WriteAllTextAsync(peers, "gru-2 gru 127.0.0.3\ngru-1 gru 127.0.0.2\n", token);
            var sourceNats = new RegionalNatsFault(nats.Url);
            var targetNats = new RegionalNatsFault(nats.Url);
            var source = new Node(root, "gru-1", "127.0.0.2", port, peers, nats, sourceNats.Url);
            var target = new Node(root, "gru-2", "127.0.0.3", port, peers, nats, targetNats.Url);
            var connection = new NatsConnection(new NatsOpts { Url = nats.Url.ToString(), AuthOpts = new NatsAuthOpts { Token = nats.Token } });
            var drill = new RecoveryDrill(nats, root, sourceNats, targetNats, source, target, connection);
            try
            {
                await connection.ConnectAsync();
                await source.StartAsync(token);
                await target.StartAsync(token);
                await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, TimeSpan.FromSeconds(15), token);
                using var management = PeerTls.CreateClient(source.Address);
                drill.User = EntityIds.New().ToString();
                management.DefaultRequestHeaders.Add("X-TerraBase-User-Id", drill.User);
                var organization = await CreateAsync("/v1/management/organizations", new { name = "Recovery drill" });
                var project = await CreateAsync($"/v1/management/organizations/{organization}/projects", new { name = "Project" });
                var database = await CreateAsync($"/v1/management/organizations/{organization}/projects/{project}/databases", new { name = "acked", replicaRegions = ReplicaRegions });
                drill.Organization = organization;
                drill.Project = project;
                drill.Database = database;
                drill.Sql = $"/v1/projects/{project}/databases/{database}/sql/execute";
                await drill.ExecuteAsync(drill.SourceHttp, "CREATE TABLE acked(id TEXT PRIMARY KEY)", token);
                for (var index = 0; index < 3; index++)
                {
                    await drill.ExecuteAsync(drill.SourceHttp, $"INSERT INTO acked VALUES ('ack-{index}')", token);
                }
                using var prime = await drill.TargetHttp.PostAsync(ManagementApi.Uri("/v1/rollout/prime?source=gru-1"), null, token);
                await Assert.That(prime.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await prime.Content.ReadAsStringAsync(token));
                return drill;

                async Task<string> CreateAsync(string path, object body)
                {
                    using var response = await management.PostAsJsonAsync(ManagementApi.Uri(path), body, token);
                    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync(token));
                    return (await response.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
                }
            }
            catch
            {
                await drill.DisposeAsync();
                throw;
            }
        }

        internal async Task<RegionalOwner> OwnerAsync(CancellationToken token)
        {
            var store = await new NatsKVContext(new NatsJSContext(connection)).GetStoreAsync(RegionalHandoff.Bucket, token);
            var entry = await store.GetEntryAsync<byte[]>("gru", cancellationToken: token);
            return JsonSerializer.Deserialize(entry.Value!, ReplicasJsonContext.Default.RegionalOwner)!;
        }

        internal static async Task<RegionalStatus> StatusAsync(HttpClient http, CancellationToken token)
        {
            using var response = await http.GetAsync(ManagementApi.Uri("/v1/rollout/status"), token);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.RegionalStatus, token))!;
        }

        internal static Task WaitWriterAsync(HttpClient http, TimeSpan bound, CancellationToken token) => TerraBaseTestServer.EventuallyAsync(async _ =>
        {
            var status = await StatusAsync(http, token);
            return status.Writer && status.Ready;
        }, bound);

        internal async Task ExecuteAsync(HttpClient http, string sql, CancellationToken token)
        {
            using var response = await http.PostAsJsonAsync(ManagementApi.Uri(Sql), new { statements = new[] { new { sql } } }, token);
            var body = await response.Content.ReadAsStringAsync(token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body);
            var result = JsonDocument.Parse(body);
            using (result)
            {
                await Assert.That(result.RootElement.TryGetProperty("error", out _)).IsFalse().Because(body);
                if (sql.StartsWith("INSERT", StringComparison.Ordinal))
                {
                    await Assert.That(result.RootElement.GetProperty("results")[0].GetProperty("rows_affected").GetInt64()).IsEqualTo(1).Because(body);
                }
            }
        }

        internal async Task AssertRowsAsync(HttpClient http, string[] expected, CancellationToken token)
        {
            using var response = await http.PostAsJsonAsync(ManagementApi.Uri(Sql), new { statements = new[] { new { sql = "SELECT id FROM acked ORDER BY id" } } }, token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync(token));
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows");
            var actual = rows.EnumerateArray().Select(row => row[0].GetString()!).ToArray();
            await Assert.That(actual).IsEquivalentTo(expected).Because("actual IDs=" + string.Join(',', actual) + "; owner=" + (await OwnerAsync(token)).Node);
        }

        public async ValueTask DisposeAsync()
        {
            SourceHttp.Dispose();
            TargetHttp.Dispose();
            await source.DisposeAsync();
            await target.DisposeAsync();
            await sourceNats.DisposeAsync();
            await targetNats.DisposeAsync();
            await connection.DisposeAsync();
            await nats.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }
}
