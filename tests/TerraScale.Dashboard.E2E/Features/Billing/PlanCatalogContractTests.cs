using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Shared.Accounts;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Features.Billing;

[ClassDataSource<DashboardE2EFixture>]
public sealed class PlanCatalogContractTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task VendoredCatalogMatchesPublishedCatalogExactly()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var published = await client.GetStringAsync("https://terrascale.tech/pricing.json");
        await Assert.That(PlanCatalog.PublishedJson).IsEqualTo(published)
            .Because("Vendor the published pricing.json again whenever the source catalog changes.");
    }

    [Test]
    [Arguments("free", "3BN8V1JQWTB3R3NSZ26SERTHWT", "")]
    [Arguments("starter", "443RCMMF70MNP6S1QMT230NF99", "monthly")]
    [Arguments("pro", "7CNW2H87JE68ATZX224CV2G625", "annual")]
    [Arguments("team", "01K8A00000TEAM000000000000", "monthly")]
    public async Task PublishedCatalogDrivesMcpBillingCardsAndRealizedAllowances(string key, string id, string interval)
    {
        using var client = fixture.CreateClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        if (key != "free")
        {
            using var checkout = await CheckoutAsync(client, csrf, key, interval, "/organization?tab=billing");
            await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var completed = await client.GetAsync(checkout.Headers.Location);
            await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.Found);
            using var callback = await client.GetAsync(completed.Headers.Location);
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }

        using var context = JsonDocument.Parse(await client.GetStringAsync("/api/v1/dashboard/context"));
        var organizationId = context.RootElement.GetProperty("selection").GetProperty("organizationId").GetString();
        var realized = context.RootElement.GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == organizationId);
        var planId = realized.GetProperty("planId").GetString()!;
        await Assert.That(TerraScaleIds.IsValid(planId)).IsTrue();
        await Assert.That(planId).IsEqualTo(id);

        var secret = await HttpJourney.CreatePatAsync(client, "Catalog contract", account.Password);
        var listed = await ToolAsync(client, secret, "list_plans");
        await Assert.That(listed.GetProperty("currentPlanKey").GetString()).IsEqualTo(key);
        if (key != "free") await Assert.That(listed.GetProperty("currentInterval").GetString()).IsEqualTo(interval);
        var paid = listed.GetProperty("plans").EnumerateArray().ToArray();
        using var source = JsonDocument.Parse(PlanCatalog.PublishedJson);
        var available = source.RootElement.GetProperty("plans").EnumerateArray().Where(plan => plan.GetProperty("available").GetBoolean()).ToArray();
        await Assert.That(available.Select(plan => plan.GetProperty("name").GetString()!).ToArray()).IsEquivalentTo(new[] { "Free", "Starter", "Pro", "Team" });
        await Assert.That(paid.Length).IsEqualTo(available.Length - 1);
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/organization?tab=billing"));
        foreach (var published in available)
        {
            var planKey = published.GetProperty("id").GetString()!;
            var limits = published.GetProperty("limits");
            var card = Regex.Match(html, "<article[^>]*aria-labelledby=\"plan-" + planKey + "\"[^>]*>.*?</article>", RegexOptions.Singleline | RegexOptions.CultureInvariant).Value;
            await Assert.That(card.Length).IsGreaterThan(0);
            await Assert.That(card).Contains(published.GetProperty("name").GetString()!);
            await Assert.That(card).Contains("$" + published.GetProperty("priceMonthlyUsd").GetDecimal().ToString("F2", CultureInfo.InvariantCulture));
            // A paid card carries each published interval's price; the interval choice shows one of them.
            var intervals = published.GetProperty("billingIntervals").EnumerateArray().Select(interval => interval.GetString()!).ToArray();
            if (intervals.Contains("year"))
                await Assert.That(card).Contains("$" + published.GetProperty("priceAnnualUsd").GetDecimal().ToString("F2", CultureInfo.InvariantCulture) + "<small>/ year</small>");
            await Assert.That(card).Contains("<small>/ month</small>");
            await Assert.That(card).Contains(Number(limits, "storageGb") + " GB-region storage");
            await Assert.That(card).Contains(limits.GetProperty("operationUnitsPerMonth").GetInt64().ToString("N0", CultureInfo.InvariantCulture) + " operation units/month (4 KiB)");
            await Assert.That(card).Contains(Number(limits, "egressGbPerMonth") + " GB client egress/month");
            foreach (var (property, singular, plural) in new[] { ("projects", "project", "projects"), ("databases", "database", "databases"), ("members", "team member", "team members") })
            {
                var count = limits.GetProperty(property).GetInt32();
                await Assert.That(card).Contains(count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural));
            }
            if (planKey == "free") continue;
            var plan = paid.Single(plan => plan.GetProperty("key").GetString() == planKey);
            await Assert.That(plan.GetProperty("name").GetString()).IsEqualTo(published.GetProperty("name").GetString());
            var monthly = published.GetProperty("priceMonthlyUsd").GetDecimal();
            var annual = published.GetProperty("priceAnnualUsd").GetDecimal();
            await Assert.That(plan.GetProperty("monthlyPriceCents").GetInt64()).IsEqualTo((long)(monthly * 100));
            await Assert.That(plan.GetProperty("annualPriceCents").GetInt64()).IsEqualTo((long)(annual * 100));
            await Assert.That(plan.GetProperty("monthlyPrice").GetString()).IsEqualTo("$" + monthly.ToString("F2", CultureInfo.InvariantCulture) + " / month");
            await Assert.That(plan.GetProperty("annualPrice").GetString()).IsEqualTo("$" + annual.ToString("F2", CultureInfo.InvariantCulture) + " / year");
            await Assert.That(plan.GetProperty("intervals").EnumerateArray().Select(interval => interval.GetString()!).ToArray())
                .IsEquivalentTo(intervals.Select(interval => interval == "year" ? "annual" : "monthly").ToArray());
            await Assert.That(plan.GetProperty("monthlyEgressBytes").GetInt64()).IsEqualTo((long)(limits.GetProperty("egressGbPerMonth").GetDecimal() * 1_000_000_000));
            await Assert.That(plan.GetProperty("trialDays").GetInt32()).IsEqualTo(published.GetProperty("trialDays").ValueKind == JsonValueKind.Null ? 0 : published.GetProperty("trialDays").GetInt32());
            await Assert.That(plan.GetProperty("maxDatabases").GetInt32()).IsEqualTo(limits.GetProperty("databases").GetInt32());
            await Assert.That(plan.GetProperty("maxMembers").GetInt32()).IsEqualTo(limits.GetProperty("members").GetInt32());
            await Assert.That(plan.GetProperty("operationUnitsPerMonth").GetInt64()).IsEqualTo(limits.GetProperty("operationUnitsPerMonth").GetInt64());
            await Assert.That(plan.GetProperty("operationUnitBytes").GetInt32()).IsEqualTo(4096);
            await Assert.That(plan.GetProperty("current").GetBoolean()).IsEqualTo(planKey == key);
        }
        var usage = await ToolAsync(client, secret, "get_usage");
        var current = available.Single(plan => plan.GetProperty("id").GetString() == key);
        var currentLimits = current.GetProperty("limits");
        await Assert.That(usage.GetProperty("planName").GetString()).IsEqualTo(current.GetProperty("name").GetString());
        var allowances = usage.GetProperty("allowances").EnumerateArray().ToArray();
        await Assert.That(allowances.Single(item => item.GetProperty("name").GetString() == "Operation units this month").GetProperty("limit").GetInt64()).IsEqualTo(currentLimits.GetProperty("operationUnitsPerMonth").GetInt64());
        foreach (var (name, property) in new[] { ("Storage across regions", "storageGb"), ("Client egress this month", "egressGbPerMonth") })
            await Assert.That(allowances.Single(item => item.GetProperty("name").GetString() == name).GetProperty("limit").GetInt64()).IsEqualTo((long)(currentLimits.GetProperty(property).GetDecimal() * 1_000_000_000));
        if (key == "free")
        {
            using var first = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "catalog-first", ["region"] = "GRU" });
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var second = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "catalog-second", ["region"] = "GRU" });
            await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(await second.Content.ReadAsStringAsync()).Contains("plan's database limit");
            using var project = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/projects", new() { ["_csrf"] = csrf, ["name"] = "Second project", ["region"] = "GRU", ["regions"] = "GRU" });
            await Assert.That(project.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(await project.Content.ReadAsStringAsync()).Contains("project limit (1 project)");
            using var invitation = await HttpJourney.PostFormAsync(client, "/api/v1/access/invitations", new() { ["_csrf"] = csrf, ["email"] = "catalog-member@example.test", ["role"] = "member" });
            await Assert.That(invitation.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(await invitation.Content.ReadAsStringAsync()).Contains("Free plan allows 1 members including pending invitations.");
        }
    }

    [Test]
    [Arguments("business", "monthly", "Choose an available plan.")]
    [Arguments("enterprise", "monthly", "Choose an available plan.")]
    [Arguments("", "monthly", "Choose an available plan.")]
    [Arguments("free", "monthly", "Free does not require Stripe checkout.")]
    [Arguments("starter", "weekly", "Choose the published billing interval for this plan.")]
    [Arguments("pro", "quarterly", "Choose the published billing interval for this plan.")]
    [Arguments("team", "biennial", "Choose the published billing interval for this plan.")]
    public async Task CheckoutRejectsUnavailablePlansAndUnsupportedCadences(string key, string interval, string error)
    {
        using var client = fixture.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        using var response = await CheckoutAsync(client, csrf, key, interval, "/organization?tab=billing");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains(error);
    }

    [Test]
    public async Task CheckoutReturnsAllowOnlyTheCurrentWorkspaceAndLocalBillingPaths()
    {
        using var client = fixture.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        var org = identity.RootElement.GetProperty("organizationId").GetString()!;
        var project = identity.RootElement.GetProperty("projectId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        foreach (var path in new[] { "/onboarding", "/billing", "/organization?tab=billing",
            "/organization?newOrganization=project&organizationID=" + org,
            "/organization?newOrganization=project&organizationId=" + org + "&projectId=" + project,
            "/organization?tab=billing&organizationId=" + org + "&projectId=" + project })
        {
            using var response = await CheckoutAsync(client, csrf, "pro", "monthly", path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(path);
            await Assert.That(response.Headers.Location!.OriginalString).Contains("/__test/checkout");
            using var canceled = await HttpJourney.PostFormAsync(client, "/billing/checkout/cancel", new() { ["_csrf"] = csrf });
            await Assert.That(canceled.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(canceled.Headers.Location!.OriginalString).IsEqualTo(QueryHelpers.AddQueryString(path, "payment", "canceled"));
        }
        foreach (var path in new[] { "https://example.test/billing", "//example.test/billing", "/billing#other", "/billing?next=//example.test",
            "/organization?newOrganization=project&organizationId=other", "/billing?organizationId=other",
            "/billing?projectId=other", "/billing?projectId=" + project + "&projectId=other",
            "/organization?newOrganization=project&organizationId=" + org + "&tab=other", "/billing?organizationId=" + org + "&organizationID=other",
            "/organization?newOrganization=payment&organizationId=" + org, "/billing\\other", "/billing\n" })
        {
            using var response = await CheckoutAsync(client, csrf, "pro", "monthly", path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity).Because(path);
            await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Choose a valid checkout return path.");
            await Assert.That(response.Headers.Location is null).IsTrue();
        }
    }

    private static string Number(JsonElement limits, string property) => limits.GetProperty(property).GetDecimal().ToString("0.##", CultureInfo.InvariantCulture);
    private static Task<HttpResponseMessage> CheckoutAsync(HttpClient client, string csrf, string plan, string interval, string path) =>
        HttpJourney.PostFormAsync(client, "/billing/checkout", new() { ["_csrf"] = csrf, ["plan"] = plan, ["interval"] = interval, ["return"] = path });
    private static async Task<JsonElement> ToolAsync(HttpClient client, string secret, string name)
    {
        var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments = new { } });
        var result = response.GetProperty("result");
        await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(response.ToString());
        return result.GetProperty("structuredContent");
    }
}
