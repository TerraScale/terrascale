using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class RegionalRoutingContractTests
{
    private const string RegionHeader = "X-TerraBase-Region";
    private const string Replay = "ts-replay";
    private const string ReplaySource = "ts-replay-src";
    private const string ReplayedFromSjc = "region=sjc";

    [Test]
    public async Task The_answering_node_reports_its_own_region()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru");
        using var anonymous = api.DataClient();
        using var health = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri("/healthz"));
        using var healthResponse = await anonymous.SendAsync(health);
        using var management = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri("/v1/management/organizations"));
        using var managementResponse = await api.Client.SendAsync(management);

        await Assert.That(healthResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(managementResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        foreach (var response in new[] { healthResponse, managementResponse })
        {
            await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru"]);
            await Assert.That(response.Headers.Contains(Replay)).IsFalse();
        }
        using var body = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
        await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo("gru");
    }

    [Test]
    public async Task A_mismatched_pin_is_replayed_once_and_a_replayed_request_is_answered_where_it_landed()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru");

        using var first = await SendPinnedAsync(api.Client, "sjc", replayed: false);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(first.Headers.GetValues(Replay)).IsEquivalentTo(["region=sjc"]);
        await Assert.That(first.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru"]);
        using var replayed = await SendPinnedAsync(api.Client, "sjc", replayed: true);
        await Assert.That(replayed.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(replayed.Headers.Contains(Replay)).IsFalse();
        await Assert.That(replayed.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru"]);
        using (var body = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync()))
        {
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("region_pin_mismatch");
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("message").GetString())
                .IsEqualTo("the pinned region has no machine to answer, so gru received the request; pin one of the serving regions (gru) or drop X-TerraScale-Region-Pin");
        }
        using var served = await SendPinnedAsync(api.Client, "gru", replayed: true);
        await Assert.That(served.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(served.Headers.Contains(Replay)).IsFalse();
    }

    private static async Task<HttpResponseMessage> SendPinnedAsync(HttpClient client, string pin, bool replayed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri("/v1/management/organizations"));
        request.Headers.TryAddWithoutValidation("X-TerraScale-Region-Pin", pin);
        if (replayed)
        {
            request.Headers.TryAddWithoutValidation(ReplaySource, ReplayedFromSjc);
        }

        return await client.SendAsync(request);
    }
}
