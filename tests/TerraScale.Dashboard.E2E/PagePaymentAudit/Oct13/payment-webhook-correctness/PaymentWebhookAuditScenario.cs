using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stripe;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;
using AccountService = TerraScale.Dashboard.Shared.Accounts.AccountService;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.PaymentWebhookCorrectness;

// Each journey gets two real app hosts and its own existing, synthetic providers.
// The wrapper cannot forward to a live network: its fallback is DemoEnvironment's transport.
internal sealed class PaymentWebhookAuditScenario : IAsyncDisposable
{
    private WebApplication? first;
    private WebApplication? second;
    public string Root { get; private set; } = "";
    public Uri Origin { get; private set; } = null!;
    public Uri ReplicaOrigin { get; private set; } = null!;
    public PaymentWebhookAuditState State { get; private set; } = null!;
    public PaymentWebhookAuditTransport Stripe { get; private set; } = null!;

    public static async Task<PaymentWebhookAuditScenario> StartAsync()
    {
        var scenario = new PaymentWebhookAuditScenario();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!System.IO.File.Exists(Path.Combine(root.FullName, "TerraScale.Dashboard.slnx")))
            root = root.Parent ?? throw new DirectoryNotFoundException("Run the audit from this checkout.");
        var environment = new DemoEnvironment();
        scenario.Root = root.FullName;
        await environment.InitializeAsync();
        try
        {
            scenario.first = environment.CreateTestHost(root.FullName, 0, "http://127.0.0.1", scenario.Configure);
            await scenario.first.StartAsync();
            scenario.Origin = Address(scenario.first);
            scenario.second = environment.CreateTestHost(root.FullName, 0, scenario.Origin.ToString().TrimEnd('/'), scenario.Configure);
            await scenario.second.StartAsync();
            scenario.ReplicaOrigin = Address(scenario.second);
            return scenario;
        }
        catch { await scenario.DisposeAsync(); throw; }
    }

    private void Configure(WebApplicationBuilder builder)
    {
        State ??= new((IDashboardState)builder.Services.Last(item => item.ServiceType == typeof(IDashboardState)).ImplementationInstance!);
        Stripe ??= new((IHttpClientFactory)builder.Services.Last(item => item.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!);
        builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(State));
        builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new TestHttpFactory(Stripe)));
    }

    public HttpClient CreateClient(bool replica = false) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer()
    })
    { BaseAddress = replica ? ReplicaOrigin : Origin, Timeout = TimeSpan.FromSeconds(30) };

    public string EventBody(string type = "customer.subscription.updated", string id = "evt_payment_audit_replay", long created = 1)
    {
        var binding = State.Billing ?? throw new InvalidOperationException("Create the local checkout first.");
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

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        if (second is not null) { await second.StopAsync(); await second.DisposeAsync(); }
        if (first is not null) { await first.StopAsync(); await first.DisposeAsync(); }
        Stripe?.Dispose();
    }
}

internal sealed class PaymentWebhookAuditState(IDashboardState underlying) : IDashboardState
{
    public BillingBinding? Billing { get; private set; }
    public bool FailNextOrganizationWrite { get; set; }
    public int InterruptedWrites { get; private set; }
    public Task InitializeAsync(CancellationToken cancellationToken) => underlying.InitializeAsync(cancellationToken);
    public Task CheckReadyAsync(CancellationToken cancellationToken) => underlying.CheckReadyAsync(cancellationToken);
    public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken) => underlying.GetAsync(collection, id, cancellationToken);
    public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken cancellationToken) => underlying.ListAsync(collection, cancellationToken);
    public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken cancellationToken) => underlying.QueryPageAsync(collection, query, cancellationToken);
    public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken)
    {
        if (collection == AccountService.OrganizationsCollection && id == Billing?.OrganizationId && FailNextOrganizationWrite)
        {
            FailNextOrganizationWrite = false;
            InterruptedWrites++;
            throw new IOException("Local audit: billing persisted, organization projection interrupted.");
        }
        await underlying.PutAsync(collection, id, document, precondition, cancellationToken);
        if (collection == StripeBilling.Collection) Billing = document.Deserialize<BillingBinding>()!;
    }
    public Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken) => underlying.DeleteAsync(collection, id, version, cancellationToken);

    public async Task AgeFreeReservationAsync()
    {
        var binding = Billing ?? throw new InvalidOperationException("Complete the Free setup first.");
        if (binding.Plan != "free" || binding.SubscriptionId.Length != 0 || !binding.PaymentValidated)
            throw new InvalidOperationException("Only a completed local Free setup can be aged.");
        var document = await underlying.GetAsync(StripeBilling.Collection, binding.OrganizationId, default)
            ?? throw new InvalidOperationException("Missing fixture binding.");
        binding.StartedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await PutAsync(StripeBilling.Collection, binding.OrganizationId, JsonSerializer.SerializeToElement(binding), TerraBasePrecondition.Matches(document.Version), default);
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
        if (request.RequestUri is not { Scheme: "https", Host: "api.stripe.com", IsDefaultPort: true })
            throw new HttpRequestException("The audit accepts only the local Stripe protocol fixture.");
        using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
        {
            forwarded.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            foreach (var header in request.Content.Headers) forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        var path = request.RequestUri.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/v1/checkout/sessions")
        {
            var form = QueryHelpers.ParseQuery(await forwarded.Content!.ReadAsStringAsync(cancellationToken));
            LastTrialDays = form.GetValueOrDefault("subscription_data[trial_period_days]").ToString();
            LastCheckoutKey = request.Headers.GetValues("Idempotency-Key").Single();
        }
        var response = await fallback.SendAsync(forwarded, cancellationToken);
        if (!response.IsSuccessStatusCode) return response;
        if (request.Method == HttpMethod.Get && path.StartsWith("/v1/subscriptions/", StringComparison.Ordinal))
        {
            var subscription = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
            subscription["status"] = Status;
            var item = subscription["items"]!["data"]![0]!;
            item["quantity"] = Quantity;
            if (PriceCurrency is not null) item["price"]!["currency"] = PriceCurrency;
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

    private static HttpResponseMessage Json(JsonNode value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };

    protected override void Dispose(bool disposing)
    {
        if (disposing) fallback.Dispose();
        base.Dispose(disposing);
    }
}
