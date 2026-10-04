using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Management;

public sealed class ManagementApiCleanupTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Disposal_cleans_independent_resources_when_server_or_tls_cleanup_fails(bool tlsCleanupFails)
    {
        var credentialDirectory = string.Empty;
        var api = await ManagementApi.StartAsync(settings =>
            credentialDirectory = Path.GetDirectoryName(settings["dashboard-credential-file"])!);
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
            await Assert.That(Directory.Exists(credentialDirectory)).IsFalse();
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
                    if (Directory.Exists(credentialDirectory))
                    {
                        Directory.Delete(credentialDirectory, recursive: true);
                    }
                }
            }
        }
    }
}
