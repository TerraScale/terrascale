using System.Net;
using System.Text.Json;
using TerraBase.Engine.Turso;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class RegionTests
{
    private const string RegionHeader = "X-TerraBase-Region";
    private const string PinHeader = "X-TerraScale-Region-Pin";
    private const string Organizations = "/v1/management/organizations";
    private const string Database = "/v1/projects/{project}/databases/{database}";

    [Test]
    [Arguments("a")]
    [Arguments("gru")]
    [Arguments("a--1")]
    [Arguments("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijk")]
    public async Task Health_reports_the_machine_region(string region)
    {
        // Outside Development a node needs its backup bucket and master key.
        var s3 = new FakeS3();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            settings["region"] = region;
            s3.Configure(settings);
        }, s3.Route, environmentName: "Production");
        using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("region").GetString()).IsEqualTo(region);
        // The deploy gate compares it with a new image's `terrabase --storage-format` before switching the region.
        await Assert.That(body.GetProperty("storageFormat").GetString()).IsEqualTo(StorageFormat.Id);
        await Assert.That(response.Headers.Contains(RegionHeader)).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("gru-1")]
    public async Task An_absent_or_exact_region_pin_is_accepted(string? pin)
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Organizations, UriKind.Relative));
        if (pin is not null)
        {
            request.Headers.TryAddWithoutValidation(PinHeader, pin);
        }

        using var response = await api.Client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru-1"]);
    }

    [Test]
    public async Task Openapi_documents_region_pins_headers_and_database_bodies_on_client_operations()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var operation = document.GetProperty("paths").GetProperty(Database).GetProperty("get");
        var parameter = operation.GetProperty("parameters").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == PinHeader);
        await Assert.That(parameter.GetProperty("in").GetString()).IsEqualTo("header");
        var responses = operation.GetProperty("responses");
        await Assert.That(responses.GetProperty("421").GetProperty("content").TryGetProperty("application/json", out _)).IsTrue();
        foreach (var response in responses.EnumerateObject())
        {
            await Assert.That(response.Value.GetProperty("headers").TryGetProperty(RegionHeader, out _)).IsTrue();
            await Assert.That(response.Value.GetProperty("headers").TryGetProperty("X-TerraBase-Machine", out _)).IsTrue();
        }

        var schema = document.GetProperty("components").GetProperty("schemas").GetProperty("ManagedDatabaseBody");
        await Assert.That(schema.GetProperty("properties").TryGetProperty("region", out _)).IsTrue();
        foreach (var path in new[] { "/healthz", "/readyz" })
        {
            var probe = document.GetProperty("paths").GetProperty(path).GetProperty("get");
            await Assert.That(probe.GetProperty("parameters").EnumerateArray().Any(item => item.GetProperty("name").GetString() == PinHeader)).IsTrue();
            await Assert.That(probe.GetProperty("responses").TryGetProperty("421", out _)).IsTrue();
            await Assert.That(probe.GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty(RegionHeader, out _)).IsTrue();
        }
    }

}
