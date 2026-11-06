using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

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
        // Kill the source handler first so it cannot catch the target's
        // disconnect and roll back in its finally block. Recovery must use
        // persisted phases, without a surviving controller or drain handler.
        await drill.Source.KillAsync();
        await drill.Target.KillAsync();
        await IgnoreControllerDisconnectAsync(draining);
        await Assert.That(await drill.OwnerAsync(token) is { Node: "gru-2", Activated: false, Transfer.Phase: "activating" }).IsTrue();
        await drill.Source.StartAsync(token);
        await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
        await Assert.That(elapsed.Elapsed < RegionalHandoff.RecoveryBound).IsTrue();
        Console.WriteLine($"DRILL target activation failed: recovered_ms={elapsed.Elapsed.TotalMilliseconds:F3}, bound_ms={RegionalHandoff.RecoveryBound.TotalMilliseconds:F0}");
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
        // Admission must follow the rollback CAS, never a read of the old
        // preparing record while a delayed activation CAS could still land.
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
            await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await completed.Content.ReadAsStringAsync(token));
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
    public async Task An_uncertified_manual_owner_CAS_during_a_native_write_cannot_admit_a_second_writer_or_lose_acknowledged_rows(CancellationToken token)
    {
        await using var drill = await RecoveryDrill.StartAsync(token);
        // First create a real prepared copy, then cancel the controller. Its
        // certificate must be unusable after rollback advances the epoch.
        var prepared = drill.SourceNats.Drop(static (subject, payload) => OwnerPhase(subject, payload, "activating"));
        using (var controller = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var draining = drill.SourceHttp.PostAsync(ManagementApi.Uri("/v1/rollout/drain?target=gru-2"), null, controller.Token);
            await prepared.WaitAsync(TimeSpan.FromSeconds(20), token);
            await controller.CancelAsync();
            await IgnoreControllerDisconnectAsync(draining);
        }
        drill.SourceNats.Allow();
        await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
        await TerraBaseTestServer.EventuallyAsync(async cancellation => (await drill.OwnerAsync(cancellation)).Transfer is null,
            RegionalHandoff.RecoveryBound);
        await File.WriteAllTextAsync(drill.Source.Arm, drill.Database, token);
        var writing = drill.SourceHttp.PostAsJsonAsync(ManagementApi.Uri(drill.Sql), new { statements = new[] { new { sql = "INSERT INTO acked VALUES ('unacknowledged')" } } }, token);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(File.Exists(drill.Source.MidWrite)), TimeSpan.FromSeconds(10));
        await Assert.That(writing.IsCompleted).IsFalse();

        var context = new NatsKVContext(new NatsJSContext(drill.Connection));
        var owners = await context.GetStoreAsync(RegionalHandoff.Bucket, token);
        var current = await owners.GetEntryAsync<byte[]>("gru", cancellationToken: token);
        var previous = JsonSerializer.Deserialize(current.Value!, TerraBaseJsonContext.Default.RegionalOwner)!;
        var manual = new RegionalOwner("gru", "gru-2", previous.Epoch + 1, "gru-1", Version: 2);
        var writers = await context.GetStoreAsync(RegionalWriterLease.Bucket, token);
        var expired = WaitForLeaseExpiryAsync(writers, token);
        drill.SourceNats.Partition();
        _ = await owners.UpdateAsync("gru", JsonSerializer.SerializeToUtf8Bytes(manual, TerraBaseJsonContext.Default.RegionalOwner), current.Revision, cancellationToken: token);
        await expired.WaitAsync(TimeSpan.FromSeconds(10), token);
        await Assert.That((await RecoveryDrill.StatusAsync(drill.TargetHttp, token)).Writer).IsFalse();
        using (var secondWriter = await drill.TargetHttp.PostAsJsonAsync(ManagementApi.Uri(drill.Sql),
            new { statements = new[] { new { sql = "INSERT INTO acked VALUES ('dual-writer')" } } }, token))
        {
            await Assert.That(secondWriter.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }
        File.Delete(drill.Source.Arm);
        using (var refused = await writing)
        {
            await Assert.That(refused.IsSuccessStatusCode).IsFalse();
        }
        await TerraBaseTestServer.EventuallyAsync(async cancellation => (await drill.OwnerAsync(cancellation)).Node == "gru-1",
            RegionalHandoff.RecoveryBound);
        drill.SourceNats.Heal();
        await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
        await drill.ExecuteAsync(drill.SourceHttp, "INSERT INTO acked VALUES ('after-manual-recovery')", token);
        await drill.AssertRowsAsync(drill.SourceHttp, ["ack-0", "ack-1", "ack-2", "after-manual-recovery"], token);
    }

    private static async Task WaitForLeaseExpiryAsync(INatsKVStore writers, CancellationToken token)
    {
        await foreach (var entry in writers.WatchAsync<byte[]>("gru", cancellationToken: token))
        {
            if (entry.Operation != NatsKVOperation.Put)
            {
                return;
            }
        }
        throw new IOException("writer lease watch ended before expiry");
    }

    private static bool OwnerPhase(string subject, ReadOnlyMemory<byte> payload, string phase)
    {
        if (subject != "$KV." + RegionalHandoff.Bucket + ".gru" || payload.IsEmpty)
        {
            return false;
        }
        var owner = JsonSerializer.Deserialize(payload.Span, TerraBaseJsonContext.Default.RegionalOwner);
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
            await File.WriteAllTextAsync(peers, "gru-1 gru 127.0.0.2\ngru-2 gru 127.0.0.3\n", token);
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
                management.DefaultRequestHeaders.Add("X-TerraBase-User-Id", EntityIds.New().ToString());
                var organization = await CreateAsync("/v1/management/organizations", new { name = "Recovery drill" });
                var project = await CreateAsync($"/v1/management/organizations/{organization}/projects", new { name = "Project" });
                var database = await CreateAsync($"/v1/management/organizations/{organization}/projects/{project}/databases", new { name = "acked", replicaRegions = ReplicaRegions });
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
            return JsonSerializer.Deserialize(entry.Value!, TerraBaseJsonContext.Default.RegionalOwner)!;
        }

        internal static async Task<RegionalStatus> StatusAsync(HttpClient http, CancellationToken token)
        {
            using var response = await http.GetAsync(ManagementApi.Uri("/v1/rollout/status"), token);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.RegionalStatus, token))!;
        }

        internal static Task WaitWriterAsync(HttpClient http, TimeSpan bound, CancellationToken token) => TerraBaseTestServer.EventuallyAsync(async _ =>
        {
            var status = await StatusAsync(http, token);
            return status.Writer && status.Ready;
        }, bound);

        internal async Task ExecuteAsync(HttpClient http, string sql, CancellationToken token)
        {
            using var response = await http.PostAsJsonAsync(ManagementApi.Uri(Sql), new { statements = new[] { new { sql } } }, token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync(token));
        }

        internal async Task AssertRowsAsync(HttpClient http, string[] expected, CancellationToken token)
        {
            using var response = await http.PostAsJsonAsync(ManagementApi.Uri(Sql), new { statements = new[] { new { sql = "SELECT id FROM acked ORDER BY id" } } }, token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync(token));
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows");
            await Assert.That(rows.EnumerateArray().Select(row => row[0].GetString()!).ToArray()).IsEquivalentTo(expected);
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
