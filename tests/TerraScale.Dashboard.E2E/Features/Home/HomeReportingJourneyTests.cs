using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Home;

[ClassDataSource<DashboardRootFixture>]
public sealed class HomeReportingJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task ConnectTabShowsPrefilledSdkSnippets()
    {
        await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, _ => Task.FromResult<IResult?>(null));
        using var client = scenario.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        foreach (var step in new Dictionary<string, string>[]
        {
            new() { ["step"] = "account-information", ["displayName"] = "Connect User" },
            new() { ["step"] = "payment-method", ["bypass"] = "1" },
            new() { ["step"] = "project", ["name"] = "Connect project", ["region"] = "GRU" }
        })
        {
            step["_csrf"] = await HttpJourney.CsrfAsync(client, "/onboarding");
            using var advanced = await HttpJourney.PostFormAsync(client, "/onboarding/step", step);
            await Assert.That(advanced.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }
        using var identityResponse = await client.GetAsync("/api/v1/auth/me");
        using var identity = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync());
        var projectId = identity.RootElement.GetProperty("projectId").GetString()!;

        // A new project with no databases and no keys: nothing is done yet and nothing is an error.
        var overview = await client.GetStringAsync("/");
        await Assert.That(overview).Contains("No activity yet. Create a database to start metering reads and writes.");
        await Assert.That(overview).Contains("No activity yet. Replication starts once this project has a database.");
        await Assert.That(overview).DoesNotContain("Replication unavailable");
        await Assert.That(overview).DoesNotContain(">Done</span>");

        using var connect = await client.GetAsync("/?tab=connect");
        await Assert.That(connect.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await connect.Content.ReadAsStringAsync());
        await Assert.That(html).Contains("Create your first database to get connection snippets.");
        await Assert.That(html).DoesNotContain("id=\"connect-js\"");
        await Assert.That(html).Contains("<title>Connect");
        using var producer = scenario.CreateProviderClient();
        foreach (var name in new[] { "app", "warehouse" })
        {
            using var added = await producer.PostAsJsonAsync("/__test/control", new { command = "internal-add-database", name });
            await Assert.That(added.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }
        html = WebUtility.HtmlDecode(await client.GetStringAsync("/?tab=connect&database=warehouse"));
        await Assert.That(html).Contains("for=\"connect-database\"");
        await Assert.That(html).Contains($"name=\"projectId\" value=\"{projectId}\"");
        await Assert.That(html).Contains("/databases/warehouse/collections");
        var fallback = WebUtility.HtmlDecode(await client.GetStringAsync("/?tab=connect&database=not-in-this-project"));
        await Assert.That(fallback).Contains("/databases/app/collections");
        await Assert.That(fallback).DoesNotContain("/databases/not-in-this-project/");
        var js = html[html.IndexOf("id=\"connect-js\"", StringComparison.Ordinal)..];
        js = js[..js.IndexOf("</pre>", StringComparison.Ordinal)];
        await Assert.That(js).Contains("createTerraBaseV1Client");
        await Assert.That(js).Contains("client.database(\"warehouse\")");
        await Assert.That(js).Contains($"project: \"{projectId}\"");
        await Assert.That(js).Contains("baseUrl: \"https://api.terrascale.tech\"");
        foreach (var id in new[] { "connect-token", "connect-curl", "connect-csharp", "connect-go", "connect-rust" })
            await Assert.That(html).Contains($"id=\"{id}\"");
        await Assert.That(html).Contains($"/v1/projects/{projectId}/databases/");
        await Assert.That(html).Contains("href=\"/api-keys\"");
        await Assert.That(html).Contains("https://docs.terrascale.tech/getting-started/");
    }

    [Test]
    public async Task HomeReadsAnalyticsAndReplicationWithScopedProjectTokens()
    {
        var reports = new ConcurrentBag<(string Path, string Query)>();
        await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, http =>
        {
            var path = http.Request.Path.Value ?? "";
            if (http.Request.Method != "GET" || !path.StartsWith("/v1/projects/", StringComparison.Ordinal) ||
                !(path.EndsWith("/analytics", StringComparison.Ordinal) || path.EndsWith("/replication", StringComparison.Ordinal)))
                return Task.FromResult<IResult?>(null);

            reports.Add((path, http.Request.QueryString.Value ?? ""));
            var range = http.Request.Query["range"].ToString();
            var projectId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[2];
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var to = DateTimeOffset.FromUnixTimeSeconds((now / 3600 + 1) * 3600);
            var from = to.AddHours(-24);
            object response = path.EndsWith("/analytics", StringComparison.Ordinal)
                ? new { projectId, range, from, to, bucketSeconds = 3600, items = Array.Empty<object>() }
                : new { projectId, range, from, to, bucketSeconds = 3600, items = Array.Empty<object>() };
            return Task.FromResult<IResult?>(Results.Json(response));
        });

        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);

        foreach (var step in new Dictionary<string, string>[]
        {
            new() { ["step"] = "account-information", ["displayName"] = "Reporting User" },
            new() { ["step"] = "payment-method", ["bypass"] = "1" },
            new() { ["step"] = "project", ["name"] = "Reporting project", ["region"] = "GRU" }
        })
        {
            step["_csrf"] = await HttpJourney.CsrfAsync(client, "/onboarding");
            using var advanced = await HttpJourney.PostFormAsync(client, "/onboarding/step", step);
            await Assert.That(advanced.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }
        using (var seeded = await producer.PostAsJsonAsync("/__test/control", new { command = "internal-add-database" }))
            await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var identityResponse = await client.GetAsync("/api/v1/auth/me");
        await Assert.That(identityResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var identity = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync());
        var identityRoot = identity.RootElement;

        using var home = await client.GetAsync("/");
        await Assert.That(home.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await home.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("No analytics measurements for this period.");
        await Assert.That(html).Contains("No replication observations are available for this project.");
        await Assert.That(html).DoesNotContain("Analytics unavailable");
        await Assert.That(html).DoesNotContain("Replication unavailable");

        await Assert.That(reports.Count).IsEqualTo(2);
        var projectId = identityRoot.GetProperty("projectId").GetString();
        await Assert.That(reports.Select(report => report.Path).Order(StringComparer.Ordinal))
            .IsEquivalentTo(new[] { $"/v1/projects/{projectId}/analytics", $"/v1/projects/{projectId}/replication" }.Order(StringComparer.Ordinal));
        foreach (var report in reports)
            await Assert.That(report.Query).IsEqualTo("?range=24h");

        using var observationResponse = await producer.GetAsync("/__test/observations");
        await Assert.That(observationResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var observation = JsonDocument.Parse(await observationResponse.Content.ReadAsStringAsync());
        var observed = observation.RootElement;
        // Analytics and replication run together and share the session's one exchanged read token.
        await Assert.That(observed.GetProperty("projectExchanges").GetInt32()).IsEqualTo(1);
        await Assert.That(observed.GetProperty("projectReportReads").GetInt32()).IsEqualTo(2);
        await Assert.That(observed.GetProperty("contractFailures").GetInt32()).IsEqualTo(0)
            .Because(observed.GetProperty("contractFailureMessages").GetRawText());
        var requests = observed.GetProperty("projectRequests").EnumerateArray().ToArray();
        await Assert.That(requests.Length).IsEqualTo(2);
        foreach (var request in requests)
        {
            await Assert.That(request.GetProperty("projectId").GetString()).IsEqualTo(identityRoot.GetProperty("projectId").GetString());
            await Assert.That(request.GetProperty("organizationId").GetString()).IsEqualTo(identityRoot.GetProperty("organizationId").GetString());
            await Assert.That(request.GetProperty("subject").GetString()).IsEqualTo(identityRoot.GetProperty("userId").GetString());
            await Assert.That(request.GetProperty("audience").GetString()).IsEqualTo("https://api.terrascale.tech");
            await Assert.That(request.GetProperty("scope").GetString()).IsEqualTo("databases:read");
            await Assert.That(request.GetProperty("tokenClass").GetString()).IsEqualTo("project");
            await Assert.That(request.GetProperty("path").GetString()).StartsWith($"/v1/projects/{projectId}/");
        }
    }
}
