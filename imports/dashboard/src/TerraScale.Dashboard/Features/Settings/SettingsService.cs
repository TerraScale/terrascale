using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Settings;

public interface ISettingsBackend
{
    Task<int> AuditRetentionAsync(AccountContext context, CancellationToken cancellationToken);
    Task<int> SetAuditRetentionAsync(AccountContext context, int days, CancellationToken cancellationToken);
}
// Audit retention is the dashboard's: it narrows the plan's audit window (OrganizationAudit.RetentionDays). TerraBase's
// management audit retention belongs to organizations TerraBase manages itself, which ts-auth organizations are not.
public sealed class SettingsBackend(AccountService accounts) : ISettingsBackend
{
    public const int DefaultDays = 90;
    public Task<int> AuditRetentionAsync(AccountContext context, CancellationToken cancellationToken) =>
        Task.FromResult(context.Organization!.AuditRetentionDays ?? DefaultDays);
    public async Task<int> SetAuditRetentionAsync(AccountContext context, int days, CancellationToken cancellationToken)
    {
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            if (!org.CanManage(context.UserId)) throw new AccountAccessException("Organization management is required.");
            org.AuditRetentionDays = days;
        }, cancellationToken);
        return days;
    }
}
public sealed record ProjectSettings(string ProjectId, string ProjectName, int AuditRetentionDays, bool CanManage, bool ProjectCanBeDeleted);

public sealed class SettingsService(AccountService accounts, ISettingsBackend backend, OrganizationLifecycleService lifecycle, BackupService backups, TimeProvider time)
{
    public async Task<ProjectSettings> ReadAsync(AccountContext context, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, false, cancellationToken);
        if (!context.CanManage) throw new AccountAccessException("Organization management is required to read audit retention.");
        var days = await backend.AuditRetentionAsync(context, cancellationToken);
        if (days is < 1 or > 90) throw new IOException("The provider returned an invalid audit retention policy.");
        return new(context.Project!.Id, context.Project.Name, days, context.CanManage, !context.Project.IsDefault && context.Organization!.Projects.Count > 1);
    }
    public async Task SetAuditRetentionAsync(AccountContext context, int days, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, true, cancellationToken);
        if (days is < 1 or > 90) throw new AccountAccessException("Audit retention must be from 1 to 90 days.", 400);
        var confirmed = await backend.SetAuditRetentionAsync(context, days, cancellationToken);
        if (confirmed != days) throw new IOException("The provider did not confirm the requested audit retention policy.");
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            if (!org.CanManage(context.UserId)) throw new AccountAccessException("Organization management is required.");
            org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "settings.audit_retention_updated", days.ToString(System.Globalization.CultureInfo.InvariantCulture), "setting", time.GetUtcNow()));
        }, cancellationToken);
    }
    public async Task DeleteProjectAsync(AccountContext context, string projectId, string confirmation, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, true, cancellationToken);
        if (context.Project!.Id != projectId) throw new AccountAccessException("Enter the selected project name exactly to confirm deletion.", 400);
        if (confirmation.Trim() != context.Project.Name) throw new AccountAccessException("Enter the selected project name exactly to confirm deletion.", 400);
        if (context.Project.IsDefault || context.Organization!.Projects.Count <= 1) throw new AccountAccessException("The default or last project cannot be deleted.", 409);
        await lifecycle.DeleteProjectAsync(context, projectId, confirmation, cancellationToken);
    }
    public async Task CreateBackupAsync(AccountContext context, string databaseId, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, true, cancellationToken);
        await backups.CreateAsync(context, databaseId, cancellationToken);
    }
    public async Task SetBackupPolicyAsync(AccountContext context, string databaseId, string fullBackupCron,
        string incrementalBackupCron, int retentionDays, bool disabled, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, true, cancellationToken);
        await backups.SetPolicyAsync(context, databaseId, fullBackupCron, incrementalBackupCron, retentionDays, disabled, cancellationToken);
    }
    public async Task RestoreBackupAsync(AccountContext context, string databaseId, string backupId, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, true, cancellationToken);
        if (backupId.Length > 0 && !TerraScale.DotNet.TerraScaleIds.IsValid(backupId))
            throw new AccountAccessException("That backup ID is not a verified provider restore point.", 400);
        await backups.RestoreAsync(context, databaseId, backupId, "", cancellationToken);
    }
    private async Task<AccountContext> FreshAsync(AccountContext context, bool manage, CancellationToken cancellationToken)
    {
        var fresh = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
        if (manage && !fresh.CanManage) throw new AccountAccessException("Organization management is required.");
        return fresh;
    }
}
