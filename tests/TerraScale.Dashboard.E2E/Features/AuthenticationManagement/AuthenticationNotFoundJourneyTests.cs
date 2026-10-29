using System.Net;

namespace TerraScale.Dashboard.E2E.Features.AuthenticationManagement;

[ClassDataSource<DashboardRootFixture>]
public sealed class AuthenticationNotFoundJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("/auth")]
    [Arguments("/authentication")]
    public async Task UnknownSectionsKeep404AndOfferBrandedRecoveryWithTheAppropriateShell(string prefix)
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        foreach (var signedIn in new[] { false, true })
        {
            if (signedIn)
            {
                using var login = await HttpJourney.LoginAsync(client);
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }
            using var response = await client.GetAsync(prefix + "/unknown-section");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
            var html = await response.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("id=\"notfound-title\"");
            await Assert.That(html).Contains("Back to dashboard");
            await Assert.That(html.Contains("id=\"ts-navigation\"", StringComparison.Ordinal)).IsEqualTo(signedIn);
        }
    }
}
