namespace TerraScale.Auth.Billing;

using System.Globalization;
using System.Text;
using TerraScale.Auth.Crypto;

/// Stripe webhook signature parsing and verification. The header is
/// <c>t=unixseconds,v1=hexsignature[,v2=...]</c>; the signature is
/// HMAC-SHA256(secret, "{t}.{rawBody}") compared constant-time against any v1 entry.
/// Pure functions — unit-testable without HTTP.
internal static class StripeWebhookSignatures
{
    /// Parses the comma-separated header into its timestamp and v1 entries (any order).
    public static (long? Timestamp, IReadOnlyList<string> V1Signatures) Parse(string? header)
    {
        long? timestamp = null;
        var signatures = new List<string>();
        if (header is null)
            return (null, signatures);
        foreach (var entry in header.Split(','))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = entry[..separator].Trim();
            var value = entry[(separator + 1)..].Trim();
            if (key == "t" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                timestamp = parsed;
            else if (key == "v1")
                signatures.Add(value);
        }
        return (timestamp, signatures);
    }

    /// HMAC-SHA256 hex signature of "{timestamp}.{payload}" under the webhook secret.
    public static string Compute(string secret, long timestamp, string payload) =>
        Encodings.HexEncode(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "." + payload)));

    /// True when any v1 entry matches the computed signature (constant-time, per entry) and
    /// the timestamp is within <paramref name="toleranceSeconds"/> of <paramref name="nowUnixSeconds"/>.
    public static bool Verify(string secret, string? header, string payload, long nowUnixSeconds, long toleranceSeconds = 300)
    {
        var (timestamp, signatures) = Parse(header);
        if (timestamp is null || signatures.Count == 0)
            return false;
        if (Math.Abs(nowUnixSeconds - timestamp.Value) > toleranceSeconds)
            return false;

        var expected = Encodings.HexDecode(Compute(secret, timestamp.Value, payload));
        foreach (var signature in signatures)
        {
            byte[] candidate;
            try
            {
                candidate = Encodings.HexDecode(signature);
            }
            catch (FormatException)
            {
                continue; // a malformed entry is not a match, not a failure
            }
            if (CryptographicOperations.FixedTimeEquals(candidate, expected))
                return true;
        }
        return false;
    }
}
