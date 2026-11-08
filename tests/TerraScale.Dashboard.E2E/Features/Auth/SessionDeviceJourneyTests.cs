using System.Net;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    public sealed class SessionDeviceJourneyTests
    {
        [Test]
        public async Task SessionsTabShowsTheDeviceEachSessionWasCreatedOn()
        {
            await using var fixture = new DashboardE2EFixture();
            await fixture.InitializeAsync();
            using var firefox = fixture.CreateClient();
            firefox.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0");
            using var login = await HttpJourney.LoginAsync(firefox);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);

            var profile = await firefox.GetStringAsync("/profile?tab=sessions");
            await Assert.That(profile).Contains("Firefox on Linux");
            await Assert.That(profile).Contains("This device");
        }

        [Test]
        public async Task RevokingOneSessionKeepsTheOthersSignedIn()
        {
            await using var fixture = new DashboardE2EFixture();
            await fixture.InitializeAsync();
            using var first = fixture.CreateClient();
            using var second = fixture.CreateClient();
            using var firstLogin = await HttpJourney.LoginAsync(first);
            using var secondLogin = await HttpJourney.LoginAsync(second);
            var profile = await first.GetStringAsync("/profile?tab=sessions");
            var id = System.Text.RegularExpressions.Regex.Match(profile, "/api/v1/dashboard/profile/sessions/([0-9a-f]{64})/revoke").Groups[1].Value;
            await Assert.That(id.Length).IsEqualTo(64);

            var csrf = await HttpJourney.CsrfAsync(first, "/profile");
            using (var revoked = await HttpJourney.PostFormAsync(first, $"/api/v1/dashboard/profile/sessions/{id}/revoke", new() { ["_csrf"] = csrf }))
            {
                await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(revoked.Headers.Location!.OriginalString).IsEqualTo("/profile?tab=sessions&status=session-revoked");
            }

            using (var secondProfile = await second.GetAsync("/profile"))
            {
                await Assert.That(secondProfile.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            }

            using (var firstProfile = await first.GetAsync("/profile"))
            {
                await Assert.That(firstProfile.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
        }
    }
}
