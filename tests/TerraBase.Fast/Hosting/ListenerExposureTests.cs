using System.Net;

namespace TerraBase.Fast.Hosting;

public sealed class ListenerExposureTests
{
    [Test]
    public async Task An_endpoint_without_exposure_metadata_stays_closed()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        using var unmarked = await api.Server.Client.GetAsync("/v1/admin/node/pressure");
        using var marked = await api.Server.Client.GetAsync("/healthz");

        await Assert.That(unmarked.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(marked.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
