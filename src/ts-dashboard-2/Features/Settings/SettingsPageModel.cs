using System.Collections.Immutable;

namespace TerraScale.Dashboard2.Features.Settings;

public sealed record SettingsPageModel(
    string ProjectId,
    string ShortProjectId,
    string ProjectName,
    bool AuthenticationEnabled,
    string SecuritySettingsUpdatedAt,
    bool CanManageByok,
    bool CanDeleteProject,
    ImmutableArray<SettingsAuthProvider> Providers,
    ImmutableArray<SettingsCredentialView> Credentials,
    ImmutableArray<DashboardDatabase> Databases,
    string SelectedDatabaseId,
    string SelectedDatabaseName,
    string FullBackupCron,
    string IncrementalBackupCron,
    int RetentionDays,
    bool PolicyDisabled,
    bool PolicySaved,
    ImmutableArray<SettingsBackupJobRow> BackupJobs,
    ImmutableArray<SettingsRestorePointRow> RestorePoints,
    ImmutableArray<SettingsRestoreJobRow> RestoreJobs,
    string BackupPlanExpiresLabel,
    string? Toast,
    string LoadedAtText,
    string Tab) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static SettingsPageModel From(DashboardStore store, string href, string tab, string? databaseId = null, string? toast = null)
    {
        var project = store.ActiveProject;
        var databases = store.ProjectDatabases(project.Id);
        var selected = databases.FirstOrDefault(d => d.Id == databaseId) ?? databases.FirstOrDefault();
        var policy = selected is null ? null : store.BackupPolicyFor(selected.Id);

        return new(
            ProjectId: project.Id,
            ShortProjectId: DashboardStore.ShortId(project.Id),
            ProjectName: project.Name,
            AuthenticationEnabled: store.AuthenticationEnabled,
            SecuritySettingsUpdatedAt: store.SecuritySettingsUpdatedAt,
            CanManageByok: store.CanManageByok,
            CanDeleteProject: store.CanDeleteProject,
            Providers: store.AuthProviders,
            Credentials: store.SettingsCredentials,
            Databases: databases,
            SelectedDatabaseId: selected?.Id ?? "",
            SelectedDatabaseName: selected?.Name ?? "",
            FullBackupCron: policy?.FullBackupCron ?? "0 2 * * *",
            IncrementalBackupCron: policy?.IncrementalBackupCron ?? "0 */6 * * *",
            RetentionDays: policy?.RetentionDays ?? 7,
            PolicyDisabled: policy?.Disabled ?? false,
            PolicySaved: policy is not null,
            BackupJobs: [.. store.BackupJobs
                .Where(job => selected is not null && job.DatabaseName == selected.Name)
                .OrderByDescending(job => job.UpdatedAt, StringComparer.Ordinal)
                .Select(job => new SettingsBackupJobRow(
                    Id: job.Id,
                    Policy: job.Policy,
                    Retention: "30 days",
                    Status: job.State,
                    StatusTone: DashboardStore.BackupStatusTone(job.State),
                    DestinationType: "LocalFolder",
                    HasRestorePoint: job.State == "Succeeded",
                    UpdatedLabel: DashboardStore.FormatUtcDateTime(job.UpdatedAt)))],
            RestorePoints: [.. store.RestorePoints
                .Where(point => selected is not null && point.DatabaseId == selected.Id)
                .OrderByDescending(point => point.CreatedAt, StringComparer.Ordinal)
                .Select(point => new SettingsRestorePointRow(
                    Name: $"backup-{point.Generation["gen_".Length..]}",
                    BackupId: point.Generation,
                    Mode: point.Mode,
                    ModeTone: point.Mode == "Incremental" ? "ts-badge-info" : "ts-badge-success",
                    EncryptionLabel: point.Encrypted ? "Encrypted" : "Not encrypted",
                    FilesToRestore: point.FilesToRestore,
                    CreatedLabel: DashboardStore.FormatUtcDateTime(point.CreatedAt)))],
            RestoreJobs: [.. store.RestoreJobs
                .Where(job => selected is not null && job.DatabaseId == selected.Id)
                .OrderByDescending(job => job.StartedAt, StringComparer.Ordinal)
                .Select(job => new SettingsRestoreJobRow(
                    Id: job.Id,
                    SourceDatabaseId: job.DatabaseId,
                    RestoredDatabaseName: job.RestoredDatabaseName,
                    RestoredDatabaseId: $"db_{job.Id["res_".Length..]}",
                    Status: job.State,
                    StatusTone: DashboardStore.BackupStatusTone(job.State),
                    UpdatedLabel: DashboardStore.FormatUtcDateTime(job.FinishedAt ?? job.StartedAt)))],
            BackupPlanExpiresLabel: DashboardStore.FormatUtcDateTime(DateTimeOffset.UtcNow.Add(DashboardStore.RestoreConfirmationTtl).ToString("O")),
            Toast: toast,
            LoadedAtText: DashboardStore.FormatUtcDateTime(DateTimeOffset.UtcNow.ToString("O")),
            Tab: tab)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
