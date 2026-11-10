using System.Net;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    [ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
    public sealed class SessionDeviceJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task SessionsTabShowsTheDeviceEachSessionWasCreatedOn()
        {
            using var firefox = fixture.CreateClient();
            firefox.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0");
            await DatabaseJourney.NativeWorkspaceAsync(firefox);

            var profile = await firefox.GetStringAsync("/profile?tab=sessions");
            await Assert.That(profile).Contains("Firefox on Linux");
            await Assert.That(profile).Contains("This device");
        }

        [Test]
        public async Task RevokingOneSessionKeepsTheOthersSignedIn()
        {
            using var first = fixture.CreateClient();
            using var second = fixture.CreateClient();
            var account = await DatabaseJourney.NativeWorkspaceAsync(first);
            using var secondLogin = await HttpJourney.LoginAsync(second, account.Email, account.Password);
            await Assert.That(secondLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var profile = await first.GetStringAsync("/profile?tab=sessions");
            var id = System.Text.RegularExpressions.Regex.Match(profile, "/api/v1/dashboard/profile/sessions/([0-9A-HJKMNP-TV-Z]{26})/revoke").Groups[1].Value;
            await Assert.That(id.Length).IsEqualTo(26);

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
