using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Billing;

[ClassDataSource<DashboardE2EFixture>]
public sealed class BillingChooserJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    [Arguments("annual")]
    [Arguments("monthly")]
    public async Task FreeBillingPreservesRequestedIntervalAndAnnouncesCanceledCheckoutWithoutPaidCurrentBadge(string interval)
    {
        using var client = fixture.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var response = await client.GetAsync("/organization?tab=billing&interval=" + interval + "&payment=canceled");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        var chooser = Regex.Match(html, "<dialog id=\"change-billing-plan\".*?</dialog>", RegexOptions.Singleline | RegexOptions.CultureInvariant).Value;
        await Assert.That(chooser.Length).IsGreaterThan(0);
        foreach (var value in new[] { "monthly", "annual" })
        {
            var input = Regex.Match(chooser, "<input[^>]*name=\"interval\"[^>]*value=\"" + value + "\"[^>]*>", RegexOptions.CultureInvariant).Value;
            await Assert.That(input.Length).IsGreaterThan(0);
            await Assert.That(input.Contains("checked", StringComparison.Ordinal)).IsEqualTo(value == interval);
        }
        await Assert.That(html).Contains("Checkout canceled");
        await Assert.That(html).Contains("Free plan. No subscription or payment method is required; no Stripe charges.");
        await Assert.That(html.Contains("ts-plan-current", StringComparison.Ordinal)).IsFalse();
        await Assert.That(Regex.IsMatch(chooser, "<input[^>]*name=\"plan\"[^>]*checked", RegexOptions.CultureInvariant)).IsFalse().Because("An unsubscribed organization has no current plan to preselect.");
    }

    [Test]
    public async Task AnnualSubscriptionOpensBillingAndChooserOnAnnualWithItsPlanCurrent()
    {
        using var client = fixture.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await BillingPlanJourneyTests.SignUpPaidAsync(client, "annual-" + suffix + "@example.test", "E2E-annual-" + suffix, suffix, "annual");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/organization?tab=billing"));
        var chooser = Regex.Match(html, "<dialog id=\"change-billing-plan\".*?</dialog>", RegexOptions.Singleline | RegexOptions.CultureInvariant).Value;
        await Assert.That(Regex.IsMatch(chooser, "<input[^>]*name=\"interval\"[^>]*value=\"annual\"[^>]*checked", RegexOptions.CultureInvariant)).IsTrue();
        await Assert.That(Regex.IsMatch(chooser, "<input[^>]*name=\"plan\"[^>]*value=\"starter\"[^>]*checked", RegexOptions.CultureInvariant)).IsTrue();
        await Assert.That(html).Contains("interval=annual\" aria-current=\"page\"");
        await Assert.That(Regex.Matches(html, "class=\"ts-plan ts-plan-current\"").Count).IsEqualTo(1);
        await Assert.That(html).Contains("aria-labelledby=\"plan-starter\" aria-current=\"true\"");
    }
}
