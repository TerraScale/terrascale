using System.Text;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AuthFlowRow.Configuration());
        modelBuilder.ApplyConfiguration(new SessionRow.Configuration());
        modelBuilder.ApplyConfiguration(new AccessTokenRow.Configuration());
        modelBuilder.ApplyConfiguration(new McpClientRow.Configuration());
        modelBuilder.ApplyConfiguration(new McpCodeRow.Configuration());
        modelBuilder.ApplyConfiguration(new SavedQueryRow.Configuration());
        modelBuilder.ApplyConfiguration(new SqlWorkspaceRow.Configuration());
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
