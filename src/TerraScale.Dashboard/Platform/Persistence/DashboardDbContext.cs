using System.Text;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class DashboardDbContext(DbContextOptions<DashboardDbContext> options) : DbContext(options)
{
    public DbSet<AuthFlowRow> AuthFlows => Set<AuthFlowRow>();

    public DbSet<SessionRow> Sessions => Set<SessionRow>();

    public DbSet<AccessTokenRow> AccessTokens => Set<AccessTokenRow>();

    public DbSet<McpClientRow> McpClients => Set<McpClientRow>();

    public DbSet<McpCodeRow> McpCodes => Set<McpCodeRow>();

    public DbSet<SavedQueryRow> SavedQueries => Set<SavedQueryRow>();

    public DbSet<SqlWorkspaceRow> SqlWorkspaces => Set<SqlWorkspaceRow>();

    public DbSet<AllowanceAlertRow> AllowanceAlerts => Set<AllowanceAlertRow>();

    public DbSet<AlertRuleRow> AlertRules => Set<AlertRuleRow>();

    public DbSet<AlertEventRow> AlertEvents => Set<AlertEventRow>();

    public DbSet<LogDrainRow> LogDrains => Set<LogDrainRow>();

    public DbSet<LogDrainCursorRow> LogDrainCursors => Set<LogDrainCursorRow>();

    public DbSet<AccountRow> Accounts => Set<AccountRow>();

    public DbSet<NotificationRow> Notifications => Set<NotificationRow>();

    public DbSet<OrganizationRow> Organizations => Set<OrganizationRow>();

    public DbSet<OrganizationProjectRow> OrganizationProjects => Set<OrganizationProjectRow>();

    public DbSet<ProjectDatabaseRow> ProjectDatabases => Set<ProjectDatabaseRow>();

    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();

    public DbSet<InvitationReservationRow> InvitationReservations => Set<InvitationReservationRow>();

    public DbSet<PendingOrganizationCheckout> PendingOrganizationCheckouts => Set<PendingOrganizationCheckout>();

    public DbSet<BillingBinding> Billing => Set<BillingBinding>();

    public DbSet<StripeCustomerRow> StripeCustomers => Set<StripeCustomerRow>();

    public DbSet<StripeCheckoutRow> StripeCheckouts => Set<StripeCheckoutRow>();

    public DbSet<BillingChangeReceipt> BillingChanges => Set<BillingChangeReceipt>();

    public DbSet<InternalBillingRow> InternalBilling => Set<InternalBillingRow>();

    public DbSet<AccountDeletionRow> AccountDeletions => Set<AccountDeletionRow>();

    public DbSet<DeletionCleanupRow> DeletionCleanup => Set<DeletionCleanupRow>();

    public DbSet<DeletionCleanupTargetRow> DeletionCleanupTargets => Set<DeletionCleanupTargetRow>();

    public DbSet<PendingRestoreRow> PendingRestores => Set<PendingRestoreRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AuthFlowRow.Configuration());
        modelBuilder.ApplyConfiguration(new SessionRow.Configuration());
        modelBuilder.ApplyConfiguration(new AccessTokenRow.Configuration());
        modelBuilder.ApplyConfiguration(new McpClientRow.Configuration());
        modelBuilder.ApplyConfiguration(new McpCodeRow.Configuration());
        modelBuilder.ApplyConfiguration(new SavedQueryRow.Configuration());
        modelBuilder.ApplyConfiguration(new SqlWorkspaceRow.Configuration());
        modelBuilder.ApplyConfiguration(new AllowanceAlertRow.Configuration());
        modelBuilder.ApplyConfiguration(new AlertRuleRow.Configuration());
        modelBuilder.ApplyConfiguration(new AlertEventRow.Configuration());
        modelBuilder.ApplyConfiguration(new LogDrainRow.Configuration());
        modelBuilder.ApplyConfiguration(new LogDrainCursorRow.Configuration());
        modelBuilder.ApplyConfiguration(new AccountRow.Configuration());
        modelBuilder.ApplyConfiguration(new NotificationRow.Configuration());
        modelBuilder.ApplyConfiguration(new OrganizationRow.Configuration());
        modelBuilder.ApplyConfiguration(new OrganizationProjectRow.Configuration());
        modelBuilder.ApplyConfiguration(new ProjectDatabaseRow.Configuration());
        modelBuilder.ApplyConfiguration(new AuditEventRow.Configuration());
        modelBuilder.ApplyConfiguration(new InvitationReservationRow.Configuration());
        modelBuilder.ApplyConfiguration(new PendingOrganizationCheckout.Configuration());
        modelBuilder.ApplyConfiguration(new BillingBinding.Configuration());
        modelBuilder.ApplyConfiguration(new StripeCustomerRow.Configuration());
        modelBuilder.ApplyConfiguration(new StripeCheckoutRow.Configuration());
        modelBuilder.ApplyConfiguration(new BillingChangeReceipt.Configuration());
        modelBuilder.ApplyConfiguration(new InternalBillingRow.Configuration());
        modelBuilder.ApplyConfiguration(new AccountDeletionRow.Configuration());
        modelBuilder.ApplyConfiguration(new DeletionCleanupRow.Configuration());
        modelBuilder.ApplyConfiguration(new DeletionCleanupTargetRow.Configuration());
        modelBuilder.ApplyConfiguration(new PendingRestoreRow.Configuration());
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
            }
        }
    }

    private static string SnakeCase(string name)
    {
        var text = new StringBuilder(name.Length + 8);
        foreach (var character in name)
        {
            if (char.IsAsciiLetterUpper(character) && text.Length > 0)
            {
                text.Append('_');
            }

            text.Append(char.ToLowerInvariant(character));
        }

        return text.ToString();
    }
}
