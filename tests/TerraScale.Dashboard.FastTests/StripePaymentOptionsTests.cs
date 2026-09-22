using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using TerraScale.Dashboard.Features.Payments.Models;

namespace TerraScale.Dashboard.FastTests;

/// <summary>The sandbox guardrails: test keys only, a webhook secret, and
/// Stripe price ids. A live key in the sandbox or a test key in production
/// fails the boot instead of charging the wrong account.</summary>
public sealed class StripePaymentOptionsTests
{
    private static StripePaymentOptions Load(string environmentName, params (string Key, string Value)[] pairs)
    {
        var values = pairs.ToDictionary(
            pair => pair.Key.Contains(':', StringComparison.Ordinal) || pair.Key.StartsWith("TS_", StringComparison.Ordinal)
                ? pair.Key
                : $"Payments:Stripe:{pair.Key}",
            pair => (string?)pair.Value);
        return StripePaymentOptions.Load(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new HostingEnvironment { EnvironmentName = environmentName });
    }

    private static bool Rejected(string environmentName, params (string Key, string Value)[] pairs)
    {
        try
        {
            Load(environmentName, pairs);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [Test]
    public async Task DisabledWithoutASecretKey()
    {
        var options = Load(Environments.Development);
        await Assert.That(options.Enabled).IsFalse();
        await Assert.That(options.PriceId("starter", "monthly")).IsNull();
    }

    [Test]
    public async Task LoadsSandboxPricesAndAliasesScaleToBusiness()
    {
        var options = Load(
            Environments.Development,
            ("SecretKey", "sk_test_fasttests"),
            ("WebhookSecret", "whsec_fasttests"),
            ("Mode", "test"),
            ("StarterMonthlyPriceId", "price_starter_monthly"),
            ("BusinessAnnualPriceId", "price_business_annual"));

        await Assert.That(options.Enabled).IsTrue();
        await Assert.That(options.IsTestMode).IsTrue();
        await Assert.That(options.PriceId("starter", "monthly")).IsEqualTo("price_starter_monthly");
        await Assert.That(options.PriceId("scale", "annual")).IsEqualTo("price_business_annual");
        await Assert.That(options.PriceId("pro", "monthly")).IsNull();
    }

    [Test]
    public async Task RejectsALiveKeyInTestMode()
    {
        await Assert.That(Rejected(
            Environments.Development,
            ("SecretKey", "sk_live_notallowed"),
            ("WebhookSecret", "whsec_fasttests"),
            ("Mode", "test"))).IsTrue();
    }

    [Test]
    public async Task RejectsATestKeyInLiveMode()
    {
        await Assert.That(Rejected(
            Environments.Development,
            ("SecretKey", "sk_test_fasttests"),
            ("WebhookSecret", "whsec_fasttests"),
            ("Mode", "live"))).IsTrue();
    }

    [Test]
    public async Task RequiresAWebhookSecretWhenEnabled()
    {
        await Assert.That(Rejected(
            Environments.Development,
            ("SecretKey", "sk_test_fasttests"),
            ("Mode", "test"))).IsTrue();
    }

    [Test]
    public async Task RejectsANonStripePriceId()
    {
        await Assert.That(Rejected(
            Environments.Development,
            ("SecretKey", "sk_test_fasttests"),
            ("WebhookSecret", "whsec_fasttests"),
            ("Mode", "test"),
            ("StarterMonthlyPriceId", "prod_not_a_price"))).IsTrue();
    }

    [Test]
    public async Task RefusesTheSandboxInProductionWithoutAcknowledgment()
    {
        await Assert.That(Rejected(
            Environments.Production,
            ("SecretKey", "sk_test_fasttests"),
            ("WebhookSecret", "whsec_fasttests"),
            ("Mode", "test"))).IsTrue();
    }

    [Test]
    public async Task AllowsTheSandboxInTheAcknowledgedProductionDemo()
    {
        var options = Load(
            Environments.Production,
            ("SecretKey", "sk_test_fasttests"),
            ("WebhookSecret", "whsec_fasttests"),
            ("Mode", "test"),
            ("TS_DASHBOARD_PRODUCTION_DEMO", "1"));
        await Assert.That(options.Enabled).IsTrue();
    }
}
