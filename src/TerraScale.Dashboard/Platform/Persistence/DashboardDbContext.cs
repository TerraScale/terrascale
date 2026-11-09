using System.Text;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class DashboardDbContext(DbContextOptions<DashboardDbContext> options) : DbContext(options)
{
    public DbSet<AuthFlowRow> AuthFlows => Set<AuthFlowRow>();

    public DbSet<SessionRow> Sessions => Set<SessionRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AuthFlowRow.Configuration());
        modelBuilder.ApplyConfiguration(new SessionRow.Configuration());
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
