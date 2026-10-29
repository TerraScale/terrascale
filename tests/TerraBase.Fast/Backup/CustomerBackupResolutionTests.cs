using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupResolutionTests
{
    private const string Unreadable = "continuous backup of this database cannot be read: an object fails authentication or its integrity check";

    [Test]
    [Arguments("unsupported-version")]
    public async Task Unresolvable_customer_destinations_return_fixed_503_on_window_and_restore_listeners(string failure)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await StartAsync(settings => s3.Configure(settings), services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var project = ProjectClient(api, database);
        var record = new ProjectBackupDestinationRecord(failure == "unsupported-version" ? 2 : 1, Ulid.NewUlid().ToString(),
            new(new Uri(FakeS3.CustomerEndpoint), "test-region-1", FakeS3.CustomerBucket, "customer", time.GetUtcNow()),
            new SealedCredentials("unwrappable-key", "unsealable-credentials"));
        // Seed persisted configuration that a previous deployment could write; exercise resolution through HTTP.
        await api.Server.Services.GetRequiredService<SystemCatalog>().WriteAsync(async transaction =>
        {
            await SystemCatalog.PutAsync(transaction, ProjectBackupDestinations.Collection(database.Project), "current", record,
                TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken);
            return true;
        }, static error => throw new InvalidOperationException(error.Message), cancellationToken);

        foreach (var (client, path) in new[] { (api.Client, database.Path), (project, database.Data) })
        {
            using var window = await client.GetAsync(Uri(path + "/point-in-time?destination=customer"), cancellationToken);
            using var restore = await client.PostAsJsonAsync(Uri(path + "/point-in-time/restore"),
                new { pointInTime = time.GetUtcNow(), name = "unavailable-restore", destination = "customer" }, cancellationToken);
            await UnavailableAsync(window, cancellationToken);
            await UnavailableAsync(restore, cancellationToken);
            using var absent = await client.GetAsync(Uri(path + "/point-in-time?destination=platform"), cancellationToken);
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        await Assert.That(s3.CustomerObjects.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("snapshot")]
    [Arguments("key")]
    public async Task Corrupt_customer_backup_material_remains_500_and_recovers_when_repaired(string material)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var project = ProjectClient(api, database);
        using (var saved = await project.PutAsJsonAsync(Uri($"/v1/projects/{database.Project}/backup-destination"), new
        {
            endpoint = FakeS3.CustomerEndpoint,
            region = "test-region-1",
            bucket = FakeS3.CustomerBucket,
            accessKeyId = FakeS3.CustomerAccessKey,
            secretKey = FakeS3.CustomerSecretKey,
            mode = "customer",
        }, cancellationToken))
        {
            saved.EnsureSuccessStatusCode();
        }

        await CreateCollectionAsync(project, database, "people");
        var text = new Faker().Lorem.Sentence(6);
        using (var written = await project.PutAsJsonAsync(Uri(DocumentPath(database, "people", "kept")), new { text }, cancellationToken))
        {
            written.EnsureSuccessStatusCode();
        }

        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
        using var window = await project.GetAsync(Uri(database.Data + "/point-in-time?destination=customer"), cancellationToken);
        window.EnsureSuccessStatusCode();
        var latest = (await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("latest").GetDateTimeOffset();
        var prefix = material == "snapshot"
            ? $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local/snapshots/"
            : $"v1/keys/{database.Organization}/{database.Project}/";
        var key = s3.CustomerObjects.Keys.Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        var original = s3.CustomerObjects[key];
        s3.SeedObject(customer: true, key, material == "snapshot" ? [1, 2, 3] : Encoding.ASCII.GetBytes("wrapped:AQID"));

        using var failed = await project.PostAsJsonAsync(Uri(database.Data + "/point-in-time/restore"),
            new { pointInTime = latest, name = "corrupt-restore", destination = "customer" }, cancellationToken);
        await ErrorAssert.ErrorAsync(failed, HttpStatusCode.Conflict, "backup_unreadable", Unreadable);
        using var legacyFailed = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = latest, name = "legacy-corrupt-restore", destination = "customer" }, cancellationToken);
        await ErrorAssert.ErrorAsync(legacyFailed, HttpStatusCode.Conflict, "backup_unreadable", Unreadable);

        s3.SeedObject(customer: true, key, original);
        using var restored = await project.PostAsJsonAsync(Uri(database.Data + "/point-in-time/restore"),
            new { pointInTime = latest, name = "repaired-restore", destination = "customer" }, cancellationToken);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var document = await project.GetAsync(Uri($"/v1/projects/{database.Project}/databases/repaired-restore/collections/people/documents/by-key?id={DocumentId("kept")}"), cancellationToken);
        document.EnsureSuccessStatusCode();
        var body = await document.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        await Assert.That(body.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
    }

    private static HttpClient ProjectClient(ManagementApi api, ManagedDatabaseRef database) =>
        api.WithToken(api.Issuer.Mint(Ulid.NewUlid().ToString(), token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
        }));

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    private static async Task UnavailableAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        using var body = JsonDocument.Parse(text);
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("message").GetString())
            .IsEqualTo("backup bucket could not be accessed");
        await Assert.That(text.Contains(FakeS3.CustomerEndpoint, StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains(FakeS3.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains(FakeS3.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
    }
}
