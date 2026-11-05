using System.Diagnostics.Metrics;
using Grpc.Net.Client;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraBase.Features.Shapes;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Features.HomeApply;
using TerraBase.Features.HomePush;
using TerraBase.Features.LocalAck;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class CompleteShreddingTests
{
    [Test]
    public async Task Persisted_partner_payload_and_cached_region_key_cannot_open_after_deletion(CancellationToken token)
    {
        var s3 = new FakeS3();
        await using var remote = await ManagementApi.StartAsync(settings =>
        {
            s3.Configure(settings);
            settings["node-name"] = "remote";
        }, services => s3.Route(services));
        var peerFile = Path.Combine(remote.Server.DataDirectory, "shred-peers");
        await File.WriteAllLinesAsync(peerFile, ["origin gru 127.0.0.2", "remote sjc 127.0.0.1"], token);
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            s3.Configure(settings);
            settings["node-name"] = "origin";
        }, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            services.AddSingleton(provider => new KeyRevocationPeers(
                provider.GetRequiredService<TerraBaseOptions>() with
                {
                    MachineId = "origin",
                    PeersFile = peerFile,
                    InternalPort = remote.Server.BaseAddress.Port,
                }, remote.Server.Credential, provider.GetRequiredService<KeyRevocationLocal>()));
        });
        var database = await api.CreateManagedDatabaseAsync();
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var crypto = api.Server.Services.GetRequiredService<ITransactionCrypto>();
        var sentinel = "private-row-" + EntityIds.New();
        var transaction = new RegionTransaction(EntityIds.New().ToString(), "sjc", null,
            [new RegionChange("insert", "items", null, new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement(sentinel) })]);
        var packed = HomePush.Encode(transaction);
        var payload = await crypto.SealAsync(tenant, packed, token);
        using var backup = await api.Client.PostAsync(ManagementApi.Uri(database.Path + "/backups"), null, token);
        await Assert.That(backup.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var backupBody = JsonDocument.Parse(await backup.Content.ReadAsByteArrayAsync(token));
        var snapshot = tenant with { DatabaseId = backupBody.RootElement.GetProperty("id").GetString()! };
        var snapshotPayload = await crypto.SealAsync(snapshot, packed, token);
        using var holdBody = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
            new HeldTransaction(1, "sjc-1", "gru", tenant.OrganizationId, tenant.ProjectId, tenant.DatabaseId, 1, payload), TerraBaseJsonContext.Default.HeldTransaction));
        holdBody.Headers.ContentType = new("application/json");
        using var held = await api.Server.Client.PostAsync(ManagementApi.Uri("/v1/sync/holds"), holdBody, token);
        await Assert.That(held.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using (var read = await store.BeginTransactionAsync(write: false, token))
        {
            var record = (await read.ListAsync(HoldStore.Kind, null, 2, 1024 * 1024, token)).Records.Single();
            await Assert.That(Encoding.UTF8.GetString(record.Json.Span).Contains(sentinel, StringComparison.Ordinal)).IsFalse();
            var persisted = JsonSerializer.Deserialize(record.Json.Span, TerraBaseJsonContext.Default.HeldTransaction)!;
            await Assert.That(await crypto.OpenAsync(tenant, persisted.Payload!, token)).IsEquivalentTo(packed);
        }
        // A native replica's local page key must disappear before the peer acknowledges shredding.
        var nativeReplica = remote.Server.Services.GetRequiredService<IDatabaseReplicas>();
        using var peerHttp = PeerTls.CreateClient(api.Server.BaseAddress);
        await nativeReplica.OpenAsync(tenant, api.Server.PullAddress(tenant), peerHttp, token);
        await Assert.That(nativeReplica.Holds(tenant)).IsTrue();
        var replicaFiles = Path.Combine(remote.Server.Services.GetRequiredService<TerraBaseOptions>().DataDirectory,
            ".replicas", tenant.OrganizationId, tenant.ProjectId!);
        await Assert.That(Directory.GetFiles(replicaFiles)).IsNotEmpty();
        // Warm a separate server's cache. Deletion must revoke it over the actual authenticated peer RPC.
        var remoteCustody = remote.Server.Services.GetRequiredService<KeyCustody>();
        var observedStore = new ObservedStore(remoteCustody.Store);
        using var otherKeys = new DataKeys(observedStore,
            remote.Server.Services.GetRequiredService<IMasterKey>(), TimeProvider.System,
            remote.Server.Services.GetRequiredService<BackupSettings>(), revocations: remoteCustody.Revocations);
        var other = new TransactionCrypto(otherKeys);
        await Assert.That(await other.OpenAsync(tenant, payload, token)).IsEquivalentTo(packed);
        await Assert.That(await other.OpenAsync(snapshot, snapshotPayload, token)).IsEquivalentTo(packed);
        var reads = observedStore.Reads;
        for (var repeat = 0; repeat < 10; repeat++)
        {
            await Assert.That(await other.OpenAsync(tenant, payload, token)).IsEquivalentTo(packed);
        }
        await Assert.That(observedStore.Reads).IsEqualTo(reads);
        using var channel = GrpcChannel.ForAddress(remote.Server.BaseAddress, new GrpcChannelOptions { HttpClient = remote.Server.Client });
        var revocations = new KeyRevocation.KeyRevocationClient(channel);
        var refused = await revocations.RevokeAsync(new KeyRevocationRequest
        {
            Organization = tenant.OrganizationId,
            Project = tenant.ProjectId!,
            Database = tenant.DatabaseId!,
        }, cancellationToken: token).ResponseAsync;
        await Assert.That(refused.Revoked).IsFalse();
        // Production shipping retains a native WAL reader between passes. DELETE must drain it.
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(token);
        await Assert.That(api.Server.Services.GetRequiredService<IContinuousBackup>().Status.Files
            .Any(file => file.File == BackupLayout.File(tenant, "origin"))).IsTrue();
        using var deleted = await api.Client.DeleteAsync(ManagementApi.Uri(database.Path), token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var certificate = await api.Server.Services.GetRequiredService<SystemCatalog>().ReadUncheckedAsync(transaction =>
            SystemCatalog.GetAsync(transaction, DatabaseBackupPurge.ProgressKind, tenant.DatabaseId!,
                BackupPurgeJsonContext.Default.BackupPurgeProgress, token), token);
        await Assert.That(certificate?.KeysShreddedAt).IsNotNull();
        await Assert.That(s3.Objects.Keys.Where(key => key.StartsWith(BackupLayout.Keys(BackupLayout.KeyScope(tenant)), StringComparison.Ordinal)
            && !key.EndsWith("/shredded", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(nativeReplica.Holds(tenant)).IsFalse();
        await Assert.That(Directory.GetFiles(replicaFiles)).IsEmpty();
        await Assert.That(await other.OpenAsync(tenant, payload, token)).IsNull();
        await Assert.That(await other.OpenAsync(snapshot, snapshotPayload, token)).IsNull();
        await api.Server.Services.GetRequiredService<DatabaseBackupPurge>().RunOnceAsync(token);
        await Assert.That(api.Server.Services.GetRequiredService<HoldStore>().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Custody_failure_never_acknowledges_shredding_and_durable_deletion_retries(CancellationToken token)
    {
        var s3 = new FakeS3 { R2 = true };
        using var cloudflare = new FakeCloudflare();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            cloudflare.Route(services);
            ManualBackupHosting.Configure(services);
            services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationFactory?.Method.ReturnType == typeof(ShapeCdnPurge)));
        });
        var database = await api.CreateManagedDatabaseAsync();
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var scope = BackupLayout.KeyScope(tenant);
        var keys = api.Server.Services.GetRequiredService<DataKeys>();
        _ = await keys.CurrentAsync(scope, token);
        using var backup = await api.Client.PostAsync(ManagementApi.Uri(database.Path + "/backups"), null, token);
        await Assert.That(backup.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var backupBody = JsonDocument.Parse(await backup.Content.ReadAsByteArrayAsync(token));
        var snapshot = tenant with { DatabaseId = backupBody.RootElement.GetProperty("id").GetString()! };
        var snapshotScope = BackupLayout.KeyScope(snapshot);
        _ = await keys.CurrentAsync(snapshotScope, token);
        s3.FailPut = key => key == BackupLayout.Shredded(snapshotScope);
        using var deleted = await api.Client.DeleteAsync(ManagementApi.Uri(database.Path), token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await api.Server.Services.GetRequiredService<SystemCatalog>().ListDatabasePurgesAsync(null, 16, token))!.Items.Single().BackupVerifiedAt).IsNull();
        // Reproduce an interrupted legacy progress publication: physical-erasure certification
        // exists but the local key certificate did not reach durable progress. Closure must recover.
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        _ = await catalog.WriteDurableAsync(async transaction =>
        {
            var record = await SystemCatalog.GetAsync(transaction, DatabasePurge.Kind, tenant.DatabaseId!,
                TerraBaseJsonContext.Default.DatabasePurgeRecord, token);
            await SystemCatalog.PutAsync(transaction, DatabasePurge.Kind, tenant.DatabaseId!,
                record! with { BackupVerifiedAt = DateTimeOffset.UtcNow }, TerraBaseJsonContext.Default.DatabasePurgeRecord, token);
            return true;
        }, _ => false, token);
        s3.FailPut = _ => false;
        await api.Server.Services.GetRequiredService<DatabaseBackupPurge>().RunOnceAsync(token);
        var repaired = await catalog.ReadUncheckedAsync(transaction => SystemCatalog.GetAsync(transaction,
            DatabaseBackupPurge.ProgressKind, tenant.DatabaseId!, BackupPurgeJsonContext.Default.BackupPurgeProgress, token), token);
        await Assert.That(repaired?.KeysShreddedAt).IsNotNull();
        await Assert.That(() => api.Server.Services.GetRequiredService<DataKeys>().CurrentAsync(scope, token)).Throws<DatabaseShreddedException>();
        await Assert.That(() => keys.CurrentAsync(snapshotScope, token)).Throws<DatabaseShreddedException>();

        // A durable entry survives both an interrupted object delete and a refused Cloudflare request.
        var services = api.Server.Services;
        var custody = services.GetRequiredService<KeyCustody>();
        var objectKey = "v1/shapes/h1-0000000000000000/" + new string('a', 64);
        var url = "https://" + TerraBase.Configuration.ShapeStorageSettings.CdnHost + "/" + objectKey;
        using var body = new MemoryStream([1, 2, 3]);
        await custody.Store.PutAsync(objectKey, body, token);
        await Assert.That(await services.GetRequiredService<IShapeCdnPurge>().QueueAsync(url, token)).IsTrue();
        await services.GetRequiredService<ShapeCdnPurge>().RunOnceAsync(token);
        await Assert.That(cloudflare.Purged).IsEmpty();
        _ = await custody.Store.DeleteAsync(objectKey, token);
        using var refused = Purger("refused-token");
        await refused.RunOnceAsync(token);
        await Assert.That(cloudflare.Purged).IsEmpty();
        using var restarted = Purger(FakeCloudflare.Token);
        await restarted.RunOnceAsync(token);
        await Assert.That(cloudflare.Purged).Contains(url[8..(url.LastIndexOf('/') + 1)]);

        ShapeCdnPurge Purger(string credential) => new(services.GetRequiredService<IHttpClientFactory>(), credential,
            TimeProvider.System, services.GetRequiredService<IMeterFactory>(), services.GetRequiredService<ILogger<ShapeCdnPurge>>(),
            services.GetRequiredService<SystemCatalog>(), custody);
    }

    private sealed class ObservedStore(IObjectStore inner) : IObjectStore
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken token)
        {
            Interlocked.Increment(ref _reads);
            return inner.ReadAsync(key, read, token);
        }
        public Task PutAsync(string key, Stream content, CancellationToken token) => inner.PutAsync(key, content, token);
        public Task<bool> DeleteAsync(string key, CancellationToken token) => inner.DeleteAsync(key, token);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, string? after, CancellationToken token) => inner.ListAsync(prefix, after, token);
    }

}
