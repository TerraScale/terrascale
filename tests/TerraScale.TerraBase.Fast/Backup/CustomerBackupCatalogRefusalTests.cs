using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Fast.Management;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupCatalogRefusalTests
{
    private const string CatalogRefusal = "the management catalog has an old or unsupported format; discard the affected data";

    [Test]
    public async Task A_destination_read_refuses_a_catalog_changed_after_authorization_without_platform_fallback()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        var hook = new UsageCommitHook();
        await using var api = await StartAsync(oci, time, hook);
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
        var prefix = $"v1/{database.Organization}/{database.Project}/{database.Id}/";
        var status = await api.Server.Client.GetFromJsonAsync(Uri("/v1/backup"), TerraBaseJsonContext.Default.BackupStatus, token);
        await Assert.That(status!.Files.Single(file => file.File == prefix[..^1]).LastError).IsEqualTo(nameof(IOException));
        await Assert.That(oci.Objects.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))).IsFalse();
        await Assert.That(oci.CustomerObjects.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))).IsFalse();
        await Assert.That(await ReadJsonAsync(store, collection, "current", token)).IsEqualTo(record);

        await WriteVersionAsync(store, version!, token);
        using var recovered = await api.Client.GetAsync(route, token);
        recovered.EnsureSuccessStatusCode();
        await Assert.That((await recovered.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("mode").GetString()).IsEqualTo("customer");
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        await Assert.That(oci.CustomerObjects.Keys.Any(key => key.StartsWith(prefix + "snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(oci.Objects.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))).IsFalse();
        await Assert.That(oci.ScheduledSecrets).IsEmpty();
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task A_destination_delete_refuses_a_catalog_changed_before_its_durable_write_without_retiring_credentials()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        var hook = new UsageCommitHook();
        await using var api = await StartAsync(oci, time, hook);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        using var saved = await api.Client.PutAsJsonAsync(route, Destination("customer"), token);
        saved.EnsureSuccessStatusCode();
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var version = await ReadJsonAsync(store, "catalog", "version", token);
        var collection = ProjectBackupDestinations.Collection(database.Project);
        var destination = await ReadJsonAsync(store, collection, "current", token);
        var secret = oci.Secrets.Single();
        await Assert.That(await ReadLifecycleAsync(store, token)).IsEmpty();
        BackupSecretWork? retirement = null;
        var secretRequests = 0;
        var objectRequests = 0;
        oci.BeforeSecretRequest = async (method, path, cancellationToken) =>
        {
            Interlocked.Increment(ref secretRequests);
            if (method == HttpMethod.Get && path == "/20180608/secrets/" + secret.Key)
            {
                // Retirement must already be durable when its first OCI request starts.
                await Assert.That(await ReadJsonAsync(store, collection, "current", cancellationToken)).IsNull();
                retirement = (await ReadLifecycleAsync(store, cancellationToken)).Single();
            }
        };
        oci.ObjectRequested = (_, _, _) => Interlocked.Increment(ref objectRequests);
        using var gate = hook.PauseWrite();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var deleting = api.Client.DeleteAsync(route, caller.Token);
        try
        {
            // Authorization completed; the real durable transaction has not opened yet.
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await WriteVersionAsync(store, "\"99\"", token);
            gate.Dispose();
            await ErrorAssert.ErrorAsync(deleting.WaitAsync(TimeSpan.FromSeconds(10), token), HttpStatusCode.ServiceUnavailable, "unavailable", CatalogRefusal);
        }
        finally
        {
            gate.Dispose();
            await caller.CancelAsync();
            await ((Task)deleting).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (deleting.IsCompletedSuccessfully)
            {
                using var response = await deleting;
            }
        }

        await Assert.That(await ReadJsonAsync(store, collection, "current", token)).IsEqualTo(destination);
        await Assert.That(await ReadLifecycleAsync(store, token)).IsEmpty();
        await Assert.That(Volatile.Read(ref secretRequests)).IsEqualTo(0);
        await Assert.That(Volatile.Read(ref objectRequests)).IsEqualTo(0);
        await Assert.That(oci.ScheduledSecrets).IsEmpty();
        await Assert.That(oci.Secrets.Keys).IsEquivalentTo([secret.Key]);
        await Assert.That(oci.Secrets[secret.Key]).IsEqualTo(secret.Value);

        await WriteVersionAsync(store, version!, token);
        using var recovered = await api.Client.DeleteAsync(route, token);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var retired = retirement ?? throw new InvalidOperationException("deletion must persist retirement before contacting OCI");
        await Assert.That(retired.Version).IsEqualTo(1);
        await Assert.That(retired.SecretId).IsEqualTo(secret.Key);
        await Assert.That(retired.SecretName).IsNull();
        await Assert.That(retired.Retiring).IsTrue();
        await Assert.That(await ReadJsonAsync(store, collection, "current", token)).IsNull();
        await Assert.That(await ReadLifecycleAsync(store, token)).IsEmpty();
        await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([secret.Key]);
        await Assert.That(oci.SecretDeletionTimes[secret.Key]).IsGreaterThanOrEqualTo(time.GetUtcNow().AddDays(7));
        await Assert.That(oci.Secrets.Keys).IsEquivalentTo([secret.Key]);
        await Assert.That(oci.Secrets[secret.Key]).IsEqualTo(secret.Value);
        await Assert.That(Volatile.Read(ref secretRequests)).IsEqualTo(2);
        await Assert.That(Volatile.Read(ref objectRequests)).IsEqualTo(0);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(route, token), HttpStatusCode.NotFound, "not_found");
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Operator_recovery_preserves_the_intent_when_its_read_or_final_write_is_refused(bool refuseFinalWrite)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await StartAsync(oci, time, new UsageCommitHook());
        await api.CreateManagedDatabaseAsync();
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var version = await ReadJsonAsync(store, "catalog", "version", token);
        var id = Ulid.NewUlid().ToString();
        var work = new BackupSecretWork(1, id, "terrabase-backup-" + id, null, true, time.GetUtcNow().AddDays(1));
        await api.Server.Services.GetRequiredService<SystemCatalog>().WriteDurableAsync(async transaction =>
        {
            await transaction.PutAsync(BackupSecretLifecycle.Collection, id, JsonSerializer.SerializeToUtf8Bytes(work, TerraBaseJsonContext.Default.BackupSecretWork), token);
            return true;
        }, static error => throw new InvalidOperationException(error.Message), token);
        var before = await ReadJsonAsync(store, BackupSecretLifecycle.Collection, id, token);
        var requests = 0;
        oci.BeforeSecretRequest = async (method, path, cancellationToken) =>
        {
            Interlocked.Increment(ref requests);
            if (refuseFinalWrite && method == HttpMethod.Get && path == "/20180608/secrets")
            {
                await WriteVersionAsync(store, "\"99\"", cancellationToken);
            }
        };
        if (!refuseFinalWrite)
        {
            await WriteVersionAsync(store, "\"99\"", token);
        }

        using var service = api.Service(api.User);
        var route = Uri("/v1/backup-secret-lifecycle/" + id + "/confirm-absent");
        var confirmation = new { secretName = work.SecretName, confirmAbsent = true };
        await ErrorAssert.ErrorAsync(service.PostAsJsonAsync(route, confirmation, token), HttpStatusCode.ServiceUnavailable, "unavailable", CatalogRefusal);
        await ErrorAssert.ErrorAsync(service.GetAsync(Uri("/v1/backup-secret-lifecycle"), token), HttpStatusCode.ServiceUnavailable, "unavailable", CatalogRefusal);
        await Assert.That(Volatile.Read(ref requests)).IsEqualTo(refuseFinalWrite ? 1 : 0);
        await Assert.That(await ReadJsonAsync(store, BackupSecretLifecycle.Collection, id, token)).IsEqualTo(before);
        await Assert.That(oci.ScheduledSecrets).IsEmpty();

        oci.BeforeSecretRequest = null;
        await WriteVersionAsync(store, version!, token);
        var page = await service.GetFromJsonAsync<BackupSecretRecoveryPage>(Uri("/v1/backup-secret-lifecycle"), token);
        await Assert.That(page!.Items).IsEquivalentTo([new BackupSecretRecoveryIntent(id, work.SecretName!, true)]);
        await Assert.That(page.Next).IsNull();
        using var recovered = await service.PostAsJsonAsync(route, confirmation, token);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await ReadJsonAsync(store, BackupSecretLifecycle.Collection, id, token)).IsNull();
        await Assert.That(oci.Secrets).IsEmpty();
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Create_cleanup_retains_its_pending_record_and_previous_destination_when_the_catalog_is_refused(bool rejectCreate)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await StartAsync(oci, time, new UsageCommitHook());
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        using var saved = await api.Client.PutAsJsonAsync(route, Destination("customer"), token);
        saved.EnsureSuccessStatusCode();
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var version = await ReadJsonAsync(store, "catalog", "version", token);
        var collection = ProjectBackupDestinations.Collection(database.Project);
        var destination = await ReadJsonAsync(store, collection, "current", token);
        BackupSecretWork? intent = null;
        string? before = null;
        oci.BeforeSecretRequest = async (method, path, cancellationToken) =>
        {
            if (method == HttpMethod.Post && path == "/20180608/secrets")
            {
                oci.BeforeSecretRequest = null;
                await using (var transaction = await store.BeginTransactionAsync(write: false, cancellationToken))
                {
                    var page = await transaction.ListAsync(BackupSecretLifecycle.Collection, null, 2, 4096, cancellationToken);
                    var document = page.Documents.Single();
                    before = Encoding.UTF8.GetString(document.Json.Span);
                    intent = EntityPayload.Read(document, TerraBaseJsonContext.Default.BackupSecretWork);
                }

                await WriteVersionAsync(store, "\"99\"", cancellationToken);
            }
        };
        oci.SecretResponseOverride = (method, path) => rejectCreate && method == HttpMethod.Post && path == "/20180608/secrets"
            ? new(HttpStatusCode.BadRequest) : null;

        await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(route, Destination("both"), token), HttpStatusCode.ServiceUnavailable, "unavailable",
            rejectCreate ? "customer backup bucket or OCI Vault could not be accessed" : CatalogRefusal);
        var pending = intent ?? throw new InvalidOperationException("the create must persist its intent before contacting OCI");
        await Assert.That(pending.Retiring).IsFalse();
        await Assert.That(pending.SecretId).IsNull();
        await Assert.That(await ReadJsonAsync(store, BackupSecretLifecycle.Collection, pending.Id, token)).IsEqualTo(before);
        await Assert.That(await ReadJsonAsync(store, collection, "current", token)).IsEqualTo(destination);
        await Assert.That(oci.Secrets.Count).IsEqualTo(rejectCreate ? 1 : 2);
        await Assert.That(oci.ScheduledSecrets).IsEmpty();
        await WriteVersionAsync(store, version!, token);
        using var recovered = await api.Client.GetAsync(route, token);
        recovered.EnsureSuccessStatusCode();
        await Assert.That((await recovered.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("mode").GetString()).IsEqualTo("customer");
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    private static async Task<ManagementApi> StartAsync(FakeOci oci, FakeTimeProvider time, UsageCommitHook hook)
    {
        var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services =>
        {
            oci.Route(services);
            ManualBackupHosting.Configure(services);
            hook.Install(services);
        }, time, managementCredential: true);
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

    private static async Task<IReadOnlyList<BackupSecretWork>> ReadLifecycleAsync(IDatabaseStore store, CancellationToken token)
    {
        await using var transaction = await store.BeginTransactionAsync(write: false, token);
        var page = await transaction.ListAsync(BackupSecretLifecycle.Collection, null, 2, 2 * BackupSecretLifecycle.MaxRecordBytes, token);
        await Assert.That(page.HasMore).IsFalse();
        return [.. page.Documents.Select(document => EntityPayload.Read(document, TerraBaseJsonContext.Default.BackupSecretWork)!)];
    }

    private static object Destination(string mode) => new
    {
        endpoint = FakeOci.CustomerEndpoint,
        region = "test-region-1",
        bucket = FakeOci.CustomerBucket,
        accessKeyId = FakeOci.CustomerAccessKey,
        secretKey = FakeOci.CustomerSecretKey,
        mode,
    };

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
}
