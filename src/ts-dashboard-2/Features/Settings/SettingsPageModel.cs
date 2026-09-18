using System.Collections.Immutable;
using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Settings;

public sealed record SettingsPageModel(
    string ProjectId,
    string ProjectName,
    bool AuthenticationEnabled,
    string SecuritySettingsUpdatedAt,
    bool CanManageByok,
    bool CanDeleteProject,
    ImmutableArray<SettingsAuthProvider> Providers,
    ImmutableArray<SettingsCredentialView> Credentials,
    ImmutableArray<DashboardDatabase> Databases,
    ImmutableArray<WebhookEndpoint> Webhooks,
    string LoadedAtText,
    string Tab) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static SettingsPageModel From(DashboardStore store, string href, string tab)
    {
        var project = store.ActiveProject;
        return new(
            ProjectId: project.Id,
            ProjectName: project.Name,
            AuthenticationEnabled: store.AuthenticationEnabled,
            SecuritySettingsUpdatedAt: store.SecuritySettingsUpdatedAt,
            CanManageByok: store.CanManageByok,
            CanDeleteProject: store.CanDeleteProject,
            Providers: store.AuthProviders,
            Credentials: store.SettingsCredentials,
            Databases: store.ProjectDatabases(project.Id),
            Webhooks: store.Webhooks,
            LoadedAtText: DashboardStore.FormatUtcDateTime(DateTimeOffset.UtcNow.ToString("O")),
            Tab: tab)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
