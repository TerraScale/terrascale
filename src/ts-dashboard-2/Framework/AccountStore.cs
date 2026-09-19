namespace TerraScale.Dashboard2;

/// <summary>Mutable account + organization state, so the profile and
/// organization forms persist in memory like their ts-dashboard actions.</summary>
public sealed partial class DashboardStore
{
    private string accountPassword = Features.Auth.PasswordLoginEndpoint.DemoPassword;

    public string AccountLocale { get; private set; } = "en";

    public bool OrganizationDeletionRequested { get; private set; }

    public bool VerifyPassword(string password) => password == accountPassword;

    public void ChangePassword(string currentPassword, string newPassword) => accountPassword = newPassword;

    public void ChangeEmail(string newEmail) => CurrentUser = CurrentUser with { Email = newEmail.Trim() };

    public void UpdateLocale(string locale) => AccountLocale = locale == "pt-BR" ? "pt-BR" : "en";

    /// <summary>Records the sign-in provider used for the demo session
    /// (google | github | passkey), as the external flows would.</summary>
    public void AdoptExternalProvider(string provider) =>
        CurrentUser = CurrentUser with { Provider = provider };

    public void RenameOrganization(string name)
    {
        if (name.Trim() is { Length: > 0 } trimmed)
        {
            ActiveOrganizationName = trimmed;
        }
    }

    public void RequestOrganizationDeletion() => OrganizationDeletionRequested = true;

    public void ResendInvitation(string invitationId) =>
        Invitations = [.. Invitations.Select(i => i.Id == invitationId
            ? i with { Status = "Pending", CreatedAt = DateTimeOffset.UtcNow.ToString("O") }
            : i)];
}
