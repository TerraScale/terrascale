using System.Text;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Notifications;
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
