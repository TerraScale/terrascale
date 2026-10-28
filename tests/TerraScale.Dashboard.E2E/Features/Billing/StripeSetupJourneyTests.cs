using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Stripe;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Billing;

public sealed class StripeSetupJourneyTests
{
    [Test]
    [Arguments("sk_test_SETUP", false)]
    [Arguments("rk_test_SETUP", false)]
    [Arguments("sk_live_SETUP", true)]
    [Arguments("rk_live_SETUP", true)]
    public async Task SetupCreatesCatalogPricesAndPortalOnceThenChangesNothing(string key, bool live)
    {
        var stripe = new FakeStripe(live);
        var mode = live ? "live" : "test";
        Check(await Run(stripe, key) == (0, "{\"mode\":\"" + mode + "\",\"changes\":[\"product_starter_created\",\"price_starter_monthly_created\",\"price_starter_annual_created\",\"product_pro_created\",\"price_pro_monthly_created\",\"price_pro_annual_created\",\"product_team_created\",\"price_team_monthly_created\",\"price_team_annual_created\",\"product_enterprise_created\",\"price_enterprise_monthly_created\",\"portal_created\"]}"),
            "The first run creates four products, seven prices and the default portal");
        Check(stripe.Products.Count == 4, "Each plan's intervals share one product");
        Check(stripe.Prices.Select(price => (string)price["unit_amount"]! + "/" + (string)price["recurring[interval]"]!).Order().SequenceEqual(
                PlanCatalog.Plans.Where(plan => plan.PriceCents > 0).SelectMany(plan => plan.Intervals, (plan, interval) => plan.PriceCentsFor(interval) + (interval == "annual" ? "/year" : "/month"))
                    .Append(PlanCatalog.ComparisonPlans.Single(plan => plan.Key == "enterprise").PriceCents + "/month").Order()),
            "Price amounts come from PlanCatalog");
        Check(stripe.Products.Single(product => product["metadata[plan]"] == "enterprise")["name"] == "TerraScale Enterprise support" &&
            !PlanCatalog.Plans.Any(plan => plan.Key == "enterprise"), "Support pricing leaves Enterprise contact-only without a selectable entitlement");
        var portal = stripe.Portals.Single();
        Check(portal.GetValueOrDefault("features[customer_update][enabled]") == "true" &&
            new[] { "name", "address", "tax_id" }.SequenceEqual(Enumerable.Range(0, 3).Select(index => portal.GetValueOrDefault($"features[customer_update][allowed_updates][{index}]"))),
            "Owners can edit their invoice name, billing address and tax IDs in the configured portal");
        var writes = stripe.Writes;
        Check(await Run(stripe, key) == (0, "{\"mode\":\"" + mode + "\",\"changes\":[]}") && stripe.Writes == writes, "A second run changes nothing");
        Check(stripe.ApiKeys.Count > 0 && stripe.ApiKeys.All(actual => actual == key), "Every Stripe request uses the delivered identity's key");
    }

    [Test]
    [Arguments("sk_test_SETUP", false)]
    [Arguments("rk_test_SETUP", false)]
    [Arguments("sk_live_SETUP", true)]
    [Arguments("rk_live_SETUP", true)]
    public async Task SetupReplacesLegacyPricesTransfersLookupKeysAndPreservesImmutableHistory(string key, bool live)
    {
        var stripe = new FakeStripe(live);
        var mode = live ? "live" : "test";
        foreach (var plan in new[] { "starter", "pro", "enterprise" })
            stripe.Products.Add(new() { ["id"] = "prod_legacy_" + plan, ["active"] = "true", ["name"] = "TerraScale " + plan, ["metadata[plan]"] = plan });
        foreach (var (plan, interval, amount) in new[] { ("starter", "monthly", "500"), ("starter", "annual", "5000"), ("pro", "monthly", "5000"), ("pro", "annual", "50000"), ("enterprise", "monthly", "49900") })
            stripe.Prices.Add(new()
            {
                ["id"] = "price_legacy_" + plan + "_" + interval,
                ["active"] = "true",
                ["product"] = "prod_legacy_" + plan,
                ["currency"] = "usd",
                ["unit_amount"] = amount,
                ["recurring[interval]"] = interval == "annual" ? "year" : "month",
                ["recurring[interval_count]"] = "1",
                ["lookup_key"] = "terrascale_" + plan + "_" + interval
            });
        var legacy = stripe.Prices.Select(price => new Dictionary<string, string>(price)).ToArray();
        Check(await Run(stripe, key) == (0, "{\"mode\":\"" + mode + "\",\"changes\":[\"price_starter_monthly_created\",\"price_starter_annual_created\",\"price_pro_monthly_created\",\"price_pro_annual_created\",\"product_team_created\",\"price_team_monthly_created\",\"price_team_annual_created\",\"price_enterprise_monthly_created\",\"portal_created\"]}"),
            "Legacy products are reused and only approved replacement prices are created");
        Check(stripe.Prices.Count == 12, "Five legacy prices and seven new prices remain addressable");
        foreach (var old in legacy)
        {
            var retained = stripe.Prices.Single(price => price["id"] == old["id"]);
            foreach (var field in new[] { "unit_amount", "currency", "product", "recurring[interval]", "recurring[interval_count]" })
                Check(retained[field] == old[field], "Legacy immutable field is preserved: " + old["id"] + "/" + field);
            Check(!retained.ContainsKey("lookup_key"), "Every legacy lookup key transfers to its approved replacement price");
        }
        foreach (var (lookupKey, amount, interval) in new[] { ("terrascale_starter_monthly", "300", "month"), ("terrascale_starter_annual", "3000", "year"), ("terrascale_pro_monthly", "2000", "month"), ("terrascale_pro_annual", "20000", "year"),
            ("terrascale_team_monthly", "5000", "month"), ("terrascale_team_annual", "50000", "year"), ("terrascale_enterprise_monthly", "99900", "month") })
        {
            var price = stripe.Prices.Single(price => price.GetValueOrDefault("lookup_key") == lookupKey);
            Check(!legacy.Any(old => old["id"] == price["id"]), "Approved price gets a new immutable ID: " + lookupKey);
            Check(price["unit_amount"] == amount && price["recurring[interval]"] == interval && price["recurring[interval_count]"] == "1" && price["currency"] == "usd" && price["active"] == "true", "Approved amount and cadence: " + lookupKey);
            Check(stripe.Products.Single(product => product["id"] == price["product"])["metadata[plan]"] == lookupKey.Split('_')[1], "Replacement price belongs to the correct plan product");
        }
        var writes = stripe.Writes;
        Check(await Run(stripe, key) == (0, "{\"mode\":\"" + mode + "\",\"changes\":[]}") && stripe.Writes == writes, "A rerun after legacy replacement performs zero writes");
    }

    [Test]
    [Arguments("sk_test_SETUP", false)]
    [Arguments("sk_live_SETUP", true)]
    public async Task SetupArchivesDuplicateSupportPricesWithoutChangingAmounts(string key, bool live)
    {
        var stripe = new FakeStripe(live);
        Check((await Run(stripe, key)).Item1 == 0, "Seed the approved catalog");
        var support = stripe.Prices.Single(price => price.GetValueOrDefault("lookup_key") == "terrascale_enterprise_monthly");
        var duplicate = new Dictionary<string, string>(support) { ["id"] = "price_enterprise_duplicate" };
        duplicate.Remove("lookup_key");
        stripe.Prices.Add(duplicate);
        var writes = stripe.Writes;
        var mode = live ? "live" : "test";
        Check(await Run(stripe, key) == (0, "{\"mode\":\"" + mode + "\",\"changes\":[\"price_enterprise_monthly_duplicate_archived\"]}") && stripe.Writes == writes + 1,
            "Only the duplicate support price is archived");
        Check(support["active"] == "true" && duplicate["active"] == "false" && support["unit_amount"] == "99900" && duplicate["unit_amount"] == "99900",
            "Archiving preserves both immutable support prices");
        writes = stripe.Writes;
        Check(await Run(stripe, key) == (0, "{\"mode\":\"" + mode + "\",\"changes\":[]}") && stripe.Writes == writes, "Duplicate cleanup also has a zero-write rerun");
    }

    [Test]
    [Arguments("--live")]
    [Arguments("--test")]
    [Arguments("--help")]
    public async Task SetupRefusesArgumentsBeforeReadingIdentity(string argument)
    {
        var command = new StripeSetupCommand(new DashboardIdentity(_ => throw new InvalidOperationException("Identity must not be read.")),
            _ => throw new InvalidOperationException("Stripe must not be contacted."), TextWriter.Null, TextWriter.Null);
        Check(await command.RunAsync([argument]) == 1, "Arguments cannot override the delivered identity's mode");
    }

    [Test]
    [Arguments(DashboardIdentity.AuthClientSecretName, "too-short")]
    [Arguments(DashboardIdentity.SessionEncryptionKeyName, "not-base64")]
    [Arguments(DashboardIdentity.StripeSecretKeyName, "not-a-stripe-key")]
    [Arguments(DashboardIdentity.StripeWebhookSecretName, "not-a-webhook-secret")]
    [Arguments(DashboardIdentity.TerraBasePeerSecretName, "not-a-peer-secret")]
    public async Task SetupRequiresAValidFullIdentityBeforeContactingStripe(string secretName, string invalidValue)
    {
        var stripe = new FakeStripe(true);
        Check(await Run(stripe, "sk_live_SETUP", secretName) == (2, "") && stripe.ApiKeys.Count == 0 && stripe.Writes == 0,
            "An incomplete identity is rejected before contacting Stripe");
        Check(await Run(stripe, "sk_live_SETUP", secretName, invalidValue) == (2, "") && stripe.ApiKeys.Count == 0 && stripe.Writes == 0,
            "An invalid identity is rejected before contacting Stripe");
    }

    [Test]
    [Arguments("unit_amount", "9999")]
    [Arguments("currency", "eur")]
    [Arguments("recurring[interval]", "month")]
    [Arguments("recurring[interval_count]", "2")]
    [Arguments("product", "prod_other")]
    public async Task StripeFakeRejectsImmutablePriceUpdates(string field, string value)
    {
        var stripe = new FakeStripe();
        Check((await Run(stripe, "sk_test_SETUP")).Item1 == 0, "Seed the approved catalog");
        var before = new Dictionary<string, string>(stripe.Prices[0]);
        var writes = stripe.Writes;
        using var client = new HttpClient(stripe);
        using var response = await client.PostAsync("https://api.stripe.com/v1/prices/" + before["id"], new FormUrlEncodedContent(new Dictionary<string, string> { [field] = value }));
        Check(response.StatusCode == HttpStatusCode.BadRequest && stripe.Writes == writes && before.OrderBy(pair => pair.Key).SequenceEqual(stripe.Prices[0].OrderBy(pair => pair.Key)), "Stripe rejects immutable mutations without changing the stored price");
    }

    private static async Task<(int, string)> Run(FakeStripe stripe, string key, string? invalidSecretName = null, string? invalidValue = null)
    {
        var output = new StringWriter();
        var identity = new DashboardIdentity(name => name == invalidSecretName ? invalidValue : name switch
        {
            DashboardIdentity.AuthClientSecretName => new string('a', 32),
            DashboardIdentity.SessionEncryptionKeyName => Convert.ToBase64String(new byte[32]),
            DashboardIdentity.StripeSecretKeyName => key,
            DashboardIdentity.StripeWebhookSecretName => "whsec_SETUP",
            DashboardIdentity.TerraBasePeerSecretName => new string('0', 64),
            _ => null
        });
        var result = await new StripeSetupCommand(identity, secret => new StripeClient(new StripeClientOptions
        { ApiKey = secret, HttpClient = new SystemNetHttpClient(new HttpClient(stripe), enableTelemetry: false) }), output, TextWriter.Null).RunAsync([]);
        return (result, output.ToString().Replace(" ", "").Replace("\n", "").Replace("\r", ""));
    }

    [Test]
    public async Task SetupRepairsMissingPortalTaxFieldsWithoutReplacingPrices()
    {
        using var stripe = new FakeStripe();
        Check((await Run(stripe, "sk_test_SETUP")).Item1 == 0, "Seed the catalog and default portal");
        stripe.Portals.Single().Remove("features[customer_update][allowed_updates][2]");
        var prices = stripe.Prices.Select(price => price["id"]).ToArray();
        var writes = stripe.Writes;
        Check(await Run(stripe, "sk_test_SETUP") == (0, "{\"mode\":\"test\",\"changes\":[\"portal_updated\"]}"), "Repair the incomplete portal");
        Check(stripe.Writes == writes + 1 && prices.SequenceEqual(stripe.Prices.Select(price => price["id"])), "Only the portal changes");
        Check(stripe.Portals.Single()["features[customer_update][allowed_updates][2]"] == "tax_id", "Tax ID edits are restored");
    }

    // An in-memory Stripe for the endpoints the setup command uses; stored objects keep their raw form fields.
    private sealed class FakeStripe(bool live = false) : HttpMessageHandler
    {
        public readonly List<Dictionary<string, string>> Products = [], Prices = [], Portals = [];
        public readonly List<string?> ApiKeys = [];
        public int Writes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKeys.Add(request.Headers.Authorization?.Parameter);
            var path = request.RequestUri!.AbsolutePath;
            var form = request.Content is null ? new Dictionary<string, string>() : (await request.Content.ReadAsStringAsync(cancellationToken)).Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
            var (store, kind) = path switch
            {
                _ when path.StartsWith("/v1/products", StringComparison.Ordinal) => (Products, "prod"),
                _ when path.StartsWith("/v1/prices", StringComparison.Ordinal) => (Prices, "price"),
                _ => (Portals, "bpc")
            };
            JsonNode body;
            if (request.Method == HttpMethod.Get)
            {
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                var listed = store.Where(item => (!query.ContainsKey("active") || item.GetValueOrDefault("active") == query["active"].ToString()) &&
                    (!query.ContainsKey("currency") || item.GetValueOrDefault("currency") == query["currency"].ToString()));
                body = new JsonObject { ["object"] = "list", ["has_more"] = false, ["url"] = path, ["data"] = new JsonArray(listed.Select(item => Json(kind, item)).ToArray()) };
            }
            else
            {
                var id = path.Split('/').Last();
                var item = store.FirstOrDefault(item => item["id"] == id);
                if (kind == "price" && item is not null && form.Keys.Any(name => name is "unit_amount" or "unit_amount_decimal" or "currency" or "product" || name.StartsWith("recurring[", StringComparison.Ordinal)))
                    return Error("Price amount, currency, recurring interval and product are immutable.");
                if (kind == "price" && form.TryGetValue("lookup_key", out var lookupKey) && lookupKey.Length > 0)
                {
                    var previous = Prices.SingleOrDefault(price => price != item && price.GetValueOrDefault("lookup_key") == lookupKey);
                    if (previous is not null)
                    {
                        if (form.GetValueOrDefault("transfer_lookup_key") != "true") return Error("Lookup key already exists.");
                        previous.Remove("lookup_key");
                    }
                }
                Writes++;
                if (item is null) store.Add(item = new() { ["id"] = kind + "_" + store.Count, ["active"] = "true", ["is_default"] = (Portals.Count == 0).ToString().ToLowerInvariant() });
                foreach (var (name, value) in form.Where(pair => pair.Key != "transfer_lookup_key"))
                    if (name == "lookup_key" && value.Length == 0) item.Remove(name); else item[name] = value;
                body = Json(kind, item);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }

        private static HttpResponseMessage Error(string message) => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(new JsonObject { ["error"] = new JsonObject { ["type"] = "invalid_request_error", ["message"] = message } }.ToJsonString(), Encoding.UTF8, "application/json")
        };

        private JsonObject Json(string kind, Dictionary<string, string> item)
        {
            bool On(string name) => item.GetValueOrDefault(name) == "true";
            var json = new JsonObject { ["id"] = item["id"], ["active"] = On("active"), ["livemode"] = live };
            if (kind == "prod")
            {
                json["object"] = "product";
                json["name"] = item.GetValueOrDefault("name");
                json["metadata"] = new JsonObject { ["plan"] = item.GetValueOrDefault("metadata[plan]") };
            }
            else if (kind == "price")
            {
                json["object"] = "price";
                json["currency"] = item["currency"];
                json["type"] = "recurring";
                json["lookup_key"] = item.GetValueOrDefault("lookup_key");
                json["unit_amount"] = long.Parse(item["unit_amount"]);
                json["recurring"] = new JsonObject { ["interval"] = item["recurring[interval]"], ["interval_count"] = int.Parse(item["recurring[interval_count]"]) };
                json["product"] = Json("prod", Products.Single(product => product["id"] == item["product"]));
            }
            else
            {
                json["object"] = "billing_portal.configuration";
                json["is_default"] = On("is_default");
                var features = new JsonObject(new[] { "payment_method_update", "invoice_history", "subscription_cancel", "subscription_update" }
                    .Select(feature => KeyValuePair.Create(feature, (JsonNode?)new JsonObject { ["enabled"] = On($"features[{feature}][enabled]") })));
                features["customer_update"] = new JsonObject
                {
                    ["enabled"] = On("features[customer_update][enabled]"),
                    ["allowed_updates"] = new JsonArray(Enumerable.Range(0, 3).Select(index => (JsonNode?)JsonValue.Create(item.GetValueOrDefault($"features[customer_update][allowed_updates][{index}]"))).ToArray())
                };
                json["features"] = features;
            }
            return json;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
