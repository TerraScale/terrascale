using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupRegistrationTests
{
    [Test]
    public async Task Discovery_never_opens_or_ships_an_orphan_but_ships_registered_databases_and_tracked_snapshots(CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-backup-registration-").FullName;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        TenantContext orphan;
        ManagedDatabaseRef database;
        string snapshot;
        try
        {
            await using (var seed = await ManagementApi.StartAsync(Configure, Services, time))
            {
                database = await seed.CreateManagedDatabaseAsync();
                using var created = await seed.Client.PostAsync(ManagementApi.Uri(database.Path + "/backups"), null, cancellationToken);
                created.EnsureSuccessStatusCode();
                snapshot = (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString()!;
                orphan = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, EntityIds.New().ToString());
                await seed.Server.Services.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(orphan, cancellationToken);
            }

            // Restart releases seed stores. Observe backup separately from unrelated maintenance readers.
            var opened = new ConcurrentQueue<string>();
            await using var api = await ManagementApi.StartAsync(Configure, services =>
            {
                Services(services);
                services.AddSingleton<IWalBackup>(provider => new ObservedBackup(provider.GetRequiredService<TursoStorageEngine>(), opened));
            }, time);
            var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
            for (var pass = 0; pass < 3; pass++)
            {
                time.Advance(TimeSpan.FromSeconds(10));
                await backup.RunOnceAsync(cancellationToken);
            }

            var orphanPrefix = BackupLayout.File(orphan, "local");
            var registeredPrefix = BackupLayout.File(orphan with { DatabaseId = database.Id }, "local");
            var snapshotPrefix = BackupLayout.File(orphan with { DatabaseId = snapshot }, "local");
            await Assert.That(opened.Contains(orphan.DatabaseId!)).IsFalse();
            await Assert.That(backup.Status.Files.Any(file => file.File == orphanPrefix)).IsFalse();
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(orphanPrefix + "/", StringComparison.Ordinal))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(directory, orphan.OrganizationId, orphan.ProjectId!, orphan.DatabaseId + ".db"))).IsTrue();
            foreach (var prefix in new[] { registeredPrefix, snapshotPrefix, BackupLayout.File(null, "local") })
            {
                await Assert.That(backup.Status.Files.Any(file => file.File == prefix && file.Generation is not null)).IsTrue();
                await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(prefix + "/snapshots/", StringComparison.Ordinal))).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        void Configure(Dictionary<string, string> settings)
        {
            settings["data-directory"] = directory;
            s3.Configure(settings);
        }

        void Services(IServiceCollection services)
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }
    }

    private sealed class ObservedBackup(IWalBackup inner, ConcurrentQueue<string> opened) : IWalBackup
    {
        public ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken)
        {
            if (database?.DatabaseId is { } id)
            {
                opened.Enqueue(id);
            }

            return inner.OpenTapAsync(database, cancellationToken);
        }

        public ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken) =>
            inner.RollForwardAsync(file, segments, cancellationToken);

        public LogCut CutLog(Stream segment, DateTimeOffset at, long? previous) => inner.CutLog(segment, at, previous);

        public DateTimeOffset? ReadMark(Stream segment) => inner.ReadMark(segment);

        public ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken) =>
            inner.ImportDatabaseAsync(database, file, cancellationToken);

        public ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken) => inner.ChecksumAsync(file, cancellationToken);
    }
}
