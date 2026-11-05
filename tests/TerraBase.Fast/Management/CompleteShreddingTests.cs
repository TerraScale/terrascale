using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraBase.Features.Shapes;
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
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => { s3.Route(services); ManualBackupHosting.Configure(services); });
        var database = await api.CreateManagedDatabaseAsync();
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var crypto = api.Server.Services.GetRequiredService<ITransactionCrypto>();
        var sentinel = "private-row-" + EntityIds.New();
        var transaction = new RegionTransaction(EntityIds.New().ToString(), "sjc", null,
            [new RegionChange("insert", "items", null, new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement(sentinel) })]);
        var packed = HomePush.Encode(transaction);
        var payload = await crypto.SealAsync(tenant, packed, token);
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
        // Warm an independent region's cache before the DELETE; its historical decrypt must still consult custody.
        using var otherKeys = new DataKeys(api.Server.Services.GetRequiredService<KeyCustody>().Store,
            api.Server.Services.GetRequiredService<IMasterKey>(), TimeProvider.System, api.Server.Services.GetRequiredService<TerraBase.Configuration.BackupSettings>());
        var other = new TransactionCrypto(otherKeys);
        await Assert.That(await other.OpenAsync(tenant, payload, token)).IsEquivalentTo(packed);
        using var deleted = await api.Client.DeleteAsync(ManagementApi.Uri(database.Path), token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(s3.Objects.Keys.Where(key => key.StartsWith(BackupLayout.Keys(BackupLayout.KeyScope(tenant)), StringComparison.Ordinal)
            && !key.EndsWith("/shredded", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(await other.OpenAsync(tenant, payload, token)).IsNull();
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
        _ = await api.Server.Services.GetRequiredService<DataKeys>().CurrentAsync(scope, token);
        s3.FailPut = key => key == BackupLayout.Shredded(scope);
        using var deleted = await api.Client.DeleteAsync(ManagementApi.Uri(database.Path), token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await api.Server.Services.GetRequiredService<SystemCatalog>().ListDatabasePurgesAsync(null, 16, token))!.Items.Single().BackupVerifiedAt).IsNull();
        s3.FailPut = _ => false;
        await api.Server.Services.GetRequiredService<DatabaseBackupPurge>().RunOnceAsync(token);
        await Assert.That(() => api.Server.Services.GetRequiredService<DataKeys>().CurrentAsync(scope, token)).Throws<DatabaseShreddedException>();

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
}
