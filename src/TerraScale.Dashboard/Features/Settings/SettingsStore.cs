using System.Collections.Immutable;
namespace TerraScale.Dashboard;

public sealed partial class DashboardStore
{
    public bool AuthenticationEnabled { get => World.AuthenticationEnabled; private set => World.AuthenticationEnabled = value; }

    public string SecuritySettingsUpdatedAt { get => World.SecuritySettingsUpdatedAt; private set => World.SecuritySettingsUpdatedAt = value; }

    public ImmutableArray<SettingsAuthProvider> AuthProviders { get => World.AuthProviders; private set => World.AuthProviders = value; }

    public bool CanManageByok => false;     // demo: not on an Enterprise plan
    public bool CanDeleteProject => true;   // demo session is the org owner

    private readonly Dictionary<string, SettingsBackupPolicy> _backupPolicies = [];

    public SettingsBackupPolicy? BackupPolicyFor(string databaseId) =>
        _backupPolicies.TryGetValue(databaseId, out var policy) ? policy : null;

    public void SaveSettingsBackupPolicy(
        string databaseId,
        string fullBackupCron,
        string incrementalBackupCron,
        int retentionDays,
        bool disabled) =>
        _backupPolicies[databaseId] = new(databaseId, fullBackupCron, incrementalBackupCron, retentionDays, disabled);

    public ImmutableArray<SettingsCredentialView> SettingsCredentials =>
    [
        .. ApiKeys.Select(k => new SettingsCredentialView(
            k.KeyId, "API key", k.Name, k.Prefix, k.LastFour, k.Scopes,
            k.Status, k.CreatedByEmail, k.CreatedAt, k.ExpiresAt ?? "", k.LastUsedAt ?? "")),
        .. M2mApplications.Select(m => new SettingsCredentialView(
            m.ClientId, "M2M application", m.Name, m.ClientId[..8], m.ClientId[^4..],
            ["documents:read", "documents:write"], m.Status, CurrentUser.Email, m.CreatedAt, "", m.LastUsedAt ?? "")),
    ];

    public void SetAuthenticationEnabled(bool enabled) =>
        AuthenticationEnabled = enabled;

    public void DeleteProject(string projectId) =>
        Projects = [.. Projects.Where(p => p.Id != projectId)];
}
