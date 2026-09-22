using Newtonsoft.Json;
using Stripe;

namespace TerraScale.Dashboard.Features.Payments.Services;

/// <summary>
/// Verifies and parses one Stripe webhook delivery with the official SDK's
/// <see cref="EventUtility"/> (HMAC-SHA256 over <c>timestamp.payload</c>, a
/// bounded clock tolerance, and a constant-time comparison).
/// </summary>
public static class StripeWebhookReader
{
    public const long DefaultToleranceSeconds = 300;

    public static bool TryRead(
        string payload,
        string signatureHeader,
        string secret,
        out Event? stripeEvent,
        out string? error,
        long toleranceSeconds = DefaultToleranceSeconds)
    {
        stripeEvent = null;
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            error = "missing_signature";
            return false;
        }

        try
        {
            // throwOnApiVersionMismatch stays false: the dashboard projects a
            // fixed set of events and the sandbox account may trail the SDK's
            // default API version.
            stripeEvent = EventUtility.ConstructEvent(
                payload,
                signatureHeader,
                secret,
                toleranceSeconds,
                throwOnApiVersionMismatch: false);
            error = null;
            return true;
        }
        catch (StripeException exception)
        {
            error = exception.Message;
            return false;
        }
        catch (JsonException)
        {
            error = "invalid_json";
            return false;
        }
    }
}
