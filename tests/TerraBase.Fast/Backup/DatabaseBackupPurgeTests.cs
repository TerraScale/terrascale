using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Backups;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class DatabaseBackupPurgeTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Protected_version_and_upload_pages_resume_after_restart_without_starving_later_entries(bool expireAttempt)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var state = new PurgeFixture();
        ManagedDatabaseRef database;
        string prefix;
        await using (var api = await state.StartAsync())
        {
            database = await api.CreateManagedDatabaseAsync();
            prefix = Prefix(database);
            for (var version = 0; version < 129; version++)
            {
                state.S3.SeedVersion(false, prefix + "a", version.ToString("D4", CultureInfo.InvariantCulture), [1]);
            }

            state.S3.SeedVersion(false, prefix + "z", "last", [1]);
            var neighbor = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "neighbor");
            state.S3.SeedVersion(false, Prefix(neighbor) + "snapshot", "untouched", [1]);
            state.S3.SeedPurgeUpload(false, prefix + "a");
            state.S3.SeedPurgeUpload(false, prefix + "z");
            state.S3.Locked = key => key == prefix + "a";
            state.S3.FailAbort = key => key == prefix + "a";
            await DeleteAsync(api, database, token);
            if (expireAttempt)
            {
                state.S3.BeforePurgeDelete = async (_, _, stop) =>
                {
                    state.S3.BeforePurgeDelete = null;
                    state.Clock.Advance(TimeSpan.FromSeconds(31));
                    await Task.Delay(Timeout.InfiniteTimeSpan, stop);
                };
            }

            await PassAsync(api, token);
            await Assert.That(await VerifiedAsync(api, token)).IsFalse();
        }

        await using var restarted = await state.StartAsync();
        string? resumedKey = null;
        string? resumedVersion = null;
        state.S3.PurgeVersionListed = (_, key, version) => (resumedKey, resumedVersion) = (key, version);
        await PassAsync(restarted, token);
        await Assert.That(resumedKey).IsEqualTo(prefix + "a");
        await Assert.That(resumedVersion).IsEqualTo("0127");
        await Assert.That(state.S3.RetainedVersions(false, prefix)).IsEqualTo(129);
        await Assert.That(state.S3.PendingUploadKeys).IsEquivalentTo([prefix + "a"]);
        await Assert.That(await VerifiedAsync(restarted, token)).IsFalse();
        state.S3.Locked = _ => false;
        state.S3.FailAbort = _ => false;
        await PassAsync(restarted, token);
        await PassAsync(restarted, token);
        await Assert.That(state.S3.RetainedVersions(false, prefix)).IsEqualTo(0);
        await Assert.That(await VerifiedAsync(restarted, token)).IsFalse();
        await PassAsync(restarted, token);
        await Assert.That(await VerifiedAsync(restarted, token)).IsFalse();
        await PassAsync(restarted, token);
        await Assert.That(await VerifiedAsync(restarted, token)).IsTrue();
        await Assert.That(state.S3.PendingUploads).IsEqualTo(0);
        await Assert.That(state.S3.RetainedVersions(false, "v1/")).IsEqualTo(1);
    }

    [Test]
    public async Task Failed_platform_and_revoked_old_destination_do_not_block_later_destination_pages()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var state = new PurgeFixture();
        ManagedDatabaseRef database;
        await using (var api = await state.StartAsync())
        {
            database = await api.CreateManagedDatabaseAsync();
            await AddDestinationsAsync(api, database, 18, revokedFirst: true, token);
            for (var index = 1; index <= 18; index++)
            {
                state.S3.SeedVersion(true, Prefix(database) + "snapshot", "old", [1], "archive-" + index);
            }

            state.S3.RejectPurgeVersions = (customer, _) => !customer;
            await DeleteAsync(api, database, token);
            await PassAsync(api, token);
            await Assert.That(state.S3.RetainedVersions(true, Prefix(database), "archive-1")).IsEqualTo(1);
            await Assert.That(state.S3.RetainedVersions(true, Prefix(database), "archive-16")).IsEqualTo(0);
            await Assert.That(state.S3.RetainedVersions(true, Prefix(database), "archive-18")).IsEqualTo(1);
            await Assert.That(await VerifiedAsync(api, token)).IsFalse();
        }

        await using var restarted = await state.StartAsync();
        await PassAsync(restarted, token);
        await Assert.That(state.S3.RetainedVersions(true, Prefix(database), "archive-18")).IsEqualTo(0);
        state.S3.RejectPurgeVersions = null;
        await PassAsync(restarted, token);
        await PassAsync(restarted, token);
        await Assert.That(state.S3.RetainedVersions(true, Prefix(database))).IsEqualTo(1);
        await Assert.That(await VerifiedAsync(restarted, token)).IsFalse();
    }

    [Test]
    public async Task The_last_bucket_snapshot_blocks_verification_until_it_is_absent()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var state = new PurgeFixture();
        state.S3.SnapshotsEnabled = true;
        state.S3.SeedSnapshot(state.Clock.GetUtcNow().AddDays(-40));
        await using var api = await state.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await DeleteAsync(api, database, token);
        await PassAsync(api, token);
        await PassAsync(api, token);
        await Assert.That(await VerifiedAsync(api, token)).IsFalse();
        await api.Server.Services.GetRequiredService<S3ObjectStore>().DeleteSnapshotAsync(state.S3.Snapshots.Single().Version, token);
        await PassAsync(api, token);
        await Assert.That(await VerifiedAsync(api, token)).IsFalse();
        await PassAsync(api, token);
        await Assert.That(await VerifiedAsync(api, token)).IsTrue();
        await Assert.That(state.S3.BucketDeletes).IsEqualTo(0);
    }

    [Test]
    [Arguments("versions", false)]
    [Arguments("versions", true)]
    [Arguments("uploads", false)]
    [Arguments("uploads", true)]
    public async Task Successful_customer_purge_xml_is_bounded_before_sdk_materialization(string listing, bool chunked)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var state = new PurgeFixture();
        await using var api = await state.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await AddDestinationsAsync(api, database, 1, revokedFirst: false, token);
        state.S3.SeedVersion(true, Prefix(database) + "snapshot", "old", [1], "archive-1");
        await DeleteAsync(api, database, token);
        var bodies = new List<MeasuredBody>();
        var root = listing == "versions" ? "ListVersionsResult" : "ListMultipartUploadsResult";
        var payload = Encoding.UTF8.GetBytes($"<{root} xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">"
            + new string(' ', 5 * 1024 * 1024) + $"<IsTruncated>false</IsTruncated></{root}>");
        state.S3.PurgeListingOverride = (customer, kind) =>
        {
            if (!customer || kind != listing)
            {
                return null;
            }

            var body = new MeasuredBody(payload, chunked);
            bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        };
        await PassAsync(api, token);
        await Assert.That(bodies.Count).IsGreaterThan(0);
        await Assert.That(bodies.All(body => body.Disposed)).IsTrue();
        await Assert.That(bodies.All(body => body.BytesRead <= 2 * 1024 * 1024 + 128 * 1024)).IsTrue();
        await Assert.That(bodies.All(body => body.BytesRead == 0)).IsEqualTo(!chunked);
        if (chunked)
        {
            await Assert.That(bodies.All(body => body.BytesRead > 2 * 1024 * 1024)).IsTrue();
        }
        await Assert.That(await VerifiedAsync(api, token)).IsFalse();
    }

    [Test]
    public async Task Unversioned_bucket_pages_resume_after_their_last_key_and_erase_only_the_database_prefix()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var state = new PurgeFixture();
        await using var api = await state.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var neighbor = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "neighbor");
        var prefix = Prefix(database);
        // Tigris lists an unversioned bucket's objects as version "null" and truncates with only NextKeyMarker (2026-10-28).
        for (var index = 0; index < 257; index++)
        {
            state.S3.SeedObject(false, prefix + index.ToString("D4", CultureInfo.InvariantCulture), [1]);
        }

        state.S3.SeedObject(false, Prefix(neighbor) + "snapshot", [1]);
        var markers = new ConcurrentQueue<(string? Key, string? Version)>();
        state.S3.PurgeVersionListed = (customer, key, version) =>
        {
            if (!customer)
            {
                markers.Enqueue((key, version));
            }
        };
        await DeleteAsync(api, database, token);
        await PassAsync(api, token);
        await Assert.That(Remaining(state, prefix)).IsEqualTo(129);
        state.Clock.Advance(TimeSpan.FromMinutes(10));
        var stuck = await MetricsAsync(api, token);
        await Assert.That(stuck).Contains("terrabase_backup_purge_pending{otel_scope_name=\"TerraScale.TerraBase\"} 1");
        var age = double.Parse(stuck.Split('\n').Single(line => line.StartsWith("terrabase_backup_purge_oldest_age_seconds{", StringComparison.Ordinal))
            .Split(' ')[^1], CultureInfo.InvariantCulture);
        await Assert.That(age).IsBetween(600, 601);

        await PassAsync(api, token);
        await Assert.That(markers.Last()).IsEqualTo((prefix + "0127", (string?)null));
        await Assert.That(Remaining(state, prefix)).IsEqualTo(1);
        for (var pass = 0; pass < 4 && !await VerifiedAsync(api, token); pass++)
        {
            await PassAsync(api, token);
        }

        await Assert.That(await VerifiedAsync(api, token)).IsTrue();
        await Assert.That(Remaining(state, prefix)).IsEqualTo(0);
        await Assert.That(state.S3.Objects.Keys).Contains(Prefix(neighbor) + "snapshot");
        await Assert.That(await MetricsAsync(api, token)).Contains("terrabase_backup_purge_pending{otel_scope_name=\"TerraScale.TerraBase\"} 0");
    }

    [Test]
    public async Task Bucket_request_timeouts_retry_within_one_pass()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var state = new PurgeFixture(shortWaitsElapse: true);
        await using var api = await state.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var prefix = Prefix(database);
        state.S3.SeedObject(false, prefix + "snapshot", [1]);
        await DeleteAsync(api, database, token);
        // More consecutive timeouts than the SDK's own attempts: only the purge's retry gets past them.
        var timeouts = 0;
        state.S3.PurgeListingOverride = (customer, _) => !customer && Interlocked.Increment(ref timeouts) <= 3
            ? new HttpResponseMessage(HttpStatusCode.RequestTimeout)
            {
                Content = new StringContent("<Error><Code>RequestTimeout</Code><Message>RequestTimeout</Message></Error>", Encoding.UTF8, "application/xml"),
            }
            : null;
        await PassAsync(api, token);
        await Assert.That(Volatile.Read(ref timeouts)).IsGreaterThan(3);
        await Assert.That(Remaining(state, prefix)).IsEqualTo(0);
    }

    private static int Remaining(PurgeFixture state, string prefix) => state.S3.Objects.Keys.Count(key => key.StartsWith(prefix, StringComparison.Ordinal));

    private static Task<string> MetricsAsync(ManagementApi api, CancellationToken token) =>
        api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token);

    private static string Prefix(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/";

    private static Task PassAsync(ManagementApi api, CancellationToken token) => api.Server.Services.GetRequiredService<DatabaseBackupPurge>().RunOnceAsync(token);

    private static async Task<bool> VerifiedAsync(ManagementApi api, CancellationToken token) =>
        (await api.Server.Services.GetRequiredService<SystemCatalog>().ListDatabasePurgesAsync(null, 100, token))!.Items.Single().BackupVerifiedAt is not null;

    private static async Task DeleteAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken token)
    {
        using var response = await api.Client.DeleteAsync(ManagementApi.Uri(database.Path), token);
        response.EnsureSuccessStatusCode();
        await api.Server.Services.GetRequiredService<EngineManagementService>().ConvergeAsync(token);
        await Assert.That(await api.Server.Services.GetRequiredService<SystemCatalog>().AreActiveCopiesAbsentAsync(
            new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id), token)).IsTrue();
    }

    private static async Task AddDestinationsAsync(ManagementApi api, ManagedDatabaseRef database, int count, bool revokedFirst, CancellationToken token)
    {
        var master = api.Server.Services.GetRequiredService<IMasterKey>();
        var working = await CustomerCredentials.SealAsync(master, FakeS3.CustomerAccessKey, FakeS3.CustomerSecretKey, token);
        var revoked = await CustomerCredentials.SealAsync(master, "revoked-test-key", "revoked-test-secret", token);
        await api.Server.Services.GetRequiredService<SystemCatalog>().WriteDurableAsync(async transaction =>
        {
            for (var index = 1; index <= count; index++)
            {
                var revision = index.ToString("D26", CultureInfo.InvariantCulture);
                var destination = new ProjectBackupDestinationRecord(1, revision,
                    new ProjectBackupDestination(new Uri(FakeS3.CustomerEndpoint), "test-region-1", "archive-" + index, "customer", DateTimeOffset.UtcNow),
                    revokedFirst && index == 1 ? revoked : working);
                await BackupDestinationHistory.SaveAsync(transaction, database.Project, destination, token);
            }

            return true;
        }, static _ => false, token);
    }

    private sealed class PurgeFixture : IAsyncDisposable
    {
        private readonly bool _shortWaitsElapse;
        private readonly string _directory = Directory.CreateTempSubdirectory("backup-purge-").FullName;
        private readonly string _user = EntityIds.New().ToString();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
        public FakeS3 S3 { get; } = new() { SnapshotsEnabled = false };

        public PurgeFixture(bool shortWaitsElapse = false)
        {
            _shortWaitsElapse = shortWaitsElapse;
            S3.Time = Clock;
        }

        public Task<ManagementApi> StartAsync() => ManagementApi.StartAsync(settings =>
        {
            S3.Configure(settings);
            settings["data-directory"] = _directory;
        }, services =>
        {
            S3.Route(services);
            ManualBackupHosting.Configure(services);
            var convergence = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(CatalogConvergence));
            services.Remove(convergence);
        }, time: _shortWaitsElapse ? new ShortWaitsElapse(Clock) : Clock, userId: _user);

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    // The clock, except that one-shot waits of up to 2 s (the purge's retry backoff) elapse at once.
    private sealed class ShortWaitsElapse(FakeTimeProvider clock) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override long GetTimestamp() => clock.GetTimestamp();
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = clock.CreateTimer(callback, state, dueTime, period);
            if (dueTime > TimeSpan.Zero && dueTime <= TimeSpan.FromSeconds(2) && period == Timeout.InfiniteTimeSpan)
            {
                _ = Task.Run(() => clock.Advance(dueTime)); // After the caller holds its timer.
            }

            return timer;
        }
    }

    private sealed class MeasuredBody(byte[] bytes, bool chunked) : MemoryStream(bytes)
    {
        public override bool CanSeek => !chunked;
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            var buffer = new byte[Math.Min(bufferSize, 81920)];
            int count;
            while ((count = await ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Disposed |= disposing;
        }
    }
}
