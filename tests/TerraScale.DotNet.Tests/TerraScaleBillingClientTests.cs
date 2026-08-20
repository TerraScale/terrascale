using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleBillingClientTests
{
    [Test]
    public async Task BillingOperations_SendPublicRoutesBodiesAndIdempotencyKey()
    {
        using var handler = new BillingRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            BearerTokenProvider = static _ => ValueTask.FromResult("test-token"),
            IdempotencyKeyFactory = () => "factory-key"
        });

        var plans = await client.Billing.GetPricingPlansAsync();
        var account = await client.Billing.GetAccountAsync("organization / a");
        var invoices = await client.Billing.ListInvoicesAsync("organization / a", skip: 2, take: 10);
        var members = await client.Billing.ListAccessAsync("organization / a");
        var updated = await client.Billing.UpdateAccessAsync(
            "organization / a",
            "user / a",
            new TerraScaleUpdateBillingAccessRequest(true),
            "retry-key");

        await Assert.That(handler.Requests).Count().IsEqualTo(5);
        await Assert.That(handler.Requests[0]).IsEqualTo(new BillingRecordedRequest(
            HttpMethod.Get, "https://api.example.invalid/api/v1/pricing/plans", null, null));
        await Assert.That(handler.Requests[1]).IsEqualTo(new BillingRecordedRequest(
            HttpMethod.Get, "https://api.example.invalid/api/v1/organizations/organization %2F a/billing", null, null));
        await Assert.That(handler.Requests[2]).IsEqualTo(new BillingRecordedRequest(
            HttpMethod.Get, "https://api.example.invalid/api/v1/organizations/organization %2F a/billing/invoices?skip=2&take=10", null, null));
        await Assert.That(handler.Requests[3]).IsEqualTo(new BillingRecordedRequest(
            HttpMethod.Get, "https://api.example.invalid/api/v1/organizations/organization %2F a/billing/access", null, null));
        await Assert.That(handler.Requests[4]).IsEqualTo(new BillingRecordedRequest(
            HttpMethod.Put, "https://api.example.invalid/api/v1/organizations/organization %2F a/billing/access/user %2F a", "{\"enabled\":true}", "retry-key"));
        await Assert.That(handler.PricingRequestIncludedCredentials).IsFalse();
        await Assert.That(plans[0].IncludedUsage[0].Quantity).IsEqualTo(1_000_000L);
        await Assert.That(account.BillingInterval).IsEqualTo("month");
        await Assert.That(invoices.TotalCount).IsEqualTo(1);
        await Assert.That(members[0].Enabled).IsTrue();
        await Assert.That(updated.UserId).IsEqualTo("user-a");
    }

    [Test]
    public async Task BillingModels_SerializeAndDeserializePublicContractFields()
    {
        var request = new TerraScaleUpdateBillingAccessRequest(true);
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var plan = JsonSerializer.Deserialize<TerraScalePricingPlan>("""
            {"planId":"plan-a","name":"Pro","currency":"USD","monthlyBaseCents":1000,"annualBaseCents":10000,"includedUsage":[{"name":"operations","quantity":1000000,"unit":"requests"}],"key":"pro","provider":"polar","monthlyPriceId":"polar-month","annualPriceId":"polar-year","monthlyPolarProductId":"product-month","annualPolarProductId":"product-year","monthlyStripePriceId":null,"annualStripePriceId":"price-year","monthlyTrialDays":14,"annualTrialDays":30,"visible":true,"maxRegions":3,"operationOverageCentsPerMillion":100,"storageOverageCentsPerGbRegion":20}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await Assert.That(json).IsEqualTo("{\"enabled\":true}");
        await Assert.That(plan).IsNotNull();
        await Assert.That(plan!.AnnualStripePriceId).IsEqualTo("price-year");
        await Assert.That(plan.IncludedUsage[0].Unit).IsEqualTo("requests");
        await Assert.That(plan.Provider).IsEqualTo("polar");
        await Assert.That(plan.MonthlyPriceId).IsEqualTo("polar-month");
        await Assert.That(plan.AnnualPolarProductId).IsEqualTo("product-year");
        await Assert.That(plan.MonthlyTrialDays).IsEqualTo(14);
        await Assert.That(plan.AnnualTrialDays).IsEqualTo(30);
    }

    [Test]
    public async Task CreateCheckoutSessionAsync_SendsOnlyCanonicalProviderNeutralFieldsAndRejectsIllegalCombinations()
    {
        using var handler = new BillingRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            BearerTokenProvider = static _ => ValueTask.FromResult("test-token")
        });

#pragma warning disable CS0618 // Compatibility members must be proven absent from the wire contract.
        var checkoutRequest = new TerraScaleCreateCheckoutSessionRequest
        {
            PlanId = "plan-a",
            BillingInterval = "annual",
            Provider = "polar",
            SuccessUrl = "https://app.example.test/checkout/success",
            CancelUrl = "https://app.example.test/checkout/cancel",
            CustomerEmail = "customer@example.test"
        };
#pragma warning restore CS0618

        var checkout = await client.Billing.CreateCheckoutSessionAsync(
            "organization-a",
            checkoutRequest);
        var bothPlanAndPrice = await Assert.ThrowsAsync<ArgumentException>(() => client.Billing.CreateCheckoutSessionAsync(
            "organization-a",
            new TerraScaleCreateCheckoutSessionRequest("legacy-price") { PlanId = "plan-a" }));
        var invalidInterval = await Assert.ThrowsAsync<ArgumentException>(() => client.Billing.CreateCheckoutSessionAsync(
            "organization-a",
            new TerraScaleCreateCheckoutSessionRequest() { BillingInterval = "weekly" }));
        var invalidProvider = await Assert.ThrowsAsync<ArgumentException>(() => client.Billing.CreateCheckoutSessionAsync(
            "organization-a",
            new TerraScaleCreateCheckoutSessionRequest() { Provider = "other" }));
        var invalidQuantity = await Assert.ThrowsAsync<ArgumentException>(() => client.Billing.CreateCheckoutSessionAsync(
            "organization-a",
            new TerraScaleCreateCheckoutSessionRequest(Quantity: 0)));

        await Assert.That(checkout.SessionId).IsEqualTo("checkout-a");
        await Assert.That(handler.Requests).Count().IsEqualTo(1);
        await Assert.That(handler.Requests[0].Body).IsEqualTo("{\"priceId\":null,\"quantity\":null,\"planId\":\"plan-a\",\"billingInterval\":\"annual\",\"provider\":\"polar\"}");
        await Assert.That(bothPlanAndPrice).IsNotNull();
        await Assert.That(invalidInterval).IsNotNull();
        await Assert.That(invalidProvider).IsNotNull();
        await Assert.That(invalidQuantity).IsNotNull();
    }

    [Test]
    public async Task BillingAccountAndInvoice_KeepLegacyPositionalFieldsCompatible()
    {
        var account = new TerraScaleBillingAccount("account-a", "organization-a", "plan-a", "active", "month", "stripe");
        var invoice = new TerraScaleBillingInvoice(
            "invoice-a", "account-a", 1000, "USD", "paid", DateTimeOffset.UnixEpoch, RefundedAmountCents: 250);
        var deserialized = JsonSerializer.Deserialize<TerraScaleBillingInvoice>("""
            {"invoiceId":"invoice-a","billingAccountId":"account-a","amountCents":1000,"currency":"USD","status":"paid","issuedAt":"1970-01-01T00:00:00+00:00"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await Assert.That(account.Provider).IsEqualTo("stripe");
        await Assert.That(invoice.RefundedAmountCents).IsEqualTo(250L);
        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized!.RefundedAmountCents).IsEqualTo(0L);
    }

    [Test]
    public async Task UpdateAccessAsync_RejectsInvalidArgumentsAndForbiddenResponseIsTyped()
    {
        using var validationHttpClient = new HttpClient(new BillingRecordingHandler());
        var validationClient = new TerraScaleClient(
            validationHttpClient,
            new TerraScaleClientOptions { BaseAddress = new Uri("https://api.example.invalid/") });
        var invalidKey = await Assert.ThrowsAsync<ArgumentException>(() => validationClient.Billing.UpdateAccessAsync(
            "organization-a", "user-a", new TerraScaleUpdateBillingAccessRequest(true), new string('x', 129)));

        using var forbiddenHandler = new BillingRecordingHandler(forbidden: true);
        using var forbiddenClient = new HttpClient(forbiddenHandler);
        var client = new TerraScaleClient(forbiddenClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            BearerTokenProvider = static _ => ValueTask.FromResult("test-token")
        });
        var forbidden = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Billing.ListAccessAsync("organization-a"));

        await Assert.That(invalidKey).IsNotNull();
        await Assert.That(forbidden!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(forbidden.ErrorCode).IsEqualTo("billing_access_denied");
        await Assert.That(forbidden.RequestId).IsEqualTo("request-a");
    }

    [Test]
    public async Task UpdateAccessAsync_RetriesTransientFailureWithExplicitIdempotencyKeyWithoutFactory()
    {
        using var handler = new TransientBillingAccessHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            BearerTokenProvider = static _ => ValueTask.FromResult("test-token"),
            MaxRetryAttempts = 1,
            RetryDelay = TimeSpan.Zero
        });

        var updated = await client.Billing.UpdateAccessAsync(
            "organization-a",
            "user-a",
            new TerraScaleUpdateBillingAccessRequest(true),
            "explicit-retry-key");

        await Assert.That(updated.Enabled).IsTrue();
        await Assert.That(handler.IdempotencyKeys).Count().IsEqualTo(2);
        await Assert.That(handler.IdempotencyKeys.All(key => key == "explicit-retry-key")).IsTrue();
    }

    [Test]
    public async Task UpdateAccessAsync_DoesNotRetryTransientFailureWithoutReplayKey()
    {
        using var handler = new TransientBillingAccessHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            BearerTokenProvider = static _ => ValueTask.FromResult("test-token"),
            MaxRetryAttempts = 1,
            RetryDelay = TimeSpan.Zero
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.Billing.UpdateAccessAsync(
            "organization-a",
            "user-a",
            new TerraScaleUpdateBillingAccessRequest(true)));

        await Assert.That(exception).IsNotNull();
        await Assert.That(handler.IdempotencyKeys).Count().IsEqualTo(1);
        await Assert.That(handler.IdempotencyKeys[0]).IsNull();
    }

    private sealed class BillingRecordingHandler(bool forbidden = false) : HttpMessageHandler
    {
        public List<BillingRecordedRequest> Requests { get; } = [];

        public bool PricingRequestIncludedCredentials { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/pricing/plans")
            {
                PricingRequestIncludedCredentials = request.Headers.Authorization is not null ||
                    request.Headers.Contains("X-TerraScale-Auth-Key") ||
                    request.Headers.Contains("X-TerraScale-Project-Id");
            }

            Requests.Add(new BillingRecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null));

            if (forbidden)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"code\":\"billing_access_denied\",\"message\":\"Billing access is required.\"}", Encoding.UTF8, "application/json"),
                    Headers = { { "X-Request-Id", "request-a" } }
                };
            }

            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/pricing/plans" => "[{\"planId\":\"plan-a\",\"name\":\"Pro\",\"currency\":\"USD\",\"monthlyBaseCents\":1000,\"annualBaseCents\":10000,\"includedUsage\":[{\"name\":\"operations\",\"quantity\":1000000,\"unit\":\"requests\"}],\"key\":\"pro\",\"provider\":\"polar\",\"monthlyPriceId\":\"polar-month\",\"annualPriceId\":\"polar-year\",\"monthlyPolarProductId\":\"product-month\",\"annualPolarProductId\":\"product-year\",\"monthlyStripePriceId\":null,\"annualStripePriceId\":\"price-year\",\"monthlyTrialDays\":14,\"annualTrialDays\":30,\"visible\":true,\"maxRegions\":3,\"operationOverageCentsPerMillion\":100,\"storageOverageCentsPerGbRegion\":20}]",
                "/api/v1/organizations/organization-a/billing/checkout-sessions" => "{\"sessionId\":\"checkout-a\",\"url\":\"https://checkout.example.test/session\"}",
                "/api/v1/organizations/organization%20%2F%20a/billing" => "{\"billingAccountId\":\"account-a\",\"organizationId\":\"organization-a\",\"planId\":\"plan-a\",\"status\":\"active\",\"billingInterval\":\"month\"}",
                "/api/v1/organizations/organization%20%2F%20a/billing/invoices" => "{\"items\":[{\"invoiceId\":\"invoice-a\",\"billingAccountId\":\"account-a\",\"amountCents\":1000,\"currency\":\"USD\",\"status\":\"paid\",\"issuedAt\":\"2026-08-05T12:00:00+00:00\",\"periodStart\":null,\"periodEnd\":null,\"hostedInvoiceUrl\":null,\"invoicePdf\":null}],\"totalCount\":1,\"nextCursor\":null}",
                _ when request.Method == HttpMethod.Put => "{\"userId\":\"user-a\",\"email\":\"user@example.test\",\"displayName\":\"User A\",\"role\":\"member\",\"enabled\":true,\"grantedAt\":\"2026-08-05T12:00:00+00:00\",\"grantedByUserId\":\"owner-a\"}",
                _ => "[{\"userId\":\"user-a\",\"email\":\"user@example.test\",\"displayName\":\"User A\",\"role\":\"member\",\"enabled\":true,\"grantedAt\":\"2026-08-05T12:00:00+00:00\",\"grantedByUserId\":\"owner-a\"}]"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TransientBillingAccessHandler : HttpMessageHandler
    {
        public List<string?> IdempotencyKeys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IdempotencyKeys.Add(request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null);
            if (IdempotencyKeys.Count == 1)
            {
                throw new HttpRequestException("transient transport failure");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"userId\":\"user-a\",\"email\":\"user@example.test\",\"displayName\":\"User A\",\"role\":\"member\",\"enabled\":true,\"grantedAt\":\"2026-08-05T12:00:00+00:00\",\"grantedByUserId\":\"owner-a\"}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record BillingRecordedRequest(HttpMethod Method, string Uri, string? Body, string? IdempotencyKey);
}
