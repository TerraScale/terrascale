using System.Text.Json;
using Stripe;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.Dashboard.Shared.Accounts;
using TerraScale.Dashboard.Shared.Databases;
using TerraScale.Dashboard.Shared.Validation;
using TerraScale.DotNet;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Billing;

public sealed class PlanCatalogTests
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
    public async Task AvailablePlansDrivePricesSharedOperationUnitsAndEveryResourceLimit()
    {
        using var source = JsonDocument.Parse(PlanCatalog.PublishedJson);
        var available = source.RootElement.GetProperty("plans").EnumerateArray().Where(plan => plan.GetProperty("available").GetBoolean()).ToArray();
        await Assert.That(PlanCatalog.Plans.Count).IsEqualTo(available.Length);
        await Assert.That(PlanCatalog.Plans.Select(plan => plan.Name).ToArray()).IsEquivalentTo(new[] { "Hobby", "Starter", "Pro" });
        await Assert.That(PlanCatalog.OperationUnitBytes).IsEqualTo(4096);
        foreach (var published in available)
        {
            var plan = PlanCatalog.Plans.Single(plan => plan.Name == published.GetProperty("name").GetString());
            var limits = published.GetProperty("limits");
            var organization = new OrganizationRecord { PlanId = plan.Id };
            await Assert.That(TerraScaleIds.IsValid(plan.Id)).IsTrue();
            await Assert.That(plan.PriceCents).IsEqualTo((long)(published.GetProperty("priceMonthlyUsd").GetDecimal() * 100));
            await Assert.That(plan.AnnualPriceCents).IsEqualTo((long)(published.GetProperty("priceAnnualUsd").GetDecimal() * 100));
            await Assert.That(plan.TrialDays).IsEqualTo(published.GetProperty("trialDays").ValueKind == JsonValueKind.Null ? 0 : published.GetProperty("trialDays").GetInt32());
            await Assert.That(PlanCatalog.Limits(organization).MonthlyOperations).IsEqualTo(limits.GetProperty("operationUnitsPerMonth").GetInt64());
            await Assert.That(PlanCatalog.Limits(organization).StorageBytes).IsEqualTo((long)(limits.GetProperty("storageGb").GetDecimal() * 1_000_000_000));
            await Assert.That(DatabaseService.DatabaseLimit(organization)).IsEqualTo(limits.GetProperty("databases").GetInt32());
            await Assert.That(OrganizationOperations.ProjectLimit(organization)).IsEqualTo(limits.GetProperty("projects").GetInt32());
            await Assert.That(PlanCatalog.ResourceLimits(organization).Members).IsEqualTo(limits.GetProperty("members").GetInt32());
            await Assert.That(plan.OperationsAllowance).Contains("operation units/month (4 KiB)");
            await Assert.That(plan.StorageAllowance).Contains("GB-region storage");
        }
        await Assert.That(PlanCatalog.For("free").Id).IsEqualTo(OrganizationRecord.FreePlanId);
        await Assert.That(PlanCatalog.ResourceLimits(OrganizationRecord.FreePlanId).Members).IsEqualTo(1);
    }

    [Test]
    [Arguments("business")]
    [Arguments("enterprise")]
    [Arguments("")]
    public async Task UnavailablePlansCannotBeSelected(string key)
    {
        await Assert.That(() => { PlanCatalog.For(key); }).Throws<OnboardingValidationException>();
    }

    [Test]
    public async Task StripePriceMustMatchThePublishedAmountIntervalAndProduct()
    {
        var plan = PlanCatalog.For("pro");
        var price = new Price
        {
            Active = true, Currency = "usd", UnitAmount = 5000,
            Recurring = new() { Interval = "month", IntervalCount = 1 },
            Product = new() { Active = true, Metadata = new() { ["plan"] = "pro" } }
        };
        await Assert.That(StripeBilling.MatchesPrice(price, plan, "monthly", requirePublished: true)).IsTrue();
        price.UnitAmount = 9900;
        await Assert.That(StripeBilling.MatchesPrice(price, plan, "monthly", requirePublished: true)).IsFalse();
        price.UnitAmount = 5000;
        price.Recurring.Interval = "year";
        await Assert.That(StripeBilling.MatchesPrice(price, plan, "monthly", requirePublished: true)).IsFalse();
        price.Recurring.Interval = "month";
        price.Product.Metadata["plan"] = "business";
        await Assert.That(StripeBilling.MatchesPrice(price, plan, "monthly", requirePublished: true)).IsFalse();
        price.Product.Metadata["plan"] = "pro";
        price.Active = false;
        await Assert.That(StripeBilling.MatchesPrice(price, plan, "monthly", requirePublished: true)).IsFalse();
    }
}
