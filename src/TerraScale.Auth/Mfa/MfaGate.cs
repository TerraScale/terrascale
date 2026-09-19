namespace TerraScale.Auth.Mfa;

using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;

/// <summary>
/// MFA decision helpers used by the sign-in flow: is MFA required, begin a
/// pending sign-in, complete it with a TOTP or recovery code.
/// </summary>
public static class MfaGate
{
    /// <summary>True when the user has a confirmed TOTP factor.</summary>
    public static async Task<bool> RequiredForUserAsync(AuthStore store, string userId, CancellationToken ct = default)
    {
        var factors = await store.ListMfaFactorsAsync(userId, ct);
        return factors.Any(f => f.Type == "totp" && f.Confirmed);
    }

    /// <summary>Issues a single-use pending-MFA token binding the in-progress sign-in to the user.</summary>
    public static async Task<(string Token, string TokenKey)> BeginPendingAsync(AuthStore store, string instanceId, string userId, CancellationToken ct = default)
    {
        var token = SecureTokens.RandomToken(40);
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "vrf_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            UserId = userId,
            Purpose = "mfa.pending",
            TokenKey = SecureTokens.Sha256Hex(token),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        return (token, SecureTokens.Sha256Hex(token));
    }

    /// <summary>Consumes a pending-MFA token, returning the user id, or null when invalid/expired/consumed.
    /// Atomic (If-Match), so of two concurrent consumes of one token only the first wins.</summary>
    public static async Task<string?> ConsumePendingAsync(AuthStore store, string instanceId, string token, CancellationToken ct = default)
    {
        var record = await store.ConsumeVerificationTokenAsync(
            SecureTokens.Sha256Hex(token),
            record => record.InstanceId == instanceId && record.Purpose == "mfa.pending"
                && !record.Consumed && record.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ct);
        return record?.UserId;
    }

    /// <summary>Verifies a TOTP or recovery code for the user, consuming recovery codes on use.</summary>
    public static async Task<bool> VerifyCodeAsync(AuthStore store, byte[] masterKey, string userId, string code, CancellationToken ct = default)
    {
        var factors = await store.ListMfaFactorsAsync(userId, ct);
        foreach (var factor in factors)
        {
            if (factor.Type == "totp" && factor.Confirmed && factor.SecretBox is not null
                && Totp.Verify(SecretBox.Decrypt(masterKey, factor.SecretBox), code, DateTimeOffset.UtcNow))
                return true;

            if (factor.Type == "recovery" && factor.RecoveryCodes is { } codes)
            {
                var hashed = SecureTokens.Sha256Hex(code);
                if (codes.Contains(hashed))
                {
                    await store.UpsertMfaFactorAsync(factor with { RecoveryCodes = codes.Where(c => c != hashed).ToArray() }, ct);
                    return true;
                }
            }
        }
        return false;
    }
}
