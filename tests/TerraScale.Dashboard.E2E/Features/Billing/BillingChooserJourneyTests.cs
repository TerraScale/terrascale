using System.Net;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Billing;

[ClassDataSource<DashboardE2EFixture>]
public sealed class BillingChooserJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    [Arguments("annual")]
    [Arguments("monthly")]
    public async Task FreeBillingShowsPublishedCadencesRegardlessOfRequestedIntervalAndAnnouncesCanceledCheckout(string interval)
    {
        using var client = fixture.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var response = await client.GetAsync("/organization?tab=billing&interval=" + interval + "&payment=canceled");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        var chooser = Regex.Match(html, "<dialog id=\"change-billing-plan\".*?</dialog>", RegexOptions.Singleline | RegexOptions.CultureInvariant).Value;
        await Assert.That(chooser.Length).IsGreaterThan(0);
        await Assert.That(chooser.Contains("name=\"interval\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(chooser).Contains("$30.00 billed yearly");
        await Assert.That(chooser).Contains("$20.00 / month · Billed monthly");
        await Assert.That(chooser).Contains("$50.00 / month · Billed monthly");
        await Assert.That(html.Contains("Save up to", StringComparison.Ordinal)).IsFalse();
        await Assert.That(html).Contains("Checkout canceled");
        await Assert.That(Regex.Matches(chooser, "name=\"plan\"").Count).IsEqualTo(3);
        await Assert.That(chooser).Contains("operation units/month (4 KiB)");
        await Assert.That(chooser.Contains("writes/month", StringComparison.Ordinal)).IsFalse();
        await Assert.That(chooser.Contains("reads/month", StringComparison.Ordinal)).IsFalse();
        await Assert.That(html).Contains("Free plan. No subscription or payment method is required; no Stripe charges.");
        await Assert.That(Regex.Matches(html, "class=\"ts-plan ts-plan-current\"").Count).IsEqualTo(1);
        await Assert.That(PlanCard(html, "free")).Contains("aria-current=\"true\"");
        await Assert.That(PlanCard(html, "free").Contains("<form", StringComparison.Ordinal)).IsFalse();
        await AssertComparisonAsync(html);
        await Assert.That(Regex.IsMatch(chooser, "<input[^>]*name=\"plan\"[^>]*checked", RegexOptions.CultureInvariant)).IsFalse().Because("An unsubscribed organization has no current plan to preselect.");
    }

    [Test]
    [Arguments("starter", "monthly")]
    [Arguments("pro", "annual")]
    [Arguments("team", "annual")]
    public async Task CheckoutRejectsUnpublishedPlanCadences(string plan, string interval)
    {
        using var client = fixture.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        using var response = await HttpJourney.PostFormAsync(client, "/billing/checkout", new()
        { ["_csrf"] = csrf, ["plan"] = plan, ["interval"] = interval, ["return"] = "/organization?tab=billing" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Choose the published billing interval for this plan.");
    }

    [Test]
    public async Task OrganizationWizardCheckoutPreservesItsCanonicalScopeThroughReturnAndRefresh()
    {
        using var client = fixture.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        using var creation = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = "Scoped billing " + Guid.NewGuid().ToString("N")[..12] });
        await Assert.That(creation.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var wizardUri = new Uri(client.BaseAddress!, creation.Headers.Location!.OriginalString);
        var organizationId = QueryHelpers.ParseQuery(wizardUri.Query)["organizationId"].ToString();
        await Assert.That(organizationId.Length).IsEqualTo(26);
        using var wizard = await client.GetAsync(wizardUri);
        await Assert.That(wizard.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var wizardBody = await wizard.Content.ReadAsStringAsync();
        await Assert.That(wizardBody).Contains("$30.00 billed yearly");
        using var checkout = await HttpJourney.PostFormAsync(client,
            "/api/v1/dashboard/organization/new-organization?organizationId=" + Uri.EscapeDataString(organizationId), new()
            { ["_csrf"] = csrf, ["step"] = "payment-method", ["organizationID"] = organizationId, ["plan"] = "starter" });
        await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(checkout.Headers.Location!.OriginalString).Contains("/__test/checkout");
        using var completed = await client.GetAsync(checkout.Headers.Location);
        await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.Found);
        using var callback = await client.GetAsync(completed.Headers.Location);
        await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var returnUri = new Uri(client.BaseAddress!, callback.Headers.Location!.OriginalString);
        var query = QueryHelpers.ParseQuery(returnUri.Query);
        await Assert.That(query["organizationId"].ToString()).IsEqualTo(organizationId);
        await Assert.That(query["newOrganization"].ToString()).IsEqualTo("project");
        await Assert.That(query["payment"].ToString()).IsEqualTo("success");
        using var returned = await client.GetAsync(returnUri);
        await Assert.That(returned.StatusCode).IsEqualTo(HttpStatusCode.OK);
        // workspace.js removes the legacy organizationID and keeps this canonical organizationId on refresh.
        using var refreshed = await client.GetAsync(returnUri);
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await refreshed.Content.ReadAsStringAsync()).Contains("Create the first project");
    }

    [Test]
    public async Task AnnualSubscriptionOpensBillingAndChooserOnAnnualWithItsPlanCurrent()
    {
        using var client = fixture.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await BillingPlanJourneyTests.SignUpPaidAsync(client, "annual-" + suffix + "@example.test", "E2E-annual-" + suffix, suffix, "annual");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/organization?tab=billing"));
        var chooser = Regex.Match(html, "<dialog id=\"change-billing-plan\".*?</dialog>", RegexOptions.Singleline | RegexOptions.CultureInvariant).Value;
        await Assert.That(Regex.IsMatch(chooser, "<input[^>]*name=\"plan\"[^>]*value=\"starter\"[^>]*checked", RegexOptions.CultureInvariant)).IsTrue();
        await Assert.That(Regex.Matches(html, "class=\"ts-plan ts-plan-current\"").Count).IsEqualTo(1);
        await Assert.That(html).Contains("aria-labelledby=\"plan-starter\" aria-current=\"true\"");
        await AssertComparisonAsync(html);
        var free = PlanCard(html, "free");
        await Assert.That(free).Contains("method=\"get\" action=\"/organization\"");
        await Assert.That(free).Contains("name=\"plan\" value=\"free\"");
        await Assert.That(free.Contains("/billing/checkout", StringComparison.Ordinal)).IsFalse();
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        using var creation = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = "Business comparison " + suffix });
        await Assert.That(creation.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var wizardUri = new Uri(client.BaseAddress!, creation.Headers.Location!.OriginalString);
        var organizationId = QueryHelpers.ParseQuery(wizardUri.Query)["organizationId"].ToString();
        await Assert.That(organizationId.Length).IsEqualTo(26);
        using var billing = await client.GetAsync("/organization?tab=billing&organizationId=" + Uri.EscapeDataString(organizationId));
        await Assert.That(billing.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var business = WebUtility.HtmlDecode(await billing.Content.ReadAsStringAsync());
        await AssertComparisonAsync(business);
        var businessFree = PlanCard(business, "free");
        await Assert.That(businessFree.Contains("<form", StringComparison.Ordinal)).IsFalse();
        await Assert.That(businessFree).Contains("Available for Personal workspaces only.");
        await Assert.That(business).Contains("Payment required");
        await Assert.That(Regex.IsMatch(business, "<input[^>]*name=\"plan\"[^>]*value=\"free\"", RegexOptions.CultureInvariant)).IsFalse();
    }

    private static string PlanCard(string html, string key) => Regex.Match(html,
        "<article[^>]*aria-labelledby=\"plan-" + key + "\"[^>]*>.*?</article>", RegexOptions.Singleline | RegexOptions.CultureInvariant).Value;

    private static async Task AssertComparisonAsync(string html)
    {
        using var published = JsonDocument.Parse(PlanCatalog.PublishedJson);
        var plans = published.RootElement.GetProperty("plans").EnumerateArray().ToArray();
        await Assert.That(Regex.Matches(html, "<article[^>]*aria-labelledby=\"plan-", RegexOptions.CultureInvariant).Count).IsEqualTo(5);
        foreach (var plan in plans)
        {
            var card = PlanCard(html, plan.GetProperty("id").GetString()!);
            await Assert.That(card.Length).IsGreaterThan(0);
            await Assert.That(card).Contains(plan.GetProperty("name").GetString()!);
            await Assert.That(card).Contains("$" + plan.GetProperty("priceMonthlyUsd").GetDecimal().ToString("F2", CultureInfo.InvariantCulture));
            var limits = plan.GetProperty("limits");
            await Assert.That(card).Contains(limits.GetProperty("storageGb").GetDecimal().ToString("0.##", CultureInfo.InvariantCulture) + " GB-region storage");
            await Assert.That(card).Contains(limits.GetProperty("operationUnitsPerMonth").GetInt64().ToString("N0", CultureInfo.InvariantCulture) + " operation units/month (4 KiB)");
            await Assert.That(card).Contains(limits.GetProperty("egressGbPerMonth").GetDecimal().ToString("0.##", CultureInfo.InvariantCulture) + " GB client egress/month");
            foreach (var (property, singular, plural) in new[] { ("projects", "project", "projects"), ("databases", "database", "databases"), ("members", "team member", "team members") })
            {
                var limit = limits.GetProperty(property);
                await Assert.That(card).Contains(limit.ValueKind == JsonValueKind.Null ? plural + ": contact sales" : limit.GetInt32().ToString(CultureInfo.InvariantCulture) + " " + (limit.GetInt32() == 1 ? singular : plural));
            }
        }
        var enterprise = PlanCard(html, "enterprise");
        await Assert.That(enterprise).Contains("$999 per month support plus usage; contact sales.");
        await Assert.That(enterprise).Contains("href=\"https://terrascale.tech/contact\"");
        await Assert.That(enterprise.Contains("<form", StringComparison.Ordinal) || enterprise.Contains("<input", StringComparison.Ordinal)).IsFalse();
    }
}
