using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Configuration;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ShapeDeletionTests
{
    [Test]
    public async Task Delete_queues_owned_shapes_before_204_without_scanning_unrelated_objects(CancellationToken token)
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
        var tenant = new TenantContext(database.Organization, PlanTier.Pro, database.Project, database.Id);
        var services = api.Server.Services;
        using var objects = new ShapeObjectStore(services.GetRequiredService<ShapeStorageSettings>(),
            services.GetRequiredService<IHttpClientFactory>(), TimeProvider.System, services.GetRequiredService<IShapeCdnPurge>());
        const string key = "v1/shapes/h1-ffffffffffffffff/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var owner = ShapeObjectStore.Owner(tenant);
        var head = new ShapeObjectHead(owner, new string('a', 64), new string('b', 64), 3, 1,
            "01M6PER7EK2HE3QCYY6MB9QYZ8", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
        _ = await objects.PutAsync(key, head, new byte[] { 1, 2, 3 }, token);
        // Poison a large unrelated inventory: the old serial shared scan would inspect it before our handle.
        for (var index = 0; index < 1100; index++)
        {
            s3.SeedObject(false, "v1/shapes/h1-0000000000000000/" + index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), [9]);
        }
        s3.ObservedListPrefix = "v1/shapes/";
        using var response = await api.ProjectClient(database.Project).DeleteAsync(ManagementApi.Uri(database.Path), token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(s3.Objects.ContainsKey(key)).IsFalse();
        await Assert.That(s3.Objects.Keys.Any(item => item.StartsWith(ShapeObjectStore.InventoryPrefix(owner), StringComparison.Ordinal))).IsFalse();
        await Assert.That(s3.ListRequests).IsEqualTo(0);
        await Assert.That(s3.Objects.Keys.Count(item => item.StartsWith("v1/shapes/", StringComparison.Ordinal))).IsEqualTo(1100);
        await services.GetRequiredService<ShapeCdnPurge>().RunOnceAsync(token);
        await Assert.That(cloudflare.Purged).Contains(ShapeStorageSettings.CdnHost + "/v1/shapes/h1-ffffffffffffffff/");
    }
}
