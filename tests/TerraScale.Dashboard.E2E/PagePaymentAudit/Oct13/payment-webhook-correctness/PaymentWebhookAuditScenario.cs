using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stripe;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.PaymentWebhookCorrectness
{
    // Each journey gets two real app hosts and its own existing, synthetic providers.
    // The wrapper cannot forward to a live network: its fallback is DemoEnvironment's transport.
    internal sealed class PaymentWebhookAuditScenario : IAsyncDisposable
    {
        private WebApplication? first;
        private WebApplication? second;
        private DemoEnvironment environment = null!;
        public string Root { get; private set; } = "";
        public Uri Origin { get; private set; } = null!;
        public Uri ReplicaOrigin => replicaOrigin ?? throw new InvalidOperationException("This scenario started without a replica host.");
        private Uri? replicaOrigin;
        public PaymentWebhookAuditTransport Stripe { get; private set; } = null!;

        public static async Task<PaymentWebhookAuditScenario> StartAsync(bool replica = true)
        {
            var scenario = new PaymentWebhookAuditScenario();
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (!System.IO.File.Exists(Path.Combine(root.FullName, "TerraScale.slnx")))
            {
                root = root.Parent ?? throw new DirectoryNotFoundException("Run the audit from this checkout.");
            }

            var environment = new DemoEnvironment();
            scenario.environment = environment;
            scenario.Root = root.FullName;
            await environment.InitializeAsync();
            try
            {
                scenario.first = await environment.CreateTestHostAsync(root.FullName, 0, "http://127.0.0.1", scenario.Configure);
                await scenario.first.StartAsync();
                scenario.Origin = Address(scenario.first);
                if (replica)
                {
                    scenario.second = await environment.CreateTestHostAsync(root.FullName, 0, scenario.Origin.ToString().TrimEnd('/'), scenario.Configure);
                    await scenario.second.StartAsync();
                    scenario.replicaOrigin = Address(scenario.second);
                }
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }

        private void Configure(WebApplicationBuilder builder)
        {
            Stripe ??= new((IHttpClientFactory)builder.Services.Last(item => item.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!);
            builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new TestHttpFactory(Stripe)));
        }

        public HttpClient CreateClient(bool replica = false)
        {
            return new(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = TestHttps.Validate,
                AllowAutoRedirect = false,
                CookieContainer = new CookieContainer()
            })
            { BaseAddress = replica ? ReplicaOrigin : Origin, Timeout = TimeSpan.FromSeconds(30) };
        }

        public Task<DashboardDbContext> StorageAsync()
        {
            return environment.StorageAsync();
        }

        public async Task<BillingBinding> BillingAsync()
        {
            await using var db = await environment.StorageAsync();
            return await db.Billing.AsNoTracking().SingleAsync(row => row.OrganizationId != ReferenceFixtures.LabsId);
        }

        public async Task<string> EventBodyAsync(string type = "customer.subscription.updated", string id = "evt_payment_audit_replay", long created = 1)
        {
            var binding = await BillingAsync();
            return JsonSerializer.Serialize(new
            {
                id,
                @object = "event",
                api_version = StripeConfiguration.ApiVersion,
                created,
                livemode = false,
                type,
                data = new
                {
                    @object = new
                    {
                        id = type.StartsWith("invoice.", StringComparison.Ordinal) ? "in_payment_audit" : binding.SubscriptionId,
                        @object = type.StartsWith("invoice.", StringComparison.Ordinal) ? "invoice" : "subscription",
                        customer = binding.CustomerId,
                        status = "active",
                        metadata = new { organization_id = binding.OrganizationId, plan = "business" }
                    }
                }
            });
        }

        public static async Task<HttpResponseMessage> SendAsync(HttpClient client, string body, bool legacy = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, legacy ? "/api/auth/stripe/webhook" : "/api/v1/billing/stripe/webhook")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("Stripe-Signature", EventUtility.GenerateSignatureHeader(body, "whsec_demo"));
            return await client.SendAsync(request);
        }

        private static Uri Address(WebApplication app)
        {
            return new(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            if (second is not null) { await second.StopAsync(); await second.DisposeAsync(); }
            if (first is not null) { await first.StopAsync(); await first.DisposeAsync(); }
            Stripe?.Dispose();
        }
    }

    internal sealed class PaymentWebhookAuditTransport(IHttpClientFactory original) : HttpMessageHandler
    {
        private readonly HttpClient fallback = original.CreateClient("platform");
        public string Status { get; set; } = "active";
        public string? PriceCurrency { get; set; }
        public bool FractionalPrice { get; set; }
        public long Quantity { get; set; } = 1;
        public string? InvoiceCurrency { get; set; }
        public long InvoiceAmount { get; set; } = 1500;
        public int SubscriptionReads { get; private set; }
        public string LastTrialDays { get; private set; } = "";
        public string LastCheckoutKey { get; private set; } = "";
        public bool HoldNextSubscriptionRead { get; set; }
        public TaskCompletionSource SubscriptionReadHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSubscriptionRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new HttpRequestException("The audit requires an outbound URI.");
            var organizationPlan = uri is { Scheme: "https", Host: "api.terrascale.tech" or "gru.api.terrascale.tech", IsDefaultPort: true }
                && uri.AbsolutePath.Split('/') is ["", "admin", "organizations", _, "plan"];
            if (!organizationPlan && uri is not { Scheme: "https", Host: "api.stripe.com", IsDefaultPort: true })
            {
                throw new HttpRequestException("The audit accepts only the local Stripe protocol fixture.");
            }

            using var forwarded = new HttpRequestMessage(request.Method, uri);
            foreach (var header in request.Headers)
            {
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                forwarded.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                foreach (var header in request.Content.Headers)
                {
                    forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            var path = uri.AbsolutePath;
            if (organizationPlan)
            {
                return await fallback.SendAsync(forwarded, cancellationToken);
            }
            if (request.Method == HttpMethod.Post && path == "/v1/checkout/sessions")
            {
                var form = QueryHelpers.ParseQuery(await forwarded.Content!.ReadAsStringAsync(cancellationToken));
                LastTrialDays = form.GetValueOrDefault("subscription_data[trial_period_days]").ToString();
                LastCheckoutKey = request.Headers.GetValues("Idempotency-Key").Single();
            }
            var response = await fallback.SendAsync(forwarded, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return response;
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/subscriptions/", StringComparison.Ordinal))
            {
                var subscription = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                subscription["status"] = Status;
                var item = subscription["items"]!["data"]![0]!;
                item["quantity"] = Quantity;
                if (PriceCurrency is not null)
                {
                    item["price"]!["currency"] = PriceCurrency;
                }

                if (FractionalPrice) { item["price"]!["unit_amount"] = null; item["price"]!["unit_amount_decimal"] = "500.5"; }
                SubscriptionReads++;
                response.Dispose();
                if (HoldNextSubscriptionRead)
                {
                    HoldNextSubscriptionRead = false;
                    SubscriptionReadHeld.TrySetResult();
                    await ReleaseSubscriptionRead.Task.WaitAsync(cancellationToken);
                }
                return Json(subscription);
            }
            if (request.Method == HttpMethod.Get && path == "/v1/invoices" && InvoiceCurrency is not null)
            {
                var listing = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                var invoice = listing["data"]![0]!.DeepClone();
                invoice["amount_due"] = InvoiceAmount;
                invoice["currency"] = InvoiceCurrency;
                response.Dispose();
                return Json(new JsonObject { ["object"] = "list", ["has_more"] = false, ["data"] = new JsonArray(invoice) });
            }
            return response;
        }

        private static HttpResponseMessage Json(JsonNode value)
        {
            return new(HttpStatusCode.OK)
            { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fallback.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
