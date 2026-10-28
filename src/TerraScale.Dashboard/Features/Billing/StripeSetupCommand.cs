using System.Text.Json;
using Stripe;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Shared.Accounts;
using PortalConfiguration = Stripe.BillingPortal.Configuration;
using PortalFeatures = Stripe.BillingPortal.ConfigurationFeaturesOptions;

namespace TerraScale.Dashboard.Features.Billing;

/// <summary>Creates what StripeAuditCommand expects in the delivered identity's account: one product per paid plan, its
/// approved billing cadence and prices from PlanCatalog, contact-only Enterprise support, and the default billing portal.
/// A second run changes nothing.</summary>
public sealed class StripeSetupCommand
{
    private readonly IDashboardIdentity identity;
    private readonly Func<string, StripeClient> clientFactory;
    private readonly TextWriter output;
    private readonly TextWriter error;

    public StripeSetupCommand(IDashboardIdentity identity,
        Func<string, StripeClient>? clientFactory = null, TextWriter? output = null, TextWriter? error = null)
    {
        this.identity = identity;
        this.clientFactory = clientFactory ?? (key => new StripeClient(key,
            httpClient: new SystemNetHttpClient(new HttpClient(), enableTelemetry: false)));
        this.output = output ?? Console.Out;
        this.error = error ?? Console.Error;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length != 0)
        {
            await error.WriteLineAsync("The setup-stripe command accepts no options.");
            return 1;
        }

        try
        {
            var stripe = (await identity.ReadAsync(cancellationToken)).Stripe;
            var client = clientFactory(stripe.SecretKey);
            var changes = new List<string>();
            await SetupPricesAsync(client, changes, cancellationToken);
            await SetupPortalAsync(client, changes, cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { mode = stripe.IsLive ? "live" : "test", changes }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(new { error = exception is StripeException ? "StripeException" : exception.GetType().Name }));
            return 2;
        }
    }

    private static async Task SetupPricesAsync(StripeClient client, List<string> changes, CancellationToken cancellationToken)
    {
        var products = new List<Product>();
        await foreach (var product in client.V1.Products.ListAutoPagingAsync(new() { Limit = 100 }, cancellationToken: cancellationToken))
            products.Add(product);
        // The same inventory the audit reads: every active recurring USD price, with its product.
        var prices = new List<Price>();
        await foreach (var price in client.V1.Prices.ListAutoPagingAsync(new()
        {
            Active = true,
            Currency = "usd",
            Type = "recurring",
            Limit = 100,
            Expand = ["data.product"]
        }, cancellationToken: cancellationToken)) prices.Add(price);
        foreach (var price in prices.Where(price => price.Product is null))
            price.Product = products.FirstOrDefault(product => product.Id == price.ProductId);

        var enterprise = PlanCatalog.ComparisonPlans.Single(plan => plan.Key == "enterprise");
        // Stripe support pricing does not make Enterprise a selectable dashboard plan.
        var setupPlans = PlanCatalog.Plans.Where(plan => plan.PriceCents > 0)
            .SelectMany(plan => plan.Intervals, (plan, interval) => (plan.Key, plan.Name, Interval: interval, Amount: plan.PriceCentsFor(interval)))
            .Append((Key: enterprise.Key, Name: enterprise.Name + " support", Interval: "monthly", Amount: enterprise.PriceCents));
        foreach (var plan in setupPlans)
        {
            var product = products.FirstOrDefault(product => product.Metadata.GetValueOrDefault("plan") == plan.Key);
            if (product is null)
            {
                product = await client.V1.Products.CreateAsync(new()
                {
                    Name = "TerraScale " + plan.Name,
                    Metadata = new() { ["plan"] = plan.Key }
                }, cancellationToken: cancellationToken);
                changes.Add("product_" + plan.Key + "_created");
                // A plan's later intervals reuse the product its first interval created.
                products.Add(product);
            }
            else if (!product.Active)
            {
                var inactive = product;
                product = await client.V1.Products.UpdateAsync(product.Id, new() { Active = true }, cancellationToken: cancellationToken);
                changes.Add("product_" + plan.Key + "_activated");
                products[products.IndexOf(inactive)] = product;
                foreach (var price in prices.Where(price => price.ProductId == product.Id)) price.Product = product;
            }
            var interval = plan.Interval;
            var name = plan.Key + "_" + interval;
            var matches = prices.Where(price => price.Active && price.Currency == "usd" && price.UnitAmount == plan.Amount &&
                    price.Recurring is { UsageType: not "metered", IntervalCount: 1 } recurring && recurring.Interval == (interval == "annual" ? "year" : "month") &&
                    price.Product is { Active: true } publishedProduct && (publishedProduct.Metadata.GetValueOrDefault("plan") == plan.Key ||
                        publishedProduct.Name == plan.Name || publishedProduct.Name == "TerraScale " + plan.Name))
                .OrderByDescending(price => price.ProductId == product.Id).ToArray();
            if (matches.Length == 0)
            {
                await client.V1.Prices.CreateAsync(new()
                {
                    Product = product.Id,
                    Currency = "usd",
                    UnitAmount = plan.Amount,
                    Recurring = new() { Interval = interval == "annual" ? "year" : "month", IntervalCount = 1 },
                    LookupKey = "terrascale_" + name,
                    TransferLookupKey = true
                }, cancellationToken: cancellationToken);
                changes.Add("price_" + name + "_created");
            }
            // Keep exactly one published price per plan and interval.
            foreach (var duplicate in matches.Skip(1))
            {
                await client.V1.Prices.UpdateAsync(duplicate.Id, new() { Active = false }, cancellationToken: cancellationToken);
                changes.Add("price_" + name + "_duplicate_archived");
            }
        }
    }

    private static async Task SetupPortalAsync(StripeClient client, List<string> changes, CancellationToken cancellationToken)
    {
        PortalConfiguration? portal = null;
        await foreach (var configuration in client.V1.BillingPortal.Configurations.ListAutoPagingAsync(new() { Limit = 100 }, cancellationToken: cancellationToken))
            if (configuration.IsDefault) portal = configuration;
        if (portal is not null && portal.Active && portal.Features?.PaymentMethodUpdate?.Enabled == true && portal.Features.InvoiceHistory?.Enabled == true &&
            portal.Features.SubscriptionCancel?.Enabled == true && portal.Features.SubscriptionUpdate?.Enabled != true &&
            BillingCustomerUpdates(portal))
            return;
        // Customers change plans in the dashboard, so the portal never offers subscription updates.
        var features = new PortalFeatures
        {
            PaymentMethodUpdate = new() { Enabled = true },
            InvoiceHistory = new() { Enabled = true },
            SubscriptionCancel = new() { Enabled = true, Mode = "at_period_end" },
            CustomerUpdate = new() { Enabled = true, AllowedUpdates = ["name", "address", "tax_id"] },
            SubscriptionUpdate = new() { Enabled = false }
        };
        if (portal is null)
        {
            await client.V1.BillingPortal.Configurations.CreateAsync(new()
            {
                Features = features,
                DefaultReturnUrl = StripeBilling.Origin + "/billing"
            }, cancellationToken: cancellationToken);
            changes.Add("portal_created");
        }
        else
        {
            await client.V1.BillingPortal.Configurations.UpdateAsync(portal.Id, new() { Active = true, Features = features }, cancellationToken: cancellationToken);
            changes.Add("portal_updated");
        }
    }

    internal static bool BillingCustomerUpdates(PortalConfiguration portal) => portal.Features?.CustomerUpdate is { Enabled: true } customer &&
        new[] { "name", "address", "tax_id" }.All(field => customer.AllowedUpdates?.Contains(field, StringComparer.Ordinal) == true);
}
