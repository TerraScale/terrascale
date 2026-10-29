using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class RegionalRoutingContractTests
{
    private const string RegionHeader = "X-TerraBase-Region";
    private const string HostPinHeader = "X-TerraScale-Region-Pin";
    private const string Replay = "ts-replay";
    private const string ReplaySource = "ts-replay-src";
    private const string ReplayedFromSjc = "region=sjc";
    private static readonly string[] InSjc = ["sjc"];

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

    [Test]
    [Arguments("gru_1.api.terrascale.tech", null)]
    [Arguments("Not-A-Region-.api.terrascale.tech", null)]
    [Arguments("x9.y9.api.terrascale.tech", null)]
    [Arguments("gru.api.terrascale.tech", "sjc")]
    [Arguments("sjc.api.terrascale.tech", "gru")]
    [Arguments("gru.api.terrascale.tech", "")]
    [Arguments("gru.api.terrascale.tech", "gru,gru")]
    public async Task Rejected_hosts_and_contradictory_pins_precede_authentication_and_mutation(string host, string? pin)
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru");
        var organization = await api.CreateOrganizationAsync("Routing refusal");
        var project = await api.CreateProjectAsync(organization, "Main");
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        using var anonymous = api.DataClient();
        foreach (var client in new[] { api.Client, anonymous })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ManagementApi.Uri(databases))
            {
                Content = JsonContent.Create(new { name = "rejected" }),
            };
            request.Headers.Host = host;
            if (pin is not null)
            {
                request.Headers.TryAddWithoutValidation(HostPinHeader, pin.Split(','));
            }

            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru"]);
            await Assert.That(response.Headers.GetValues("X-TerraBase-Machine")).IsEquivalentTo(["local"]);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var code = pin switch
            {
                null => "misdirected_request",
                "sjc" or "gru" => "region_pin_mismatch",
                _ => "invalid_region_pin",
            };
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
        }

        var list = await api.Client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(databases));
        await Assert.That(list.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_empty_regional_label_is_misdirected_before_authentication_and_mutation(bool authenticated)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru");
        var organization = await api.CreateOrganizationAsync("Empty regional label");
        var project = await api.CreateProjectAsync(organization, "Main");
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        var authorization = authenticated ? $"Authorization: Bearer {api.Issuer.Mint(api.User)}\r\n" : string.Empty;
        const string payload = "{\"name\":\"rejected\"}";
        var request = $"POST {databases} HTTP/1.0\r\nHost: .api.terrascale.tech\r\n{authorization}Content-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}";
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, api.Server.ClientAddress!.Port, token);
        await using var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var response = await reader.ReadToEndAsync(token);
        await Assert.That(response).StartsWith("HTTP/1.1 421 ");
        var parts = response.Split("\r\n\r\n", 2);
        await Assert.That(parts.Length).IsEqualTo(2);
        var headers = parts[0].ToLowerInvariant() + "\r\n";
        await Assert.That(headers).Contains("\r\ncache-control: no-store\r\n");
        await Assert.That(headers).Contains("\r\nx-terrabase-region: gru\r\n");
        await Assert.That(headers).Contains("\r\nx-terrabase-machine: local\r\n");
        using var body = JsonDocument.Parse(parts[1]);
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("misdirected_request");
        var list = await api.Client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(databases), token);
        await Assert.That(list.GetProperty("items").GetArrayLength()).IsEqualTo(0);
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

    private static async Task AssertWrongRegionAsync(HttpResponseMessage response, string? replay, string regions)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(response.Headers.TryGetValues(Replay, out var replayed) ? replayed.Single() : null).IsEqualTo(replay);
        await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo(["sjc"]);
        await Assert.That(response.Headers.GetValues("X-TerraBase-Database-Home")).IsEquivalentTo(["gru"]);
        await Assert.That(response.Headers.GetValues("X-TerraBase-Database-Regions")).IsEquivalentTo([regions]);
        await ErrorAssert.ErrorAsync(response, HttpStatusCode.MisdirectedRequest, "wrong_region", "the database is served in gru");
    }
}
