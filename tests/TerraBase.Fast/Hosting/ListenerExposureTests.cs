using System.Net;

namespace TerraBase.Fast.Hosting;

public sealed class ListenerExposureTests
{
    [Test]
    public async Task The_internal_pressure_route_answers_on_the_internal_listener()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        using var marked = await api.Server.Client.GetAsync("/v1/admin/node/pressure");

        await Assert.That(marked.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
