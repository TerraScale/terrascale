using System.Net;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>]
public sealed class NotFoundJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task MissingPagesKeepStatusAndUseTheShellOnlyForSignedInUsers()
    {
        using var client = fixture.CreateClient();
        using (var unauthorized = await Json(client, HttpMethod.Post, "/api/v1/dashboard/databases"))
        {
            await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(unauthorized.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
            await Assert.That(await unauthorized.Content.ReadAsStringAsync()).Contains("\"detail\":");
        }
        using var anonymous = await client.GetAsync("/__missing-page");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await anonymous.Content.ReadAsStringAsync()).DoesNotContain("id=\"ts-navigation\"");

        await Assert.That(await anonymous.Content.ReadAsStringAsync()).Contains(anonymous.Headers.GetValues("X-Request-ID").Single());

        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var signedIn = await client.GetAsync("/__missing-page");
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var html = await signedIn.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("id=\"ts-navigation\"");
        await Assert.That(html).Contains("Back to dashboard");
        await Assert.That(html).Contains(signedIn.Headers.GetValues("X-Request-ID").Single());

        foreach (var path in new[] { "/api/__missing-page", "/__missing-page", "/databases/__missing/documents/orders" })
        {
            using var response = await Json(client, HttpMethod.Get, path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
            await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain("<html");
        }

        // A browser opening a database it cannot see gets the HTML not-found page, not raw problem JSON.
        foreach (var path in new[] { "/databases/__missing", "/databases/__missing/documents/orders" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
            await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Back to dashboard");
        }
    }

    private static Task<HttpResponseMessage> Json(HttpClient client, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.ParseAdd("application/json");
        return client.SendAsync(request);
    }
}
