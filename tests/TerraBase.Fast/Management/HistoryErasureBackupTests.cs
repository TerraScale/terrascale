using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>
/// TS-873 on one real host with a fake clock and the fake S3 bucket behind real continuous backup: an offline listed holder
/// keeps an erasure (and a database purge) pending, and the operator age gauges cross 20 days; backup closure waits for
/// every pre-erasure generation, then completes. The test plays ts-auth.
/// </summary>
public sealed class HistoryErasureBackupTests
{
    private const string Users = "users";
    private const string Jobs = "deletion_jobs";
    private const double Day20 = 20 * 86_400;
    private static readonly TenantContext Auth = HistoryErasures.AuthDatabase;

    [Test]
    public async Task An_offline_listed_holder_keeps_erasure_and_purge_pending_and_the_age_gauges_cross_day_20()
    {
        await using var host = new Host();
        await using var api = await host.StartAsync();
        await Host.SeedAsync(api);
        await Host.WriteSystemAsync(api, transaction => transaction.PutAsync(CatalogGate.Members, "retired-node", "{\"region\":\"sjc\"}"u8.ToArray(), CancellationToken.None));
        var (fence, erasure) = (Id(), Id());
        await Host.PutAsync(api, Jobs, fence, """{"scope":"account"}""");
        await Host.PutAsync(api, HistoryErasure.MarkCollection, erasure, $$"""{"kind":"request","erasureId":"{{erasure}}","fenceId":"{{fence}}"}""");
        var requested = host.Clock.GetUtcNow();
        await Host.RequestAsync(api, erasure, requested);
        var database = await api.CreateManagedDatabaseAsync();
        using (var deleted = await api.Client.DeleteAsync(new Uri(database.Path, UriKind.Relative)))
        {
            await Assert.That(deleted.IsSuccessStatusCode).IsTrue();
        }

        host.Clock.Advance(TimeSpan.FromDays(19));
        await Host.PassAsync(api);
        var early = await Host.RequestAsync(api, erasure, requested);
        await Assert.That(early.Status).IsEqualTo(HistoryErasure.Fencing);
        await Assert.That(early.PendingHolders).IsEqualTo(1).Because("retired-node never marks");
        await Assert.That(await Host.MetricAsync(api, "terrabase_history_erasure_oldest_age")).IsLessThan(Day20);
        await Assert.That(await Host.MetricAsync(api, "terrabase_purge_oldest_age")).IsLessThan(Day20);

        host.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await Host.PassAsync(api);
        var late = await Host.RequestAsync(api, erasure, requested);
        await Assert.That(late.Status).IsEqualTo(HistoryErasure.Fencing).Because("no automatic membership removal: the holder stays expected");
        await Assert.That(await Host.MetricAsync(api, "terrabase_history_erasure_oldest_age")).IsGreaterThanOrEqualTo(Day20);
        await Assert.That(await Host.MetricAsync(api, "terrabase_history_erasure_pending")).IsEqualTo(1);
        await Assert.That(await Host.MetricAsync(api, "terrabase_purge_oldest_age")).IsGreaterThanOrEqualTo(Day20);
        await Assert.That(await Host.MetricAsync(api, "terrabase_purge_pending")).IsEqualTo(1);
    }

    // Real continuous backup ships both files before the erasure; their generations keep it at "backup" until the existing
    // plan retention and bucket snapshot retention remove them. The deleted payload is then in no local file, scratch included.
    [Test]
    [Timeout(300_000)] // About 30 simulated days of real shipping and retention passes.
    public async Task Backup_closure_waits_for_pre_erasure_generations_and_snapshots_then_completes(CancellationToken _1)
    {
        await using var host = new Host();
        await using var api = await host.StartAsync();
        await Host.SeedAsync(api);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var (fence, erasure, subject, survivor) = (Id(), Id(), Id(), Id());
        var secret = "ts873-backup-" + Id();
        await Host.PutAsync(api, Users, survivor, """{"n":1}""");
        await Host.PutAsync(api, Users, subject, $$"""{"secret":"{{secret}}"}""");
        await backup.RunOnceAsync(CancellationToken.None);
        await Host.PutAsync(api, Jobs, fence, """{"scope":"account"}""");
        await Host.DeleteAsync(api, Users, subject);
        await Host.PutAsync(api, HistoryErasure.MarkCollection, erasure, $$"""{"kind":"request","erasureId":"{{erasure}}","fenceId":"{{fence}}"}""");
        await backup.RunOnceAsync(CancellationToken.None);
        await Assert.That(host.S3.Objects.Keys.Any(key => key.StartsWith(BackupLayout.Snapshots(BackupLayout.File(Auth)), StringComparison.Ordinal))).IsTrue();

        // Backup keys carry millisecond timestamps. Separate the pre-erasure inventory from the rewrite cut explicitly.
        host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var requested = host.Clock.GetUtcNow();
        await Host.RequestAsync(api, erasure, requested);
        await Host.PassWithBackupAsync(api, backup);
        var settled = await Host.RequestAsync(api, erasure, requested);
        await Assert.That(settled.SettlementToken).IsNotNull();
        await Assert.That((await Host.RequestAsync(api, erasure, requested, settled.SettlementToken)).Status).IsEqualTo(HistoryErasure.Rewriting);
        await Assert.That(await Host.UntilPhaseAsync(host, api, backup, erasure, requested, HistoryErasure.Residue)).IsEqualTo(HistoryErasure.Residue);

        // Residue clears by the existing rules: tombstone collection at this machine's real horizon (no peers, so its clock
        // less 60 s) and feed retention (7 days).
        host.Clock.Advance(TimeSpan.FromDays(8));
        await Host.CollectAndTrimAsync(api);
        await Assert.That(await Host.UntilPhaseAsync(host, api, backup, erasure, requested, HistoryErasure.Backup)).IsEqualTo(HistoryErasure.Backup).Because("generations and bucket snapshots from before the residue rewrite remain");

        // Shipping restarts generations on the rewritten files; plan retention (1 day here) removes the older objects.
        await DaysOfBackupAsync(host, backup, 3);

        // A versioned bucket keeps what retention deleted: a noncurrent version under either prefix holds closure.
        host.S3.SeedVersion(customer: false, BackupLayout.File(Auth) + "/snapshots/hidden", "v-old", [1]);
        await Host.PassWithBackupAsync(api, backup);
        await Assert.That((await Host.RequestAsync(api, erasure, requested)).Status).IsEqualTo(HistoryErasure.Backup);
        await Assert.That((await Host.RecordAsync(api, erasure)).BackupCleanAt).IsNull();
        await Assert.That(host.S3.ExpireVersion(customer: false, BackupLayout.File(Auth) + "/snapshots/hidden", "v-old")).IsTrue();

        // The first clean inventory is only a cut: bucket snapshots taken until then (daily, kept 14 days) still hold the old
        // objects, so the erasure stays at backup inside that window.
        await Host.PassWithBackupAsync(api, backup);
        var clean = (await Host.RecordAsync(api, erasure)).BackupCleanAt;
        await Assert.That(clean).IsNotNull();
        await Assert.That(host.S3.Snapshots.Any(snapshot => snapshot.At <= clean)).IsTrue();
        await Host.PassWithBackupAsync(api, backup);
        await Assert.That((await Host.RequestAsync(api, erasure, requested)).Status).IsEqualTo(HistoryErasure.Backup).Because("a bucket snapshot from before the clean inventory remains");

        await DaysOfBackupAsync(host, backup, 15);
        await Assert.That(host.S3.Snapshots.Any(snapshot => snapshot.At <= clean)).IsFalse();
        await Host.PassWithBackupAsync(api, backup);
        var certified = await Host.RecordAsync(api, erasure);
        await Assert.That(certified.Certified).IsTrue();
        await Assert.That(certified.FinalMarks is null && certified.SettledMarks is null && certified.SettlementToken is null).IsTrue().Because("only opaque ids stay permanently");
        await Assert.That((await Host.RequestAsync(api, erasure, requested)).Status).IsEqualTo(HistoryErasure.Retire);

        // ts-auth retires the fence (job, markers, request document last, in one transaction); this machine then rewrites once
        // the retirement's tombstones and feed events are gone, and only then is the erasure complete.
        await Host.RetireAsync(api, fence, erasure);
        await Host.PassWithBackupAsync(api, backup);
        await Assert.That((await Host.RequestAsync(api, erasure, requested)).Status).IsEqualTo(HistoryErasure.Retire).Because("the retirement's tombstones are not collected yet");
        host.Clock.Advance(TimeSpan.FromDays(8));
        await Host.CollectAndTrimAsync(api);
        await Assert.That(await Host.UntilPhaseAsync(host, api, backup, erasure, requested, HistoryErasure.Complete)).IsEqualTo(HistoryErasure.Complete);

        // A machine joining after completion copies the erased files: the certificate stands (no fencing, no null receipt), and
        // nothing settles or finalizes again.
        await Host.WriteSystemAsync(api, transaction => transaction.PutAsync(CatalogGate.Members, "joined-later", "{\"region\":\"sjc\"}"u8.ToArray(), CancellationToken.None));
        await Host.PassWithBackupAsync(api, backup);
        var joined = await Host.RequestAsync(api, erasure, requested);
        await Assert.That(joined.Status).IsEqualTo(HistoryErasure.Complete);
        await Assert.That(joined.SettlementToken).IsNull();
        await Assert.That(await Host.ResultAsync(api, erasure, requested, settled.SettlementToken)).IsEqualTo("settlement_stale");
        // Every file the stopped host leaves in its data directory, backup staging (.backup) included.
        var staged = Directory.EnumerateFiles(host.Directory, "*", SearchOption.AllDirectories).Count(file => file.Contains("/.backup/", StringComparison.Ordinal));
        await api.DisposeAsync();
        foreach (var file in Directory.EnumerateFiles(host.Directory, "*", SearchOption.AllDirectories))
        {
            await Assert.That((await File.ReadAllBytesAsync(file, CancellationToken.None)).AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0).IsFalse().Because(file);
        }

        await Assert.That(staged).IsGreaterThan(0).Because("shipping staged frames of the rewritten files while running");

    }

    // The auth rewrite succeeds and SYSTEM's is refused: the auth lineage is recorded as it succeeded, so the next pass does not
    // take its own rewrite for a restore and fence again; and the refused file waits (doubling) before another attempt.
    [Test]
    public async Task A_refused_system_rewrite_keeps_the_auth_lineage_and_backs_off()
    {
        var refusing = 1;
        var systemAttempts = 0;
        await using var host = new Host();
        await using var api = await host.StartAsync(services => services.AddSingleton<Hosting.DatabaseLifecycleCheckpoint>((step, database) =>
        {
            if (step == "rewrite.replacing" && database.OrganizationId == TursoStorageEngine.SystemName)
            {
                Interlocked.Increment(ref systemAttempts);
                if (Volatile.Read(ref refusing) == 1)
                {
                    throw new IOException("injected refusal before the rename");
                }
            }
        }));
        // This test targets a refusal of SYSTEM after auth rewrites successfully. Backup shipping owns native readers
        // and can legitimately time out auth's drain first; the backup-closure test covers that overlap separately.
        await Host.SeedAsync(api);
        var (fence, erasure) = (Id(), Id());
        await Host.PutAsync(api, Jobs, fence, """{"scope":"account"}""");
        await Host.PutAsync(api, HistoryErasure.MarkCollection, erasure, $$"""{"kind":"request","erasureId":"{{erasure}}","fenceId":"{{fence}}"}""");
        var requested = host.Clock.GetUtcNow();
        await Host.RequestAsync(api, erasure, requested);
        await Host.PassAsync(api);
        var token = (await Host.RequestAsync(api, erasure, requested)).SettlementToken;
        await Assert.That((await Host.RequestAsync(api, erasure, requested, token)).Status).IsEqualTo(HistoryErasure.Rewriting);
        var marked = await Host.OwnAsync(api, erasure);

        await Host.PassAsync(api);
        await Assert.That(systemAttempts).IsEqualTo(1);
        var auth = await Host.AuthIncarnationAsync(api);
        await Assert.That((await Host.LineageAsync(api)).Auth).IsEqualTo(auth.ToString()).Because("the auth rewrite's lineage is recorded as it succeeds");
        await Host.PassAsync(api);
        await Assert.That(systemAttempts).IsEqualTo(1).Because("the refused file waits before its next attempt");
        var own = await Host.OwnAsync(api, erasure);
        await Assert.That(own.AuthMark).IsEqualTo(marked.AuthMark).Because("its own rewrite is no restore: no new fence marker");

        Volatile.Write(ref refusing, 0);
        host.Clock.Advance(TimeSpan.FromSeconds(11));
        await Host.PassAsync(api);
        own = await Host.OwnAsync(api, erasure);
        await Assert.That(own.Phase).IsEqualTo(HistoryErasure.Rewritten);
        await Assert.That(own.AuthMark).IsEqualTo(marked.AuthMark);
        await Assert.That(own.Auth!.Source).IsEqualTo(auth.ToString()).Because("the second auth rewrite replaced exactly the file its coverage read");
    }

    private static string Id() => EntityIds.New().ToString();

    private static async Task DaysOfBackupAsync(Host host, IContinuousBackup backup, int days)
    {
        for (var day = 0; day < days; day++)
        {
            host.Clock.Advance(TimeSpan.FromDays(1));
            await backup.RunOnceAsync(CancellationToken.None);
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("terrabase-history-").FullName;

        public FakeS3 S3 { get; } = new();

        // A frozen sub-millisecond clock would make a newly shipped generation's rounded timestamp precede its rewrite cut.
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        public Task<ManagementApi> StartAsync(Action<IServiceCollection>? configure = null)
        {
            S3.Time = Clock;
            return ManagementApi.StartAsync(settings =>
        {
            S3.Configure(settings);
            settings["data-directory"] = Directory;
            // SYSTEM uses the longest plan retention, so every plan must share this fixture's one-day window.
            settings["backup-retention-days"] = string.Join(",", Enum.GetValues<PlanTier>().Select(plan => $"{plan.ToString().ToLowerInvariant()}=1"));
        }, services =>
        {
            S3.Route(services);
            ManualBackupHosting.Configure(services);
            // Only the test runs erasure passes.
            services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationFactory?.Method.ReturnType == typeof(HistoryErasures)));
            configure?.Invoke(services);
        }, time: Clock);
        }

        public static async Task SeedAsync(ManagementApi api)
        {
            var seed = api.Server.Services.GetRequiredService<IPlatformCatalog>();
            await Assert.That(await seed.EnsurePlatformAsync(CancellationToken.None) is Done).IsTrue();
            foreach (var collection in new[] { Users, Jobs, HistoryErasure.MarkCollection })
            {
                await WriteAsync(api, async transaction => _ = await transaction.CreateCollectionAsync(new CollectionInfo(collection, 1), CancellationToken.None));
            }
        }

        // A pass while continuous backup keeps shipping, as on a running host: a rewrite waits for the backup tap, which ends at its
        // next use and lets the old file go.
        public static async Task PassWithBackupAsync(ManagementApi api, IContinuousBackup backup)
        {
            var pass = PassAsync(api);
            while (!pass.IsCompleted)
            {
                await backup.RunOnceAsync(CancellationToken.None);
                using var pause = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await pass.WaitAsync(pause.Token).ContinueWith(static _ => { }, TaskScheduler.Default);
            }

            await pass;
        }

        public static async Task<HistoryErasureReceipt> OwnAsync(ManagementApi api, string erasure) =>
            (await api.Server.Services.GetRequiredService<SystemCatalog>().ReadUncheckedAsync(transaction =>
                SystemCatalog.GetAsync(transaction, HistoryErasure.Receipts(erasure), "local", Hosting.TerraBaseJsonContext.Default.HistoryErasureReceipt, CancellationToken.None), CancellationToken.None))!;

        public static async Task<HistoryErasureLineage> LineageAsync(ManagementApi api) =>
            (await api.Server.Services.GetRequiredService<SystemCatalog>().ReadUncheckedAsync(transaction =>
                SystemCatalog.GetAsync(transaction, HistoryErasure.Lineage, "local", Hosting.TerraBaseJsonContext.Default.HistoryErasureLineage, CancellationToken.None), CancellationToken.None))!;

        public static async Task<Ulid> AuthIncarnationAsync(ManagementApi api) =>
            (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(Auth, CancellationToken.None))!.Incarnation;

        public static async Task<HistoryErasureRecord> RecordAsync(ManagementApi api, string erasure) =>
            (await api.Server.Services.GetRequiredService<SystemCatalog>().ReadUncheckedAsync(transaction =>
                SystemCatalog.GetAsync(transaction, HistoryErasure.Collection, erasure, Hosting.TerraBaseJsonContext.Default.HistoryErasureRecord, CancellationToken.None), CancellationToken.None))!;

        public static async Task<string> ResultAsync(ManagementApi api, string erasure, DateTimeOffset requested, string? token) =>
            await api.Server.Services.GetRequiredService<HistoryErasures>().RequestAsync(erasure, requested, token, CancellationToken.None) switch
            {
                HistoryErasureStatus status => status.Status,
                Conflict conflict => conflict.Code,
                Unavailable unavailable => unavailable.Code,
            };

        // ts-auth's retirement transaction (AuthStore.RetireDeletionFenceAsync): the job, every marker, the request document last.
        public static async Task RetireAsync(ManagementApi api, string fence, string erasure)
        {
            var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(Auth, CancellationToken.None))!;
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            _ = await transaction.DeleteAsync(Jobs, fence, CancellationToken.None);
            foreach (var mark in (await transaction.ListAsync(HistoryErasure.MarkCollection, null, 100, 1 << 20, CancellationToken.None)).Documents.Where(mark => mark.Id != erasure))
            {
                _ = await transaction.DeleteAsync(HistoryErasure.MarkCollection, mark.Id, CancellationToken.None);
            }

            _ = await transaction.DeleteAsync(HistoryErasure.MarkCollection, erasure, CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        // Passes until the erasure reaches `phase`, at most five: a rewrite refused under load (its drain deadline) waits out its
        // file's backoff, which the fake clock steps past (an hour covers every backoff step).
        public static async Task<string> UntilPhaseAsync(Host host, ManagementApi api, IContinuousBackup backup, string erasure, DateTimeOffset requested, string phase)
        {
            var status = "";
            for (var attempt = 0; attempt < 5 && status != phase; attempt++)
            {
                if (attempt > 0)
                {
                    host.Clock.Advance(TimeSpan.FromHours(1));
                }

                await PassWithBackupAsync(api, backup);
                status = (await RequestAsync(api, erasure, requested)).Status;
            }

            return status;
        }

        public static Task PassAsync(ManagementApi api) => api.Server.Services.GetRequiredService<HistoryErasures>().RunOnceAsync(CancellationToken.None);

        public static async Task<HistoryErasureStatus> RequestAsync(ManagementApi api, string erasure, DateTimeOffset requested, string? token = null) =>
            await api.Server.Services.GetRequiredService<HistoryErasures>().RequestAsync(erasure, requested, token, CancellationToken.None) switch
            {
                HistoryErasureStatus status => status,
                Conflict conflict => throw new InvalidOperationException(conflict.Code),
                Unavailable unavailable => throw new InvalidOperationException(unavailable.Code),
            };

        public static async Task<double> MetricAsync(ManagementApi api, string name)
        {
            var metrics = await api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
            var line = metrics.Split('\n').FirstOrDefault(line => line.StartsWith(name, StringComparison.Ordinal) && !line.StartsWith('#'));
            return line is null ? double.NaN : double.Parse(line.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture);
        }

        public static async Task WriteAsync(ManagementApi api, Func<IStorageTransaction, Task> work)
        {
            var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(Auth, CancellationToken.None))!;
            _ = await store.WriteAsync(async (transaction, _) =>
            {
                await work(transaction);
                return true;
            }, static _ => true, CancellationToken.None);
        }

        public static async Task WriteSystemAsync(ManagementApi api, Func<IStorageTransaction, ValueTask<long>> write)
        {
            var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            _ = await write(transaction);
            await transaction.CommitAsync(CancellationToken.None);
        }

        public static Task PutAsync(ManagementApi api, string collection, string id, string json) =>
            WriteAsync(api, async transaction => _ = await transaction.PutAsync(collection, id, Encoding.UTF8.GetBytes(json), CancellationToken.None));

        public static Task DeleteAsync(ManagementApi api, string collection, string id) =>
            WriteAsync(api, async transaction => _ = await transaction.DeleteAsync(collection, id, CancellationToken.None));

        // What the change source's maintenance pass and feed retention do on their schedules, run now at this host's clock.
        public static async Task CollectAndTrimAsync(ManagementApi api)
        {
            var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
            var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(Auth, CancellationToken.None))!;
            await using var transaction = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(CancellationToken.None);
            var now = engine.Member.Clock.Tick();
            _ = await transaction.CollectAsync(await transaction.GetHorizonAsync(now, CancellationToken.None), CancellationToken.None);
            _ = await transaction.TrimFeedAsync(new Hlc(now.Value - (ulong)FeedCursor.Retention.TotalMilliseconds * 65_536), 100_000, CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
