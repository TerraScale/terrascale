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

namespace TerraBase.Slow.Backup;

public sealed class DatabaseBackupPurgeTests
{
    [Test]
    public async Task Unversioned_bucket_pages_resume_after_their_last_key_and_erase_only_the_database_prefix()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var state = new PurgeFixture();
        await using var api = await state.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var neighbor = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "neighbor");
        var prefix = Prefix(database);
        for (var index = 0; index < 257; index++)
        {
            state.S3.SeedObject(false, prefix + (index < 129 ? "nodes/node-a/" : "nodes/node-b/") + index.ToString("D4", CultureInfo.InvariantCulture), [1]);
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
        await Assert.That(markers.Last()).IsEqualTo((prefix + "nodes/node-a/0127", (string?)null));
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

    private static int Remaining(PurgeFixture state, string prefix) => state.S3.Objects.Keys.Count(key => key.StartsWith(prefix, StringComparison.Ordinal));

    private static Task<string> MetricsAsync(ManagementApi api, CancellationToken token) =>
        api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token);

    private static string Prefix(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/";

    private static Task PassAsync(ManagementApi api, CancellationToken token) => api.Server.Services.GetRequiredService<DatabaseBackupPurge>().RunOnceAsync(token);

    private static async Task<bool> VerifiedAsync(ManagementApi api, CancellationToken token) =>
        (await api.Server.Services.GetRequiredService<SystemCatalog>().ListDatabasePurgesAsync(null, 100, token))!.Items.Single().BackupVerifiedAt is not null;

    private static async Task DeleteAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken token)
    {
        using var response = await api.ProjectClient(database.Project).DeleteAsync(ManagementApi.Uri(database.Path), token);
        response.EnsureSuccessStatusCode();
        await api.Server.Services.GetRequiredService<EngineManagementService>().RunMaintenanceAsync(token);
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
        private readonly string _directory = Directory.CreateTempSubdirectory("backup-purge-").FullName;
        private readonly string _user = EntityIds.New().ToString();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
        public FakeS3 S3 { get; } = new() { SnapshotsEnabled = false };

        public PurgeFixture()
        {
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
            var convergence = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(CatalogMaintenance));
            services.Remove(convergence);
        }, time: Clock, userId: _user);

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
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
