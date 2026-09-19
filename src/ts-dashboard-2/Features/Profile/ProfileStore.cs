using System.Collections.Immutable;
namespace TerraScale.Dashboard2;

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

    public string PersonalOrganizationId => World.ActiveOrganizationId;

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
