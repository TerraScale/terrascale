using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CryptoShreddingTests
{
    [Test]
    public async Task Deleting_a_database_shreds_its_keys_so_its_backups_cannot_be_restored_or_decrypted(CancellationToken cancellationToken)
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync("main");
        await api.SetPlanAsync(api.User, database.Organization, "pro", cancellationToken);
        var sibling = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "sibling");
        var tenant = new TenantContext(database.Organization, PlanTier.Pro, database.Project, database.Id);
        var scope = BackupLayout.KeyScope(tenant);
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
        var shippedAt = time.GetUtcNow();
        var sealedObjects = s3.Objects.Where(item => item.Key.StartsWith(BackupLayout.FileRoot(tenant) + "/", StringComparison.Ordinal)
            && item.Value.AsSpan().StartsWith("TBK1"u8)).ToDictionary(item => item.Key, item => item.Value.ToArray());
        var keys = api.Server.Services.GetRequiredService<DataKeys>();
        await Assert.That(sealedObjects).IsNotEmpty();
        await Assert.That(KeysOf(s3, scope)).IsNotEmpty();
        foreach (var (key, bytes) in sealedObjects)
        {
            await DecryptAsync(keys, scope, key, bytes, cancellationToken); // Readable while the database exists.
        }

        using var deleted = await api.ProjectClient(database.Project).DeleteAsync(ManagementApi.Uri(database.Path), cancellationToken);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await api.Server.Services.GetRequiredService<DatabaseBackupPurge>().RunOnceAsync(cancellationToken);

        await Assert.That(KeysOf(s3, scope)).IsEmpty();
        var record = JsonSerializer.Deserialize<JsonElement>(s3.Objects[BackupLayout.Shredded(scope)]);
        await Assert.That(record.GetProperty("databaseId").GetString()).IsEqualTo(database.Id);
        await Assert.That(record.GetProperty("shreddedAt").GetDateTimeOffset()).IsGreaterThanOrEqualTo(shippedAt);
        foreach (var (key, bytes) in sealedObjects)
        {
            await Assert.That(() => DecryptAsync(keys, scope, key, bytes, cancellationToken)).Throws<DatabaseShreddedException>().Because(key);
        }

        var target = tenant with { DatabaseId = EntityIds.New().ToString() };
        var restored = await api.Server.Services.GetRequiredService<IPointInTimeRestore>().RestoreAsync(tenant, shippedAt, target, cancellationToken);
        await Assert.That(restored is Conflict { Code: "database_shredded" }).IsTrue();
        var siblingScope = BackupLayout.KeyScope(tenant with { DatabaseId = sibling.Id });
        await Assert.That(s3.Objects.ContainsKey(BackupLayout.Shredded(siblingScope))).IsFalse();
        await Assert.That(KeysOf(s3, siblingScope)).IsNotEmpty();
    }

    [Test]
    public async Task Account_erasure_shreds_the_keys_of_every_database_of_its_project(CancellationToken cancellationToken)
    {
        var s3 = new FakeS3();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        });
        var first = await api.CreateManagedDatabaseAsync("first");
        var second = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "second");
        var keys = api.Server.Services.GetRequiredService<DataKeys>();
        string[] scopes = [.. new[] { first, second }.Select(database =>
            BackupLayout.KeyScope(new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id)))];
        foreach (var scope in scopes)
        {
            _ = await keys.CurrentAsync(scope, cancellationToken);
            await Assert.That(KeysOf(s3, scope)).IsNotEmpty();
        }

        // ts-auth's account deletion hands each owned project to TerraBase as a version 2 closure.
        var closure = EntityIds.New().ToString();
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            await api.Server.Services.GetRequiredService<DatabaseBackupPurge>().RunOnceAsync(token);
            using var response = await api.Server.Client.PutAsJsonAsync(ManagementApi.Uri($"/admin/projects/{first.Project}/closure"),
                new { version = 2, closureId = closure, authorityOrganizationId = first.Organization }, token);
            await Assert.That(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted).IsTrue();
            return response.StatusCode == HttpStatusCode.OK;
        }, timeout: TimeSpan.FromSeconds(60));
        await api.Server.Services.GetRequiredService<DatabaseBackupPurge>().RunOnceAsync(cancellationToken);

        foreach (var scope in scopes)
        {
            await Assert.That(KeysOf(s3, scope)).IsEmpty().Because(scope);
            await Assert.That(s3.Objects.ContainsKey(BackupLayout.Shredded(scope))).IsTrue().Because(scope);
            await Assert.That(() => keys.CurrentAsync(scope, cancellationToken)).Throws<DatabaseShreddedException>().Because(scope);
        }
    }

    private static string[] KeysOf(FakeS3 s3, string scope) =>
        [.. s3.Objects.Keys.Where(key => key.StartsWith(BackupLayout.Keys(scope), StringComparison.Ordinal) && key != BackupLayout.Shredded(scope))];

    private static async Task DecryptAsync(DataKeys keys, string scope, string key, byte[] bytes, CancellationToken cancellationToken)
    {
        using var sealedObject = new MemoryStream(bytes);
        await BackupObject.ReadAsync(sealedObject, Stream.Null, key, id => keys.GetAsync(scope, id, cancellationToken), cancellationToken);
    }
}
