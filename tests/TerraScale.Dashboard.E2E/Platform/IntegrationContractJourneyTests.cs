using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardE2EFixture>]
public sealed class IntegrationContractJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task ServedOpenApiMatchesCommittedContractAndAuthenticatedResponses()
    {
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync("/openapi/integration.json");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var document = JsonDocument.Parse(bytes);
        await Assert.That(document.RootElement.GetProperty("servers")[0].GetProperty("url").GetString())
            .IsEqualTo("https://dash.terrascale.tech");
        var paths = document.RootElement.GetProperty("paths");
        var identity = paths.GetProperty("/api/v1/auth/me").GetProperty("get");
        await Assert.That(identity.GetProperty("operationId").GetString()).IsEqualTo("getDashboardIdentity");
        await Assert.That(identity.GetProperty("responses").TryGetProperty("401", out _)).IsTrue();
        await Assert.That(identity.GetProperty("security")[0].TryGetProperty("dashboardSession", out _)).IsTrue();
        foreach (var route in new[] { "switch-organization", "switch-project" })
        {
            var operation = paths.GetProperty("/api/v1/dashboard/" + route).GetProperty("post");
            await Assert.That(operation.GetProperty("requestBody").GetProperty("content")
                .TryGetProperty("application/x-www-form-urlencoded", out _)).IsTrue();
            await Assert.That(operation.GetProperty("responses").TryGetProperty("303", out _)).IsTrue();
        }
        var generated = Path.Combine(fixture.Root, "test-results", "openapi", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        await File.WriteAllBytesAsync(generated, bytes);
        var snapshot = Path.Combine(fixture.Root, "contracts", "dashboard", "integration.openapi.json");
        await Assert.That(File.Exists(snapshot)).IsTrue().Because("Commit the generated integration contract; output: " + generated);
        using var expected = JsonDocument.Parse(await File.ReadAllBytesAsync(snapshot));
        await Assert.That(JsonElement.DeepEquals(expected.RootElement, document.RootElement)).IsTrue()
            .Because("An HTTP contract change must update its committed document explicitly; output: " + generated);

        using var anonymous = await client.GetAsync("/api/v1/auth/me");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var me = await client.GetAsync("/api/v1/auth/me");
        await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await me.Content.ReadAsByteArrayAsync());
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("AccountIdentityResponse").GetProperty("properties");
        await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray())
            .IsEquivalentTo(schema.EnumerateObject().Select(property => property.Name).Order().ToArray());
        using var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-project",
            new() { ["projectId"] = "foreign", ["_csrf"] = "invalid" });
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ScalarServesLocalScriptsWithThePerRequestContentSecurityPolicyNonce()
    {
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync("/reference/integration");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        var nonce = WebUtility.HtmlDecode(Regex.Match(html, "nonce=\"([^\"]+)\"").Groups[1].Value);
        await Assert.That(nonce.Length).IsGreaterThan(0);
        await Assert.That(response.Headers.GetValues("Content-Security-Policy").Single()).Contains("'nonce-" + nonce + "'");
        foreach (Match script in Regex.Matches(html, "<script[^>]+src=\"([^\"]+)\""))
        {
            var source = script.Groups[1].Value;
            var assetUri = new Uri(response.RequestMessage!.RequestUri!, source);
            await Assert.That(assetUri.GetLeftPart(UriPartial.Authority)).IsEqualTo(fixture.Origin.GetLeftPart(UriPartial.Authority));
            using var asset = await client.GetAsync(assetUri);
            await Assert.That(asset.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That(html).Contains("integration");
    }
}
