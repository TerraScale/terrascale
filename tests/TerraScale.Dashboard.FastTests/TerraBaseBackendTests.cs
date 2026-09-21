using Microsoft.Extensions.Configuration;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.FastTests;

public sealed class TerraBaseBackendTests
{
    [Test]
    public async Task ReadsEndpointAndApiKeyFromApplicationConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TERRABASE_URL"] = "http://127.0.0.1:8420/",
                ["TERRABASE_API_KEY"] = "member-key",
            })
            .Build();

        var backend = new TerraBaseBackend(configuration);

        await Assert.That(backend.Configured).IsTrue();
        await Assert.That(backend.Url).IsEqualTo(new Uri("http://127.0.0.1:8420/"));
        await Assert.That(backend.ApiKey).IsEqualTo("member-key");
    }

    [Test]
    public async Task RejectsNonOriginUrlsBeforeCreatingSdkClients()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TERRABASE_URL"] = "https://example.test/member?region=gru",
            })
            .Build();

        var backend = new TerraBaseBackend(configuration);

        await Assert.That(backend.Configured).IsFalse();
        await Assert.That(backend.Url).IsNull();
    }
}
