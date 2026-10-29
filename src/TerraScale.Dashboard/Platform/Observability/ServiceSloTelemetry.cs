using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http.Features;

namespace TerraScale.Dashboard.Platform.Observability
{
    // Counters belong to one host; only the current operation flows into dependency calls.
    public sealed class ServiceSloTelemetry
    {
        private static readonly object OutcomeKey = new();
        private static readonly AsyncLocal<Outcome?> Current = new();
        private static readonly string[] UntrackedPaths = ["/health", "/healthz", "/readyz", "/metrics", "/internal", "/__test", "/css", "/js", "/fonts", "/brand", "/flags", "/favicon.ico"];
        private sealed class Outcome { public int Bad; }
        private sealed class ObservedLifetime(IHttpRequestLifetimeFeature inner, Outcome outcome) : IHttpRequestLifetimeFeature
        {
            public CancellationToken RequestAborted { get => inner.RequestAborted; set => inner.RequestAborted = value; }
            public void Abort() { Interlocked.Exchange(ref outcome.Bad, 1); inner.Abort(); }
        }
        private long good;
        private long bad;

        public static void MarkBad(HttpContext? http = null)
        {
            if ((http?.Items[OutcomeKey] as Outcome ?? Current.Value) is { } outcome)
            {
                Interlocked.Exchange(ref outcome.Bad, 1);
            }
        }

        public static void MarkFailure(Exception error)
        {
            var status = error is AccountAccessException access ? access.StatusCode :
                error is HttpRequestException { StatusCode: { } code } ? (int)code : 503;
            if (status is >= 500 or 408)
            {
                MarkBad();
            }
        }

        public async Task ObserveAsync(HttpContext http, RequestDelegate next)
        {
            var path = http.Request.Path;
            if (UntrackedPaths.Any(prefix => path.StartsWithSegments(prefix)))
            { await next(http); return; }
            var previous = Current.Value;
            var outcome = new Outcome();
            http.Items[OutcomeKey] = Current.Value = outcome;
            var lifetime = http.Features.Get<IHttpRequestLifetimeFeature>();
            if (lifetime is not null)
            {
                http.Features.Set<IHttpRequestLifetimeFeature>(new ObservedLifetime(lifetime, outcome));
            }

            try
            {
                await next(http);
                await http.Response.CompleteAsync();
            }
            catch { Interlocked.Exchange(ref outcome.Bad, 1); throw; }
            finally
            {
                // Kestrel exposes the same token for caller disconnects and service Abort().
                // Without positive transport attribution, both conservatively count as bad.
                if (outcome.Bad != 0 || http.Response.StatusCode >= 500 || http.Response.StatusCode == 408 || http.RequestAborted.IsCancellationRequested)
                {
                    Interlocked.Increment(ref bad);
                }
                else
                {
                    Interlocked.Increment(ref good);
                }

                if (lifetime is not null)
                {
                    http.Features.Set(lifetime);
                }

                Current.Value = previous;
            }
        }

        // Capture this before forwarded-header middleware can rewrite the peer address.
        public static bool PrivateScrape(HttpContext http)
        {
            return http.Connection.RemoteIpAddress is { } peer && IPAddress.IsLoopback(peer) &&
            IPAddress.TryParse(http.Request.Host.Host.Trim('[', ']'), out var host) && IPAddress.IsLoopback(host) &&
            !http.Request.Headers.Keys.Any(name => name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase));
        }

        public Task ScrapeAsync(HttpContext http, bool admitted)
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!admitted) { http.Response.StatusCode = 404; return Task.CompletedTask; }
            http.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            return http.Response.WriteAsync("# TYPE terrascale_slo_requests_total counter\n" +
                "terrascale_slo_requests_total{service=\"ts-dashboard\",outcome=\"good\"} " + Interlocked.Read(ref good).ToString(CultureInfo.InvariantCulture) + "\n" +
                "terrascale_slo_requests_total{service=\"ts-dashboard\",outcome=\"bad\"} " + Interlocked.Read(ref bad).ToString(CultureInfo.InvariantCulture) + "\n");
        }
    }
}
