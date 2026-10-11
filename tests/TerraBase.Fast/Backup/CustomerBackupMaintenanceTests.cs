using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupMaintenanceTests
{
    [Test]
    [Arguments(true, false)]
    [Arguments(true, true)]
    [Arguments(false, false)]
    public async Task Failed_destination_maintenance_preserves_measurements_and_refreshes_acknowledged_status(bool platform, bool deletion)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(oci.ConfigureCustomer, services =>
        {
            oci.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, token);
        await CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence();
        await PutAsync(data, database, "before", text, token);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var initial = await RecordAsync(api, database, token);
        await Assert.That(initial.SizeBytes).IsNotNull();
        var file = File(database);
        var oldest = BackupLayout.Snapshot(file, time.GetUtcNow().AddDays(-4), Ulid.NewUlid(), 0);
        var floor = BackupLayout.Snapshot(file, time.GetUtcNow().AddDays(-3), Ulid.NewUlid(), 0);
        foreach (var customer in new[] { false, true })
        {
            oci.SeedObject(customer, oldest, [1]);
            oci.SeedObject(customer, floor, [2]);
        }

        oci.ObjectRequested = (customer, method, key) =>
        {
            if (customer == !platform && (deletion ? method == HttpMethod.Delete && key == oldest : method == HttpMethod.Get && key.Length == 0))
            {
                throw new HttpRequestException("maintenance destination unavailable");
            }
        };
        time.Advance(TimeSpan.FromHours(1));
        await PutAsync(data, database, "after", text, token);
        await backup.RunOnceAsync(token);
        var updated = await RecordAsync(api, database, token);
        await Assert.That(updated.UpdatedAt).IsEqualTo(time.GetUtcNow());
        await Assert.That(updated.LastShippedAt!.Value).IsGreaterThan(initial.LastShippedAt!.Value);
        await Assert.That(updated.SizeBytes).IsEqualTo(initial.SizeBytes);
        await Assert.That(updated.SizedAt).IsEqualTo(initial.SizedAt);
        await Assert.That((platform ? oci.Objects : oci.CustomerObjects).ContainsKey(oldest)).IsTrue();
        await Assert.That((platform ? oci.CustomerObjects : oci.Objects).ContainsKey(oldest)).IsFalse();
        time.Advance(TimeSpan.FromDays(1));
        await backup.RunOnceAsync(token);
        var partialInventory = await RecordAsync(api, database, token);
        await Assert.That(partialInventory.SizeBytes).IsEqualTo(initial.SizeBytes);
        await Assert.That(partialInventory.SizedAt).IsEqualTo(initial.SizedAt);
        oci.ObjectRequested = null;
        time.Advance(TimeSpan.FromHours(1));
        await backup.RunOnceAsync(token);
        await Assert.That(oci.Objects.ContainsKey(oldest)).IsFalse();
        await Assert.That(oci.CustomerObjects.ContainsKey(oldest)).IsFalse();
        await RestoreAsync(api, data, database, partialInventory.LastShippedAt!.Value, text, token);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Customer_body_transport_errors_are_sanitized_in_persisted_drill_diagnostics_and_restore_recovers()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(oci.ConfigureCustomer, services =>
        {
            oci.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, token);
        await CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence();
        await PutAsync(data, database, "before", text, token);
        await PutAsync(data, database, "after", text, token);
        var marker = Ulid.NewUlid().ToString();
        oci.CustomerContentOverride = key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal)
            ? new StreamContent(new BrokenBody(marker + FakeOci.CustomerAccessKey + FakeOci.CustomerSecretKey)) : null;
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var record = await RecordAsync(api, database, token);
        await Assert.That(record.LastDrill!.Passed).IsFalse();
        await Assert.That(record.LastDrill.Error).IsEqualTo("customer backup object body could not be read");
        var diagnostic = JsonSerializer.Serialize(record, TerraBaseJsonContext.Default.BackupStatusRecord);
        foreach (var secret in new[] { marker, FakeOci.CustomerAccessKey, FakeOci.CustomerSecretKey })
        {
            await Assert.That(diagnostic.Contains(secret, StringComparison.Ordinal)).IsFalse();
        }

        oci.CustomerContentOverride = null;
        await RestoreAsync(api, data, database, record.LastShippedAt!.Value, text, token);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}";

    private static async Task SetAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken token)
    {
        using var saved = await api.Client.PutAsJsonAsync(Uri(database.ProjectPath + "/backup-destination"), new
        {
            endpoint = FakeOci.CustomerEndpoint,
            region = "test-region-1",
            bucket = FakeOci.CustomerBucket,
            accessKeyId = FakeOci.CustomerAccessKey,
            secretKey = FakeOci.CustomerSecretKey,
            mode = "customer",
        }, token);
        saved.EnsureSuccessStatusCode();
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string id, string text, CancellationToken token)
    {
        using var written = await data.PutAsJsonAsync(Uri(DocumentPath(database, "people", id)), new { text }, token);
        written.EnsureSuccessStatusCode();
    }

    private static async Task<BackupStatusRecord> RecordAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken token) =>
        (await api.Server.Services.GetRequiredService<SystemCatalog>().ReadAsync(transaction => SystemCatalog.GetAsync(transaction,
            "databases/" + database.Id + "/backup_status", "current", TerraBaseJsonContext.Default.BackupStatusRecord, token),
            static error => throw new InvalidOperationException(error.Message), token))!;

    private static async Task RestoreAsync(ManagementApi api, HttpClient data, ManagedDatabaseRef database, DateTimeOffset at, string text, CancellationToken token)
    {
        using var restored = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), new
        {
            pointInTime = at,
            name = "maintenance-recovered",
            destination = "customer",
        }, token);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        foreach (var id in new[] { "before", "after" })
        {
            using var read = await data.GetAsync(Uri($"/v1/projects/{database.Project}/databases/maintenance-recovered/collections/people/documents/by-key?id={DocumentId(id)}"), token);
            read.EnsureSuccessStatusCode();
            var document = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            await Assert.That(document.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
        }
    }

    private sealed class BrokenBody(string message) : MemoryStream
    {
        public override bool CanSeek => false;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(new IOException(message));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException(message));
    }
}
