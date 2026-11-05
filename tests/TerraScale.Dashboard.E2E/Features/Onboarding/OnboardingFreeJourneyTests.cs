using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Onboarding
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class OnboardingFreeJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task NewSignupDefaultsToFreePlanWithoutCheckout()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var login = await HttpJourney.LoginAsync(client, returnTo: "/onboarding", signUp: true);
            Check(login.StatusCode == HttpStatusCode.SeeOther, "The hosted sign-up starts a fresh account");

            var csrf = await HttpJourney.CsrfAsync(client, "/onboarding");
            using var savedName = await HttpJourney.PostFormAsync(client, "/onboarding/step", new()
            { ["_csrf"] = csrf, ["step"] = "account-information", ["displayName"] = "Free Default" });
            Check(savedName.StatusCode == HttpStatusCode.SeeOther, "The account step advances to the payment step");

            using var page = await client.GetAsync("/onboarding");
            var html = await page.Content.ReadAsStringAsync();
            var firstCard = html.IndexOf("ts-onboarding-plan\"", StringComparison.Ordinal);
            var freeCard = html.IndexOf("value=\"free\"", StringComparison.Ordinal);
            var paidCard = html.IndexOf("value=\"starter\"", StringComparison.Ordinal);
            Check(firstCard >= 0 && freeCard >= 0 && paidCard >= 0, "The payment step renders the plan cards");
            Check(freeCard < paidCard && html.IndexOf("checked", StringComparison.Ordinal) > freeCard &&
                html.Substring(freeCard, html.IndexOf('>', freeCard) - freeCard).Contains("checked", StringComparison.Ordinal),
                "The Free card is the first card and is selected by default");
            Check(html.Contains("Continue on Free", StringComparison.Ordinal), "The primary button offers Continue on Free for the default selection");

            using var submitted = await HttpJourney.PostFormAsync(client, "/onboarding/step", new()
            { ["_csrf"] = csrf, ["step"] = "payment-method", ["plan"] = "free", ["return"] = "/onboarding" });
            Check(submitted.StatusCode == HttpStatusCode.SeeOther && (submitted.Headers.Location?.ToString() ?? "") is "/onboarding",
                "Submitting the default Free selection continues without a checkout redirect");

            using var after = await client.GetAsync("/onboarding");
            var next = await after.Content.ReadAsStringAsync();
            Check(next.Contains("Create your first project", StringComparison.Ordinal), "The Free submission reaches the project step");
            using var json = await client.GetAsync("/api/v1/auth/me");
            using var parsed = JsonDocument.Parse(await json.Content.ReadAsStringAsync());
            Check(!parsed.RootElement.GetProperty("onboardingCompleted").GetBoolean(), "Setup is unfinished until the project step");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
