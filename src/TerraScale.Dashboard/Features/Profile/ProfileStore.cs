using System.Collections.Immutable;
using System.Security.Cryptography;
namespace TerraScale.Dashboard;

public sealed partial class DashboardStore
{
    public string ProfileDisplayName { get => World.ProfileDisplayName; private set => World.ProfileDisplayName = value; }   // empty → CurrentUser.Name

    public string ProfilePhoneNumber { get => World.ProfilePhoneNumber; private set => World.ProfilePhoneNumber = value; }

    public string EffectiveDisplayName =>
        ProfileDisplayName.Length > 0 ? ProfileDisplayName : CurrentUser.Name;

    public ImmutableArray<string> ProfileRoles => ["Organization Owner", "Member"];

    public ImmutableArray<string> ProfilePermissions =>
    [
        "projects:read", "projects:write", "databases:read", "databases:write",
        "credentials:read", "credentials:write", "audit:read", "usage:read",
    ];

    public ImmutableArray<string> MfaFactors { get => World.MfaFactors; private set => World.MfaFactors = value; }

    /// <summary>Codes revealed once by a regeneration; consumed by the next
    /// security tab render, like the webhook secret reveal.</summary>
    public ImmutableArray<string> PendingRecoveryCodes { get => World.PendingRecoveryCodes; private set => World.PendingRecoveryCodes = value; }

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

    public ImmutableArray<ProfilePasskeyView> Passkeys { get => World.Passkeys; private set => World.Passkeys = value; }

    public string PersonalOrganizationId => World.PersonalOrganizationId;

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
