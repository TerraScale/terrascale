using System.Net;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>]
public sealed class NotFoundJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task MissingPagesKeepStatusAndUseTheShellOnlyForSignedInUsers()
    {
        using var client = fixture.CreateClient();
        using var anonymous = await client.GetAsync("/__missing-page");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await anonymous.Content.ReadAsStringAsync()).DoesNotContain("id=\"ts-navigation\"");

        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var signedIn = await client.GetAsync("/__missing-page");
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var html = await signedIn.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("id=\"ts-navigation\"");
        await Assert.That(html).Contains("Back to dashboard");

        foreach (var path in new[] { "/api/__missing-page", "/__missing-page" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain("<html");
        }
    }
}
