using System.Net;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class ListenerBindingTests
{
    [Test]
    public async Task Local_development_binds_every_listener_to_loopback_only()
    {
        await using var server = await TerraBaseTestServer.StartAsync();

        foreach (var listener in server.Listeners)
        {
            await Assert.That(IPAddress.IsLoopback(IPAddress.Parse(listener.Host))).IsTrue();
        }
    }

    [Test]
    public async Task Outside_development_the_public_and_metrics_listeners_bind_every_interface()
    {
        // Outside Development a node needs its backup bucket and master key.
        var s3 = new FakeS3();
        await using var server = await TerraBaseTestServer.StartAsync(s3.Configure, s3.Route, environmentName: "Production");

        foreach (var listener in server.Listeners.Skip(1))
        {
            var address = IPAddress.Parse(listener.Host.Trim('[', ']'));
            await Assert.That(address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Any)).IsTrue();
        }
    }
}
