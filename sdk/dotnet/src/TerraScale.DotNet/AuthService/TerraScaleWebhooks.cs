using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>Reports a refused webhook delivery: a missing header, a timestamp outside the 5-minute tolerance, an unusable secret or no matching v1 signature.</summary>
public sealed class TerraScaleWebhookException : Exception
{
    internal TerraScaleWebhookException(string errorCode, string message) : base(message) => ErrorCode = errorCode;

    /// <summary>One of missing_headers, invalid_secret, invalid_timestamp, timestamp_out_of_tolerance, signature_mismatch, invalid_body.</summary>
    public string ErrorCode { get; }
}

/// <summary>Verifies ts-auth end-user lifecycle webhook deliveries following the Standard Webhooks spec.</summary>
public static class TerraScaleWebhooks
{
    private const int ToleranceSeconds = 300;

    /// <summary>Verifies the delivery's signatures and timestamp and returns the parsed event JSON. Throws <see cref="TerraScaleWebhookException"/> when the delivery must be refused.</summary>
    public static JsonElement VerifyWebhook(string secret, IReadOnlyDictionary<string, string> headers, string rawBody, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rawBody);

        var id = FindHeader(headers, "webhook-id");
        var timestamp = FindHeader(headers, "webhook-timestamp");
        var signatureHeader = FindHeader(headers, "webhook-signature");
        if (id is null || timestamp is null || signatureHeader is null)
        {
            throw new TerraScaleWebhookException("missing_headers", "The delivery must carry webhook-id, webhook-timestamp and webhook-signature.");
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            throw new TerraScaleWebhookException("invalid_timestamp", "webhook-timestamp must be unix seconds.");
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - unixSeconds) > ToleranceSeconds)
        {
            throw new TerraScaleWebhookException("timestamp_out_of_tolerance", $"webhook-timestamp {timestamp} is more than {ToleranceSeconds} seconds from now.");
        }

        if (!secret.StartsWith("whsec_", StringComparison.Ordinal))
        {
            throw new TerraScaleWebhookException("invalid_secret", "The secret must be the whsec_ value answered once at registration or rotation.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(secret["whsec_".Length..]);
        }
        catch (FormatException)
        {
            throw new TerraScaleWebhookException("invalid_secret", "The secret's whsec_ payload must be base64.");
        }

        if (key.Length == 0)
        {
            throw new TerraScaleWebhookException("invalid_secret", "The secret's whsec_ payload must not be empty.");
        }

        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Concat(id, ".", timestamp, ".", rawBody)));
        if (!AnySignatureMatches(signatureHeader, mac))
        {
            throw new TerraScaleWebhookException("signature_mismatch", "No v1 signature entry matched the delivery.");
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(rawBody);
        }
        catch (JsonException)
        {
            throw new TerraScaleWebhookException("invalid_body", "The delivery body is not valid JSON.");
        }
    }

    private static bool AnySignatureMatches(string signatureHeader, byte[] mac)
    {
        foreach (var entry in signatureHeader.Split(' '))
        {
            if (!entry.StartsWith("v1,", StringComparison.Ordinal))
            {
                continue;
            }

            byte[] candidate;
            try
            {
                candidate = Convert.FromBase64String(entry["v1,".Length..]);
            }
            catch (FormatException)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(mac, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindHeader(IReadOnlyDictionary<string, string> headers, string name)
    {
        if (headers.TryGetValue(name, out var value))
        {
            return value;
        }

        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
