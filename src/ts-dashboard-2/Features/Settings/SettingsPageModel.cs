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
    ImmutableArray<WebhookDelivery> WebhookDeliveries,
    (string Plan, int Used, int Limit) WebhookAllowance,
    string? WebhookSecretId,
    string? WebhookSecretUrl,
    string? WebhookSecretValue,
    string? Toast,
    string LoadedAtText,
    string Tab) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static SettingsPageModel From(DashboardStore store, string href, string tab, string? toast = null, bool rotated = false)
    {
        var project = store.ActiveProject;
        var pending = store.ConsumePendingWebhookSecret();
        (string WebhookId, string Url, string Secret)? secret = rotated && pending is { } p ? p : null;
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
            Webhooks: store.ProjectWebhooks,
            WebhookDeliveries: store.WebhookDeliveries,
            WebhookAllowance: store.WebhookEndpointAllowance,
            WebhookSecretId: secret?.WebhookId,
            WebhookSecretUrl: secret?.Url,
            WebhookSecretValue: secret?.Secret,
            Toast: toast,
            LoadedAtText: DashboardStore.FormatUtcDateTime(DateTimeOffset.UtcNow.ToString("O")),
            Tab: tab)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
