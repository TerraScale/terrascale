namespace TerraScale.Dashboard;

/// <summary>Mutable account + organization state, so the profile and
/// organization forms persist in memory like their ts-dashboard actions.
/// All of it lives on the active account world.</summary>
public sealed partial class DashboardStore
{
    public string AccountLocale { get => World.AccountLocale; private set => World.AccountLocale = value; }

    public bool OrganizationDeletionRequested { get => World.OrganizationDeletionRequested; private set => World.OrganizationDeletionRequested = value; }

    public bool VerifyPassword(string password) => World.VerifyPassword(password);

    public void ChangePassword(string currentPassword, string newPassword) => World.ChangePassword(currentPassword, newPassword);

    public void ChangeEmail(string newEmail)
    {
        World.CurrentUser = World.CurrentUser with { Email = newEmail.Trim() };
        World.RegisteredEmail = newEmail.Trim();
    }

    public void UpdateLocale(string locale) => AccountLocale = locale == "pt-BR" ? "pt-BR" : "en";

    /// <summary>Records the sign-in provider used for the session
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
