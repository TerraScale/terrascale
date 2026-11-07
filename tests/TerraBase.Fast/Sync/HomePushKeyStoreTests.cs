using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Features.HomePush;
using TerraBase.Testing;

namespace TerraBase.Fast.Sync;

public sealed class HomePushKeyStoreTests
{
    [Test]
    public async Task A_node_with_backup_seals_durable_local_writes_under_its_key_store()
    {
        var s3 = new FakeS3();
        await using var api = await ManagementApi.StartAsync(s3.Configure, s3.Route);

        await Assert.That(api.Server.Services.GetRequiredService<ITransactionCrypto>().Seals).IsTrue();
    }

    [Test]
    public async Task A_node_with_nats_and_no_key_store_refuses_to_start()
    {
        Exception? refused = null;
        try
        {
            await using var api = await ManagementApi.StartAsync(services: services =>
            {
                var registered = services.Single(service => service.ServiceType == typeof(TerraBaseOptions));
                services[services.IndexOf(registered)] = ServiceDescriptor.Singleton(
                    (TerraBaseOptions)registered.ImplementationInstance! with { NatsUrl = new Uri("nats://127.0.0.1:9"), NatsToken = "token" });
            });
        }
        catch (Exception error)
        {
            refused = error;
        }

        await Assert.That(refused?.ToString()).Contains("TS_NATS_URL requires backup");
    }
}
