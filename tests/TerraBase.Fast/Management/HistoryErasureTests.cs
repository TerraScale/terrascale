using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ReplicaCluster = TerraBase.Fast.Replication.PeerStreamTests.Cluster;
using ReplicaMachine = TerraBase.Fast.Replication.PeerStreamTests.Machine;

namespace TerraBase.Fast.Management;

/// <summary>
/// TS-873 shared-history erasure across real hosts with real replication (docs/terrabase/history-erasure.md). The test plays
/// ts-auth: it writes the fence, its request document and the subject's records into the platform auth database and presents the settlement token.
/// </summary>
public sealed class HistoryErasureTests
{
    private const string Users = "users";
    private const string Jobs = "deletion_jobs";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly TenantContext Auth = HistoryErasures.AuthDatabase;


    // Another origin wrote a subject record before it held the fence; the requester only settles once that origin's committed
    // marker (and so the late record) is applied, auth's re-verification removes it, and every holder rewrites both files from
    // a cut covering the final marker: neither payload survives in any file, survivors and the GC floor do.
    [Test]
    public async Task A_late_pre_fence_write_of_another_origin_is_covered_before_settlement_and_rewritten_away()
    {
        await using var cluster = await StartAsync();
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (subject, late, survivor, fence, erasure) = (Id(), Id(), Id(), Id(), Id());
        var (secret, lateSecret) = ("ts873-secret-" + Id(), "ts873-late-" + Id());
        await PutAsync(gru, Users, survivor, """{"n":1}""");
        await PutAsync(gru, Users, subject, $$"""{"secret":"{{secret}}"}""");
        await Assert.That(await UntilAsync(sjc, Users, subject, live: true)).IsTrue();

        // Hold sjc's auth batches at gru: sjc's late record and its marker stay unapplied there.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gru.BeforeApply = async (database, _, token) =>
        {
            if (database.DatabaseId == Auth.DatabaseId)
            {
                await held.Task.WaitAsync(token);
            }
        };
        try
        {
            await PutAsync(sjc, Users, late, $$"""{"secret":"{{lateSecret}}","owner":"{{subject}}"}""");
            // Auth's account deletion: the fence and the root removal, at gru.
            await PutAsync(gru, Jobs, fence, """{"scope":"account"}""");
            await RequestDocumentAsync(gru, erasure, fence);
            await DeleteAsync(gru, Users, subject);
            await Assert.That(await UntilAsync(sjc, Jobs, fence, live: true)).IsTrue();

            var requested = DateTimeOffset.UtcNow;
            var first = await RequestAsync(gru, erasure, requested);
            await Assert.That(first.Status).IsEqualTo(HistoryErasure.Fencing);
            await Assert.That(first.PendingHolders).IsEqualTo(2);
            await RunAsync(gru);
            await RunAsync(sjc);
            await Assert.That(await UntilAsync(gru, () => ReceiptsAsync(gru, erasure), 2)).IsTrue();

            // gru holds both receipts (SYSTEM is not held) but has not applied sjc's committed auth marker: no token.
            await Assert.That(await LiveAsync(gru, Users, late)).IsFalse().Because("the late record is still in flight");
            var settling = await RequestAsync(gru, erasure, requested);
            await Assert.That(settling.Status).IsEqualTo(HistoryErasure.Settling);
            await Assert.That(settling.SettlementToken).IsNull();
        }
        finally
        {
            held.TrySetResult();
            gru.BeforeApply = null;
        }

        var requestedAt = (await RecordAsync(gru, erasure))!.RequestedAt;
        HistoryErasureStatus settled = null!;
        await Assert.That(await UntilAsync(gru, async () => (settled = await RequestAsync(gru, erasure, requestedAt)).SettlementToken is not null ? 1 : 0, 1)).IsTrue();
        await Assert.That(settled.Status).IsEqualTo(HistoryErasure.Reverify);
        await Assert.That(await LiveAsync(gru, Users, late)).IsTrue().Because("covering sjc's marker applied every earlier write of sjc");

        // Another request time conflicts; another machine's token is stale.
        await Assert.That(Result(await Service(gru).RequestAsync(erasure, requestedAt.AddSeconds(1), null, CancellationToken.None)))
            .IsEqualTo("erasure_conflict");
        await Assert.That(Result(await Service(sjc).RequestAsync(erasure, requestedAt, settled.SettlementToken, CancellationToken.None)))
            .IsEqualTo("settlement_stale").Because("a token is only the issuing machine's");

        // Auth re-verifies: the late record goes.
        await DeleteAsync(gru, Users, late);
        var final = await RequestAsync(gru, erasure, requestedAt, settled.SettlementToken);
        await Assert.That(final.Status).IsEqualTo(HistoryErasure.Rewriting);
        await Assert.That(Result(await Service(gru).RequestAsync(erasure, requestedAt, settled.SettlementToken, CancellationToken.None)))
            .IsEqualTo("settlement_stale").Because("a token is single-use");

        var before = (await IncarnationAsync(gru, Auth), await IncarnationAsync(sjc, Auth));
        await Assert.That(await UntilAsync(gru, async () =>
        {
            await RunAsync(gru);
            await RunAsync(sjc);
            return (await RequestAsync(gru, erasure, requestedAt)).Status == HistoryErasure.Residue ? 1 : 0;
        }, 1)).IsTrue();

        foreach (var machine in new[] { gru, sjc })
        {
            await Assert.That(await LiveAsync(machine, Users, survivor)).IsTrue();
            await Assert.That(await LiveAsync(machine, Users, subject)).IsFalse();
            await Assert.That(await LiveAsync(machine, Users, late)).IsFalse();
            foreach (var file in await FilesAsync(machine))
            {
                var bytes = await File.ReadAllBytesAsync(file);
                await Assert.That(Contains(bytes, secret) || Contains(bytes, lateSecret)).IsFalse().Because(file);
            }
        }

        await Assert.That(await IncarnationAsync(gru, Auth)).IsNotEqualTo(before.Item1);
        await Assert.That(await IncarnationAsync(sjc, Auth)).IsNotEqualTo(before.Item2);
        var status = await RequestAsync(gru, erasure, requestedAt);
        await Assert.That(status.ActiveOverdue).IsFalse();
        await Assert.That(status.Status).IsNotEqualTo(HistoryErasure.Complete).Because("tombstone and feed residue at or below the cut remain, and backups are unverified");
    }

    // An offline holder never marks: the erasure stays pending, and overdue past the deadline from auth's original request time.
    [Test]
    public async Task An_offline_holder_keeps_the_erasure_pending_and_overdue()
    {
        await using var cluster = await StartAsync();
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (fence, erasure) = (Id(), Id());
        await PutAsync(gru, Jobs, fence, """{"scope":"account"}""");
        await RequestDocumentAsync(gru, erasure, fence);
        await sjc.StopAsync();
        var requested = DateTimeOffset.UtcNow.AddDays(-31);
        await RequestAsync(gru, erasure, requested);
        await RunAsync(gru);
        var status = await RequestAsync(gru, erasure, requested);
        await Assert.That(status.Status).IsEqualTo(HistoryErasure.Fencing);
        await Assert.That(status.PendingHolders).IsEqualTo(1);
        await Assert.That(status.ActiveOverdue).IsTrue();
        await Assert.That(status.ActiveDueAt).IsEqualTo(requested.ToUniversalTime().AddDays(30));
        // No backup is configured here: zero retention, so the backup deadline is the active one.
        await Assert.That(status.BackupDueAt).IsEqualTo(status.ActiveDueAt);
    }

    // A file another path replaced after this machine's rewrite (a restore, rebuild or copy) is fenced again: the final cut no
    // longer covers the new marker, so the erasure goes back to settlement instead of counting the old receipt.
    [Test]
    public async Task A_replaced_file_is_fenced_again_and_its_old_receipt_stops_counting()
    {
        await using var cluster = await StartAsync();
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (fence, erasure) = (Id(), Id());
        await PutAsync(gru, Jobs, fence, """{"scope":"account"}""");
        await RequestDocumentAsync(gru, erasure, fence);
        await Assert.That(await UntilAsync(sjc, Jobs, fence, live: true)).IsTrue();
        var requested = DateTimeOffset.UtcNow;
        await RequestAsync(gru, erasure, requested);
        await Assert.That(await UntilAsync(gru, async () =>
        {
            await RunAsync(gru);
            await RunAsync(sjc);
            var status = await RequestAsync(gru, erasure, requested);
            if (status.SettlementToken is { } token)
            {
                status = await RequestAsync(gru, erasure, requested, token);
            }

            return status.Status == HistoryErasure.Residue ? 1 : 0;
        }, 1)).IsTrue();

        // Not this service's rewrite: as a restore would, it leaves another incarnation than the lineage recorded.
        _ = await sjc.Api.Server.Services.GetRequiredService<TursoStorageEngine>().RewriteAsync(Auth, 0, CancellationToken.None);
        await RunAsync(sjc);
        await Assert.That(await UntilAsync(gru, async () => (await RequestAsync(gru, erasure, requested)).Status is HistoryErasure.Reverify or HistoryErasure.Settling ? 1 : 0, 1)).IsTrue();
    }

    // Q-EPOCH fallback (TS-875): a holder restarts uncleanly (a new replica epoch). Peers drop its old epoch's applied progress on
    // its first ack, so nothing proves the old epoch's acknowledged writes covered: the erasure stalls, never completes, and the
    // epoch_stalled gauge (a direct operator alert) reads 1.
    [Test]
    public async Task An_unclean_restart_of_a_holder_stalls_the_erasure_and_raises_the_operator_alert_at_once()
    {
        await using var cluster = await StartAsync();
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (fence, erasure) = (Id(), Id());
        await PutAsync(gru, Jobs, fence, """{"scope":"account"}""");
        await RequestDocumentAsync(gru, erasure, fence);
        await Assert.That(await UntilAsync(sjc, HistoryErasure.MarkCollection, erasure, live: true)).IsTrue();
        var requested = DateTimeOffset.UtcNow;
        await RequestAsync(gru, erasure, requested);
        await RunAsync(gru);
        await RunAsync(sjc);
        await Assert.That(await UntilAsync(gru, () => ReceiptsAsync(gru, erasure), 2)).IsTrue();
        var before = sjc.Node;

        var directory = Path.GetDirectoryName(((TursoDatabaseStore)await sjc.Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None)).FilePath)!;
        await sjc.StopAsync();
        MarkRunning(directory);
        await sjc.StartAsync(WithoutPasses);
        await Assert.That(sjc.Node.Member).IsEqualTo(before.Member);
        await Assert.That(sjc.Node.Epoch).IsEqualTo((ushort)(before.Epoch + 1));

        // sjc's passes start once its catalog passed the restore gate again.
        await Assert.That(await UntilAsync(gru, async () =>
        {
            await RunAsync(sjc);
            return (await RequestAsync(gru, erasure, requested)).Status == HistoryErasure.EpochStalled ? 1 : 0;
        }, 1)).IsTrue();
        await RunAsync(gru);
        await Assert.That(await MetricAsync(gru, "terrabase_history_erasure_epoch_stalled")).IsEqualTo(1);
        await Assert.That(await MetricAsync(gru, "terrabase_history_erasure_oldest_age")).IsLessThan(HistoryErasures.AlertAge.TotalSeconds).Because("the age gauge stays the true age");
        var stalled = await RequestAsync(gru, erasure, requested);
        await Assert.That(stalled.SettlementToken).IsNull().Because("no settlement while an old epoch is unproven");
    }

    // What an OS crash leaves: the identity file still marked running, so the next start takes the next epoch.
    private static void MarkRunning(string directory)
    {
        var path = Path.Combine(directory, ReplicaIdentityFile.FileName);
        var bytes = File.ReadAllBytes(path);
        bytes[7] = 0;
        System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(0, 40)).AsSpan(0, 4).CopyTo(bytes.AsSpan(40));
        File.WriteAllBytes(path, bytes);
    }

    private static async Task<double> MetricAsync(ReplicaMachine machine, string name)
    {
        var metrics = await machine.Api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        var line = metrics.Split('\n').FirstOrDefault(line => line.StartsWith(name, StringComparison.Ordinal));
        return line is null ? double.NaN : double.Parse(line.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<ReplicaCluster> StartAsync()
    {
        // Only the test runs erasure passes.
        var cluster = await ReplicaCluster.StartAsync(cdc: true, clock: null, configure: (_, services) => WithoutPasses(services), "gru", "sjc");
        // As PlatformSeed does on every start (the harness leaves it off): retried until this node's catalog takes the writes.
        var seed = cluster["gru"].Api.Server.Services.GetRequiredService<IPlatformCatalog>();
        await Assert.That(await UntilAsync(cluster["gru"], async () => await seed.EnsurePlatformAsync(CancellationToken.None) is Done ? 1 : 0, 1)).IsTrue();

        foreach (var machine in cluster.Machines)
        {
            await Assert.That(await UntilAsync(machine, async () => await Engine(machine).OpenDatabaseAsync(Auth, CancellationToken.None) is null ? 0 : 1, 1)).IsTrue();
        }

        // ts-auth provisions its collections; the marker collection is one of them.
        foreach (var collection in new[] { Users, Jobs, HistoryErasure.MarkCollection })
        {
            await WriteAsync(cluster["gru"], async transaction => _ = await transaction.CreateCollectionAsync(new CollectionInfo(collection, 1), CancellationToken.None));
        }

        foreach (var machine in cluster.Machines)
        {
            await Assert.That(await UntilAsync(machine, async () =>
            {
                var store = await Engine(machine).OpenDatabaseAsync(Auth, CancellationToken.None);
                return store is not null && await store.HasCollectionAsync(HistoryErasure.MarkCollection, CancellationToken.None) ? 1 : 0;
            }, 1)).IsTrue();
        }

        return cluster;
    }

    private static void WithoutPasses(IServiceCollection services) =>
        services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && descriptor.ImplementationFactory?.Method.ReturnType == typeof(HistoryErasures)));

    private static HistoryErasures Service(ReplicaMachine machine) => machine.Api.Server.Services.GetRequiredService<HistoryErasures>();

    private static IStorageEngine Engine(ReplicaMachine machine) => machine.Api.Server.Services.GetRequiredService<IStorageEngine>();

    private static Task RunAsync(ReplicaMachine machine) => Service(machine).RunOnceAsync(CancellationToken.None);

    private static async Task<HistoryErasureStatus> RequestAsync(ReplicaMachine machine, string erasure, DateTimeOffset requested, string? token = null) =>
        await Service(machine).RequestAsync(erasure, requested, token, CancellationToken.None) switch
        {
            HistoryErasureStatus status => status,
            var other => throw new InvalidOperationException(Result(other)),
        };

    private static string Result(HistoryErasureResult result) => result switch
    {
        HistoryErasureStatus status => status.Status,
        Conflict conflict => conflict.Code,
        Unavailable unavailable => unavailable.Code,
    };

    private static async Task<HistoryErasureRecord?> RecordAsync(ReplicaMachine machine, string erasure) =>
        await machine.Api.Server.Services.GetRequiredService<SystemCatalog>().ReadUncheckedAsync(transaction =>
            SystemCatalog.GetAsync(transaction, HistoryErasure.Collection, erasure, Hosting.TerraBaseJsonContext.Default.HistoryErasureRecord, CancellationToken.None), CancellationToken.None);

    private static async Task<int> ReceiptsAsync(ReplicaMachine machine, string erasure) =>
        await machine.Api.Server.Services.GetRequiredService<SystemCatalog>().ReadUncheckedAsync(async transaction =>
            (await transaction.ListAsync(HistoryErasure.Receipts(erasure), null, 100, 1 << 20, CancellationToken.None)).Documents.Count, CancellationToken.None);

    private static async Task<Ulid> IncarnationAsync(ReplicaMachine machine, TenantContext database) =>
        (await Engine(machine).OpenDatabaseAsync(database, CancellationToken.None))!.Incarnation;

    // The auth file and every sidecar or private copy beside it.
    private static async Task<string[]> FilesAsync(ReplicaMachine machine)
    {
        var store = (TursoDatabaseStore)(await machine.Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(Auth, CancellationToken.None))!;
        var name = Path.GetFileName(store.FilePath);
        return [.. Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!).Where(file => Path.GetFileName(file).Contains(Path.GetFileNameWithoutExtension(name), StringComparison.Ordinal))];
    }

    private static bool Contains(byte[] bytes, string marker) => bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0;

    private static async Task WriteAsync(ReplicaMachine machine, Func<IStorageTransaction, Task> work)
    {
        var store = (await Engine(machine).OpenDatabaseAsync(Auth, CancellationToken.None))!;
        _ = await store.WriteAsync(async (transaction, _) =>
        {
            await work(transaction);
            return true;
        }, static _ => true, CancellationToken.None);
    }

    private static Task PutAsync(ReplicaMachine machine, string collection, string id, string json) =>
        WriteAsync(machine, async transaction => _ = await transaction.PutAsync(collection, id, Encoding.UTF8.GetBytes(json), CancellationToken.None));

    // ts-auth's request document: the erasure id names the fence; SYSTEM never sees the fence id.
    private static Task RequestDocumentAsync(ReplicaMachine machine, string erasure, string fence) =>
        PutAsync(machine, HistoryErasure.MarkCollection, erasure, $$"""{"kind":"request","erasureId":"{{erasure}}","fenceId":"{{fence}}"}""");

    private static Task DeleteAsync(ReplicaMachine machine, string collection, string id) =>
        WriteAsync(machine, async transaction => _ = await transaction.DeleteAsync(collection, id, CancellationToken.None));

    private static async Task<bool> LiveAsync(ReplicaMachine machine, string collection, string id)
    {
        var store = (await Engine(machine).OpenDatabaseAsync(Auth, CancellationToken.None))!;
        await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        return await transaction.GetVersionAsync(collection, id, CancellationToken.None) is { Deleted: false };
    }

    private static Task<bool> UntilAsync(ReplicaMachine machine, string collection, string id, bool live) =>
        UntilAsync(machine, async () => await LiveAsync(machine, collection, id) == live ? 1 : 0, 1);

    private static async Task<bool> UntilAsync(ReplicaMachine machine, Func<Task<int>> probe, int expected)
    {
        _ = machine;
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() >= expected)
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static string Id() => EntityIds.New().ToString();
}
