namespace TerraScale.Auth.Passkeys;

using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;

/// <summary>WebAuthn challenge issue/consume helpers over the verification-token store.</summary>
public static class PasskeyGate
{
    /// <summary>Issues a WebAuthn challenge for a ceremony; returns the base64url challenge.</summary>
    public static async Task<string> IssueChallengeAsync(AuthStore store, string instanceId, string purpose, string? userId, CancellationToken ct = default)
    {
        var challenge = Encodings.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "vrf_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            UserId = userId,
            Purpose = purpose,
            TokenKey = SecureTokens.Sha256Hex(challenge),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        return challenge;
    }

    /// <summary>Consumes a challenge for the purpose; false when unknown, expired, or already used.
    /// Atomic (If-Match), so of two concurrent consumes of one challenge only the first wins.</summary>
    public static async Task<bool> ConsumeChallengeAsync(AuthStore store, string instanceId, string purpose, string base64UrlChallenge, CancellationToken ct = default)
    {
        var record = await store.ConsumeVerificationTokenAsync(
            SecureTokens.Sha256Hex(base64UrlChallenge),
            record => record.InstanceId == instanceId && record.Purpose == purpose && !record.Consumed
                && record.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ct);
        return record is not null;
    }
}
