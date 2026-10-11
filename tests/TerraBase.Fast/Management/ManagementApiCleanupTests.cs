using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ManagementApiCleanupTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Disposal_cleans_independent_resources_when_server_or_tls_cleanup_fails(bool tlsCleanupFails)
    {
        var api = await ManagementApi.StartAsync();
        var tlsSettings = new Dictionary<string, string>(StringComparer.Ordinal);
        api.Tls.Apply(tlsSettings);
        var tlsDirectory = Path.GetDirectoryName(tlsSettings["client-certificate"])!;
        try
        {
            using var health = await api.Server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
            await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);

            if (tlsCleanupFails)
            {
                // The next TLS disposal fails after the real server shuts down.
                api.Tls.Dispose();
            }
            else
            {
                // Close this server's database files before removing only its own directory.
                await api.Server.Services.GetRequiredService<IStorageEngine>().DisposeAsync();
                Directory.Delete(api.Server.DataDirectory, recursive: true);
            }

            await Assert.That(async () => await api.DisposeAsync()).Throws<DirectoryNotFoundException>();

            await Assert.That(Directory.Exists(api.Server.DataDirectory)).IsFalse();
            await Assert.That(Directory.Exists(tlsDirectory)).IsFalse();
            await Assert.That(File.Exists(tlsSettings["client-certificate"])).IsFalse();
            await Assert.That(File.Exists(tlsSettings["client-private-key"])).IsFalse();
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
                try
                {
                    if (Directory.Exists(tlsDirectory))
                    {
                        api.Tls.Dispose();
                    }
                }
                finally
                {
                    api.Issuer.Dispose();
                }
            }
        }
    }
}
