using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Billing;

namespace TerraScale.Dashboard.E2E.Features.Usage
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class SpendingFormJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task InternalReportOnlyFormPersistsExactCentsAndRejectsMalformedOrLegacyControls()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            await InternalBillingAuthenticationJourneyTests.SignInAsync(browser, producer, "admin");
            using var seeded = await producer.PostAsJsonAsync("/__test/control", new { command = "internal-seed", count = 1, paymentMethodValidated = false });
            seeded.EnsureSuccessStatusCode();
            using var targets = JsonDocument.Parse(await seeded.Content.ReadAsStringAsync());
            var organization = targets.RootElement.GetProperty("targets")[0].GetProperty("organizationId").GetString()!;
            var arguments = InternalBillingAuthenticationJourneyTests.CommandArguments("configure-internal-billing", organization);
            var configured = await InternalBillingAuthenticationJourneyTests.RunApprovedAsync(scenario, producer, arguments);
            await Assert.That(configured.ExitCode).IsEqualTo(0).Because(configured.Error);
            var csrf = await HttpJourney.CsrfAsync(browser, "/profile");
            long expectedCap = 0;

            foreach (var (value, expected) in new[] { ("", 0L), ("0", 0L), ("12.50", 1250L), ("12.5", 1250L), ("92233720368547758.07", long.MaxValue) })
            {
                expectedCap = expected;
                await SubmitAsync([new("_csrf", csrf), new("spendCap", value)], true);
            }
            foreach (var value in new[] { "-1", "1.001", "1e2", "1,000", "92233720368547758.08" })
            {
                await SubmitAsync([new("_csrf", csrf), new("spendCap", value)], false);
            }

            foreach (var key in new[] { "paidExcess", "initial_paidExcess", "projCap_01JA0000000000000000000001", "dbWrite_01JA0000000000000000000002" })
            {
                await SubmitAsync([new("_csrf", csrf), new("spendCap", "120.00"), new(key, "1")], false);
            }

            await SubmitAsync([new("_csrf", csrf)], false);
            await SubmitAsync([new("_csrf", csrf), new("spendCap", "1"), new("spendCap", "2")], false);

            using var observed = JsonDocument.Parse(await producer.GetStringAsync("/__test/observations"));
            await Assert.That(observed.RootElement.GetProperty("billing").GetProperty("stripeRequests").GetArrayLength()).IsEqualTo(0)
                .Because("The report-only form never creates payment resources.");

            async Task SubmitAsync(KeyValuePair<string, string>[] fields, bool accepted)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/usage/internal-spending-cap")
                { Content = new FormUrlEncodedContent(fields) };
                request.Headers.Add("Origin", browser.BaseAddress!.GetLeftPart(UriPartial.Authority));
                using var response = await browser.SendAsync(request);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo("/usage?tab=settings&saved=" + (accepted ? "ok" : "invalid"));
                using var snapshot = JsonDocument.Parse(await producer.GetStringAsync("/__test/observations"));
                var stored = snapshot.RootElement.GetProperty("billing").GetProperty("organizations").EnumerateArray()
                    .Single(row => row.GetProperty("id").GetString() == organization).GetProperty("document");
                await Assert.That(stored.GetProperty("SpendCapCents").GetInt64()).IsEqualTo(expectedCap)
                    .Because(accepted ? "Accepted dollar amounts persist their exact cents." : "Rejected controls preserve the previous organization cap.");
            }
        }
    }
}
