using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public const string VersionProperty = "Version";

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        foreach (var entry in this.ChangeTracker.Entries<ProjectRecord>().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Property(NameKeyProperty).CurrentValue = entry.Entity.Name.ToUpperInvariant();
        }

        foreach (var entry in this.ChangeTracker.Entries<InvitationRecord>().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Property(TargetProperty).CurrentValue = entry.Entity.ProjectId ?? "-";
        }

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcInstantConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcInstantConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureSignUp(modelBuilder);
        ConfigureIdentity(modelBuilder);
        ConfigureAttackProtection(modelBuilder);
        ConfigureImpersonation(modelBuilder);
        ConfigurePlatform(modelBuilder);
        ConfigureOidc(modelBuilder);
        ConfigureOperations(modelBuilder);
        ConfigureProjectOrganizations(modelBuilder);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Snake(property.Name));
            }

            foreach (var complex in entity.GetComplexProperties())
            {
                foreach (var property in complex.ComplexType.GetProperties())
                {
                    property.SetColumnName(Snake(complex.Name) + "_" + Snake(property.Name));
                }
            }
        }
    }

    private static void Versioned(EntityTypeBuilder entity) =>
        entity.Property<string>(VersionProperty).IsRequired().HasMaxLength(26).IsConcurrencyToken();

    private static string Snake(string name)
    {
        var text = new StringBuilder(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsAsciiLetterUpper(character) && index > 0 && !char.IsAsciiLetterUpper(name[index - 1]))
            {
                text.Append('_');
            }

            text.Append(char.ToLowerInvariant(character));
        }

        return text.ToString();
    }

    private sealed class UtcInstantConverter() : ValueConverter<DateTimeOffset, DateTime>(
        instant => instant.UtcDateTime,
        stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)));
}
