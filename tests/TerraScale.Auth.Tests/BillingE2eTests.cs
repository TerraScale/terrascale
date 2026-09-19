namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TerraBase.Api;
using TerraBase.Integration;
using TerraBase.Security;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

/// Pure unit tests for the Stripe webhook signature helpers (no HTTP).
public sealed class StripeSignatureTests
{
    private const string Secret = "whsec_test";
    private const string Payload = """{"id":"evt_1","type":"checkout.session.completed"}""";

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string HexHmac(string secret, long timestamp, string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{payload}")));

    [Test]
    public async Task VerifiesWithCorrectSignature()
    {
        var t = Now;
        await Assert.That(StripeWebhookSignatures.Verify(Secret, $"t={t},v1={HexHmac(Secret, t, Payload)}", Payload, t))
            .IsEqualTo(StripeWebhookVerification.Valid);
    }

    [Test]
    public async Task VerifiesUppercaseHexSignature()
    {
        var t = Now;
        var upper = HexHmac(Secret, t, Payload).ToUpperInvariant();
        await Assert.That(StripeWebhookSignatures.Verify(Secret, $"t={t},v1={upper}", Payload, t))
            .IsEqualTo(StripeWebhookVerification.Valid);
    }

    [Test]
    public async Task RejectsTamperedBody()
    {
        var t = Now;
        var header = $"t={t},v1={HexHmac(Secret, t, Payload)}";
        await Assert.That(StripeWebhookSignatures.Verify(Secret, header, Payload + " ", t))
            .IsEqualTo(StripeWebhookVerification.InvalidSignature);
    }

    [Test]
    public async Task RejectsStaleTimestamp()
    {
        var stale = Now - 301;
        await Assert.That(StripeWebhookSignatures.Verify(Secret, $"t={stale},v1={HexHmac(Secret, stale, Payload)}", Payload, Now))
            .IsEqualTo(StripeWebhookVerification.StaleTimestamp);
        var future = Now + 301;
        await Assert.That(StripeWebhookSignatures.Verify(Secret, $"t={future},v1={HexHmac(Secret, future, Payload)}", Payload, Now))
            .IsEqualTo(StripeWebhookVerification.StaleTimestamp);
        // At exactly the 300s tolerance (both directions) it still verifies.
        await Assert.That(StripeWebhookSignatures.Verify(Secret, $"t={Now - 300},v1={HexHmac(Secret, Now - 300, Payload)}", Payload, Now))
            .IsEqualTo(StripeWebhookVerification.Valid);
    }

    [Test]
    public async Task VerifiesAnyV1Entry()
    {
        var t = Now;
        var header = $"t={t},v1={HexHmac("whsec_other", t, Payload)},v1={HexHmac(Secret, t, Payload)}";
        await Assert.That(StripeWebhookSignatures.Verify(Secret, header, Payload, t)).IsEqualTo(StripeWebhookVerification.Valid);
        await Assert.That(StripeWebhookSignatures.Verify("whsec_other", $"t={t},v1={HexHmac(Secret, t, Payload)}", Payload, t))
            .IsEqualTo(StripeWebhookVerification.InvalidSignature);
    }

    [Test]
    public async Task RejectsMalformedHeaders()
    {
        var t = Now;
        var valid = HexHmac(Secret, t, Payload);
        await Assert.That(StripeWebhookSignatures.Verify(Secret, null, Payload, t)).IsEqualTo(StripeWebhookVerification.InvalidSignature);
        await Assert.That(StripeWebhookSignatures.Verify(Secret, "", Payload, t)).IsEqualTo(StripeWebhookVerification.InvalidSignature);
        await Assert.That(StripeWebhookSignatures.Verify(Secret, "garbage", Payload, t)).IsEqualTo(StripeWebhookVerification.InvalidSignature);
        await Assert.That(StripeWebhookSignatures.Verify(Secret, $"t={t}", Payload, t)).IsEqualTo(StripeWebhookVerification.InvalidSignature);
        await Assert.That(StripeWebhookSignatures.Verify(Secret, $"t={t},v1=zz-not-hex", Payload, t)).IsEqualTo(StripeWebhookVerification.InvalidSignature);
        await Assert.That(StripeWebhookSignatures.Verify(Secret, $"v1={valid},t={t}", Payload, t)).IsEqualTo(StripeWebhookVerification.Valid); // order-independent
    }

    [Test]
    public async Task RejectsNonNumericTimestamp()
    {
        var valid = HexHmac(Secret, Now, Payload);
        // A t that fails to parse leaves no timestamp: a signature failure, not a stale one.
        await Assert.That(StripeWebhookSignatures.Verify(Secret, $"t=not-a-number,v1={valid}", Payload, Now))
            .IsEqualTo(StripeWebhookVerification.InvalidSignature);
        await Assert.That(StripeWebhookSignatures.Verify(Secret, $"t=17.5,v1={valid}", Payload, Now))
            .IsEqualTo(StripeWebhookVerification.InvalidSignature);
    }
}

/// Unit tests for the outbound Stripe client: the wire format it produces (Stripe's
/// bracket-nested form encoding) and the failure modes of the upstream response.
/// A one-shot local HttpListener stands in for Stripe.
public sealed class StripeClientTests
{
    private static AuthOptions Options(int port) => new()
    {
        Listen = "http://+:0",
        PublicOrigin = new Uri("https://auth.test"),
        TerrascaleApiUrl = new Uri("https://api.terrascale.tech"),
        TerrascaleApiKey = "key",
        Database = "db",
        MasterKey = new byte[32],
        OperatorKey = "op",
        StripeSecretKey = "sk_test_key",
        StripeApiUrl = new Uri($"http://127.0.0.1:{port}"),
    };

    /// A listener answering every request with <paramref name="respond"/>; captures raw
    /// body and headers. <see cref="Stop"/> kills the listener for transport-failure probes.
    private sealed class OneShotStripe : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Func<(int, string)> respond;

        public string? RawBody { get; private set; }
        public string? Authorization { get; private set; }
        public string? ContentType { get; private set; }
        public int Port { get; }

        private OneShotStripe(int port, Func<(int, string)> respond)
        {
            Port = port;
            this.respond = respond;
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = Task.Run(ListenAsync);
        }

        public static OneShotStripe Start(Func<(int, string)> respond)
        {
            for (var port = 18241; port <= 18259; port++)
            {
                try
                {
                    return new OneShotStripe(port, respond);
                }
                catch (HttpListenerException)
                {
                }
            }
            throw new InvalidOperationException("no free port in 18241-18259 for the one-shot Stripe");
        }

        private async Task ListenAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                RawBody = await reader.ReadToEndAsync();
                Authorization = context.Request.Headers["Authorization"];
                ContentType = context.Request.ContentType;
                var (status, body) = respond();
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Stop() => listener.Close();

        public void Dispose() => listener.Close();
    }

    /// Decodes a form body with standard form semantics (what Stripe's server does):
    /// `+` is space in encoded values, %xx everywhere, keys are decoded too.
    private static Dictionary<string, string?> DecodeForm(string raw) =>
        raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : null);

    [Test]
    public async Task PostsBracketNestedFormWithFullyEscapedValues()
    {
        using var stripe = OneShotStripe.Start(() => (200, """{"id":"cs_1","url":"https://checkout.example/s_1"}"""));
        var value = "price pro & + = %20 ünï-🙂";
        await StripeClient.PostAsync(Options(stripe.Port), "/v1/checkout/sessions", new Dictionary<string, string>
        {
            ["mode"] = "subscription",
            ["line_items[0][price]"] = value,
            ["line_items[0][quantity]"] = "1",
            ["metadata[instance_id]"] = value,
        }, default);

        await Assert.That(stripe.Authorization).IsEqualTo("Bearer sk_test_key");
        await Assert.That(stripe.ContentType).IsEqualTo("application/x-www-form-urlencoded");
        var form = DecodeForm(stripe.RawBody!);
        // Exact bracket nesting — a `line_items[0].price` flat key would fail this.
        await Assert.That(form.ContainsKey("line_items[0][price]")).IsTrue();
        await Assert.That(form.ContainsKey("line_items[0][quantity]")).IsTrue();
        await Assert.That(form.ContainsKey("metadata[instance_id]")).IsTrue();
        await Assert.That(form["line_items[0][price]"]).IsEqualTo(value); // + survives, & and = and unicode round-trip
        await Assert.That(form["metadata[instance_id]"]).IsEqualTo(value);
        // No dot-style keys and no raw (unescaped) separators leaked into the wire body.
        await Assert.That(stripe.RawBody!.Contains("line_items[0].")).IsFalse();
        await Assert.That(stripe.RawBody!.Split('&').Select(p => p.Split('=')[0]).Any(k => k.Contains('.'))).IsFalse();
    }

    [Test]
    public async Task ErrorStatusSurfacesAsStripeApiException()
    {
        using var stripe = OneShotStripe.Start(() => (402, """{"error":{"message":"card declined"}}"""));
        var exception = await Assert.ThrowsAsync<StripeApiException>(
            () => StripeClient.PostAsync(Options(stripe.Port), "/v1/checkout/sessions", new Dictionary<string, string> { ["mode"] = "subscription" }, default));
        await Assert.That(exception!.StatusCode).IsEqualTo(402);
    }

    [Test]
    public async Task UnparseableSuccessBodySurfacesAsStripeResponseException()
    {
        // A proxy/interceptor answering 200 with HTML must not surface as an unhandled
        // JsonException — the caller maps this to a clean 502 envelope.
        using var stripe = OneShotStripe.Start(() => (200, "<html>maintenance</html>"));
        await Assert.ThrowsAsync<StripeResponseException>(
            () => StripeClient.PostAsync(Options(stripe.Port), "/v1/checkout/sessions", new Dictionary<string, string> { ["mode"] = "subscription" }, default));
    }

    [Test]
    public async Task TransportFailureSurfacesAsHttpRequestException()
    {
        using var stripe = OneShotStripe.Start(() => (200, "{}"));
        var port = stripe.Port;
        stripe.Stop();
        await Assert.ThrowsAsync<HttpRequestException>(
            () => StripeClient.PostAsync(Options(port), "/v1/checkout/sessions", new Dictionary<string, string> { ["mode"] = "subscription" }, default));
    }
}

/// Stripe billing end to end: a second app+engine booted with Stripe env pointing at an
/// in-process fake Stripe, driven through checkout, the signed webhook receiver, and the
/// hosted billing pages. The shared fixture (no Stripe env) covers the 503 path.
[NotInParallel]
public sealed class BillingE2eTests
{
    private const string OperatorKey = "operator-billing-key";
    private const string WebhookSecret = "whsec_test";

    /// <summary>The dedicated app: booted once for this class, Stripe vars set at build time only.</summary>
    private static readonly Task<BillingApp> SharedApp = BootAsync();

    /// <summary>A second app booted lazily without TS_AUTH_PRICE_ENTERPRISE, for the
    /// unconfigured-plan probes. Shares nothing with <see cref="SharedApp"/>.</summary>
    private static Task<BillingApp>? noEnterpriseApp;

    // ---- Tests -------------------------------------------------------------------------

    [Test]
    public async Task BillingStatusStartsFreeAndEnforceCheckTracksQuota()
    {
        var app = await SharedApp;
        var (_, slug, apiKey) = await NewInstanceAsync(app);

        using var status = await app.Http.SendAsync(Get($"/v1/{slug}/billing", apiKey));
        await Assert.That(status.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await status.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("plan").GetString()).IsEqualTo("free");
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("free");
        await Assert.That(body.GetProperty("quota").GetInt32()).IsEqualTo(100);
        await Assert.That(body.GetProperty("user_count").GetInt32()).IsEqualTo(0);
        // Free instances carry no Stripe linkage (null and absent are both acceptable).
        await Assert.That(body.TryGetProperty("stripe_customer_id", out var customerId)
            && customerId.ValueKind != JsonValueKind.Null).IsFalse();

        await Assert.That((await EnforceCheckAsync(app, slug, apiKey, 0)).GetProperty("allowed").GetBoolean()).IsTrue();
        await Assert.That((await EnforceCheckAsync(app, slug, apiKey, 99)).GetProperty("allowed").GetBoolean()).IsTrue();
        await Assert.That((await EnforceCheckAsync(app, slug, apiKey, 100)).GetProperty("allowed").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task CheckoutCreatesStripeSessionWithCorrectForm()
    {
        var app = await SharedApp;
        var (instanceId, slug, apiKey) = await NewInstanceAsync(app);

        using var checkout = await app.Http.SendAsync(PostJson($"/v1/{slug}/billing/checkout", apiKey, new { plan = "pro" }));
        await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("checkout_url").GetString()).IsEqualTo("https://checkout.example/s_1");

        var request = app.Stripe.LastRequest("/v1/checkout/sessions");
        await Assert.That(request).IsNotNull();
        await Assert.That(request!.Authorization).IsEqualTo("Bearer test_key");
        await Assert.That(request.Form["mode"]).IsEqualTo("subscription");
        await Assert.That(request.Form["line_items[0][price]"]).IsEqualTo("price_pro");
        await Assert.That(request.Form["line_items[0][quantity]"]).IsEqualTo("1");
        await Assert.That(request.Form["client_reference_id"]).IsEqualTo(instanceId);
        await Assert.That(request.Form["metadata[instance_id]"]).IsEqualTo(instanceId);
        await Assert.That(request.Form["success_url"]).IsEqualTo($"https://auth.test/ui/{slug}/billing/return?status=success");
        await Assert.That(request.Form["cancel_url"]).IsEqualTo($"https://auth.test/ui/{slug}/billing/return?status=cancel");

        using var badPlan = await app.Http.SendAsync(PostJson($"/v1/{slug}/billing/checkout", apiKey, new { plan = "gold" }));
        await Assert.That(badPlan.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CheckoutRejectsFreeMissingAndMalformedPlansBeforeCallingStripe()
    {
        var app = await SharedApp;
        var (_, slug, apiKey) = await NewInstanceAsync(app);
        var stripeCalls = app.Stripe.RequestCount("/v1/checkout/sessions");

        // Free has no price to check out; missing/uppercase plans are invalid; malformed
        // JSON and non-JSON bodies are 400s — all without a Stripe round-trip.
        foreach (var body in new object[] { new { plan = "free" }, new { }, new { plan = "PRO" }, new { plan = (string?)null } })
        {
            using var rejected = await app.Http.SendAsync(PostJson($"/v1/{slug}/billing/checkout", apiKey, body));
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_plan");
        }

        using var invalidJson = new HttpRequestMessage(HttpMethod.Post, $"/v1/{slug}/billing/checkout")
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json"),
        };
        invalidJson.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var malformed = await app.Http.SendAsync(invalidJson);
        await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var malformedError = await malformed.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(malformedError.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_json");

        using var textBody = new HttpRequestMessage(HttpMethod.Post, $"/v1/{slug}/billing/checkout")
        {
            Content = new StringContent("plan=pro", Encoding.UTF8, "text/plain"),
        };
        textBody.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var nonJson = await app.Http.SendAsync(textBody);
        await Assert.That(nonJson.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        await Assert.That(app.Stripe.RequestCount("/v1/checkout/sessions")).IsEqualTo(stripeCalls);

        // The success/cancel URLs come from the configured origin only: query parameters,
        // headers, and body fields cannot steer where Stripe redirects the operator.
        using var influenced = await app.Http.SendAsync(PostJson(
            $"/v1/{slug}/billing/checkout?success_url=https%3A%2F%2Fevil.example&cancel_url=https%3A%2F%2Fevil.example&plan=enterprise", apiKey, new { plan = "pro" }));
        await Assert.That(influenced.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var request = app.Stripe.LastRequest("/v1/checkout/sessions")!;
        await Assert.That(request.Form["success_url"]).IsEqualTo($"https://auth.test/ui/{slug}/billing/return?status=success");
        await Assert.That(request.Form["cancel_url"]).IsEqualTo($"https://auth.test/ui/{slug}/billing/return?status=cancel");
        await Assert.That(request.Form["line_items[0][price]"]).IsEqualTo("price_pro");
    }

    [Test]
    public async Task CheckoutAndPortalMapUpstreamFailuresTo502()
    {
        var app = await SharedApp;
        var (_, slug, apiKey) = await NewInstanceAsync(app);

        // Error statuses (Stripe declines with 402, throttles with 429) and an interceptor
        // answering 200 with HTML all map to the clean 502 envelope — never a raw crash.
        foreach (var (status, body) in new[]
                 {
                     (402, """{"error":{"message":"card declined","type":"card_error"}}"""),
                     (429, """{"error":{"message":"rate limited"}}"""),
                     (500, """{"error":{"message":"internal"}}"""),
                     (200, "<html>proxy maintenance page</html>"),
                 })
        {
            app.Stripe.ResponseOverrides["POST /v1/checkout/sessions"] = (status, body);
            using var checkout = await app.Http.SendAsync(PostJson($"/v1/{slug}/billing/checkout", apiKey, new { plan = "pro" }));
            await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
            var error = await checkout.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("stripe_error");
            // The upstream body (HTML or Stripe error detail) never leaks into the response.
            var raw = await checkout.Content.ReadAsStringAsync();
            await Assert.That(raw.Contains("maintenance")).IsFalse();
            await Assert.That(raw.Contains("card declined")).IsFalse();
        }
        app.Stripe.ResponseOverrides.Remove("POST /v1/checkout/sessions");

        app.Stripe.ResponseOverrides["POST /v1/billing_portal/sessions"] = (429, """{"error":{"message":"rate limited"}}""");
        var (_, slug2, apiKey2, _) = await InstanceWithActiveProAsync(app);
        using var portal = await app.Http.SendAsync(PostJson($"/v1/{slug2}/billing/portal", apiKey2, new { }));
        await Assert.That(portal.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        app.Stripe.ResponseOverrides.Remove("POST /v1/billing_portal/sessions");
    }

    [Test]
    public async Task CheckoutForPlanWithoutConfiguredPriceReturns503WithoutCallingStripe()
    {
        noEnterpriseApp ??= BootAsync(priceEnterprise: null);
        var app = await noEnterpriseApp;
        var (_, slug, apiKey) = await NewInstanceAsync(app);

        // Enterprise has no price id in this deployment: a clean 503 before any Stripe call.
        using var enterprise = await app.Http.SendAsync(PostJson($"/v1/{slug}/billing/checkout", apiKey, new { plan = "enterprise" }));
        await Assert.That(enterprise.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var error = await enterprise.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("billing_not_configured");
        await Assert.That(app.Stripe.LastRequest("/v1/checkout/sessions")).IsNull();

        // Pro is configured and still works; the posted price id is the configured one.
        using var pro = await app.Http.SendAsync(PostJson($"/v1/{slug}/billing/checkout", apiKey, new { plan = "pro" }));
        await Assert.That(pro.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var request = app.Stripe.LastRequest("/v1/checkout/sessions")!;
        await Assert.That(request.Form["line_items[0][price]"]).IsEqualTo("price_pro");
    }

    [Test]
    public async Task WebhookCheckoutCompletedActivatesSubscriptionAndCustomer()
    {
        var app = await SharedApp;
        var (instanceId, slug, apiKey, customer) = await InstanceWithActiveProAsync(app);

        var instance = await app.Store.GetInstanceAsync(instanceId);
        await Assert.That(instance!.StripeCustomerId).IsEqualTo(customer);

        var subscriptions = await app.Store.ListSubscriptionsAsync(instanceId);
        await Assert.That(subscriptions.Count).IsEqualTo(1);
        await Assert.That(subscriptions[0].Plan).IsEqualTo("pro");
        await Assert.That(subscriptions[0].Status).IsEqualTo("active");
        await Assert.That(subscriptions[0].StripeCustomerId).IsEqualTo(customer);
        await Assert.That(subscriptions[0].StripeSubscriptionId).IsEqualTo("sub_1");
        await Assert.That(subscriptions[0].CurrentPeriodEnd).IsEqualTo(1893456000L);

        // The plan came from the fake Stripe's GET /v1/subscriptions/sub_1.
        var fetch = app.Stripe.LastRequest("/v1/subscriptions/sub_1");
        await Assert.That(fetch).IsNotNull();
        await Assert.That(fetch!.Method).IsEqualTo("GET");

        using var status = await app.Http.SendAsync(Get($"/v1/{slug}/billing", apiKey));
        var body = await status.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("plan").GetString()).IsEqualTo("pro");
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("active");
        await Assert.That(body.GetProperty("quota").GetInt32()).IsEqualTo(10_000);
        await Assert.That(body.GetProperty("stripe_customer_id").GetString()).IsEqualTo(customer);
        await Assert.That(body.GetProperty("stripe_subscription_id").GetString()).IsEqualTo("sub_1");
        await Assert.That(body.GetProperty("current_period_end").GetInt64()).IsEqualTo(1893456000L);
        await Assert.That((await EnforceCheckAsync(app, slug, apiKey, 9_999)).GetProperty("allowed").GetBoolean()).IsTrue();
        await Assert.That((await EnforceCheckAsync(app, slug, apiKey, 10_000)).GetProperty("allowed").GetBoolean()).IsFalse();

        var audits = await app.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "billing.plan_changed")).IsTrue();

        // With a customer on file, the portal session opens.
        using var portal = await app.Http.SendAsync(PostJson($"/v1/{slug}/billing/portal", apiKey, new { }));
        await Assert.That(portal.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var portalBody = await portal.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(portalBody.GetProperty("portal_url").GetString()).IsEqualTo("https://portal.example/p_1");
    }

    [Test]
    public async Task PortalWithoutCustomerReturns400()
    {
        var app = await SharedApp;
        var (_, slug, apiKey) = await NewInstanceAsync(app);

        using var portal = await app.Http.SendAsync(PostJson($"/v1/{slug}/billing/portal", apiKey, new { }));
        await Assert.That(portal.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var body = await portal.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("no_customer");
    }

    [Test]
    public async Task WebhookSubscriptionUpdatedPastDueKeepsPlan()
    {
        var app = await SharedApp;
        var (instanceId, slug, apiKey, customer) = await InstanceWithActiveProAsync(app);

        var json = $$"""
            {
              "id": "evt_{{Unique()}}",
              "type": "customer.subscription.updated",
              "data": { "object": {
                "id": "sub_1", "object": "subscription", "customer": "{{customer}}",
                "status": "past_due", "current_period_end": 1893456000,
                "items": [ { "price": { "id": "price_pro" } } ]
              } }
            }
            """;
        using var response = await SendWebhookAsync(app.Http, json);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var status = await app.Http.SendAsync(Get($"/v1/{slug}/billing", apiKey));
        var body = await status.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("plan").GetString()).IsEqualTo("pro");
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("past_due");

        var subscription = await app.Store.FindSubscriptionByCustomerAsync(customer);
        await Assert.That(subscription!.Plan).IsEqualTo("pro");
        await Assert.That(subscription.Status).IsEqualTo("past_due");
        var audits = await app.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "billing.subscription_updated")).IsTrue();
    }

    [Test]
    public async Task WebhookSubscriptionDeletedCancelsButKeepsPlan()
    {
        var app = await SharedApp;
        var (instanceId, slug, apiKey, customer) = await InstanceWithActiveProAsync(app);

        var json = $$"""
            {
              "id": "evt_{{Unique()}}",
              "type": "customer.subscription.deleted",
              "data": { "object": {
                "id": "sub_1", "object": "subscription", "customer": "{{customer}}",
                "status": "canceled", "current_period_end": 1893456000,
                "items": [ { "price": { "id": "price_pro" } } ]
              } }
            }
            """;
        using var response = await SendWebhookAsync(app.Http, json);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Canceled flags the subscription; the paid plan stays on the document.
        var subscription = await app.Store.FindSubscriptionByCustomerAsync(customer);
        await Assert.That(subscription!.Status).IsEqualTo("canceled");
        await Assert.That(subscription.Plan).IsEqualTo("pro");

        var audits = await app.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "billing.subscription_canceled")).IsTrue();
    }

    [Test]
    public async Task WebhookInvalidSignatureRejectedWithoutSideEffects()
    {
        var app = await SharedApp;
        var (instanceId, _, apiKey, customer) = await InstanceWithActiveProAsync(app);

        var json = CheckoutCompletedJson("evt_" + Unique(), instanceId, customer);
        using var response = await SendWebhookAsync(app.Http, json, secret: "whsec_wrong");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // State is unchanged: still exactly one active pro subscription for this customer.
        var subscription = await app.Store.FindSubscriptionByCustomerAsync(customer);
        await Assert.That(subscription!.Status).IsEqualTo("active");
        await Assert.That(subscription.Plan).IsEqualTo("pro");
        await Assert.That((await app.Store.ListSubscriptionsAsync(instanceId)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task WebhookUnknownEventAcknowledgedAsNoOp()
    {
        var app = await SharedApp;
        var (instanceId, _, _) = await NewInstanceAsync(app);

        var json = $$"""{ "id": "evt_{{Unique()}}", "type": "invoice.paid", "data": { "object": {} } }""";
        using var response = await SendWebhookAsync(app.Http, json);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await app.Store.ListSubscriptionsAsync(instanceId)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task WebhookReplayedEventProcessedOnce()
    {
        var app = await SharedApp;
        var (instanceId, slug, apiKey) = await NewInstanceAsync(app);
        var customer = "cus_" + Unique();

        var json = CheckoutCompletedJson("evt_replay_" + Unique(), instanceId, customer);
        using var first = await SendWebhookAsync(app.Http, json);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var replay = await SendWebhookAsync(app.Http, json);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // One subscription document, one plan-change audit event — not two.
        await Assert.That((await app.Store.ListSubscriptionsAsync(instanceId)).Count).IsEqualTo(1);
        var audits = await app.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Count(e => e.Type == "billing.plan_changed")).IsEqualTo(1);

        using var status = await app.Http.SendAsync(Get($"/v1/{slug}/billing", apiKey));
        var body = await status.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("plan").GetString()).IsEqualTo("pro");
    }

    [Test]
    public async Task WebhookStaleTimestampRejectedAs400()
    {
        var app = await SharedApp;
        var (instanceId, _, _, customer) = await InstanceWithActiveProAsync(app);

        // Correctly signed for its own (stale) timestamp: outside tolerance it must be a 400,
        // not a 401, and must not touch state.
        var json = CheckoutCompletedJson("evt_" + Unique(), instanceId, customer);
        using var response = await SendWebhookAsync(app.Http, json, ageSeconds: 600);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var subscription = await app.Store.FindSubscriptionByCustomerAsync(customer);
        await Assert.That(subscription!.Status).IsEqualTo("active");
    }

    [Test]
    public async Task WebhookBadOrMissingSignatureReturnsNonDescriptive401()
    {
        var app = await SharedApp;
        var (instanceId, _, _, customer) = await InstanceWithActiveProAsync(app);

        var json = CheckoutCompletedJson("evt_" + Unique(), instanceId, customer);

        using var wrongSecret = await SendWebhookAsync(app.Http, json, secret: "whsec_wrong");
        await Assert.That(wrongSecret.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var wrongBody = (await wrongSecret.Content.ReadAsStringAsync()).ToLowerInvariant();
        await Assert.That(wrongBody).DoesNotContain("signature");
        await Assert.That(wrongBody).DoesNotContain("secret");
        await Assert.That(wrongBody).DoesNotContain("timestamp");

        using var noHeader = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/webhook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var missing = await app.Http.SendAsync(noHeader);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        var subscription = await app.Store.FindSubscriptionByCustomerAsync(customer);
        await Assert.That(subscription!.Status).IsEqualTo("active");
    }

    [Test]
    public async Task WebhookCheckoutForForeignCustomerDoesNotCrossInstances()
    {
        var app = await SharedApp;
        var (instanceA, _, _, customerA) = await InstanceWithActiveProAsync(app);
        var (instanceB, _, _) = await NewInstanceAsync(app);

        // A checkout event referencing instance B but paid by instance A's customer: B must
        // not steal A's customer link (which would open A's billing portal to B), and A's
        // subscription must not be mutated by B's event — here it would flip A to enterprise.
        app.Stripe.SubscriptionBodies["sub_ent"] = """{"id":"sub_ent","customer":"cus_x","items":[{"price":{"id":"price_ent"}}],"current_period_end":1893456000}""";
        var json = CheckoutCompletedJson("evt_" + Unique(), instanceB, customerA, subscription: "sub_ent");
        using var response = await SendWebhookAsync(app.Http, json);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await Assert.That((await app.Store.GetInstanceAsync(instanceB))!.StripeCustomerId).IsNull();
        await Assert.That((await app.Store.ListSubscriptionsAsync(instanceB)).Count).IsEqualTo(0);

        var subscriptionA = await app.Store.FindSubscriptionByCustomerAsync(customerA);
        await Assert.That(subscriptionA!.InstanceId).IsEqualTo(instanceA);
        await Assert.That(subscriptionA.Plan).IsEqualTo("pro");
        await Assert.That(subscriptionA.Status).IsEqualTo("active");

        var auditsB = await app.Store.ListAuditEventsAsync(instanceB, 100);
        await Assert.That(auditsB.Any(e => e.Type.StartsWith("billing.", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task WebhookUnknownInstanceReferenceAcknowledgedAsNoOp()
    {
        var app = await SharedApp;
        var customer = "cus_" + Unique();

        var json = CheckoutCompletedJson("evt_" + Unique(), "ins_does_not_exist", customer);
        using var response = await SendWebhookAsync(app.Http, json);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await Assert.That(await app.Store.FindSubscriptionByCustomerAsync(customer)).IsNull();
    }

    [Test]
    public async Task WebhookFailedProcessingCanRetry()
    {
        var app = await SharedApp;
        var (instanceId, _, _) = await NewInstanceAsync(app);
        var customer = "cus_" + Unique();
        var json = CheckoutCompletedJson("evt_retry_" + Unique(), instanceId, customer, subscription: "sub_bad");

        // The subscription fetch returns a 200 with a garbage body: processing fails (Kestrel
        // answers 500; the test host instead propagates the server exception to the client).
        app.Stripe.SubscriptionBodies["sub_bad"] = "{not json";
        try
        {
            using var failed = await SendWebhookAsync(app.Http, json);
        }
        catch
        {
            // the unhandled server-side JsonException surfaced through the test host
        }

        app.Stripe.SubscriptionBodies.Remove("sub_bad");
        using var retry = await SendWebhookAsync(app.Http, json);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var subscriptions = await app.Store.ListSubscriptionsAsync(instanceId);
        await Assert.That(subscriptions.Count).IsEqualTo(1);
        await Assert.That(subscriptions[0].Plan).IsEqualTo("pro");
        await Assert.That(subscriptions[0].Status).IsEqualTo("active");
    }

    [Test]
    public async Task WebhookSubscriptionTransitionsEnforceQuotaPerPlan()
    {
        var app = await SharedApp;
        var (_, slug, apiKey, customer) = await InstanceWithActiveProAsync(app); // pro, quota 10_000

        // Upgrade to enterprise: quota rises to 1_000_000.
        await SendSubscriptionUpdatedAsync(app, customer, "active", "price_ent");
        var upgraded = await EnforceCheckAsync(app, slug, apiKey, 999_999);
        await Assert.That(upgraded.GetProperty("plan").GetString()).IsEqualTo("enterprise");
        await Assert.That(upgraded.GetProperty("allowed").GetBoolean()).IsTrue();
        await Assert.That((await EnforceCheckAsync(app, slug, apiKey, 1_000_000)).GetProperty("allowed").GetBoolean()).IsFalse();

        // Downgrade to pro: the quota is enforced at the new lower ceiling immediately.
        await SendSubscriptionUpdatedAsync(app, customer, "active", "price_pro");
        var downgraded = await EnforceCheckAsync(app, slug, apiKey, 9_999);
        await Assert.That(downgraded.GetProperty("plan").GetString()).IsEqualTo("pro");
        await Assert.That(downgraded.GetProperty("quota").GetInt32()).IsEqualTo(10_000);
        await Assert.That(downgraded.GetProperty("allowed").GetBoolean()).IsTrue();
        await Assert.That((await EnforceCheckAsync(app, slug, apiKey, 10_000)).GetProperty("allowed").GetBoolean()).IsFalse();

        // An unknown price id keeps the current plan instead of crashing or wiping it.
        await SendSubscriptionUpdatedAsync(app, customer, "active", "price_unknown");
        var unknown = await EnforceCheckAsync(app, slug, apiKey, 0);
        await Assert.That(unknown.GetProperty("plan").GetString()).IsEqualTo("pro");
        await Assert.That(unknown.GetProperty("quota").GetInt32()).IsEqualTo(10_000);

        // Cancellation falls back to the free quota.
        var deleted = $$"""
            {
              "id": "evt_{{Unique()}}",
              "type": "customer.subscription.deleted",
              "data": { "object": {
                "id": "sub_1", "object": "subscription", "customer": "{{customer}}",
                "status": "canceled", "current_period_end": 1893456000,
                "items": [ { "price": { "id": "price_pro" } } ]
              } }
            }
            """;
        using var cancelResponse = await SendWebhookAsync(app.Http, deleted);
        await Assert.That(cancelResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var canceled = await EnforceCheckAsync(app, slug, apiKey, 99);
        await Assert.That(canceled.GetProperty("plan").GetString()).IsEqualTo("free");
        await Assert.That(canceled.GetProperty("quota").GetInt32()).IsEqualTo(100);
        await Assert.That(canceled.GetProperty("allowed").GetBoolean()).IsTrue();
        await Assert.That((await EnforceCheckAsync(app, slug, apiKey, 100)).GetProperty("allowed").GetBoolean()).IsFalse();
        var subscription = await app.Store.FindSubscriptionByCustomerAsync(customer);
        await Assert.That(subscription!.Plan).IsEqualTo("pro"); // kept on the document, canceled flags it
    }

    [Test]
    public async Task SharedFixtureWithoutStripeEnvReturns503()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (_, slug, apiKey) = await service.CreateInstanceAsync("billing-shared-" + Unique());

        using var checkout = await service.Http.SendAsync(PostJson($"/v1/{slug}/billing/checkout", apiKey, new { plan = "pro" }));
        await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var checkoutBody = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(checkoutBody.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("billing_not_configured");

        using var portal = await service.Http.SendAsync(PostJson($"/v1/{slug}/billing/portal", apiKey, new { }));
        await Assert.That(portal.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);

        // The webhook receiver fails loudly instead of processing unsigned traffic.
        using var webhook = await service.Http.PostAsync(
            "/v1/billing/webhook", new StringContent("""{"id":"evt_x","type":"invoice.paid"}""", Encoding.UTF8, "application/json"));
        await Assert.That(webhook.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task BillingUiShowsReturnPageAndKeyedDashboard()
    {
        var app = await SharedApp;
        var (_, slug, apiKey) = await NewInstanceAsync(app);

        var success = await app.Http.GetStringAsync($"/ui/{slug}/billing/return?status=success");
        await Assert.That(success).Contains("confirmed");
        var canceled = await app.Http.GetStringAsync($"/ui/{slug}/billing/return?status=cancel");
        await Assert.That(canceled).Contains("canceled");

        var dashboard = await app.Http.GetStringAsync($"/ui/{slug}/billing?key={apiKey}");
        await Assert.That(dashboard).Contains("free");
        await Assert.That(dashboard).Contains("of 100");

        using var noKey = await app.Http.GetAsync($"/ui/{slug}/billing");
        await Assert.That(noKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var badKey = await app.Http.GetAsync($"/ui/{slug}/billing?key=sk_not_a_key");
        await Assert.That(badKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    // ---- Arranging helpers ----------------------------------------------------------------

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<(string InstanceId, string Slug, string ApiKey)> NewInstanceAsync(BillingApp app)
    {
        using var request = PostJson("/v1/instances", OperatorKey, new { slug = "billing-" + Unique(), name = "Billing E2E" });
        var response = await app.Http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (
            body.GetProperty("instance").GetProperty("id").GetString()!,
            body.GetProperty("instance").GetProperty("slug").GetString()!,
            body.GetProperty("api_key").GetString()!);
    }

    /// <summary>A fresh instance driven through a signed checkout.session.completed webhook:
    /// Stripe customer linked, pro/active subscription in place.</summary>
    private static async Task<(string InstanceId, string Slug, string ApiKey, string Customer)> InstanceWithActiveProAsync(BillingApp app)
    {
        var (instanceId, slug, apiKey) = await NewInstanceAsync(app);
        var customer = "cus_" + Unique();
        using var response = await SendWebhookAsync(app.Http, CheckoutCompletedJson("evt_" + Unique(), instanceId, customer));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return (instanceId, slug, apiKey, customer);
    }

    private static string CheckoutCompletedJson(string eventId, string instanceId, string customer, string subscription = "sub_1") => $$"""
        {
          "id": "{{eventId}}",
          "type": "checkout.session.completed",
          "data": { "object": {
            "id": "cs_test_1", "object": "checkout_session",
            "client_reference_id": "{{instanceId}}",
            "customer": "{{customer}}",
            "subscription": "{{subscription}}",
            "metadata": { "instance_id": "{{instanceId}}" }
          } }
        }
        """;

    private static async Task<JsonElement> EnforceCheckAsync(BillingApp app, string slug, string apiKey, int currentUsers)
    {
        using var response = await app.Http.SendAsync(PostJson($"/v1/{slug}/billing/enforce-check", apiKey, new { current_users = currentUsers }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpRequestMessage Get(string path, string bearer) =>
        Send(HttpMethod.Get, path, bearer);

    private static HttpRequestMessage PostJson(string path, string? bearer, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        if (bearer is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    private static HttpRequestMessage Send(HttpMethod method, string path, string bearer)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    private static async Task<HttpResponseMessage> SendWebhookAsync(HttpClient http, string json, string secret = WebhookSecret, long ageSeconds = 0)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ageSeconds;
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{json}")));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/webhook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={signature}");
        return await http.SendAsync(request);
    }

    private static async Task SendSubscriptionUpdatedAsync(BillingApp app, string customer, string status, string priceId)
    {
        var json = $$"""
            {
              "id": "evt_{{Unique()}}",
              "type": "customer.subscription.updated",
              "data": { "object": {
                "id": "sub_1", "object": "subscription", "customer": "{{customer}}",
                "status": "{{status}}", "current_period_end": 1893456000,
                "items": [ { "price": { "id": "{{priceId}}" } } ]
              } }
            }
            """;
        using var response = await SendWebhookAsync(app.Http, json);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // ---- The dedicated app: fixture boot + Stripe env + fake Stripe ----------------------

    private static async Task<BillingApp> BootAsync(string? priceEnterprise = "price_ent")
    {
        var stripe = FakeStripe.Start();
        var database = "tsauth-billing-" + Guid.NewGuid().ToString("N")[..8];
        var root = Path.Combine(Path.GetTempPath(), "ts-auth-billing-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var engine = TerraBaseHost.CreateEmbedded(builder =>
        {
            builder.Services.AddSingleton<IStartupFilter, AuthenticatedPrincipalStartupFilter>();
            builder.Services.AddSingleton<ITenantAuthorizationHook, AllowAllAuthorizationHook>();
            builder.Configuration["TERRABASE_REGION"] = "GRU";
            builder.Configuration["TERRABASE_DATABASE_PATH"] = root;
            builder.Configuration["TERRABASE_ALLOW_SCHEMALESS"] = "true";
            builder.Configuration["TERRABASE_DATABASE_ROUTE"] = database;
            builder.Configuration["TERRABASE_DATABASE_TENANT"] = "tsauth-billing-e2e";
            builder.Configuration["TERRABASE_DATABASE_ULID"] = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
            builder.Configuration["TERRABASE_DATABASE_MODE"] = "explicit";
            builder.Configuration["TERRABASE_DATABASE_HOME_REGION"] = "GRU";
            builder.Configuration["TERRABASE_DATABASE_SELECTED_REGIONS"] = "GRU";
        });
        await engine.Application.StartAsync();

        var engineClient = new TerraScaleClient(
            new HttpClient(engine.CreateHandler()) { BaseAddress = new Uri("http://127.0.0.1/") },
            new TerraScaleClientOptions { BaseAddress = new Uri("http://127.0.0.1/"), ApiKey = "billing-e2e-key" });

        // AuthOptions loads from the process environment at build time. The shared fixture's
        // app already captured its own options, so the Stripe vars affect only this app; they
        // are restored right after the build so no later AuthOptions.Load call sees them.
        var stripeVars = new Dictionary<string, string?>
        {
            ["TS_AUTH_STRIPE_SECRET_KEY"] = "test_key",
            ["TS_AUTH_STRIPE_WEBHOOK_SECRET"] = WebhookSecret,
            ["TS_AUTH_STRIPE_API_URL"] = $"http://127.0.0.1:{stripe.Port}",
            ["TS_AUTH_PRICE_PRO"] = "price_pro",
            ["TS_AUTH_PRICE_ENTERPRISE"] = priceEnterprise,
        };
        var saved = stripeVars.Keys.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
        foreach (var (name, value) in stripeVars)
            Environment.SetEnvironmentVariable(name, value);
        WebApplication application;
        try
        {
            Environment.SetEnvironmentVariable("TS_AUTH_PUBLIC_ORIGIN", "https://auth.test");
            Environment.SetEnvironmentVariable("TS_AUTH_TERRASCALE_API_KEY", "billing-e2e-key");
            Environment.SetEnvironmentVariable("TS_AUTH_DATABASE", database);
            Environment.SetEnvironmentVariable("TS_AUTH_MASTER_KEY", Convert.ToBase64String(new byte[32]));
            Environment.SetEnvironmentVariable("TS_AUTH_OPERATOR_KEY", OperatorKey);

            var (builder, options) = TerraScale.Auth.App.CreateBuilder([]);
            builder.WebHost.UseTestServer();
            builder.Services.RemoveAll<TerraScaleClient>();
            builder.Services.AddSingleton(engineClient);
            application = builder.Build();
            TerraScale.Auth.App.Configure(application, options);
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
        await application.StartAsync();
        var http = application.GetTestClient();

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await http.GetAsync("/healthz")).IsSuccessStatusCode)
                break;
            await Task.Delay(100);
        }

        return new BillingApp(http, engineClient, database, stripe, engine, application, root);
    }

    private sealed record BillingApp(
        HttpClient Http, TerraScaleClient Sdk, string Database, FakeStripe Stripe,
        EmbeddedTerraBaseHost Engine, WebApplication Application, string Root) : IAsyncDisposable
    {
        public AuthStore Store => new(Sdk, Database);

        public async ValueTask DisposeAsync()
        {
            await Application.StopAsync();
            await Application.DisposeAsync();
            await Engine.DisposeAsync();
            Stripe.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    // ---- Fake Stripe: local HttpListener answering the three calls ts-auth makes ----------

    private sealed class FakeStripe : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly List<Request> requests = [];

        public sealed record Request(string Method, string Path, string? Authorization, Dictionary<string, string?> Form);

        public int Port { get; }

        /// <summary>Per-subscription-id response overrides for GET /v1/subscriptions/{id},
        /// used to inject a failing (unparseable) or alternate-plan fetch for one specific
        /// subscription. Keyed by id so concurrently running tests cannot cross-contaminate.</summary>
        public readonly Dictionary<string, string> SubscriptionBodies = new();

        /// <summary>Per-route canned responses overriding the defaults ("POST /v1/..."):
        /// failure-path probes (error statuses, HTML interception) point Stripe routes at these.</summary>
        public readonly Dictionary<string, (int Status, string Body)> ResponseOverrides = new();

        private FakeStripe(int port) => Port = port;

        public static FakeStripe Start()
        {
            for (var port = 18121; port <= 18139; port++)
            {
                var candidate = new FakeStripe(port);
                candidate.listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    candidate.listener.Start();
                }
                catch (HttpListenerException)
                {
                    continue;
                }
                _ = Task.Run(candidate.ListenAsync);
                return candidate;
            }
            throw new InvalidOperationException("no free port in 18121-18139 for the fake Stripe");
        }

        public Request? LastRequest(string path)
        {
            lock (requests)
                return requests.LastOrDefault(request => request.Path == path);
        }

        public int RequestCount(string path)
        {
            lock (requests)
                return requests.Count(request => request.Path == path);
        }

        private async Task ListenAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                try
                {
                    await HandleAsync(context);
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var request = context.Request;
            string? form = null;
            if (request.HasEntityBody)
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    form = await reader.ReadToEndAsync();

            var path = request.Url?.AbsolutePath ?? "";
            lock (requests)
                requests.Add(new(request.HttpMethod, path, request.Headers["Authorization"], form is null ? [] : ParseForm(form)));

            var (status, json) = ResponseOverrides.TryGetValue($"{request.HttpMethod} {path}", out var canned)
                ? canned
                : (request.HttpMethod, path) switch
            {
                ("POST", "/v1/checkout/sessions") => (200, """{"id":"cs_test_1","url":"https://checkout.example/s_1"}"""),
                ("POST", "/v1/billing_portal/sessions") => (200, """{"url":"https://portal.example/p_1"}"""),
                ("GET", var p) when p.StartsWith("/v1/subscriptions/", StringComparison.Ordinal)
                    => (200, SubscriptionBodies.GetValueOrDefault(
                        p["/v1/subscriptions/".Length..],
                        """{"id":"sub_1","customer":"cus_1","items":[{"price":{"id":"price_pro"}}],"current_period_end":1893456000}""")),
                _ => (404, """{"error":{"message":"no such route"}}"""),
            };
            var body = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }

        private static Dictionary<string, string?> ParseForm(string raw) =>
            raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : null);

        public void Dispose()
        {
            listener.Stop();
            listener.Close();
        }
    }

    // Copied from AuthServiceFixture (its boot helpers are private): the embedded engine
    // needs an authenticated principal and an allow-all tenant hook for document writes.
    private sealed class AuthenticatedPrincipalStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, continuation) =>
            {
                context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "tsauth-billing-e2e")],
                    "tsauth-billing-e2e"));
                await continuation().ConfigureAwait(false);
            });
            next(application);
        };
    }

    private sealed class AllowAllAuthorizationHook : ITenantAuthorizationHook
    {
        public ValueTask<TenantAuthorizationDecision> AuthorizeAsync(
            TenantAuthorizationRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TenantAuthorizationDecision.AllowUntil(DateTimeOffset.UtcNow.AddSeconds(10)));
    }
}
