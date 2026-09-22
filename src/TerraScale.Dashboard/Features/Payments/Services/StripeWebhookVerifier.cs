using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Dashboard.Features.Payments.Services;

public enum StripeSignatureResult
{
    Valid,
    MissingSignature,
    MalformedSignature,
    TimestampOutOfTolerance,
    SignatureMismatch,
}

/// <summary>
/// Verifies the <c>Stripe-Signature</c> header exactly as Stripe documents:
/// HMAC-SHA256 over <c>{timestamp}.{payload}</c> with the endpoint's signing
/// secret, a constant-time comparison, and a bounded clock tolerance that
/// rejects replayed deliveries.
/// </summary>
public static class StripeWebhookVerifier
{
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static StripeSignatureResult Verify(
        string payload,
        string signatureHeader,
        string secret,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return StripeSignatureResult.MissingSignature;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return StripeSignatureResult.MissingSignature;
        }

        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = part[..separator];
            var value = part[(separator + 1)..];
            if (name == "t" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                timestamp = parsed;
            }
            else if (name == "v1" && value.Length > 0)
            {
                signatures.Add(value);
            }
        }

        if (timestamp is null || signatures.Count == 0)
        {
            return StripeSignatureResult.MalformedSignature;
        }

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        if ((now - signedAt).Duration() > tolerance)
        {
            return StripeSignatureResult.TimestampOutOfTolerance;
        }

        var expected = Convert.ToHexString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes($"{timestamp.Value}.{payload}"))).ToLowerInvariant();

        foreach (var candidate in signatures)
        {
            if (CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(candidate.ToLowerInvariant())))
            {
                return StripeSignatureResult.Valid;
            }
        }

        return StripeSignatureResult.SignatureMismatch;
    }
}
