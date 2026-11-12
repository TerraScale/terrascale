using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace TerraScale.Dashboard.Platform.Observability
{
    // Counters belong to one host; only the current operation flows into dependency calls.
    public sealed class ServiceSloTelemetry
    {
        private static readonly object OutcomeKey = new();
        private static readonly AsyncLocal<Outcome?> Current = new();
        private static readonly string[] UntrackedPaths = ["/health", "/healthz", "/readyz", "/metrics", "/internal", "/css", "/js", "/fonts", "/brand", "/flags", "/favicon.ico"];
        private sealed class Outcome(CancellationToken caller)
        {
            public CancellationToken Caller { get; } = caller;
            public int Bad;
            public int Abandoned;
        }
        private sealed class ObservedLifetime(IHttpRequestLifetimeFeature inner, Outcome outcome) : IHttpRequestLifetimeFeature
        {
            public CancellationToken RequestAborted { get => inner.RequestAborted; set => inner.RequestAborted = value; }
            public void Abort() { Interlocked.Exchange(ref outcome.Bad, 1); inner.Abort(); }
        }
        private long good;
        private long bad;
        private long failingDeletionEntries;
        private long organizationCreationFailures;
        private long organizationMetadataFailures;

        public void ReportOrganizationCreationFailure()
        {
            Interlocked.Increment(ref organizationCreationFailures);
        }

        public void ReportOrganizationMetadataFailure()
        {
            Interlocked.Increment(ref organizationMetadataFailures);
        }

        public void ReportDeletionCleanupFailures(int entries)
        {
            Interlocked.Exchange(ref failingDeletionEntries, entries);
        }

        public static void MarkBad(HttpContext? http = null)
        {
            if ((http?.Items[OutcomeKey] as Outcome ?? Current.Value) is { } outcome)
            {
                Interlocked.Exchange(ref outcome.Bad, 1);
            }
        }

        private static bool MarkCallerAbandoned(Exception error, Outcome? outcome)
        {
            if (outcome is null || error is not OperationCanceledException cancellation ||
                !outcome.Caller.IsCancellationRequested || cancellation.CancellationToken != outcome.Caller)
            {
                return false;
            }

            Interlocked.Exchange(ref outcome.Abandoned, 1);
            return true;
        }

        public static void MarkFailure(Exception error, HttpContext? http = null)
        {
            if (MarkCallerAbandoned(error, http?.Items[OutcomeKey] as Outcome ?? Current.Value))
            {
                return;
            }

            var status = error is AccountAccessException access ? access.StatusCode : error is DbUpdateConcurrencyException ? StatusCodes.Status412PreconditionFailed :
                error is HttpRequestException { StatusCode: { } code } ? (int)code : 503;
            if (status is >= 500 or 408)
            {
                MarkBad(http);
            }
        }

        public async Task ObserveAsync(HttpContext http, RequestDelegate next)
        {
            var path = http.Request.Path;
            if (UntrackedPaths.Any(prefix => path.StartsWithSegments(prefix)))
            { await next(http); return; }
            var previous = Current.Value;
            var outcome = new Outcome(http.RequestAborted);
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
            catch (Exception error)
            {
                if (!MarkCallerAbandoned(error, outcome))
                {
                    Interlocked.Exchange(ref outcome.Bad, 1);
                }

                throw;
            }
            finally
            {
                if (outcome.Bad != 0 || outcome.Abandoned == 0 &&
                    (http.Response.StatusCode >= 500 || http.Response.StatusCode == 408 ||
                     outcome.Caller.IsCancellationRequested || http.RequestAborted.IsCancellationRequested))
                {
                    Interlocked.Increment(ref bad);
                }
                else if (outcome.Abandoned == 0)
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
                "terrascale_slo_requests_total{service=\"ts-dashboard\",outcome=\"bad\"} " + Interlocked.Read(ref bad).ToString(CultureInfo.InvariantCulture) + "\n" +
                "# TYPE terrascale_organization_checkout_failures_total counter\n" +
                "terrascale_organization_checkout_failures_total{phase=\"creating\"} " + Interlocked.Read(ref organizationCreationFailures).ToString(CultureInfo.InvariantCulture) + "\n" +
                "terrascale_organization_checkout_failures_total{phase=\"metadata\"} " + Interlocked.Read(ref organizationMetadataFailures).ToString(CultureInfo.InvariantCulture) + "\n" +
                "# TYPE terrascale_deletion_cleanup_failing_entries gauge\n" +
                "terrascale_deletion_cleanup_failing_entries{service=\"ts-dashboard\"} " + Interlocked.Read(ref failingDeletionEntries).ToString(CultureInfo.InvariantCulture) + "\n");
        }
    }
}
