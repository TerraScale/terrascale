using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Platform
{
    // ts-orch delivers OCI Vault secrets as files and sets <NAME>_FILE to each path.
    [ClassDataSource<SharedDemoEnvironment>(Shared = SharedType.PerTestSession)]
    public sealed class SecretFilesJourneyTests(SharedDemoEnvironment shared)
    {
        [Test]
        public async Task HostStartsWithSecretsDeliveredAsFiles()
        {
            var directory = Directory.CreateTempSubdirectory("dashboard-secrets-");
            try
            {
                var values = new Dictionary<string, string>
                {
                    [DashboardIdentity.AuthClientSecretName] = "test-dashboard-client-secret-0123456789abcdef",
                    [DashboardIdentity.SessionEncryptionKeyName] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    [DashboardIdentity.StripeSecretKeyName] = "sk_test_fixture",
                    [DashboardIdentity.StripeWebhookSecretName] = "whsec_fixture",
                    [DashboardIdentity.TerraBasePeerSecretName] = TestIdentity.PeerSecret
                };
                var files = new Dictionary<string, string>();
                foreach (var (name, value) in values)
                {
                    var path = Path.Combine(directory.FullName, name.ToLowerInvariant());
                    await File.WriteAllTextAsync(path, value + "\n");
                    files[name + "_FILE"] = path;
                }
                var identity = new DashboardIdentity(files.GetValueOrDefault);
                var credentials = await identity.ReadAsync(CancellationToken.None);
                await Assert.That(credentials.SessionEncryptionKey).IsEqualTo(values[DashboardIdentity.SessionEncryptionKeyName]);

                await using var app = await shared.Environment.CreateHostAsync(shared.Root, 0, "http://127.0.0.1", builder =>
                    builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardIdentity>(identity)));
                await app.StartAsync();
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
                using var ready = await client.GetAsync("/health/ready");
                await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }
    }
}
