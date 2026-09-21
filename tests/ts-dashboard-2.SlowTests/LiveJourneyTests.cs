#if TS_AUTH_STACK
namespace TerraScale.Dashboard.SlowTests;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TerraScale.DotNet;

/// <summary>
/// The complete live journey a user can take, end to end against the real
/// stack: full account creation through the onboarding wizard, then the two
/// product actions that must never break — creating a database and creating
/// an API key — both verified with the TerraScale C# SDK against real
/// TerraBase: the issued key authenticates, the database accepts documents,
/// and the wrong key is refused.
/// </summary>
[TUnit.Core.NotInParallel]
public sealed class LiveJourneyTests
{
    private const string Password = "Journey-live-123!";

    private static async Task<(WebApplicationFactory<DashboardStore> Factory, HttpClient Client, string Email)> SignedUpMemberAsync(LiveDashboardFixture fixture, string email)
    {
        var factory = fixture.CreateDashboardFactory();
        var client = fixture.CreateDashboardClient(factory);

        using var registered = await PostFormAsync(client, "/onboarding/register", new Dictionary<string, string>
        {
            ["displayName"] = "Journey Live",
            ["email"] = email,
            ["password"] = Password,
            ["confirmPassword"] = Password,
            ["terms"] = "true",
        });
        AssertRedirect(registered);

        foreach (var (step, name) in new[]
        {
            ("organization", "Journey Labs"),
            ("payment-method", ""),
            ("project", "journey-project"),
        })
        {
            var fields = new Dictionary<string, string> { ["step"] = step };
            if (name.Length > 0)
                fields["name"] = name;
            using var response = await PostFormAsync(client, "/onboarding/step", fields);
            AssertRedirect(response);
        }

        return (factory, client, email);
    }

    [Test]
    public async Task Full_journey_creates_database_and_api_key_that_both_work_over_the_sdk()
    {
        var fixture = await LiveDashboardFixture.Shared;
        var email = $"journey-{Guid.NewGuid():N}@example.test";
        var (factory, client, _) = await SignedUpMemberAsync(fixture, email);
        await using var _factory = factory;

        // --- onboarding finished: the dashboard is reachable -----------------
        var overview = await client.GetAsync("/");
        await Assert.That(overview.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var overviewPage = HtmlPage.Capture(200, await overview.Content.ReadAsStringAsync());
        await Assert.That(overviewPage.Heading).IsEqualTo("Overview");

        // --- create a database through the dashboard form --------------------
        var databaseName = "journey-" + Guid.NewGuid().ToString("N")[..8];
        using (var response = await PostFormAsync(client, "/api/v1/dashboard/databases/create", new Dictionary<string, string> { ["name"] = databaseName }))
        {
            AssertRedirect(response);
        }

        var databasesPage = await HtmlPageOfAsync(client, "/databases");
        await Assert.That(databasesPage.Contains(databaseName)).IsTrue();

        // The control plane really has it, and it is not deleted.
        var record = await fixture.StateSdk.Databases.GetAsync(databaseName);
        await Assert.That(record.Deleted).IsFalse();

        // --- create an API key through the dashboard form --------------------
        using (var response = await PostFormAsync(client, "/api/v1/dashboard/credentials/key", new Dictionary<string, string>
        {
            ["name"] = "journey credential",
            ["scopes"] = "database:read, database:write",
            ["databaseId"] = databaseName,
        }))
        {
            AssertRedirect(response);
        }

        var secretPage = await HtmlPageOfAsync(client, "/api-keys");
        var secret = secretPage.SecretCode;
        await Assert.That(secret).IsNotNull();
        await Assert.That(secret!.StartsWith("tsk_live_")).IsTrue();

        // --- the database works, verified with the SDK and the issued key ----
        var keyClient = new TerraScaleClient(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = fixture.StateUrl },
            new TerraScaleClientOptions { BaseAddress = fixture.StateUrl, ApiKey = secret });
        var payload = JsonSerializer.SerializeToElement(new { kind = "journey", value = 42 });
        var put = await keyClient.Documents.PutAsync(databaseName, "journey", "doc-1", payload);
        await Assert.That(put.ChangeToken).IsNotNull();
        var got = await keyClient.Documents.GetAsync(databaseName, "journey", "doc-1");
        await Assert.That(got.Data.GetProperty("value").GetInt32()).IsEqualTo(42);

        // --- a wrong key is refused ------------------------------------------
        var wrongClient = new TerraScaleClient(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = fixture.StateUrl },
            new TerraScaleClientOptions { BaseAddress = fixture.StateUrl, ApiKey = "tsk_live_not-the-key" });
        await Assert.ThrowsAsync<TerraScaleApiException>(async () =>
        {
            await wrongClient.Documents.GetAsync(databaseName, "journey", "doc-1");
        });

        // --- the key's database scope is enforced -----------------------------
        // The key was scoped to the journey database: the state database (which
        // holds other accounts' worlds) must refuse it.
        await Assert.ThrowsAsync<TerraScaleApiException>(async () =>
        {
            await keyClient.Documents.PutAsync(
                fixture.StateDatabase, "dashboard_account_worlds", "probe", payload);
        });
    }

    private static async Task<HtmlPage> HtmlPageOfAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        return HtmlPage.Capture((int)response.StatusCode, html);
    }

    private static void AssertRedirect(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.RedirectMethod))
        {
            throw new InvalidOperationException($"Expected a redirect, got {(int)response.StatusCode}");
        }
    }

    /// <summary>Live mode enforces same-origin CSRF on unsafe requests — a
    /// browser always sends Origin with form posts, so every POST does too.</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, IReadOnlyDictionary<string, string> fields)
    {
        using var form = new FormUrlEncodedContent(fields);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        return await client.SendAsync(request);
    }
}
#endif
