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
    private static readonly string[] Replicas = ["fra"];

    [Test]
    public async Task A_snapshot_without_a_known_region_refuses_restore()
    {
        await using var api = await ManagementApi.StartAsync();
        var source = await api.CreateManagedDatabaseAsync();
        using var backup = await api.Client.PostAsync(ManagementApi.Uri(source.Path + "/backups"), null);
        backup.EnsureSuccessStatusCode();
        var id = (await backup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            var record = (await SystemCatalog.GetAsync(transaction, BackupCatalog.Records(source.Organization), id, TerraBaseJsonContext.Default.BackupRecord, CancellationToken.None))!;
            await SystemCatalog.PutAsync(transaction, BackupCatalog.Records(source.Organization), id, record with { Region = null }, TerraBaseJsonContext.Default.BackupRecord, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        using var response = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{source.Path}/backups/{id}/restore"), new { name = "copy" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("backup_region_unknown");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task A_snapshot_in_a_replica_region_keeps_the_primary_and_enforces_the_database_allowance(bool atCapacity, bool removed)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-replica-restore-").FullName;
        try
        {
            string user, organization, project, route;
            await using (var gru = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = "gru";
                settings["data-directory"] = directory;
            }, new OtherRegions("fra", "sjc").Register))
            {
                user = gru.User;
                organization = await gru.CreateOrganizationAsync("Replica restore");
                project = await gru.CreateProjectAsync(organization, "Copies");
                var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
                using var created = await gru.Client.PostAsJsonAsync(ManagementApi.Uri(databases), new { name = "source", replicaRegions = Replicas });
                created.EnsureSuccessStatusCode();
                route = $"{databases}/{(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}";
            }

            await using var fra = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = "fra";
                settings["data-directory"] = directory;
            }, services =>
            {
                new OtherRegions("gru", "sjc").Register(services);
                if (atCapacity)
                {
                    services.AddSingleton(new DatabaseAllowance(_ => 1));
                }
            });
            using var owner = fra.As(user);
            using var backup = await owner.PostAsync(ManagementApi.Uri(route + "/backups"), null);
            backup.EnsureSuccessStatusCode();
            var id = (await backup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            if (removed)
            {
                // The primary's placement change has replicated after this region took the snapshot.
                var source = (await owner.GetFromJsonAsync(ManagementApi.Uri(route), TerraBaseJsonContext.Default.ManagedDatabase))!;
                var store = await fra.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
                await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
                await SystemCatalog.PutAsync(transaction, $"projects/{project}/databases", source.Id, source with { ReplicaRegions = ["sjc"], PlacementVersion = 2 },
                    TerraBaseJsonContext.Default.ManagedDatabase, CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
            }

            using var restored = await owner.PostAsJsonAsync(ManagementApi.Uri($"{route}/backups/{id}/restore"), new { name = "copy" });
            await Assert.That(restored.StatusCode).IsEqualTo(atCapacity || removed ? HttpStatusCode.Conflict : HttpStatusCode.Created).Because(await restored.Content.ReadAsStringAsync());
            var body = await restored.Content.ReadFromJsonAsync<JsonElement>();
            if (atCapacity || removed)
            {
                await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo(removed ? "backup_region_not_placed" : "database_limit");
            }
            else
            {
                await Assert.That(body.GetProperty("homeRegion").GetString()).IsEqualTo("gru");
                await Assert.That(body.GetProperty("replicaRegions").EnumerateArray().Select(region => region.GetString()!)).IsEquivalentTo(Replicas);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Copies_preserve_the_source_placement(bool snapshot, bool allRegions)
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru", new OtherRegions("fra", "sjc").Register);
        var organization = await api.CreateOrganizationAsync("Copy placement");
        var project = await api.CreateProjectAsync(organization, "Copies");
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        using var created = await api.Client.PostAsJsonAsync(ManagementApi.Uri(databases), new { name = "source", replicaRegions = allRegions ? null : Replicas, allRegions });
        created.EnsureSuccessStatusCode();
        var source = await created.Content.ReadFromJsonAsync<JsonElement>();
        var route = $"{databases}/{source.GetProperty("id").GetString()}";
        if (snapshot)
        {
            using var backup = await api.Client.PostAsync(ManagementApi.Uri(route + "/backups"), null);
            backup.EnsureSuccessStatusCode();
            var id = (await backup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            route += $"/backups/{id}/restore";
        }
        else
        {
            route += "/branches";
        }

        using var copied = await api.Client.PostAsJsonAsync(ManagementApi.Uri(route), new { name = "copy" });
        await Assert.That(copied.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await copied.Content.ReadAsStringAsync());
        var copy = await copied.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(copy.GetProperty("homeRegion").GetString()).IsEqualTo(source.GetProperty("homeRegion").GetString());
        await Assert.That(Regions(copy)).IsEquivalentTo(Regions(source));
        await Assert.That(copy.GetProperty("allRegions").GetBoolean()).IsEqualTo(allRegions);

        static string?[] Regions(JsonElement database)
        {
            return database.GetProperty("replicaRegions") is { ValueKind: JsonValueKind.Array } regions
                ? [.. regions.EnumerateArray().Select(value => value.GetString())] : [];
        }
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
                using var placed = await gru.Client.PatchAsJsonAsync(ManagementApi.Uri(source.Path), new { replicaRegions = Replicas }, cancellationToken);
                placed.EnsureSuccessStatusCode();
                sourceId = source.Id;
                using var data = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(data, source, "items", cancellationToken);
                using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(source, "items", "seed")), new { text }, cancellationToken);
                written.EnsureSuccessStatusCode();
                using var backup = await gru.Client.PostAsync(ManagementApi.Uri(source.Path + "/backups"), null, cancellationToken);
                backup.EnsureSuccessStatusCode();
                var id = (await backup.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString()!;
                restore = $"{source.Path}/backups/{id}/restore";
                // Retain the catalog, but remove this fixture's snapshot to model a region without its file.
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
                }, new OtherRegions("gru", "fra", "sjc").Register);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
