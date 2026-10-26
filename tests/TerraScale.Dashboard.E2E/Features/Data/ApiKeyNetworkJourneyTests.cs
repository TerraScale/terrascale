using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace TerraScale.Dashboard.E2E.Features.Data;

// Allowlists and the expiring-soon notice through the dashboard's own routes, against the test ts-auth.
[ClassDataSource<DashboardRootFixture>]
public sealed class ApiKeyNetworkJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task KeyAndProjectAllowlistsRoundTripAndAKeyExpiringThisWeekNotifiesOnce()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1");
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(client);

        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/key", new()
        { ["_csrf"] = csrf, ["name"] = "Office key", ["scopes"] = "documents:read", ["allowedCidrs"] = "203.0.113.0/24\n198.51.100.7" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var issued = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var keyId = issued.RootElement.GetProperty("apiKey").GetProperty("keyId").GetString()!;
        await Assert.That(Cidrs(issued.RootElement.GetProperty("apiKey"))).IsEquivalentTo(new[] { "203.0.113.0/24", "198.51.100.7/32" });

        using var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/allowed-cidrs", new()
        { ["_csrf"] = csrf, ["keyId"] = keyId, ["allowedCidrs"] = "10.0.0.0/33" });
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("allowed_cidrs must hold up to 32 valid IPs or CIDR ranges");
        using var replaced = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/allowed-cidrs", new()
        { ["_csrf"] = csrf, ["keyId"] = keyId, ["allowedCidrs"] = "2001:db8::/48" });
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var listed = JsonDocument.Parse(await client.GetStringAsync("/api/v1/dashboard/credentials"));
        await Assert.That(Cidrs(listed.RootElement.EnumerateArray().Single())).IsEquivalentTo(new[] { "2001:db8::/48" });

        using var network = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/project-allowed-cidrs", new()
        { ["_csrf"] = csrf, ["allowedCidrs"] = "192.0.2.0/24" });
        await Assert.That(network.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        environment.ApiKeys.ExpireAt(keyId, DateTimeOffset.UtcNow.AddDays(3));
        var page = await client.GetStringAsync("/api-keys");
        await Assert.That(page).Contains("Network access");
        await Assert.That(page).Contains("192.0.2.0/24");
        await Assert.That(page).Contains("2001:db8::/48");
        await Assert.That(page).Contains("Expires soon");
        await Assert.That(page).Contains("Revoking stops this key everywhere within 2 seconds: new requests are refused and open connections (including Postgres) are closed within 3 seconds.");
        await Assert.That(page).Contains("Rotating issues a new secret, shown once. With no grace, the old secret and its tokens stop everywhere within 2 seconds; open connections (including Postgres) close within 3 seconds. With a grace period, the old secret works until it ends; these bounds start then.");
        await Assert.That(page).Contains(">No grace period</option>");
        await Assert.That(page.Contains("tokens it already obtained keep working", StringComparison.Ordinal)).IsFalse();

        await client.GetStringAsync("/notifications");
        var inbox = await client.GetStringAsync("/notifications");
        await Assert.That(inbox.Split("API key Office key expires on ").Length - 1).IsEqualTo(1);
    }

    private static string[] Cidrs(JsonElement key) => key.GetProperty("allowedCidrs").EnumerateArray().Select(item => item.GetString()!).ToArray();
}
