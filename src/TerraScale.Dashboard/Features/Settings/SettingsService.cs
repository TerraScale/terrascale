using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.Settings
{
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
        public Task<int> AuditRetentionAsync(AccountContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(context.Organization!.AuditRetentionDays ?? DefaultDays);
        }

        public async Task<int> SetAuditRetentionAsync(AccountContext context, int days, CancellationToken cancellationToken)
        {
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                if (!org.CanManage(context.UserId))
                {
                    throw new AccountAccessException("Organization management is required.");
                }

                org.AuditRetentionDays = days;
            }, cancellationToken);
            return days;
        }
    }
    public sealed record ProjectSettings(string ProjectId, string ProjectName, int AuditRetentionDays, bool CanManage, bool ProjectCanBeDeleted);

    public readonly union SettingsRead(ProjectSettings, Forbidden);

    public readonly union AuditRetentionSaved(Done, Validation, Forbidden);

    public sealed class SettingsService(AccountService accounts, ISettingsBackend backend, TimeProvider time)
    {
        public async Task<SettingsRead> ReadAsync(AccountContext context, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            if (!context.CanManage)
            {
                return new Forbidden("Organization management is required to read audit retention.");
            }

            var days = await backend.AuditRetentionAsync(context, cancellationToken);
            if (days is < 1 or > 90)
            {
                throw new IOException("The provider returned an invalid audit retention policy.");
            }

            return new ProjectSettings(context.Project!.Id, context.Project.Name, days, context.CanManage, !context.Project.IsDefault && context.Organization!.Projects.Count > 1);
        }

        public async Task<AuditRetentionSaved> SetAuditRetentionAsync(AccountContext context, int days, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            if (!context.CanManage)
            {
                return new Forbidden("Organization management is required.");
            }

            if (days is < 1 or > 90)
            {
                return new Validation("Audit retention must be from 1 to 90 days.");
            }

            var confirmed = await backend.SetAuditRetentionAsync(context, days, cancellationToken);
            if (confirmed != days)
            {
                throw new IOException("The provider did not confirm the requested audit retention policy.");
            }

            await accounts.UpdateOrganizationAsync(context, org =>
            {
                if (!org.CanManage(context.UserId))
                {
                    throw new AccountAccessException("Organization management is required.");
                }

                org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "settings.audit_retention_updated", days.ToString(System.Globalization.CultureInfo.InvariantCulture), "setting", time.GetUtcNow()));
            }, cancellationToken);
            return new Done();
        }
    }
}
