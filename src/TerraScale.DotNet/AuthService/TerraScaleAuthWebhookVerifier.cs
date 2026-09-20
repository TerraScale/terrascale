using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TerraScale.DotNet;

/// <summary>
/// Verifies inbound ts-auth webhook deliveries. The dispatcher sends the raw
/// body with <c>X-TsAuth-Timestamp</c> (unix seconds) and
/// <c>X-TsAuth-Signature: sha256=&lt;hex&gt;</c>, where the signature is
/// HMAC-SHA256 over UTF-8 bytes of <c>timestamp + "." + body</c>.
/// </summary>
public static class TerraScaleAuthWebhookVerifier
{
    private const string SignaturePrefix = "sha256=";

    /// <summary>
    /// Verifies one delivery. Compare the exact raw request body the receiver
    /// read; re-serialized JSON does not verify. Malformed inputs return false.
    /// </summary>
    /// <param name="signingSecret">The one-time secret returned when the webhook endpoint was created.</param>
    /// <param name="timestamp">The <c>X-TsAuth-Timestamp</c> header value.</param>
    /// <param name="signature">The <c>X-TsAuth-Signature</c> header value.</param>
    /// <param name="payload">The raw request body.</param>
    public static bool Verify(string signingSecret, string? timestamp, string? signature, ReadOnlySpan<byte> payload) =>
        Verify(signingSecret, timestamp, signature, payload, tolerance: null, now: null);

    /// <summary>
    /// Verifies one delivery and rejects timestamps outside
    /// <paramref name="tolerance"/> of <paramref name="now"/> (replay window).
    /// </summary>
    public static bool Verify(
        string signingSecret,
        string? timestamp,
        string? signature,
        ReadOnlySpan<byte> payload,
        TimeSpan? tolerance,
        DateTimeOffset? now)
    {
        if (string.IsNullOrWhiteSpace(signingSecret) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        // A non-numeric timestamp is malformed in both variants: reject it even
        // when no replay window is configured (it is never MAC'd as legitimate).
        if (!long.TryParse(timestamp, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return false;
        }

        // DateTimeOffset's unix-second range is the representable window;
        // outside it the timestamp is malformed, not a throw.
        if (unixSeconds is < -62_135_596_800L or > 253_402_300_799L)
        {
            return false;
        }

        if (tolerance is { } window)
        {
            var reference = now ?? DateTimeOffset.UtcNow;
            var age = reference - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            if (age < TimeSpan.Zero)
            {
                age = -age;
            }

            if (age > window)
            {
                return false;
            }
        }

        var secretBytes = Encoding.UTF8.GetBytes(signingSecret);
        var signedBytes = BuildSignedBytes(timestamp, payload);
        Span<byte> digest = stackalloc byte[32];
        HMACSHA256.HashData(secretBytes, signedBytes, digest);

        Span<char> expected = stackalloc char[SignaturePrefix.Length + 64];
        SignaturePrefix.CopyTo(expected);
        EncodeHex(digest, expected[SignaturePrefix.Length..]);
        return FixedTimeEquals(expected, signature);
    }

    private static byte[] BuildSignedBytes(string timestamp, ReadOnlySpan<byte> payload)
    {
        var timestampBytes = Encoding.UTF8.GetBytes(timestamp);
        var signed = new byte[timestampBytes.Length + 1 + payload.Length];
        timestampBytes.CopyTo(signed, 0);
        signed[timestampBytes.Length] = (byte)'.';
        payload.CopyTo(signed.AsSpan(timestampBytes.Length + 1));
        return signed;
    }

    private static bool FixedTimeEquals(ReadOnlySpan<char> expected, string actual)
    {
        // Compare as UTF-16 code units; both sides are ASCII hex with the fixed
        // "sha256=" prefix, so unit-wise comparison is exact.
        var expectedBytes = MemoryMarshal.AsBytes(expected);
        var actualBytes = MemoryMarshal.AsBytes(actual.AsSpan());
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static void EncodeHex(ReadOnlySpan<byte> bytes, Span<char> destination)
    {
        const string HexDigits = "0123456789abcdef";
        for (var index = 0; index < bytes.Length; index++)
        {
            destination[index * 2] = HexDigits[bytes[index] >> 4];
            destination[index * 2 + 1] = HexDigits[bytes[index] & 0x0F];
        }
    }
}
