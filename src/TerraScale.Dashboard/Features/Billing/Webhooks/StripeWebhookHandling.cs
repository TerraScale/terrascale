using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace TerraScale.Dashboard.Features.Billing.Webhooks
{
    internal static class StripeWebhookHandling
    {
        public static async Task<IResult> HandleAsync(HttpContext http, StripeBilling billing)
        {
            const int bodyLimit = 1 << 20;
            const int rejectionDrainAllowance = 64 * 1024;
            const int lingeringDiscardLimit = 4 << 20;
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = bodyLimit + rejectionDrainAllowance;
            }

            if (http.Request.ContentLength is > bodyLimit + rejectionDrainAllowance and <= lingeringDiscardLimit
                && http.Request.Protocol is "HTTP/1.0" or "HTTP/1.1")
            {
                return await RejectAfterLingeringDiscardAsync(http, http.Request.ContentLength.Value);
            }

            if (http.Request.ContentLength > bodyLimit + rejectionDrainAllowance)
            {
                return RejectUndrainedBody(http);
            }

            if (http.Request.ContentLength > bodyLimit)
            {
                return Results.StatusCode(413);
            }

            using var payload = new MemoryStream();
            var buffer = new byte[8192];
            long received = 0;
            var oversized = false;
            try
            {
                int count;
                while ((count = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) != 0)
                {
                    received += count;
                    if (received > bodyLimit + rejectionDrainAllowance)
                    {
                        return RejectUndrainedBody(http);
                    }
                    if (received > bodyLimit) { oversized = true; continue; }
                    payload.Write(buffer, 0, count);
                }
            }
            catch (BadHttpRequestException error) when (error.StatusCode == 413) { return RejectUndrainedBody(http); }
            if (oversized)
            {
                return Results.StatusCode(413);
            }

            var body = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
            try { await billing.HandleWebhookAsync(body, http.Request.Headers["Stripe-Signature"].ToString(), http.RequestAborted); return Results.Ok(); }
            catch (StripeWebhookRejectedException) { return Results.BadRequest(); }
            catch (StripeWebhookDeferredException deferred)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("StripeWebhook").StripeEventWasNotAppliedAnswering(deferred.EventId, deferred.EventType, deferred.InnerException!.GetType().Name, deferred.InnerException.Message);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        private static async Task<IResult> RejectAfterLingeringDiscardAsync(HttpContext http, long length)
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = length;
            }

            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            http.Response.Headers.Connection = "close";
            await http.Response.CompleteAsync();
            using var linger = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            linger.CancelAfter(TimeSpan.FromSeconds(5));
            var buffer = new byte[8192];
            try { while (await http.Request.Body.ReadAsync(buffer, linger.Token) != 0) { } }
            catch (Exception error) when (error is OperationCanceledException or IOException or BadHttpRequestException) { }
            return Results.Empty;
        }

        private static IResult RejectUndrainedBody(HttpContext http)
        {
            if (http.Request.Protocol is "HTTP/1.0" or "HTTP/1.1")
            {
                http.Response.Headers.Connection = "close";
            }

            return Results.StatusCode(413);
        }
    }
}
