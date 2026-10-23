using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Stripe;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Billing;

public sealed class StripeSetupJourneyTests
{
    [Test]
    public async Task SetupCreatesCatalogPricesAndPortalOnceThenChangesNothing()
    {
        var stripe = new FakeStripe();
        Check(await Run(stripe, "sk_test_SETUP") == (0, "{\"mode\":\"test\",\"changes\":[\"product_starter_created\",\"price_starter_monthly_created\",\"price_starter_annual_created\",\"product_pro_created\",\"price_pro_monthly_created\",\"price_pro_annual_created\",\"portal_created\"]}"),
            "The first run creates two products, four prices and the default portal");
        Check(stripe.Prices.Select(price => (string)price["unit_amount"]! + "/" + (string)price["recurring[interval]"]!).Order().SequenceEqual(
                PlanCatalog.Plans.Where(plan => plan.PriceCents > 0).SelectMany(plan => new[] { plan.PriceCents + "/month", plan.AnnualPriceCents + "/year" }).Order()),
            "Price amounts come from PlanCatalog");
        var writes = stripe.Writes;
        Check(await Run(stripe, "sk_test_SETUP") == (0, "{\"mode\":\"test\",\"changes\":[]}") && stripe.Writes == writes, "A second run changes nothing");
        Check(await Run(stripe, "sk_live_SETUP") == (1, "") && stripe.Writes == writes, "Live keys are refused before Stripe is contacted");
    }

    private static async Task<(int, string)> Run(FakeStripe stripe, string key)
    {
        var output = new StringWriter();
        var identity = new Identity(new DashboardCredentials(new string('a', 32), Convert.ToBase64String(new byte[32]), new(key, "whsec_SETUP"), new string('0', 64)));
        var result = await new StripeSetupCommand(identity, secret => new StripeClient(new StripeClientOptions
        { ApiKey = secret, HttpClient = new SystemNetHttpClient(new HttpClient(stripe), enableTelemetry: false) }), output, TextWriter.Null).RunAsync([]);
        return (result, output.ToString().Replace(" ", "").Replace("\n", "").Replace("\r", ""));
    }

    private sealed class Identity(DashboardCredentials credentials) : IDashboardIdentity
    {
        public Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(credentials);
    }

    // An in-memory Stripe for the endpoints the setup command uses; stored objects keep their raw form fields.
    private sealed class FakeStripe : HttpMessageHandler
    {
        public readonly List<Dictionary<string, string>> Products = [], Prices = [], Portals = [];
        public int Writes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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
                body = new JsonObject { ["object"] = "list", ["has_more"] = false, ["url"] = path, ["data"] = new JsonArray(store.Select(item => Json(kind, item)).ToArray()) };
            else
            {
                Writes++;
                var id = path.Split('/').Last();
                var item = store.FirstOrDefault(item => item["id"] == id);
                if (item is null) store.Add(item = new() { ["id"] = kind + "_" + store.Count, ["active"] = "true", ["is_default"] = (Portals.Count == 0).ToString().ToLowerInvariant() });
                foreach (var (name, value) in form) item[name] = value;
                body = Json(kind, item);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }

        private JsonObject Json(string kind, Dictionary<string, string> item)
        {
            bool On(string name) => item.GetValueOrDefault(name) == "true";
            var json = new JsonObject { ["id"] = item["id"], ["active"] = On("active"), ["livemode"] = false };
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
                json["unit_amount"] = long.Parse(item["unit_amount"]);
                json["recurring"] = new JsonObject { ["interval"] = item["recurring[interval]"], ["interval_count"] = int.Parse(item["recurring[interval_count]"]) };
                json["product"] = Json("prod", Products.Single(product => product["id"] == item["product"]));
            }
            else
            {
                json["object"] = "billing_portal.configuration";
                json["is_default"] = On("is_default");
                json["features"] = new JsonObject(new[] { "payment_method_update", "invoice_history", "subscription_cancel", "subscription_update" }
                    .Select(feature => KeyValuePair.Create(feature, (JsonNode?)new JsonObject { ["enabled"] = On($"features[{feature}][enabled]") })));
            }
            return json;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
