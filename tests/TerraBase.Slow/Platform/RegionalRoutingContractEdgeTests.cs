using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Slow.Platform;

public sealed class RegionalRoutingContractEdgeTests
{
    private const string RegionHeader = "X-TerraBase-Region";
    private const string HostPinHeader = "X-TerraScale-Region-Pin";

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

}
