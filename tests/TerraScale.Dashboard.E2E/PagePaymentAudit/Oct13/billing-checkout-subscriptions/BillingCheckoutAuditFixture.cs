using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TUnit.Core.Interfaces;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.BillingCheckout
{
    public sealed class BillingCheckoutAuditFixture : IAsyncInitializer, IAsyncDisposable
    {
        private WebApplication? app;
        public string Root { get; } = FindRoot();
        public Uri Origin { get; private set; } = null!;
        public string Evidence => Path.Combine(Root, "docs/reviews/2026-10-13/billing-checkout-subscriptions/evidence");

        public Task InitializeAsync()
        {
            return StartAsync(0, "http://127.0.0.1");
        }

        public async Task StartAsync(int port, string origin)
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            var invoices = new AuditInvoiceTransport();
            app = environment.CreateTestHost(Root, port, origin, builder =>
            {
                var existing = (IHttpClientFactory)builder.Services.Single(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                invoices.Underlying = existing.CreateClient("platform");
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(invoices));
            });
            // The checkout has no generated JS. Serve the exact committed script inputs;
            // the normal build copies Datastar unchanged and bundles button-loading with workspace scope.
            app.MapGet("/js/datastar.js", () => Results.File(Path.Combine(Root, "src/TerraScale.Dashboard/web/assets/js/vendor/datastar.js"), "text/javascript"));
            app.MapGet("/js/button-loading.js", () => Results.File(Path.Combine(Root, "src/TerraScale.Dashboard/web/assets/js/button-loading.js"), "text/javascript"));
            app.MapGet("/js/workspace.js", () => Results.File(Path.Combine(Root, "src/TerraScale.Dashboard/web/assets/js/workspace.js"), "text/javascript"));
            app.MapPost("/__audit/billing/invoices", async (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
            {
                var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form) || context.Role != "owner")
                {
                    return Results.StatusCode(403);
                }

                var binding = await billing.ReadBindingAsync(context, http.RequestAborted);
                var scenario = form["scenario"].ToString();
                if (binding?.CustomerId is not { Length: > 0 } || scenario is not ("partial-payment" or "offline" or "normal"))
                {
                    return Results.BadRequest();
                }

                invoices.Scenarios[binding.CustomerId] = scenario;
                return Results.NoContent();
            });
            await app.StartAsync();
            Origin = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()
                .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
            Directory.CreateDirectory(Evidence);
        }

        public HttpClient CreateClient(CookieContainer? cookies = null)
        {
            return new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = cookies ?? new() })
            { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(30) };
        }

        public async ValueTask DisposeAsync()
        {
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
        }

        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TerraScale.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Run this audit from its bound checkout.");
        }
    }

    internal sealed class AuditInvoiceTransport : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient Underlying { get; set; } = null!;
        public ConcurrentDictionary<string, string> Scenarios { get; } = new();
        public HttpClient CreateClient(string name)
        {
            return new(this, disposeHandler: false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "api.stripe.com")
            {
                return await ForwardAsync(request, cancellationToken);
            }

            var customer = QueryHelpers.ParseQuery(request.RequestUri.Query).GetValueOrDefault("customer").ToString();
            if (request.Method != HttpMethod.Get || request.RequestUri.AbsolutePath != "/v1/invoices" ||
                !Scenarios.TryGetValue(customer, out var scenario) || scenario == "normal")
            {
                return await ForwardAsync(request, cancellationToken);
            }

            if (scenario == "offline")
            {
                return new(HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("{\"error\":{\"message\":\"Local invoice outage\"}}", System.Text.Encoding.UTF8, "application/json") };
            }

            var value = new
            {
                @object = "list",
                has_more = false,
                data = new[] { new { id = "in_local_audit", @object = "invoice", customer, number = "AUDIT-001", status = "open",
                    amount_due = 1234, amount_paid = 1000, amount_remaining = 234,
                    currency = "usd", created = 1791763200L, period_start = 0, period_end = 0 } }
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
        }

        private async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
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
            return await Underlying.SendAsync(forwarded, cancellationToken);
        }
    }

}
