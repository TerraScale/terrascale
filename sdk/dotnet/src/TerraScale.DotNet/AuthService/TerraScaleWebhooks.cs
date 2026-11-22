using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

/// <summary>One fixed refusal of a webhook delivery: missing_headers, invalid_timestamp, timestamp_out_of_tolerance, invalid_secret, signature_mismatch or invalid_body.</summary>
public sealed record WebhookRefused(string ErrorCode, string Message);

/// <summary>Either the delivery's parsed event JSON or its fixed refusal.</summary>
public readonly union WebhookVerification(JsonElement, WebhookRefused);

/// <summary>Verifies ts-auth end-user lifecycle webhook deliveries following the Standard Webhooks spec.</summary>
public static class TerraScaleWebhooks
{
    private const int ToleranceSeconds = 300;

    /// <summary>Verifies the delivery's signatures and timestamp and returns the parsed event JSON, or the delivery's fixed refusal.</summary>
    public static WebhookVerification VerifyWebhook(string secret, IReadOnlyDictionary<string, string> headers, string rawBody, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rawBody);

        var id = FindHeader(headers, "webhook-id");
        var timestamp = FindHeader(headers, "webhook-timestamp");
        var signatureHeader = FindHeader(headers, "webhook-signature");
        if (id is null || timestamp is null || signatureHeader is null)
        {
            return new WebhookRefused("missing_headers", "The delivery must carry webhook-id, webhook-timestamp and webhook-signature.");
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return new WebhookRefused("invalid_timestamp", "webhook-timestamp must be unix seconds.");
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - unixSeconds) > ToleranceSeconds)
        {
            return new WebhookRefused("timestamp_out_of_tolerance", $"webhook-timestamp {timestamp} is more than {ToleranceSeconds} seconds from now.");
        }

        if (!secret.StartsWith("whsec_", StringComparison.Ordinal))
        {
            return new WebhookRefused("invalid_secret", "The secret must be the whsec_ value answered once at registration or rotation.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(secret["whsec_".Length..]);
        }
        catch (FormatException)
        {
            return new WebhookRefused("invalid_secret", "The secret's whsec_ payload must be base64.");
        }

        if (key.Length == 0)
        {
            return new WebhookRefused("invalid_secret", "The secret's whsec_ payload must not be empty.");
        }

        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Concat(id, ".", timestamp, ".", rawBody)));
        if (!AnySignatureMatches(signatureHeader, mac))
        {
            return new WebhookRefused("signature_mismatch", "No v1 signature entry matched the delivery.");
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(rawBody);
        }
        catch (JsonException)
        {
            return new WebhookRefused("invalid_body", "The delivery body is not valid JSON.");
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
