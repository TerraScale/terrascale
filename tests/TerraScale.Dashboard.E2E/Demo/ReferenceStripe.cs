using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.E2E
{
    // Only the source fixture's allocated customer is intercepted. Everything
    // else retains the existing Stripe/reporting protocol fixtures. Production
    // InvoiceService still serializes requests and parses all provider responses.
    internal sealed class ReferenceStripeTransport(IHttpClientFactory original) : HttpMessageHandler
    {
        internal const string CustomerId = "cus_go_reference_labs";
        private readonly HttpClient fallback = original.CreateClient("platform");
        private static readonly IReadOnlyList<SourceInvoice> invoices = Invoices();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = QueryHelpers.ParseQuery(uri.Query);
            var sourceCustomer = uri.AbsolutePath == "/v1/customers/" + CustomerId;
            var sourceInvoices = uri.AbsolutePath == "/v1/invoices" && query.GetValueOrDefault("customer") == CustomerId;
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Host != "api.stripe.com" ||
                request.Method != HttpMethod.Get || !sourceCustomer && !sourceInvoices)
            {
                return ForwardAsync(request, cancellationToken);
            }

            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: "sk_test_demo" })
            {
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, new { error = new { type = "authentication_error", message = "Invalid fixture Stripe credential." } }));
            }

            if (sourceCustomer)
            {
                if (query.GetValueOrDefault("expand[0]") != "invoice_settings.default_payment_method")
                {
                    return Task.FromResult(Json(HttpStatusCode.BadRequest, new { error = new { type = "invalid_request_error", message = "The card summary must use the official expanded customer read." } }));
                }
                // internal/app/demo_fixture.go stores exactly Visa4242, expiry08/28.
                // This allocated customer only adapts those captured source facts
                // to the real SDK response; it grants no production entitlement.
                return Task.FromResult(Json(HttpStatusCode.OK, new
                {
                    id = CustomerId,
                    @object = "customer",
                    metadata = new { organization_id = ReferenceFixtures.LabsId },
                    invoice_settings = new
                    {
                        default_payment_method = new
                        {
                            id = "pm_go_reference_labs",
                            @object = "payment_method",
                            customer = CustomerId,
                            type = "card",
                            card = new { brand = "visa", last4 = "4242", exp_month = 8, exp_year = 2028 }
                        }
                    }
                }));
            }
            if (query.GetValueOrDefault("limit") != "100")
            {
                return Task.FromResult(Json(HttpStatusCode.BadRequest, new { error = new { type = "invalid_request_error", message = "Invoice history must request its bounded SDK page size." } }));
            }

            var cursor = query.GetValueOrDefault("starting_after").ToString();
            var index = cursor.Length == 0 ? 0 : invoices.ToList().FindIndex(row => row.Id == cursor) + 1;
            if (cursor.Length > 0 && index == 0)
            {
                return Task.FromResult(Json(HttpStatusCode.BadRequest, new { error = new { type = "invalid_request_error", message = "Unknown source invoice cursor." } }));
            }

            var page = invoices.Skip(index).Take(7).ToArray();
            return Task.FromResult(Json(HttpStatusCode.OK, new
            {
                @object = "list",
                url = "/v1/invoices",
                has_more = index + page.Length < invoices.Count,
                data = page.Select(row => new
                {
                    id = row.Id,
                    @object = "invoice",
                    customer = CustomerId,
                    number = row.Id,
                    status = row.Status,
                    amount_due = row.Amount,
                    amount_remaining = row.Status == "open" ? row.Amount : 0,
                    currency = "usd",
                    created = row.CreatedAt.ToUnixTimeSeconds(),
                    period_start = row.PeriodStart.ToUnixTimeSeconds(),
                    period_end = row.PeriodEnd.ToUnixTimeSeconds()
                }).ToArray()
            }));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fallback.Dispose();
            }

            base.Dispose(disposing);
        }

        private async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // HttpClient has already marked the incoming request as sent before
            // calling this handler. A second client needs a fresh request while
            // the original SDK message and its content remain caller-owned.
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers)
            {
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            foreach (var option in request.Options)
            {
                forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            }

            if (request.Content is not null)
            {
                forwarded.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                foreach (var header in request.Content.Headers)
                {
                    forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            var response = await fallback.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.RequestMessage = request;
            return response;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object value)
        {
            return new(status)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        }

        private static IReadOnlyList<SourceInvoice> Invoices()
        {
            // internal/app/demo_fixture.go:marioInvoices, including historical
            // amounts, exact periods, original invoice IDs and its void invoice.
            var rows = new List<SourceInvoice>
            {
                new("in_01JCA2QG4M6P8S1V3X5Z7B9DF", "paid", 2900, At(2026, 9, 1, 8, 2), At(2026, 8, 1), At(2026, 9, 1)),
                new("in_01JCA3RH5N7Q9T2W4Y6A8C2EG", "open", 4140, At(2026, 10, 1, 8), At(2026, 9, 1), At(2026, 10, 1))
            };
            for (var index = 1; index <= 12; index++)
            {
                var end = At(2026, 8, 1).AddMonths(-index);
                var id = $"in_01JC{end.Year:D4}{end.Month:D2}HIST{index:D2}Q7T9V2X5";
                rows.Add(new(id, index == 7 ? "void" : "paid", 2900 + index % 3 * 380, end.AddHours(8), end.AddMonths(-1), end));
            }
            return rows;
        }

        private static DateTimeOffset At(int year, int month, int day, int hour = 0, int minute = 0)
        {
            return new(year, month, day, hour, minute, 0, TimeSpan.Zero);
        }

        private sealed record SourceInvoice(string Id, string Status, long Amount, DateTimeOffset CreatedAt, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd);
    }

    internal sealed class ReferenceHttpFactory(ReferenceStripeTransport transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new(transport, disposeHandler: false);
        }
    }
}
