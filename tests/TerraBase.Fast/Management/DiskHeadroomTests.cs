using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class DiskHeadroomTests
{
    [Test]
    public async Task Retained_import_recovers_its_workspace_under_capacity_pressure_and_releases_it_after_deletion()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-import-disk-").FullName;
        var servers = new List<ManagementApi>();
        var failedStarts = new List<Task>();
        IDisposable? held = null;
        try
        {
            await TerraBaseTestServer.InitializeDataDirectoryAsync(directory);
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var first = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory,
                time: time, failedStartCleanup: failedStarts.Add);
            servers.Add(first);
            var database = await first.CreateManagedDatabaseAsync();
            using var upload = first.WithToken(ImportClient.Token(first, database.Project, ImportClient.AllScopes));
            var import = ManagementApi.Uri(database.Data + "/import");
            byte[] source = "{\"n\":1}\n{\"n\":2}\n"u8.ToArray();
            using var created = await upload.PostAsJsonAsync(import, new
            {
                format = "ndjson",
                sourceBytes = source.Length,
                sourceSha256 = ImportClient.Sha256(source),
                options = new { collection = "notes" },
            });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var content = new ByteArrayContent(source[..8]);
            content.Headers.ContentType = new("application/octet-stream");
            using var partial = await upload.PutAsync(ManagementApi.Uri(import + "/source?offset=0"), content);
            partial.EnsureSuccessStatusCode();
            var workspace = DiskBudget.WorkspaceBytes(source.Length);
            await Assert.That(Reserved(first)).IsEqualTo(workspace);
            await first.DisposeAsync();
            await first.CleanupCompletion;

            long outstanding = 0;
            await using var restarted = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, services =>
                services.AddSingleton<IDiskBudget>(provider =>
                {
                    var budget = new DiskBudget(directory, provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName));
                    outstanding = new DriveInfo(directory).AvailableFreeSpace - DiskBudget.Headroom - (8L << 20);
                    held = budget.Reserve(outstanding);
                    return budget;
                }), time, userId: first.User, failedStartCleanup: failedStarts.Add);
            servers.Add(restarted);
            using var client = restarted.WithToken(ImportClient.Token(restarted, database.Project, ImportClient.AllScopes));
            var job = await client.GetFromJsonAsync<JsonElement>(import);
            await Assert.That(job.GetProperty("source").GetProperty("receivedBytes").GetInt64()).IsEqualTo(8);
            await Assert.That(Reserved(restarted)).IsEqualTo(outstanding + workspace);
            using var cancelled = await client.PostAsync(ManagementApi.Uri(import + "/cancel"), null);
            await Assert.That(cancelled.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That((await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString()).IsEqualTo("cancelled");
            using var next = new ByteArrayContent(source[8..9]);
            next.Headers.ContentType = new("application/octet-stream");
            using var resumed = await client.PutAsync(ManagementApi.Uri(import + "/source?offset=8"), next);
            resumed.EnsureSuccessStatusCode();
            await Assert.That(Reserved(restarted)).IsEqualTo(outstanding + workspace);
            var staging = Path.Combine(directory, ".import", database.Id);
            await Assert.That(new FileInfo(Path.Combine(staging, "source")).Length).IsEqualTo(9);
            using var deleted = await restarted.Client.DeleteAsync(ManagementApi.Uri(database.Path));
            deleted.EnsureSuccessStatusCode();
            time.Advance(TimeSpan.FromHours(1));
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(!Directory.Exists(staging) && Reserved(restarted) == outstanding), TimeSpan.FromSeconds(30));
            held!.Dispose();
            await Assert.That(Reserved(restarted)).IsEqualTo(0);
        }
        finally
        {
            await Task.WhenAll(servers.Select(server => server.CleanupCompletion).Concat(failedStarts));
            held?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static long Reserved(ManagementApi api)
    {
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long reserved = -1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "terrabase.disk.reserved")
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => reserved = value);
        listener.Start();
        listener.RecordObservableInstruments();
        return reserved;
    }

    [Test]
    public async Task Outstanding_disk_claim_refuses_backup_without_blocking_writes_and_release_allows_restore()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, new FakeTimeProvider());
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people", token);
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        var measurements = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name.StartsWith("terrabase.disk.", StringComparison.Ordinal))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => measurements[instrument.Name] = value);
        listener.Start();
        var bytes = new DriveInfo(api.Server.DataDirectory).AvailableFreeSpace - DiskBudget.Headroom - (8L << 20);
        using var claim = api.Server.Services.GetRequiredService<IDiskBudget>().Reserve(bytes);
        listener.RecordObservableInstruments();
        await Assert.That(measurements["terrabase.disk.reserved"]).IsEqualTo(bytes);
        await Assert.That(measurements["terrabase.disk.free"]).IsGreaterThan(0);

        using var refused = await api.Client.PostAsync(ManagementApi.Uri(database.Path + "/backups"), null, token);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("error");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("disk_capacity");
        await Assert.That(error.GetProperty("message").GetString()!).Contains("Retry after active jobs finish or free data-volume capacity");
        var directory = Path.Combine(api.Server.DataDirectory, database.Organization, database.Project);
        await Assert.That(Directory.GetFiles(directory, "*.db").Select(file => Path.GetFileNameWithoutExtension(file)!)).IsEquivalentTo([database.Id]);
        await Assert.That(Directory.GetFiles(directory, "*.creating", SearchOption.AllDirectories).Length).IsEqualTo(0);
        await Assert.That(measurements["terrabase.disk.refused"]).IsGreaterThan(0);

        var path = ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "saved"));
        var name = new Faker().Name.FullName();
        using var put = await data.PutAsJsonAsync(path, new { name }, token);
        put.EnsureSuccessStatusCode();
        using var update = await data.PutAsJsonAsync(path, new { name, updated = true }, token);
        update.EnsureSuccessStatusCode();
        using var read = await data.GetAsync(path, token);
        read.EnsureSuccessStatusCode();
        await Assert.That(await read.Content.ReadAsStringAsync(token)).Contains(name);
        using var deleted = await data.DeleteAsync(path, token);
        deleted.EnsureSuccessStatusCode();
        using var saved = await data.PutAsJsonAsync(path, new { name }, token);
        saved.EnsureSuccessStatusCode();
        var organization = await api.CreateOrganizationAsync(new Faker().Company.CompanyName());
        using var metadata = await api.Client.GetAsync(ManagementApi.Uri("/v1/management/organizations/" + organization), token);
        metadata.EnsureSuccessStatusCode();
        listener.RecordObservableInstruments();
        await Assert.That(measurements["terrabase.disk.reserved"]).IsEqualTo(bytes);

        claim.Dispose();
        listener.RecordObservableInstruments();
        await Assert.That(measurements["terrabase.disk.reserved"]).IsEqualTo(0);
        using var created = await api.Client.PostAsync(ManagementApi.Uri(database.Path + "/backups"), null, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var backupId = (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString();
        using var restored = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/backups/{backupId}/restore"), new { name = "recovered" }, token);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var recovered = await data.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/recovered/collections/people/documents/by-key?id={ManagementApi.DocumentId("saved")}"), token);
        recovered.EnsureSuccessStatusCode();
        await Assert.That(await recovered.Content.ReadAsStringAsync(token)).Contains(name);
        listener.RecordObservableInstruments();
        await Assert.That(measurements["terrabase.disk.reserved"]).IsEqualTo(0);
    }
}
