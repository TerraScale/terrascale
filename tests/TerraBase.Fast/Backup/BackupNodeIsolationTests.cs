using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupNodeIsolationTests
{
    [Test]
    [Arguments("platform")]
    [Arguments("customer")]
    [Arguments("both")]
    public async Task Independent_nodes_restore_their_own_history_and_retention_preserves_the_other_nodes_baseline(string mode, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("backup-node-isolation-").FullName;
        var firstDirectory = Path.Combine(directory, "first");
        var secondDirectory = Path.Combine(directory, "second");
        var s3 = new FakeS3();
        var start = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var firstTime = new FakeTimeProvider(start);
        var secondTime = new FakeTimeProvider(start);
        var user = EntityIds.New().ToString();
        try
        {
            ManagedDatabaseRef database;
            await using (var seed = await StartAsync(firstDirectory, "seed", firstTime))
            {
                database = await seed.CreateManagedDatabaseAsync();
                await Assert.That(await seed.Server.Services.GetRequiredService<IPlatformCatalog>().EnsurePlatformAsync(cancellationToken) is Done).IsTrue();
                if (mode != "platform")
                {
                    using var destination = await seed.Client.PutAsJsonAsync(ManagementApi.Uri(database.ProjectPath + "/backup-destination"), new
                    {
                        endpoint = FakeS3.CustomerEndpoint,
                        region = "test-region-1",
                        bucket = FakeS3.CustomerBucket,
                        accessKeyId = FakeS3.CustomerAccessKey,
                        secretKey = FakeS3.CustomerSecretKey,
                        mode,
                    }, cancellationToken);
                    destination.EnsureSuccessStatusCode();
                }
            }

            foreach (var file in Directory.EnumerateFiles(firstDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(secondDirectory, Path.GetRelativePath(firstDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }

            var seedAnchor = s3.Objects["catalog-anchor/seed"];
            var anchorKeyId = Encoding.ASCII.GetString(seedAnchor, 4, 26);
            var anchorKey = s3.Unwrap(Encoding.ASCII.GetString(s3.Objects[BackupLayout.Key(BackupLayout.KeyScope(null), anchorKeyId)]));
            using var anchor = new MemoryStream();
            using (var sealedSeed = new MemoryStream(seedAnchor))
            {
                await BackupObject.ReadAsync(sealedSeed, anchor, "catalog-anchor/seed", _ => Task.FromResult(anchorKey), cancellationToken);
            }

            foreach (var node in new[] { "node-a", "node-b" })
            {
                anchor.Position = 0;
                using var sealedAnchor = new MemoryStream();
                var key = "catalog-anchor/" + node;
                await BackupObject.WriteAsync(anchor, sealedAnchor, key, anchorKeyId, anchorKey, cancellationToken);
                s3.SeedObject(false, key, sealedAnchor.ToArray());
            }

            await using var first = await StartAsync(firstDirectory, "node-a", firstTime);
            await using var second = await StartAsync(secondDirectory, "node-b", secondTime);
            await Assert.That(first.Server.DataDirectory).IsNotEqualTo(second.Server.DataDirectory);
            await Assert.That(first.Server.ClientAddress).IsNotEqualTo(second.Server.ClientAddress);
            await Assert.That(first.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId).IsEqualTo("node-a");
            await Assert.That(second.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId).IsEqualTo("node-b");
            using var firstData = await first.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            using var secondData = await second.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(firstData, database, "people", cancellationToken);
            await ManagementApi.CreateCollectionAsync(secondData, database, "people", cancellationToken);
            var firstText = "node-a " + new Faker().Lorem.Sentence();
            var secondText = "node-b " + new Faker().Lorem.Sentence();
            await PutAsync(firstData, database, firstText, cancellationToken);
            await PutAsync(secondData, database, secondText, cancellationToken);
            var firstBackup = first.Server.Services.GetRequiredService<IContinuousBackup>();
            var secondBackup = second.Server.Services.GetRequiredService<IContinuousBackup>();
            firstTime.Advance(TimeSpan.FromSeconds(10));
            await firstBackup.RunOnceAsync(cancellationToken);
            secondTime.Advance(TimeSpan.FromSeconds(20));
            await secondBackup.RunOnceAsync(cancellationToken);
            var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
            var firstFile = BackupLayout.File(tenant, "node-a");
            var secondFile = BackupLayout.File(tenant, "node-b");
            var objects = mode == "customer" ? s3.CustomerObjects : s3.Objects;
            var firstBaseline = objects.Keys.Single(key => key.StartsWith(BackupLayout.Snapshots(firstFile), StringComparison.Ordinal));
            var secondBaseline = objects.Keys.Single(key => key.StartsWith(BackupLayout.Snapshots(secondFile), StringComparison.Ordinal));
            foreach (var (backup, node) in new[] { (firstBackup, "node-a"), (secondBackup, "node-b") })
            {
                await Assert.That(backup.Status.Files.Select(file => file.File)).Contains(BackupLayout.File(null, node));
                await Assert.That(backup.Status.Files.Select(file => file.File)).Contains(BackupLayout.File(HistoryErasures.AuthDatabase, node));
                await Assert.That(backup.Status.Files.All(file => file.File.EndsWith("/nodes/" + node, StringComparison.Ordinal))).IsTrue();
                await Assert.That(backup.Status.Files.All(file => file.LastError is null && file.LastDrill?.Passed == true)).IsTrue();
            }

            firstTime.Advance(TimeSpan.FromSeconds(10));
            firstText += " updated";
            secondText += " updated";
            await PutAsync(firstData, database, firstText, cancellationToken);
            await PutAsync(secondData, database, secondText, cancellationToken);
            firstTime.Advance(TimeSpan.FromSeconds(1));
            secondTime.Advance(TimeSpan.FromSeconds(1));
            await firstBackup.RunOnceAsync(cancellationToken);
            await secondBackup.RunOnceAsync(cancellationToken);
            var at = firstTime.GetUtcNow();
            var destinationName = mode == "platform" ? "platform" : "customer";
            await RestoreAsync(first, firstData, database, at, destinationName, "first-restored", firstText, start.AddSeconds(10), cancellationToken);
            await RestoreAsync(second, secondData, database, at, destinationName, "second-restored", secondText, start.AddSeconds(20), cancellationToken);
            await Assert.That(firstBackup.LastShipped(tenant).HasValue).IsEqualTo(mode != "customer");
            await Assert.That(secondBackup.LastShipped(tenant).HasValue).IsEqualTo(mode != "customer");
            var firstGeneration = Ulid.Parse(firstBackup.Status.Files.Single(file => file.File == firstFile).Generation!);
            var secondGeneration = Ulid.Parse(secondBackup.Status.Files.Single(file => file.File == secondFile).Generation!);
            await Assert.That(firstBackup.LastShipped(tenant, secondGeneration)).IsNull();
            await Assert.That(secondBackup.LastShipped(tenant, firstGeneration)).IsNull();
            var otherObjects = objects.Where(item => item.Key.StartsWith(secondFile + "/", StringComparison.Ordinal)).ToDictionary();
            firstTime.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(2));
            await firstBackup.RunOnceAsync(cancellationToken);
            await Assert.That(objects.ContainsKey(firstBaseline)).IsTrue();
            await Assert.That(objects.Where(item => item.Key.StartsWith(secondFile + "/", StringComparison.Ordinal))).IsEquivalentTo(otherObjects);
            firstTime.Advance(TimeSpan.FromDays(1));
            await firstBackup.RunOnceAsync(cancellationToken);
            await Assert.That(objects.ContainsKey(firstBaseline)).IsFalse();
            await Assert.That(objects.ContainsKey(secondBaseline)).IsTrue();
            await Assert.That(objects.Where(item => item.Key.StartsWith(secondFile + "/", StringComparison.Ordinal))).IsEquivalentTo(otherObjects);
            await RestoreAsync(second, secondData, database, at, destinationName, "after-retention", secondText, start.AddSeconds(20), cancellationToken);
            await Assert.That(firstBackup.Status.Files.Single(file => file.File == firstFile).LastDrill!.Passed).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Task<ManagementApi> StartAsync(string dataDirectory, string node, TimeProvider time)
        {
            return ManagementApi.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["data-directory"] = dataDirectory;
                settings["node-name"] = node;
            }, services =>
            {
                s3.Route(services);
                ManualBackupHosting.Configure(services);
            }, time, cancellationToken, userId: user);
        }
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string text, CancellationToken cancellationToken)
    {
        using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "saved")), new { text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task RestoreAsync(ManagementApi api, HttpClient data, ManagedDatabaseRef database, DateTimeOffset at,
        string destination, string name, string expected, DateTimeOffset snapshotAt, CancellationToken cancellationToken)
    {
        using var window = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time?destination=" + destination), cancellationToken);
        window.EnsureSuccessStatusCode();
        var bounds = await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(bounds.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(snapshotAt);
        await Assert.That(bounds.GetProperty("latest").GetDateTimeOffset()).IsEqualTo(at);
        using var planned = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"),
            new { pointInTime = at, destination }, cancellationToken);
        planned.EnsureSuccessStatusCode();
        var plan = await planned.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(plan.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(snapshotAt);
        using var restored = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = at, name, destination }, cancellationToken);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await restored.Content.ReadAsStringAsync(cancellationToken));
        using var read = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database with { Name = name }, "people", "saved")), cancellationToken);
        read.EnsureSuccessStatusCode();
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(expected);
    }
}
