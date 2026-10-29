using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class ManagementApiCleanupTests
{
    [Test]
    public async Task Disposal_cleans_independent_resources_when_server_cleanup_fails()
    {
        var api = await ManagementApi.StartAsync();
        try
        {
            using var health = await api.Server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
            await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);

            // Close this server's database files before removing only its own directory.
            await api.Server.Services.GetRequiredService<IStorageEngine>().DisposeAsync();
            Directory.Delete(api.Server.DataDirectory, recursive: true);

            await Assert.That(async () => await api.DisposeAsync()).Throws<DirectoryNotFoundException>();

            await Assert.That(Directory.Exists(api.Server.DataDirectory)).IsFalse();
            // The issuer's signing keys were disposed too.
            await Assert.That(() => api.Issuer.Mint(EntityIds.New().ToString())).Throws<ObjectDisposedException>();
        }
        finally
        {
            // Clean up even if an assertion fails against the original, leaking implementation.
            try
            {
                if (Directory.Exists(api.Server.DataDirectory))
                {
                    await api.Server.DisposeAsync();
                }
            }
            finally
            {
                api.Client.Dispose();
                api.Issuer.Dispose();
            }
        }
    }
}
