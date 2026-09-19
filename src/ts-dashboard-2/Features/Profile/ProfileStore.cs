using System.Collections.Immutable;
using System.Security.Cryptography;
namespace TerraScale.Dashboard2;

public sealed partial class DashboardStore
{
    public string ProfileDisplayName { get; private set; } = "";   // empty → CurrentUser.Name

    public string ProfilePhoneNumber { get; private set; } = "+55 11 90000-0000";

    public string EffectiveDisplayName =>
        ProfileDisplayName.Length > 0 ? ProfileDisplayName : CurrentUser.Name;

    public ImmutableArray<string> ProfileRoles => ["Organization Owner", "Member"];

    public ImmutableArray<string> ProfilePermissions =>
    [
        "projects:read", "projects:write", "databases:read", "databases:write",
        "credentials:read", "credentials:write", "audit:read", "usage:read",
    ];

    public ImmutableArray<string> MfaFactors { get; private set; } = ["totp"];

    /// <summary>Codes revealed once by a regeneration; consumed by the next
    /// security tab render, like the webhook secret reveal.</summary>
    public ImmutableArray<string> PendingRecoveryCodes { get; private set; } = [];

    public void EnrollTotp() => MfaFactors = ["totp"];

    /// <summary>TOTP enrollment shown until the verification code completes
    /// the flow (enrollTotpAction + verifyTotp in SecurityPanel.tsx).</summary>
    public TotpEnrollment? PendingTotpEnrollment { get; private set; }

    public TotpEnrollment BeginTotpEnrollment()
    {
        // Base32-style groups, formatted the way an authenticator shows them.
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        PendingTotpEnrollment = new(
            $"enr_{Guid.NewGuid():N}"[..20],
            string.Join(" ", Enumerable.Range(0, secret.Length / 4).Select(i => secret.Substring(i * 4, 4))));
        return PendingTotpEnrollment;
    }

    public TotpEnrollment? FindTotpEnrollment(string token) =>
        PendingTotpEnrollment is { } pending && pending.Token == token ? pending : null;

    public void CompleteTotpEnrollment(string token)
    {
        if (PendingTotpEnrollment is null || PendingTotpEnrollment.Token != token)
        {
            return;
        }

        EnrollTotp();
        RegenerateRecoveryCodes();
        PendingTotpEnrollment = null;
    }

    public void RemoveMfaFactor(string factor)
    {
        MfaFactors = [.. MfaFactors.Where(f => f != factor)];
        CurrentUser = CurrentUser with { MfaEnabled = MfaFactors.Length > 0 };
    }

    public void RegisterPasskey(string name) =>
        Passkeys = Passkeys.Add(new(
            $"pas_{Guid.NewGuid():N}"[..20],
            name.Trim().Length > 0 ? name.Trim() : "Unnamed passkey",
            DateTimeOffset.UtcNow.ToString("O")));

    public void RegenerateRecoveryCodes() =>
        PendingRecoveryCodes = [.. Enumerable.Range(0, 8)
            .Select(_ => Convert.ToHexString(Guid.NewGuid().ToByteArray())[..10].ToLowerInvariant())];

    public ImmutableArray<string> ConsumeRecoveryCodes()
    {
        var codes = PendingRecoveryCodes;
        PendingRecoveryCodes = [];
        return codes;
    }

    public ImmutableArray<ProfilePasskeyView> Passkeys { get; private set; } =
    [
        new("pass_01JC9ZQ7T9V2X5A8C1E4G9K2N", "YubiKey 5C", "2026-05-02T10:11:00Z"),
    ];

    public string PersonalOrganizationId => "org_01JC9Q5V2N4P6T8R0X2Z4B6D9";

    public void UpdateProfile(string name, string phoneNumber)
    {
        if (name.Trim().Length > 0)
        {
            ProfileDisplayName = name.Trim();
        }

        ProfilePhoneNumber = phoneNumber.Trim();
    }

    public void RemovePasskey(string id) =>
        Passkeys = [.. Passkeys.Where(p => p.Id != id)];
}
