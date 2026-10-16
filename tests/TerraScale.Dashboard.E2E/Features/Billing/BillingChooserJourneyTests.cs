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
    }
}
