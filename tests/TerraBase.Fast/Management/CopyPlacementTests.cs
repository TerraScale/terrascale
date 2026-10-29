using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class CopyPlacementTests
{

    [Test]
    [Arguments("registered")]
    [Arguments("pending")]
    [Arguments("snapshotting")]
    [Arguments("expiring")]
    public async Task A_region_does_not_sweep_another_regions_snapshot(string state)
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru");
        var source = await api.CreateManagedDatabaseAsync();
        using var backup = await api.Client.PostAsync(ManagementApi.Uri(source.Path + "/backups"), null);
        backup.EnsureSuccessStatusCode();
        var id = (await backup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            var record = (await SystemCatalog.GetAsync(transaction, BackupCatalog.Records(source.Organization), id, TerraBaseJsonContext.Default.BackupRecord, CancellationToken.None))!;
            await SystemCatalog.PutAsync(transaction, BackupCatalog.Records(source.Organization), id, record with { State = state, Region = "fra" },
                TerraBaseJsonContext.Default.BackupRecord, CancellationToken.None);
            await StorageSweep.AddAsync(transaction, new SweepEntry(StorageSweep.Backup, source.Organization, id), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await api.Server.Services.GetRequiredService<IManagedBackupService>().RunScheduleAsync(CancellationToken.None);
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var retained = await SystemCatalog.GetAsync(read, BackupCatalog.Records(source.Organization), id, TerraBaseJsonContext.Default.BackupRecord, CancellationToken.None);
        await Assert.That(retained?.State).IsEqualTo(state);
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, source.Organization, source.Project, id + ".db"))).IsTrue();
    }

    [Test]
    public async Task Snapshot_restore_replays_to_its_owning_region_before_opening_a_local_file(CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-snapshot-routing-").FullName;
        try
        {
            string user, organization, project, sourceId, restore;
            var text = new Faker().Lorem.Sentence();
            await using (var gru = await StartAsync("gru"))
            {
                user = gru.User;
                organization = await gru.CreateOrganizationAsync("Snapshot routing");
                project = await gru.CreateProjectAsync(organization, "Copies");
                var source = await gru.CreateManagedDatabaseAsync(organization, project, "source");
                sourceId = source.Id;
                using var data = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(data, source, "items", cancellationToken);
                using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(source, "items", "seed")), new { text }, cancellationToken);
                written.EnsureSuccessStatusCode();
                using var backup = await gru.Client.PostAsync(ManagementApi.Uri(source.Path + "/backups"), null, cancellationToken);
                backup.EnsureSuccessStatusCode();
                var id = (await backup.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString()!;
                restore = $"{source.Path}/backups/{id}/restore";

                await gru.Server.Services.GetRequiredService<IStorageEngine>().DeleteDatabaseAsync(new TenantContext(organization, PlanTier.Hobby, project, id), cancellationToken);
            }

            await using var fra = await StartAsync("fra");
            using var owner = fra.As(user);
            using var response = await owner.PostAsJsonAsync(ManagementApi.Uri(restore), new { name = "copy" }, cancellationToken);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest).Because(await response.Content.ReadAsStringAsync(cancellationToken));
            await Assert.That(response.Headers.GetValues("ts-replay")).IsEquivalentTo(["region=gru"]);
            using var pointInTime = await owner.PostAsJsonAsync(ManagementApi.Uri(restore[..restore.IndexOf("/backups/", StringComparison.Ordinal)] + "/point-in-time/restore"),
                new { pointInTime = DateTimeOffset.UtcNow, name = "pitr-copy" }, cancellationToken);
            await Assert.That(pointInTime.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            await Assert.That(pointInTime.Headers.GetValues("ts-replay")).IsEquivalentTo(["region=gru"]);
            using var replay = new HttpRequestMessage(HttpMethod.Post, ManagementApi.Uri(restore)) { Content = JsonContent.Create(new { name = "copy" }) };
            replay.Headers.Add("ts-replay-src", "region=gru");
            using var repeated = await owner.SendAsync(replay, cancellationToken);
            await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            await Assert.That(repeated.Headers.Contains("ts-replay")).IsFalse();
            using var wrongDatabase = await owner.PostAsJsonAsync(ManagementApi.Uri(restore.Replace(sourceId, TerraBase.Primitives.EntityIds.New().ToString(), StringComparison.Ordinal)), new { name = "copy" }, cancellationToken);
            await Assert.That(wrongDatabase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

            Task<ManagementApi> StartAsync(string region)
            {
                return ManagementApi.StartAsync(settings =>
                {
                    settings["region"] = region;
                    settings["data-directory"] = directory;
                });
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
