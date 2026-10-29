using System.Text.Json;
using Stripe;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Billing;

/// <summary>Read-only Stripe configuration audit using the official Stripe.net V1 client.</summary>
public sealed class StripeAuditCommand(IDashboardIdentity identity,
    Func<string, StripeClient>? clientFactory = null, TextWriter? output = null, TextWriter? error = null)
{
    private readonly IDashboardIdentity identity = identity;
    private readonly Func<string, StripeClient> clientFactory = clientFactory ?? (key => new StripeClient(key,
            httpClient: new SystemNetHttpClient(new HttpClient(), enableTelemetry: false)));
    private readonly TextWriter output = output ?? Console.Out;
    private readonly TextWriter error = error ?? Console.Error;

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length != 0)
        {
            await error.WriteLineAsync("The audit-stripe command accepts no options.");
            return 1;
        }

        try
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            credentials.Validate();
            var key = credentials.Stripe!.SecretKey;
            var live = credentials.Stripe.IsLive;

            var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["key_matches_mode"] = true,
                ["webhook_secret_present"] = credentials.Stripe.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal),
                ["public_https_origin"] = Uri.TryCreate(StripeBilling.Origin, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0,
            };
            var details = new Dictionary<string, object?>
            {
                ["mode"] = live ? "live" : "test",
                ["expected_webhook_api_version"] = StripeConfiguration.ApiVersion
            };
            var client = clientFactory(key);
            await AuditPricesAsync(client, live, checks, cancellationToken);
            checks["published_prices_valid"] = PlanCatalog.Plans.Where(plan => plan.PriceCents > 0)
                .SelectMany(plan => plan.CheckoutIntervals.Select(interval => plan.Key + "_" + interval)).All(name => checks.GetValueOrDefault(name));
            var webhookEndpoints = new List<WebhookEndpoint>();
            await foreach (var endpoint in client.V1.WebhookEndpoints.ListAutoPagingAsync(new() { Limit = 100 }, cancellationToken: cancellationToken))
                webhookEndpoints.Add(endpoint);
            var expectedUrls = new HashSet<string>(StringComparer.Ordinal)
            {
                StripeBilling.Origin + "/api/v1/billing/stripe/webhook",
                StripeBilling.Origin + "/api/auth/stripe/webhook"
            };
            var matching = webhookEndpoints.Where(endpoint => expectedUrls.Contains(endpoint.Url) && endpoint.Livemode == live).ToArray();
            details["enabled_invoice_created_endpoints"] = webhookEndpoints.Count(endpoint => endpoint.Status == "enabled" && endpoint.Livemode == live &&
                (endpoint.EnabledEvents.Contains("invoice.created", StringComparer.Ordinal) || endpoint.EnabledEvents.Contains("*", StringComparer.Ordinal)));
            details["matching_webhook_endpoints"] = matching.Length;
            checks["webhook_endpoint"] = matching.Length == 1 && matching[0].Status == "enabled";
            checks["webhook_api_version"] = matching.Length > 0 && !string.IsNullOrWhiteSpace(StripeConfiguration.ApiVersion) && matching[0].ApiVersion == StripeConfiguration.ApiVersion;
            var events = matching.FirstOrDefault()?.EnabledEvents ?? [];
            checks["webhook_events"] = matching.Length > 0 && (events.Contains("*", StringComparer.Ordinal) || StripeBilling.WebhookEvents.IsSubsetOf(events));
            if (matching.Length > 0 && !events.Contains("*", StringComparer.Ordinal))
                details["missing_webhook_events"] = StripeBilling.WebhookEvents.Except(events, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            details["delivery_verification"] = "Check endpoint delivery attempts in Stripe Workbench; the public API event list cannot prove per-endpoint HTTP success.";

            var recentEvents = new List<Event>();
            await foreach (var stripeEvent in client.V1.Events.ListAutoPagingAsync(new() { Type = "invoice.created", Limit = 10 }, cancellationToken: cancellationToken))
            {
                recentEvents.Add(stripeEvent);
                if (recentEvents.Count == 10) break;
            }
            checks["invoice_event_mode"] = recentEvents.All(stripeEvent => stripeEvent.Livemode == live);
            details["recent_invoice_created_with_pending_webhooks"] = recentEvents.Count(stripeEvent => stripeEvent.PendingWebhooks > 0);

            var portals = new List<Stripe.BillingPortal.Configuration>();
            await foreach (var portal in client.V1.BillingPortal.Configurations.ListAutoPagingAsync(new() { Limit = 100 }, cancellationToken: cancellationToken))
                portals.Add(portal);
            checks["portal_payment_and_invoices"] = portals.Any(portal => portal.IsDefault && portal.Active && portal.Livemode == live &&
                portal.Features?.PaymentMethodUpdate?.Enabled == true && portal.Features.InvoiceHistory?.Enabled == true &&
                portal.Features.SubscriptionCancel?.Enabled == true && portal.Features.SubscriptionUpdate?.Enabled != true && StripeSetupCommand.BillingCustomerUpdates(portal));

            var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var currentCustomerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var subscriptionModesMatch = true;
            await foreach (var subscription in client.V1.Subscriptions.ListAutoPagingAsync(new() { Limit = 100, Status = "all" }, cancellationToken: cancellationToken))
            {
                subscriptionModesMatch &= subscription.Livemode == live;
                statusCounts[subscription.Status ?? "unknown"] = statusCounts.GetValueOrDefault(subscription.Status ?? "unknown") + 1;
                if (subscription.Status is "active" or "trialing" or "past_due" or "unpaid")
                {
                    var customer = subscription.CustomerId;
                    if (!string.IsNullOrEmpty(customer)) currentCustomerCounts[customer] = currentCustomerCounts.GetValueOrDefault(customer) + 1;
                }
            }
            checks["subscription_mode"] = subscriptionModesMatch;
            details["subscription_status_counts"] = statusCounts;
            details["customers_with_multiple_current_subscriptions"] = currentCustomerCounts.Count(pair => pair.Value > 1);
            details["subscription_attribution"] = "Customer counts are an inventory only; organization links require a trusted local account record.";
            var report = new { checks, details, passed = checks.Values.All(value => value) };
            await output.WriteLineAsync(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return report.passed ? 0 : 1;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(new { passed = false, error = SafeErrorName(exception) }));
            return 2;
        }
    }

    private static async Task AuditPricesAsync(StripeClient client, bool live, Dictionary<string, bool> checks, CancellationToken cancellationToken)
    {
        var prices = new List<Price>();
        await foreach (var price in client.V1.Prices.ListAutoPagingAsync(new()
        {
            Active = true,
            Currency = "usd",
            Type = "recurring",
            Limit = 100,
            Expand = ["data.product"]
        }, cancellationToken: cancellationToken)) prices.Add(price);
        var products = new Dictionary<string, Product>(StringComparer.Ordinal);
        foreach (var price in prices)
        {
            if (price.Product is not null || string.IsNullOrEmpty(price.ProductId)) continue;
            if (!products.TryGetValue(price.ProductId, out var product))
            {
                product = await client.V1.Products.GetAsync(price.ProductId, cancellationToken: cancellationToken);
                products.Add(price.ProductId, product);
            }
            price.Product = product;
        }
        foreach (var plan in PlanCatalog.Plans.Where(plan => plan.PriceCents > 0))
            foreach (var interval in plan.CheckoutIntervals)
            {
                checks[plan.Key + "_" + interval] = prices.Count(price => price.Livemode == live &&
                    price.Product?.Livemode == live && StripeBilling.MatchesPrice(price, plan, interval, requirePublished: true)) == 1;
            }
    }

    private static string SafeErrorName(Exception error) => error is StripeException ? "StripeException" : error.GetType().Name;
}
