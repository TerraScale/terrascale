using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Features.Replication.V1;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using ReplicaCluster = TerraBase.Fast.Replication.PeerStreamTests.Cluster;
using ReplicaMachine = TerraBase.Fast.Replication.PeerStreamTests.Machine;

namespace TerraBase.Fast.Management;

/// <summary>
/// TS-873 shared-history erasure across real hosts with real replication (docs/terrabase/history-erasure.md). The test plays
/// ts-auth: it writes the fence, its request document and the subject's records into the platform auth database and presents the settlement token.
/// </summary>
public sealed class HistoryErasureTests
{
    private const string CrashInputPath = "TERRABASE_TEST_ERASURE_EPOCH_CRASH";
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
            // Hosted erasure passes are disabled here. The fence can arrive before the later request/record, so keep
            // driving both holders until each has actually observed the request and published its receipt.
            await Assert.That(await UntilAsync(gru, async () =>
            {
                await RunAsync(gru);
                await RunAsync(sjc);
                return await ReceiptsAsync(gru, erasure);
            }, 2)).IsTrue();

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

    // Two real process deaths leave ACKed tails only on a third holder. No proof/token while snapshots are unavailable;
    // all holders re-fence, actual snapshot import recovers both tails, scoped re-verification removes only the erased rows,
    // and all holders rewrite a fresh final cut. The existing epoch alert remains active while witnesses are unavailable.
    [Test]
    public async Task An_unclean_restart_of_a_holder_stalls_the_erasure_and_raises_the_operator_alert_at_once()
    {
        if (Environment.GetEnvironmentVariable(CrashInputPath) is { } input)
        {
            await CrashChildAsync(input);
            throw new InvalidOperationException("the crash fixture returned");
        }

        var allowSnapshot = 0;
        await using var cluster = await StartAsync((_, services) =>
        {
            var source = services.Last(service => service.ServiceType == typeof(IReplicaSnapshotSource));
            services.AddSingleton<IReplicaSnapshotSource>(provider => new BlockedSnapshots(
                (IReplicaSnapshotSource)source.ImplementationFactory!(provider), () => Volatile.Read(ref allowSnapshot) == 1));
        }, "gru", "sjc", "fra");
        var (gru, sjc, fra) = (cluster["gru"], cluster["sjc"], cluster["fra"]);
        var (fence, erasure) = (Id(), Id());
        await PutAsync(gru, Jobs, fence, """{"scope":"account"}""");
        await RequestDocumentAsync(gru, erasure, fence);
        await Assert.That(await UntilAsync(sjc, HistoryErasure.MarkCollection, erasure, live: true)).IsTrue();
        var requested = DateTimeOffset.UtcNow;
        await RequestAsync(gru, erasure, requested);
        // Hosted passes are disabled. A holder's single pass can precede replication of the request;
        // keep driving every holder until each has observed it and published the required receipt.
        await Assert.That(await UntilAsync(gru, async () =>
        {
            await RunAsync(gru);
            await RunAsync(sjc);
            await RunAsync(fra);
            return await ReceiptsAsync(gru, erasure);
        }, 3)).IsTrue();
        List<string> lateRows = [];
        List<string> survivors = [];
        HistoryErasureStatus recovered = null!;
        for (var crash = 0; crash < 2; crash++)
        {
            var before = sjc.Node;

            // Recovery replaces sjc's file and invalidates its outgoing cursors. Before stopping the snapshot source,
            // wait for fra to ACK a fresh local group in that incarnation. A receipt/token only proves the incoming
            // recovery: an unfinished outgoing refill otherwise leaves fra refusing this origin's crash-fixture batch.
            await PutAsync(sjc, Jobs, Id(), """{"replicationReady":true}""");
            var source = sjc.Groups;
            var incarnation = await IncarnationAsync(sjc, Auth);
            var head = await source.HeadAsync(Auth, incarnation, CancellationToken.None);
            await Assert.That(head).IsNotNull();
            await Assert.That(await UntilAsync(sjc, async () =>
            {
                var cursor = await source.AckedAsync(Auth, fra.Node, CancellationToken.None);
                return cursor is { RequiresRefill: false } ack && ack.Incarnation == incarnation && ack.Acked >= head!.Value ? 1 : 0;
            }, 1)).IsTrue();

            var directory = Path.GetDirectoryName(((TursoDatabaseStore)await sjc.Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None)).FilePath)!;
            var oldIncarnation = await IncarnationAsync(sjc, Auth);
            var (late, survivor) = (Id(), Id());
            lateRows.Add(late);
            survivors.Add(survivor);
            Volatile.Write(ref allowSnapshot, 0);
            await sjc.StopAsync();
            // The child obtains a real durable stream ACK for a tail absent from its local file, then dies without disposing
            // the engine. This makes the acknowledged data survive only on fra and leaves a genuinely running identity.
            await CrashWithPeerOnlyTailAsync(new CrashInput(directory, sjc.Name, fra.Api.Server.BaseAddress.ToString(),
                oldIncarnation.ToString(), PeerTls.Secret, late, survivor));
            await Assert.That(await LiveAsync(fra, Users, late)).IsTrue();
            await Assert.That(await LiveAsync(gru, Users, late)).IsFalse();
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

            // Import the old epoch from another holder's pinned snapshot before issuing any new settlement token.
            Volatile.Write(ref allowSnapshot, 1);
            // First use gru, which lacks fra's ACKed tail. Even after sjc proves its old marker, no token is allowed until
            // fra publishes a new all-holder fence with its actual old-epoch frontier.
            await RecoveryCursorAsync(sjc, erasure, fra.Name);
            await RunAsync(sjc);
            await RunAsync(sjc);
            await Assert.That(await UntilAsync(gru, async () =>
            {
                var (_, receipts) = await Service(gru).StatusAsync((await RecordAsync(gru, erasure))!, CancellationToken.None);
                if (receipts.FirstOrDefault(receipt => receipt.MachineId == sjc.Name && receipt.Replica == sjc.Node.Value) is not { PreviousReplicas: null } own)
                {
                    return 0;
                }
                var auth = (await Engine(gru).OpenDatabaseAsync(Auth, CancellationToken.None))!;
                await using var read = await auth.BeginTransactionAsync(write: false, CancellationToken.None);
                var applied = await read.GetAppliedAsync(CancellationToken.None);
                return applied.TryGetValue(sjc.Node, out var held) && held.Value >= own.AuthMark ? 1 : 0;
            }, 1)).IsTrue();
            await Assert.That((await RequestAsync(gru, erasure, requested)).SettlementToken).IsNull()
                .Because("fra has not re-fenced after this crash; its old receipt predates the surviving ACKed tail");
            recovered = stalled;
            await Assert.That(await UntilAsync(gru, async () =>
            {
                foreach (var holder in cluster.Machines)
                {
                    await RunAsync(holder);
                }
                recovered = await RequestAsync(gru, erasure, requested);
                return recovered.SettlementToken is not null ? 1 : 0;
            }, 1)).IsTrue();
            await Assert.That(recovered.Status).IsEqualTo(HistoryErasure.Reverify);
            await Assert.That(await LiveAsync(sjc, Users, late)).IsTrue().Because("the lost old-epoch tail came from the peer snapshot");
            await Assert.That(await LiveAsync(sjc, Users, survivor)).IsTrue();
        }

        // Auth's scoped re-verification removes late subject data before presenting the fresh token.
        foreach (var late in lateRows)
        {
            await DeleteAsync(gru, Users, late);
        }
        var final = await RequestAsync(gru, erasure, requested, recovered.SettlementToken);
        await Assert.That(final.Status).IsEqualTo(HistoryErasure.Rewriting);
        await Assert.That(await UntilAsync(gru, async () =>
        {
            foreach (var holder in cluster.Machines)
            {
                await RunAsync(holder);
            }
            return (await RequestAsync(gru, erasure, requested)).Status == HistoryErasure.Residue ? 1 : 0;
        }, 1)).IsTrue();
        foreach (var holder in cluster.Machines)
        {
            foreach (var late in lateRows)
            {
                await Assert.That(await LiveAsync(holder, Users, late)).IsFalse();
            }
            foreach (var survivor in survivors)
            {
                await Assert.That(await LiveAsync(holder, Users, survivor)).IsTrue();
            }
        }
    }

    [Test]
    public async Task Retired_epochs_without_advancing_witnesses_do_not_reimport_on_every_pass()
    {
        var snapshots = 0;
        await using var cluster = await StartAsync((region, services) =>
        {
            if (region == "sjc")
            {
                var source = services.Last(service => service.ServiceType == typeof(IReplicaSnapshotSource));
                services.AddSingleton<IReplicaSnapshotSource>(provider => new BlockedSnapshots(
                    (IReplicaSnapshotSource)source.ImplementationFactory!(provider), static () => true,
                    () => Interlocked.Increment(ref snapshots)));
            }
        });
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var erasures = Enumerable.Range(0, 3).Select(_ => Id()).ToArray();
        var requested = DateTimeOffset.UtcNow;
        foreach (var erasure in erasures)
        {
            var fence = Id();
            await PutAsync(gru, Jobs, fence, """{"scope":"account"}""");
            await RequestDocumentAsync(gru, erasure, fence);
            await RequestAsync(gru, erasure, requested);
            await Assert.That(await UntilAsync(gru, async () =>
            {
                await RunAsync(gru);
                await RunAsync(sjc);
                return await ReceiptsAsync(gru, erasure);
            }, 2)).IsTrue();
        }

        foreach (var erasure in erasures)
        {
            var (_, receipts) = await Service(gru).StatusAsync((await RecordAsync(gru, erasure))!, CancellationToken.None);
            var own = receipts.Single(receipt => receipt.MachineId == gru.Name);
            var system = await Engine(gru).OpenSystemDatabaseAsync(CancellationToken.None);
            await using var transaction = await system.BeginDurableTransactionAsync(CancellationToken.None);
            // A retired member never reports again. Neither its original markers nor legacy missing cuts can be
            // manufactured by repeatedly importing a witness whose old-epoch frontier does not advance.
            var old = ReplicaId.Of(gru.Node.Region, gru.Node.Member ^ 1U, 0).Value;
            OriginCut[] cuts = erasure == erasures[0] ? [] : [new(old, own.AuthMark)];
            await SystemCatalog.PutAsync(transaction, HistoryErasure.Receipts(erasure), gru.Name,
                own with { PreviousReplicas = [old], PreviousAuth = cuts, PreviousSystem = cuts,
                    RecutReplicas = [.. receipts.Select(receipt => receipt.Replica).Order()] },
                Hosting.TerraBaseJsonContext.Default.HistoryErasureReceipt, CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        Interlocked.Exchange(ref snapshots, 0);
        await RunAsync(gru);
        var first = Volatile.Read(ref snapshots);
        await Assert.That(first).IsEqualTo(6);
        for (var pass = 0; pass < 4; pass++)
        {
            await RunAsync(gru);
        }

        await Assert.That(Volatile.Read(ref snapshots)).IsEqualTo(first);
        foreach (var erasure in erasures)
        {
            var stalled = await RequestAsync(gru, erasure, requested);
            await Assert.That(stalled.Status).IsEqualTo(HistoryErasure.EpochStalled);
            await Assert.That(stalled.SettlementToken).IsNull();
        }
    }

    private static async Task RecoveryCursorAsync(ReplicaMachine machine, string erasure, string after)
    {
        var system = await Engine(machine).OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await system.BeginDurableTransactionAsync(CancellationToken.None);
        var own = (await SystemCatalog.GetAsync(transaction, HistoryErasure.Receipts(erasure), machine.Name,
            Hosting.TerraBaseJsonContext.Default.HistoryErasureReceipt, CancellationToken.None))!;
        await SystemCatalog.PutAsync(transaction, HistoryErasure.Receipts(erasure), machine.Name, own with { RecoveryAfter = after },
            Hosting.TerraBaseJsonContext.Default.HistoryErasureReceipt, CancellationToken.None);
        _ = await transaction.CommitAsync(CancellationToken.None);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("history-epoch-crash-").FullName;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed record CrashInput(string Directory, string Machine, string Peer, string Incarnation, string Credential, string Late, string Survivor);

    private static async Task CrashWithPeerOnlyTailAsync(CrashInput input)
    {
        using var scratch = new TempDirectory();
        var path = Path.Combine(scratch.Root, "input.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(input));
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("the test executable path is unavailable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        }

        foreach (var argument in new[] { "--treenode-filter", "/*/*/HistoryErasureTests/An_unclean_restart*", "--no-progress", "--disable-logo",
            "--exit-on-process-exit", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--results-directory", Path.Combine(scratch.Root, "results") })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[CrashInputPath] = path;
        start.Environment["TMPDIR"] = scratch.Root;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("the crash fixture did not start");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }

        var stdout = await output;
        var stderr = await error;
        await Assert.That(child.ExitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Contains("crashed after remote tail ACK", StringComparison.Ordinal)).IsTrue().Because(stdout + stderr);
    }

    private static async Task CrashChildAsync(string path)
    {
        var input = JsonSerializer.Deserialize<CrashInput>(await File.ReadAllTextAsync(path))!;
        using var meter = new Meter("erasure-crash-fixture");
        await using var engine = new TursoStorageEngine(input.Directory, 1, 64, 64 * 1024 * 1024, meter,
            NullLogger<TursoStorageEngine>.Instance, new TestMasterKey(), TimeSpan.FromSeconds(5), replicaOwner: input.Machine);
        var origin = engine.ReplicaId;
        var stamp = engine.Member.Clock.Tick();
        using var body = JsonDocument.Parse("{\"oldEpochTail\":true}");
        var state = DocumentState.Bottom.Put(new EntryVersion(stamp, origin), body.RootElement);
        using var channel = PeerChannel.Create(new Uri(input.Peer), new PeerCredential(input.Credential));
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = client.Push(cancellationToken: stop.Token);
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc", Machine = input.Machine } }, stop.Token);
        await Assert.That(await call.ResponseStream.MoveNext(stop.Token)).IsTrue();
        await call.RequestStream.WriteAsync(new PushFrame
        {
            Batch = new Batch
            {
                Database = ChangeGroups.Wire(Auth, Ulid.Parse(input.Incarnation)),
                LastChangeId = 1,
                Groups = ChangeGroups.Encode([new OriginGroup(1, [new ChangeDelta(Users, input.Late, stamp, state), new ChangeDelta(Users, input.Survivor, stamp, state)])]),
            },
        }, stop.Token);
        await Assert.That(await call.ResponseStream.MoveNext(stop.Token)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Ack.Refused).IsFalse();
        Environment.FailFast("crashed after remote tail ACK");
    }

    private sealed class BlockedSnapshots(IReplicaSnapshotSource inner, Func<bool> allowed, Action? onRead = null) : IReplicaSnapshotSource
    {
        public async IAsyncEnumerable<ReplicaSnapshotPage> SnapshotAsync(TenantContext database, Ulid incarnation,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            onRead?.Invoke();
            if (!allowed())
            {
                throw new IOException("the witness snapshot is unavailable");
            }

            await foreach (var page in inner.SnapshotAsync(database, incarnation, cancellationToken))
            {
                yield return page;
            }
        }
    }

    private static async Task<double> MetricAsync(ReplicaMachine machine, string name)
    {
        var metrics = await machine.Api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        var line = metrics.Split('\n').FirstOrDefault(line => line.StartsWith(name, StringComparison.Ordinal));
        return line is null ? double.NaN : double.Parse(line.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<ReplicaCluster> StartAsync(Action<string, IServiceCollection>? configure = null, params string[] regions)
    {
        // Only the test runs erasure passes.
        var cluster = await ReplicaCluster.StartAsync(cdc: true, clock: null, configure: (region, services) =>
        {
            WithoutPasses(services);
            configure?.Invoke(region, services);
        }, regions.Length == 0 ? ["gru", "sjc"] : regions);
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
