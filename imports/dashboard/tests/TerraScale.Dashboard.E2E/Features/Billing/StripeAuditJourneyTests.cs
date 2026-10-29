using System.Text.Json;
using System.Diagnostics;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Billing;

[ClassDataSource<DashboardRootFixture>]
public sealed class StripeAuditJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task ProductionExecutableRejectsAuditConfigurationSwitchesBeforeIdentityRead()
    {
        var project = Path.Combine(fixture.Root, "src", "TerraScale.Dashboard");
        var executable = Path.Combine(project, "bin", "Release", "net10.0", "TerraScale.Dashboard.dll");
        Check(File.Exists(executable), "The production dashboard assembly must be built with the E2E executable");
        var start = new ProcessStartInfo("dotnet")
        { WorkingDirectory = project, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(executable);
        start.ArgumentList.Add("audit-stripe");
        start.ArgumentList.Add("--online");
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("STRIPE_", StringComparison.Ordinal) ||
                     name.Equals("DASHBOARD_PUBLIC_ORIGIN", StringComparison.Ordinal) || name.Contains("proxy", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        using var process = Process.Start(start) ?? throw new IOException("Could not launch the production dashboard CLI.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            Check(process.ExitCode == 1 && (await output).Length == 0, "Production CLI dispatch rejects unsupported switches without starting an online audit");
            Check((await error).Contains("accepts no options", StringComparison.Ordinal), "The production executable reports only the fixed audit command contract");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token);
        }
    }

    [Test]
    [Arguments("test", "", false)]
    [Arguments("live", "", false)]
    [Arguments("test", "", true)]
    [Arguments("live", "", true)]
    public async Task StripeNetAuditProcessReadsCompleteInventoryThroughTrustedTls(string mode, string fault, bool restricted)
    {
        await using var scenario = new StripeAuditScenario(fixture.Root, mode, fault, restricted);
        var result = await scenario.RunAsync();
        Safe(result);
        Check(result.ExitCode == 0 && result.Error == "", $"The production audit command reads the Stripe account selected by the delivered identity key (exit {result.ExitCode}; stdout: {result.Output}; stderr: {result.Error})");
        using var report = JsonDocument.Parse(result.Output);
        Check(report.RootElement.GetProperty("passed").GetBoolean(), "All read-only Stripe.net V1 inventory checks pass through actual Stripe HTTP responses");
        var details = report.RootElement.GetProperty("details");
        Check(details.GetProperty("mode").GetString() == mode && details.GetProperty("expected_webhook_api_version").GetString() == scenario.ApiVersion,
            "The process reports the key-derived mode and pinned Stripe.net API version");
        Check(details.GetProperty("matching_webhook_endpoints").GetInt32() == 1 &&
            details.GetProperty("recent_invoice_created_with_pending_webhooks").GetInt32() == 1 &&
            details.GetProperty("customers_with_multiple_current_subscriptions").GetInt32() == 1,
            "The process reports webhook and paginated subscription aggregates without customer identities");
        var counts = details.GetProperty("subscription_status_counts");
        Check(new[] { "active", "past_due", "trialing", "canceled" }.All(status => counts.GetProperty(status).GetInt32() == 1),
            "Both real SDK subscription pages contribute to the inventory");
        Protocol(scenario, 8);
        Check(scenario.Requests.Count(request => request.Target.StartsWith("/v1/prices?", StringComparison.Ordinal)) == 2 &&
            scenario.Requests.Count(request => request.Target == "/v1/products/prod_team_monthly") == 1 &&
            scenario.Requests.Count(request => request.Target.StartsWith("/v1/subscriptions?", StringComparison.Ordinal)) == 2,
            "The SDK discovers and paginates six published plan prices, resolves an unexpanded product, and follows subscription pagination");
    }

    [Test]
    public async Task UnsupportedAuditOptionsAndInvalidIdentityCannotContactStripe()
    {
        await using var scenario = new StripeAuditScenario(fixture.Root);
        var unsupported = await scenario.RunAsync(["--online"]);
        Safe(unsupported);
        Check(unsupported.ExitCode == 1 && unsupported.Output.Length == 0, $"The audit has no online/offline or mode switches (exit {unsupported.ExitCode}; stdout: {unsupported.Output}; stderr: {unsupported.Error})");
        Protocol(scenario, 0);

        foreach (var key in new[] { "sk_unknown_PRIVATE", "rk_unknown_PRIVATE", "pk_test_PRIVATE" })
        {
            var invalid = await scenario.RunAsync(identity: scenario.Identity(key));
            Safe(invalid);
            Check(invalid.ExitCode == 2 && invalid.Output.Length == 0, $"An unrecognized or public Stripe key fails closed (exit {invalid.ExitCode}; stdout: {invalid.Output}; stderr: {invalid.Error})");
            Protocol(scenario, 0);
        }

        var completeIdentity = scenario.Identity();
        var incomplete = completeIdentity with { Stripe = null! };
        var unavailable = await scenario.RunAsync(identity: incomplete);
        Safe(unavailable);
        Check(unavailable.ExitCode == 2 && unavailable.Output.Length == 0, $"An incomplete delivered identity fails closed (exit {unavailable.ExitCode}; stdout: {unavailable.Output}; stderr: {unavailable.Error})");
        Protocol(scenario, 0);
    }

    [Test]
    [Arguments("price-mode", "starter_annual")]
    [Arguments("product-mode", "starter_annual")]
    [Arguments("webhook-mode", "webhook_endpoint")]
    [Arguments("portal-mode", "portal_payment_and_invoices")]
    [Arguments("event-mode", "invoice_event_mode")]
    [Arguments("subscription-mode", "subscription_mode")]
    [Arguments("duplicate-webhooks", "webhook_endpoint")]
    [Arguments("wrong-webhook", "webhook_endpoint")]
    [Arguments("unsafe-default-portal", "portal_payment_and_invoices")]
    [Arguments("portal-tax-fields", "portal_payment_and_invoices")]
    public async Task ActualStripeResponsesRejectForeignModeAndUnsafeConfigurations(string fault, string failedCheck)
    {
        await using var scenario = new StripeAuditScenario(fixture.Root, fault: fault);
        var result = await scenario.RunAsync();
        Safe(result);
        Check(result.ExitCode == 1 && result.Error.Length == 0, $"Unsafe producer facts produce a failed audit report (exit {result.ExitCode}; stdout: {result.Output}; stderr: {result.Error})");
        using var report = JsonDocument.Parse(result.Output);
        Check(!report.RootElement.GetProperty("passed").GetBoolean() && !report.RootElement.GetProperty("checks").GetProperty(failedCheck).GetBoolean(),
            "The audit identifies the foreign-mode object, duplicate/wrong endpoint or unsafe default portal");
        Protocol(scenario, 8);
        if (fault == "duplicate-webhooks")
            Check(report.RootElement.GetProperty("details").GetProperty("matching_webhook_endpoints").GetInt32() == 2,
                "Two individually valid webhook routes remain a duplicate configuration");
        if (fault == "unsafe-default-portal")
            Check(scenario.Requests.Any(request => request.Target.StartsWith("/v1/billing_portal/configurations?", StringComparison.Ordinal)),
                "A safe non-default configuration cannot hide the actual unsafe default returned over HTTP");
    }

    [Test]
    [Arguments("missing-price", "starter_annual")]
    [Arguments("duplicate-price", "pro_monthly")]
    [Arguments("inactive-price", "starter_annual")]
    [Arguments("inactive-product", "starter_annual")]
    public async Task ActualPublishedPriceRowsRejectMissingDuplicateOrInactivePlanFacts(string fault, string failedCheck)
    {
        await using var scenario = new StripeAuditScenario(fixture.Root, fault: fault);
        var result = await scenario.RunAsync();
        Safe(result);
        Check(result.ExitCode == 1 && result.Error.Length == 0, $"A bad published price inventory fails the live SDK audit (exit {result.ExitCode}; stdout: {result.Output}; stderr: {result.Error})");
        using var report = JsonDocument.Parse(result.Output);
        Check(!report.RootElement.GetProperty("passed").GetBoolean() &&
            !report.RootElement.GetProperty("checks").GetProperty(failedCheck).GetBoolean(),
            "The audit rejects the plan price whose provider facts are missing, ambiguous, or inactive");
        var facts = scenario.PriceFacts;
        Check(facts.Count == (fault == "missing-price" ? 5 : fault == "duplicate-price" ? 7 : 6),
            "The TLS producer returned the intended actual SDK price records");
        if (fault == "missing-price")
            Check(facts.All(fact => !fact.StartsWith("price_starter_annual|", StringComparison.Ordinal)), "The paid starter annual price is actually absent from provider responses");
        if (fault == "duplicate-price")
            Check(facts.Count(fact => fact.Contains("|pro|monthly|2000|True|True", StringComparison.Ordinal)) == 2,
                "Two independently identified active provider prices match the same paid plan and interval");
        if (fault == "inactive-price")
            Check(facts.Any(fact => fact.StartsWith("price_starter_annual|", StringComparison.Ordinal) && fact.EndsWith("|False|True", StringComparison.Ordinal)),
                "The provider response contains the matching price marked inactive");
        if (fault == "inactive-product")
            Check(facts.Any(fact => fact.StartsWith("price_starter_annual|", StringComparison.Ordinal) && fact.EndsWith("|True|False", StringComparison.Ordinal)),
                "The provider response contains the matching active price with an inactive product");
        Protocol(scenario, fault == "duplicate-price" ? 9 : 8);
    }

    [Test]
    [Arguments("http-error")]
    [Arguments("malformed-json")]
    public async Task HttpAndMalformedResponsesFailWithoutPrintingProviderSecrets(string fault)
    {
        await using var scenario = new StripeAuditScenario(fixture.Root, fault: fault);
        var result = await scenario.RunAsync();
        Safe(result);
        Check(result.ExitCode == 2 && result.Output.Length == 0, $"Provider failures produce only a sanitized error on stderr (exit {result.ExitCode}; stdout: {result.Output}; stderr: {result.Error})");
        using var report = JsonDocument.Parse(result.Error);
        Check(!report.RootElement.GetProperty("passed").GetBoolean() && report.RootElement.EnumerateObject().Count() == 2,
            "Only a safe exception type is reported for HTTP or malformed-body failures");
        Protocol(scenario, fault == "http-error" ? 3 : 1);
    }

    private static void Safe(StripeAuditScenario.Result result) =>
        Check(!(result.Output + result.Error).Contains("PRIVATE", StringComparison.Ordinal),
            "The real process output never includes credentials, webhook secrets or customer identities");

    private static void Protocol(StripeAuditScenario scenario, int requests)
    {
        Check(scenario.Failures.Count == 0, "The TLS producer accepts the SDK transport contract: " + string.Join(',', scenario.Failures));
        Check(scenario.Requests.Count == requests && scenario.Connects == requests,
            "Each Stripe SDK GET opens one trusted owning-account TLS connection; rejected local inputs open none");
        Check(scenario.Requests.All(request => request.Method == "GET" && request.Headers["Host"] == "api.stripe.com" &&
            request.Headers["Authorization"] == "Bearer " + scenario.Secret && request.Headers["Stripe-Version"] == scenario.ApiVersion),
            "Every SDK request is read-only and uses the owning Stripe authority, key and pinned API version");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
