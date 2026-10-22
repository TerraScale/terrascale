using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace TerraScale.Dashboard.Features.Billing.Webhooks;

public static class StripeWebhookEndpoints
{
    public static IEndpointRouteBuilder MapStripeWebhooks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/billing/stripe/webhook", HandleStripeWebhookAsync);
        endpoints.MapPost("/api/auth/stripe/webhook", HandleStripeWebhookAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleStripeWebhookAsync(HttpContext http, StripeBilling billing)
    {
        const int bodyLimit = 1 << 20;
        const int rejectionDrainAllowance = 64 * 1024;
        // Raise the limit before any early 413 so Kestrel can drain a slightly
        // oversized known-length upload instead of resetting the sender mid-write.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = bodyLimit + rejectionDrainAllowance;
        if (http.Request.ContentLength > bodyLimit + rejectionDrainAllowance) return RejectUndrainedBody(http);
        if (http.Request.ContentLength > bodyLimit) return Results.StatusCode(413);
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
                if (received > bodyLimit + rejectionDrainAllowance) return RejectUndrainedBody(http);
                // A bounded drain lets a slightly oversized chunked sender finish
                // uploading and observe 413. Rejected bytes are never buffered or
                // parsed, and Kestrel still imposes a fixed hard ceiling.
                if (received > bodyLimit) { oversized = true; continue; }
                payload.Write(buffer, 0, count);
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return RejectUndrainedBody(http); }
        if (oversized) return Results.StatusCode(413);
        var body = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
        try { await billing.HandleWebhookAsync(body, http.Request.Headers["Stripe-Signature"].ToString(), http.RequestAborted); return Results.Ok(); }
        catch (StripeWebhookRejectedException) { return Results.BadRequest(); }
    }

    private static IResult RejectUndrainedBody(HttpContext http)
    {
        // Kestrel cannot drain past its hard limit; do not advertise a reusable HTTP/1 connection.
        if (http.Request.Protocol is "HTTP/1.0" or "HTTP/1.1") http.Response.Headers.Connection = "close";
        return Results.StatusCode(413);
    }
}
