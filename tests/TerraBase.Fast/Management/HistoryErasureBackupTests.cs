using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed partial class HistoryErasureBackupTests
{
    private const string Users = "users";
    private const string Jobs = "deletion_jobs";
    private static readonly TenantContext Auth = HistoryErasures.AuthDatabase;

    [Test]
    [Timeout(300_000)]
    public async Task Staggered_erasures_share_each_files_rewrite_window_and_all_complete(CancellationToken cancellationToken)
    {
        await using var host = new Host();
        RecordingRewrite recording = null!;
        await using var api = await host.StartAsync(services => services.AddSingleton<IHealthyRewrite>(provider =>
            recording = new RecordingRewrite(provider.GetRequiredService<TursoStorageEngine>(), host.Clock)));
        await Host.SeedAsync(api);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        List<(string Fence, string Erasure, DateTimeOffset Requested)> erasures = [];

        async Task<HistoryErasureStatus> RequestAsync(string erasure, DateTimeOffset requested, string? token = null)
        {
            using var response = await api.Server.Client.PutAsJsonAsync(new Uri("/admin/history-erasures/" + erasure, UriKind.Relative),
                new HistoryErasureRequest(1, requested, token), Hosting.TerraBaseJsonContext.Default.HistoryErasureRequest, cancellationToken);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync(Hosting.TerraBaseJsonContext.Default.HistoryErasureStatus, cancellationToken))!;
        }

        for (var index = 0; index < 3; index++)
        {
            var (fence, erasure, requested) = (Id(), Id(), host.Clock.GetUtcNow());
            erasures.Add((fence, erasure, requested));
            await Host.PutAsync(api, Jobs, fence, """{"scope":"account"}""");
            await Host.PutAsync(api, HistoryErasure.MarkCollection, erasure, $$"""{"kind":"request","erasureId":"{{erasure}}","fenceId":"{{fence}}"}""");
            await RequestAsync(erasure, requested);
            await Host.PassAsync(api);
            var settled = await RequestAsync(erasure, requested);
            await Assert.That(settled.SettlementToken).IsNotNull();
            await RequestAsync(erasure, requested, settled.SettlementToken);
            await Host.PassAsync(api);
            await Assert.That(recording.Starts.Count(call => call.Database is not null)).IsEqualTo(1);
            await Assert.That(recording.Starts.Count(call => call.Database is null)).IsEqualTo(1);
            await Assert.That((await RequestAsync(erasure, requested)).Status).IsEqualTo(index == 0 ? HistoryErasure.Residue : HistoryErasure.Rewriting);
            host.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        host.Clock.Advance(TimeSpan.FromMinutes(7) - TimeSpan.FromTicks(1));
        await Host.PassAsync(api);
        await Assert.That(recording.Starts.Count).IsEqualTo(2);
        host.Clock.Advance(TimeSpan.FromTicks(1));
        await Host.PassAsync(api);
        await Assert.That(recording.Starts.Count).IsEqualTo(4).Because("one auth/SYSTEM pair covers both later final cuts");
        var second = await Host.OwnAsync(api, erasures[1].Erasure);
        var third = await Host.OwnAsync(api, erasures[2].Erasure);
        await Assert.That(second.Auth).IsEqualTo(third.Auth);
        await Assert.That(second.System).IsEqualTo(third.System);

        host.Clock.Advance(TimeSpan.FromDays(8));
        await Host.CollectAndTrimAsync(api);
        await Host.PassAsync(api);
        await Assert.That(recording.Starts.Count).IsEqualTo(5).Because("residue is also batched");
        await DaysOfBackupAsync(host, backup, 3);
        await Host.PassWithBackupAsync(api, backup);
        await DaysOfBackupAsync(host, backup, 15);
        await Host.PassWithBackupAsync(api, backup);
        foreach (var (fence, erasure, requested) in erasures)
        {
            var status = await RequestAsync(erasure, requested);
            await Assert.That(status.Status).IsEqualTo(HistoryErasure.Retire);
            await Assert.That(status.ActiveDueAt).IsEqualTo(requested.AddDays(30));
            await Assert.That(status.BackupDueAt).IsEqualTo(requested.AddDays(31));
            await Assert.That(host.Clock.GetUtcNow()).IsLessThan(status.BackupDueAt);
            await Assert.That(status.ActiveOverdue || status.BackupOverdue).IsFalse();
            await Host.RetireAsync(api, fence, erasure);
        }

        await Host.PassWithBackupAsync(api, backup);
        host.Clock.Advance(TimeSpan.FromDays(8));
        await Host.CollectAndTrimAsync(api);
        await Host.PassWithBackupAsync(api, backup);

        for (var retry = 0; retry < 4; retry++)
        {
            var complete = true;
            foreach (var (_, erasure, requested) in erasures)
            {
                complete &= (await RequestAsync(erasure, requested)).Status == HistoryErasure.Complete;
            }
            if (complete)
            {
                break;
            }
            host.Clock.Advance(TimeSpan.FromMinutes(10));
            await Host.PassWithBackupAsync(api, backup);
        }
        foreach (var (_, erasure, requested) in erasures)
        {
            await Assert.That((await RequestAsync(erasure, requested)).Status).IsEqualTo(HistoryErasure.Complete);
        }

        await Assert.That(recording.Starts.Count - recording.TransientRefusals).IsEqualTo(6)
            .Because("four successful auth rewrites and two SYSTEM rewrites complete three erasures; other refusal reasons still fail");
        foreach (var file in recording.Starts.GroupBy(call => call.Database is null))
        {
            foreach (var (earlier, later) in file.Zip(file.Skip(1)))
            {
                await Assert.That(later.Started - earlier.Started).IsGreaterThanOrEqualTo(TimeSpan.FromMinutes(10));
            }
        }
    }

    private sealed class RecordingRewrite(IHealthyRewrite engine, TimeProvider clock) : IHealthyRewrite
    {
        public List<(TenantContext? Database, DateTimeOffset Started)> Starts { get; } = [];
        public int TransientRefusals { get; private set; }

        public async ValueTask<HealthyRewrite> RewriteAsync(TenantContext? database, long minimumOffset, CancellationToken cancellationToken)
        {
            Starts.Add((database, clock.GetUtcNow()));
            try
            {
                return await engine.RewriteAsync(database, minimumOffset, cancellationToken);
            }
            catch (RewriteRefusedException error) when (error.Reason is RewriteRefusal.Budget or RewriteRefusal.Pressure or RewriteRefusal.Deadline)
            {
                TransientRefusals++;
                Console.WriteLine($"Batched erasure rewrite refused: {error.Reason} ({error.Message})");
                throw;
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [Timeout(300_000)]
    public async Task Backup_closure_waits_for_pre_erasure_generations_and_snapshots_then_completes(bool shipBeforeReceipt, CancellationToken _1)
    {
        await using var host = new Host();
        await using var api = await host.StartAsync(services =>
        {
            if (shipBeforeReceipt)
            {
                services.AddSingleton<IHealthyRewrite>(provider => new ShipBeforeReceipt(
                    provider.GetRequiredService<TursoStorageEngine>(), provider.GetRequiredService<IContinuousBackup>(), host.Clock));
            }
        });
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
        await Assert.That(host.S3.Objects.Keys.Any(key => key.StartsWith(BackupLayout.Snapshots(BackupLayout.File(Auth, "local")), StringComparison.Ordinal))).IsTrue();

        var preErasureGeneration = host.S3.Objects.Keys.Where(key => key.StartsWith(BackupLayout.Snapshots(BackupLayout.File(Auth, "local")), StringComparison.Ordinal))
            .Select(BackupLayout.ParseSnapshot).First()!.Generation;

        host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var requested = host.Clock.GetUtcNow();
        await Host.RequestAsync(api, erasure, requested);
        await Host.PassWithBackupAsync(api, backup);
        var settled = await Host.RequestAsync(api, erasure, requested);
        await Assert.That(settled.SettlementToken).IsNotNull();
        await Assert.That((await Host.RequestAsync(api, erasure, requested, settled.SettlementToken)).Status).IsEqualTo(HistoryErasure.Rewriting);
        await Assert.That(await Host.UntilPhaseAsync(host, api, backup, erasure, requested, HistoryErasure.Residue)).IsEqualTo(HistoryErasure.Residue);

        host.Clock.Advance(TimeSpan.FromDays(8));
        await Host.CollectAndTrimAsync(api);
        await Assert.That(await Host.UntilPhaseAsync(host, api, backup, erasure, requested, HistoryErasure.Backup)).IsEqualTo(HistoryErasure.Backup).Because("generations and bucket snapshots from before the residue rewrite remain");

        if (shipBeforeReceipt)
        {
            var receipt = await Host.OwnAsync(api, erasure);
            await Assert.That(host.S3.Objects.Keys.Where(key => key.Contains("/snapshots/", StringComparison.Ordinal)).Select(BackupLayout.ParseSnapshot).Any(snapshot => snapshot is not null
                && snapshot.Key.StartsWith(BackupLayout.Snapshots(BackupLayout.File(Auth, "local")), StringComparison.Ordinal)
                && snapshot.Generation.Time < receipt.ResidueAt && snapshot.At < receipt.ResidueAt)).IsTrue()
                .Because("a real generation was pinned on the rewritten file before its receipt was published");
        }

        await DaysOfBackupAsync(host, backup, 3);
        await Assert.That(host.S3.Objects.ContainsKey(BackupLayout.GenerationProof(BackupLayout.File(Auth, "local"), preErasureGeneration))).IsFalse()
            .Because("retention removes obsolete certificates after their snapshots and WAL are gone");

        host.S3.SeedVersion(customer: false, BackupLayout.File(Auth, "local") + "/snapshots/hidden", "v-old", [1]);
        await Host.PassWithBackupAsync(api, backup);
        await Assert.That((await Host.RequestAsync(api, erasure, requested)).Status).IsEqualTo(HistoryErasure.Backup);
        await Assert.That((await Host.RecordAsync(api, erasure)).BackupCleanAt).IsNull();
        await Assert.That(host.S3.ExpireVersion(customer: false, BackupLayout.File(Auth, "local") + "/snapshots/hidden", "v-old")).IsTrue();

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

        await Host.RetireAsync(api, fence, erasure);
        await Host.PassWithBackupAsync(api, backup);
        await Assert.That((await Host.RequestAsync(api, erasure, requested)).Status).IsEqualTo(HistoryErasure.Retire).Because("the retirement's tombstones are not collected yet");
        host.Clock.Advance(TimeSpan.FromDays(8));
        await Host.CollectAndTrimAsync(api);
        await Assert.That(await Host.UntilPhaseAsync(host, api, backup, erasure, requested, HistoryErasure.Complete)).IsEqualTo(HistoryErasure.Complete);

        await Host.PassWithBackupAsync(api, backup);
        var joined = await Host.RequestAsync(api, erasure, requested);
        await Assert.That(joined.Status).IsEqualTo(HistoryErasure.Complete);
        await Assert.That(joined.SettlementToken).IsNull();
        await Assert.That(await Host.ResultAsync(api, erasure, requested, settled.SettlementToken)).IsEqualTo("settlement_stale");

        var staged = Directory.EnumerateFiles(host.Directory, "*", SearchOption.AllDirectories).Count(file => file.Contains("/.backup/", StringComparison.Ordinal));
        await api.DisposeAsync();
        foreach (var file in Directory.EnumerateFiles(host.Directory, "*", SearchOption.AllDirectories))
        {
            await Assert.That((await File.ReadAllBytesAsync(file, CancellationToken.None)).AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0).IsFalse().Because(file);
        }

        await Assert.That(staged).IsGreaterThan(0).Because("shipping staged frames of the rewritten files while running");

    }

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

        host.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        await Host.PassAsync(api);
        await Assert.That(systemAttempts).IsEqualTo(1);
        host.Clock.Advance(TimeSpan.FromTicks(1));
        await Host.PassAsync(api);
        own = await Host.OwnAsync(api, erasure);

        for (var window = 0; window < 3 && own.Phase != HistoryErasure.Rewritten; window++)
        {
            host.Clock.Advance(TimeSpan.FromMinutes(10));
            await Host.PassAsync(api);
            own = await Host.OwnAsync(api, erasure);
        }

        await Assert.That(systemAttempts).IsGreaterThanOrEqualTo(2);
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

    [Test]
    [Arguments("valid")]
    [Arguments("missing")]
    [Arguments("tampered")]
    [Arguments("other_file")]
    [Arguments("later_offset")]
    public async Task A_generation_predating_the_receipt_needs_authenticated_file_and_offset_evidence(string evidence)
    {
        await using var host = new Host();
        await using var api = await host.StartAsync();
        await Host.SeedAsync(api);
        await Host.PutAsync(api, Users, Id(), "{\"survivor\":true}");
        var native = api.Server.Services.GetRequiredService<IStorageEngine>();
        var auth = (await native.OpenDatabaseAsync(Auth, CancellationToken.None))!;
        long offset;
        await using (var read = await auth.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            offset = await read.GetOffsetAsync(CancellationToken.None);
        }
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(CancellationToken.None);
        var file = BackupLayout.File(Auth, "local");
        var snapshot = host.S3.Objects.Keys.Where(key => key.StartsWith(BackupLayout.Snapshots(file), StringComparison.Ordinal))
            .Select(BackupLayout.ParseSnapshot).First()!;
        var proof = BackupLayout.GenerationProof(file, snapshot.Generation);
        await Assert.That(host.S3.Objects.ContainsKey(proof)).IsTrue();
        if (evidence == "missing")
        {
            _ = await api.Server.Services.GetRequiredService<S3ObjectStore>().DeleteAsync(proof, CancellationToken.None);
        }
        else if (evidence == "tampered")
        {
            host.S3.Objects[proof][^1] ^= 1;
        }
        var target = evidence == "other_file" ? Id() : auth.Incarnation.ToString();
        var minimum = evidence == "later_offset" ? long.MaxValue : offset;
        host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var cut = host.Clock.GetUtcNow();
        await Assert.That(snapshot.At < cut && snapshot.Generation.Time < cut).IsTrue();
        var clean = false;
        try
        {
            clean = await HistoryErasures.OlderAbsentAsync(api.Server.Services.GetRequiredService<S3ObjectStore>(), file, cut,
                CancellationToken.None, [new HistoryRewrite(Id(), target, minimum)], api.Server.Services.GetRequiredService<DataKeys>(), BackupLayout.KeyScope(Auth));
        }
        catch (InvalidDataException) when (evidence == "tampered")
        {

        }
        await Assert.That(clean).IsEqualTo(evidence == "valid");
    }

    private sealed class ShipBeforeReceipt(IHealthyRewrite engine, IContinuousBackup backup, FakeTimeProvider clock) : IHealthyRewrite
    {
        public async ValueTask<HealthyRewrite> RewriteAsync(TenantContext? database, long minimumOffset, CancellationToken cancellationToken)
        {
            var result = await engine.RewriteAsync(database, minimumOffset, cancellationToken);
            await backup.RunOnceAsync(cancellationToken);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            return result;
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("terrabase-history-").FullName;

        public FakeS3 S3 { get; } = new();

        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        public Task<ManagementApi> StartAsync(Action<IServiceCollection>? configure = null)
        {
            S3.Time = Clock;
            return ManagementApi.StartAsync(settings =>
        {
            S3.Configure(settings);
            settings["data-directory"] = Directory;
        }, services =>
        {

            ((BackupSettings)services.Single(service => service.ServiceType == typeof(BackupSettings)).ImplementationInstance!).RetentionDays =
                Enum.GetValues<PlanTier>().ToDictionary(plan => plan, _ => 1);
            S3.Route(services);
            ManualBackupHosting.Configure(services);

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

        public static async Task CollectAndTrimAsync(ManagementApi api)
        {
            await api.Server.Services.GetRequiredService<FeedRetention>().TrimAsync(CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
