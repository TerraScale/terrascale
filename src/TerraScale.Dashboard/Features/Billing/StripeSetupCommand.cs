using System.Text.Json;
using Stripe;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Shared.Accounts;
using PortalConfiguration = Stripe.BillingPortal.Configuration;
using PortalFeatures = Stripe.BillingPortal.ConfigurationFeaturesOptions;

namespace TerraScale.Dashboard.Features.Billing;

/// <summary>Creates what StripeAuditCommand expects in a TEST account: one product per paid plan, its
/// monthly and annual prices from PlanCatalog, and the default billing portal. A second run changes nothing.</summary>
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
            // shortcut: test mode only, as the go-live plan requires; drop this guard when live setup is approved.
            if (stripe.IsLive)
            {
                await error.WriteLineAsync("The setup-stripe command only runs against a Stripe test account.");
                return 1;
            }
            var client = clientFactory(stripe.SecretKey);
            var changes = new List<string>();
            await SetupPricesAsync(client, changes, cancellationToken);
            await SetupPortalAsync(client, changes, cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { mode = "test", changes }, new JsonSerializerOptions { WriteIndented = true }));
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

        foreach (var plan in PlanCatalog.Plans.Where(plan => plan.PriceCents > 0))
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
            }
            else if (!product.Active)
            {
                product = await client.V1.Products.UpdateAsync(product.Id, new() { Active = true }, cancellationToken: cancellationToken);
                changes.Add("product_" + plan.Key + "_activated");
                foreach (var price in prices.Where(price => price.ProductId == product.Id)) price.Product = product;
            }
            foreach (var interval in new[] { "monthly", "annual" })
            {
                var name = plan.Key + "_" + interval;
                var matches = prices.Where(price => StripeBilling.MatchesPrice(price, plan, interval, requirePublished: true))
                    .OrderByDescending(price => price.ProductId == product.Id).ToArray();
                if (matches.Length == 0)
                {
                    await client.V1.Prices.CreateAsync(new()
                    {
                        Product = product.Id,
                        Currency = "usd",
                        UnitAmount = interval == "annual" ? plan.AnnualPriceCents : plan.PriceCents,
                        Recurring = new() { Interval = interval == "annual" ? "year" : "month", IntervalCount = 1 },
                        LookupKey = "terrascale_" + name,
                        TransferLookupKey = true
                    }, cancellationToken: cancellationToken);
                    changes.Add("price_" + name + "_created");
                }
                // The audit and checkout require exactly one published price per plan and interval.
                foreach (var duplicate in matches.Skip(1))
                {
                    await client.V1.Prices.UpdateAsync(duplicate.Id, new() { Active = false }, cancellationToken: cancellationToken);
                    changes.Add("price_" + name + "_duplicate_archived");
                }
            }
        }
    }

    private static async Task SetupPortalAsync(StripeClient client, List<string> changes, CancellationToken cancellationToken)
    {
        PortalConfiguration? portal = null;
        await foreach (var configuration in client.V1.BillingPortal.Configurations.ListAutoPagingAsync(new() { Limit = 100 }, cancellationToken: cancellationToken))
            if (configuration.IsDefault) portal = configuration;
        if (portal is not null && portal.Active && portal.Features?.PaymentMethodUpdate?.Enabled == true && portal.Features.InvoiceHistory?.Enabled == true &&
            portal.Features.SubscriptionCancel?.Enabled == true && portal.Features.SubscriptionUpdate?.Enabled != true)
            return;
        // Customers change plans in the dashboard, so the portal never offers subscription updates.
        var features = new PortalFeatures
        {
            PaymentMethodUpdate = new() { Enabled = true },
            InvoiceHistory = new() { Enabled = true },
            SubscriptionCancel = new() { Enabled = true, Mode = "at_period_end" },
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
}
