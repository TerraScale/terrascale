using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Slow.Management;
using TerraBase.Features.Backup;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Slow.Backup;

public sealed class CustomerBackupCatalogRefusalTests
{
    private const string CatalogRefusal = "the management catalog has an old or unsupported format; discard the affected data";

    [Test]
    public async Task A_destination_read_refuses_a_catalog_changed_after_authorization_without_platform_fallback()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        var hook = new UsageCommitHook();
        await using var api = await StartAsync(s3, time, hook);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        using var saved = await api.Client.PutAsJsonAsync(route, Destination("customer"), token);
        saved.EnsureSuccessStatusCode();
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var version = await ReadJsonAsync(store, "catalog", "version", token);
        var collection = ProjectBackupDestinations.Collection(database.Project);
        var record = await ReadJsonAsync(store, collection, "current", token);
        hook.AfterGet = async (readCollection, id, cancellationToken) =>
        {
            if (readCollection == $"organizations/{database.Organization}/projects" && id == database.Project)
            {
                // Authorization keeps its valid snapshot; the destination lookup must check its own snapshot.
                hook.AfterGet = null;
                await WriteVersionAsync(store, "\"99\"", cancellationToken);
            }
        };

        await ErrorAssert.ErrorAsync(api.Client.GetAsync(route, token), HttpStatusCode.ServiceUnavailable, "unavailable", CatalogRefusal);
        await Assert.That(hook.AfterGet is null).IsTrue();
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        await backup.RunOnceAsync(token);
        var prefix = $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local/";
        var status = await api.Server.Client.GetFromJsonAsync(Uri("/v1/backup"), TerraBaseJsonContext.Default.BackupStatus, token);
        // Unsupported ownership prevents discovery from opening a tenant tap at all.
        await Assert.That(status!.Files.Any(file => file.File == prefix[..^1])).IsFalse();
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))).IsFalse();
        await Assert.That(s3.CustomerObjects.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))).IsFalse();
        await Assert.That(await ReadJsonAsync(store, collection, "current", token)).IsEqualTo(record);

        await WriteVersionAsync(store, version!, token);
        using var recovered = await api.Client.GetAsync(route, token);
        recovered.EnsureSuccessStatusCode();
        await Assert.That((await recovered.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("mode").GetString()).IsEqualTo("customer");
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        await Assert.That(s3.CustomerObjects.Keys.Any(key => key.StartsWith(prefix + "snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))).IsFalse();
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    private static async Task<ManagementApi> StartAsync(FakeS3 s3, FakeTimeProvider time, UsageCommitHook hook)
    {
        var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            hook.Install(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        return api;
    }

    private static async Task<string?> ReadJsonAsync(IDatabaseStore store, string collection, string id, CancellationToken token)
    {
        await using var transaction = await store.BeginTransactionAsync(write: false, token);
        var document = await transaction.GetAsync(collection, id, token);
        return document is null ? null : Encoding.UTF8.GetString(document.Json.Span);
    }

    private static async Task WriteVersionAsync(IDatabaseStore store, string version, CancellationToken token)
    {
        await using var transaction = await store.BeginTransactionAsync(write: true, token);
        await transaction.PutAsync("catalog", "version", Encoding.UTF8.GetBytes(version), token);
        await transaction.CommitAsync(token);
    }

    private static object Destination(string mode) => new
    {
        endpoint = FakeS3.CustomerEndpoint,
        region = "test-region-1",
        bucket = FakeS3.CustomerBucket,
        accessKeyId = FakeS3.CustomerAccessKey,
        secretKey = FakeS3.CustomerSecretKey,
        mode,
    };

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
}
