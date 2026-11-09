using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext
{
    public DbSet<OidcApplication> OidcApplications => this.Set<OidcApplication>();

    public DbSet<OidcAuthorization> OidcAuthorizations => this.Set<OidcAuthorization>();

    public DbSet<OidcToken> OidcTokens => this.Set<OidcToken>();

    private static void ConfigureOidc(ModelBuilder model)
    {
        model.Entity<OidcApplication>(application =>
        {
            application.ToTable("oidc_applications");
            application.HasKey(row => new { row.InstanceId, row.Id });
            application.Property(row => row.InstanceId).IsRequired();
            application.Property(row => row.Id).IsRequired();
            application.Property(row => row.ClientId).IsRequired();
            application.Property(row => row.Version).IsRequired().HasMaxLength(26).IsConcurrencyToken();
            application.Property(row => row.SecretBindings).HasJson(StorageJson.Default.IReadOnlyListCredentialBinding);
            application.Property(row => row.Permissions).HasJson(StorageJson.Default.IReadOnlyListString);
            application.Property(row => row.PostLogoutRedirectUris).HasJson(StorageJson.Default.IReadOnlyListString);
            application.Property(row => row.RedirectUris).HasJson(StorageJson.Default.IReadOnlyListString);
            application.Property(row => row.Requirements).HasJson(StorageJson.Default.IReadOnlyListString);
            application.Property(row => row.Properties).HasJson(StorageJson.Default.IReadOnlyDictionaryStringJsonElement);
            application.Property(row => row.Settings).HasJson(StorageJson.Default.IReadOnlyDictionaryStringString);
        });

        model.Entity<OidcAuthorization>(authorization =>
        {
            authorization.ToTable("oidc_authorizations", table =>
                table.HasCheckConstraint("ck_oidc_authorizations_status", "status IN ('valid', 'revoked')"));
            authorization.HasKey(row => row.Id);
            authorization.Property(row => row.Id).IsRequired();
            authorization.Property(row => row.InstanceId).IsRequired();
            authorization.Property(row => row.Version).IsRequired().HasMaxLength(26).IsConcurrencyToken();
            authorization.HasIndex(row => new { row.InstanceId, row.Subject });
            authorization.HasIndex(row => new { row.InstanceId, row.ApplicationId });
            authorization.HasIndex(row => row.CreationDate);
            authorization.Property(row => row.Scopes).HasJson(StorageJson.Default.IReadOnlyListString);
            authorization.Property(row => row.Properties).HasJson(StorageJson.Default.IReadOnlyDictionaryStringJsonElement);
        });

        model.Entity<OidcToken>(token =>
        {
            token.ToTable("oidc_tokens");
            token.HasKey(row => row.Id);
            token.Property(row => row.Id).IsRequired();
            token.Property(row => row.InstanceId).IsRequired();
            token.Property(row => row.Version).IsRequired().HasMaxLength(26).IsConcurrencyToken();
            token.HasOne<OidcAuthorization>().WithMany().HasForeignKey(row => row.AuthorizationId).OnDelete(DeleteBehavior.Cascade);
            token.HasIndex(row => new { row.InstanceId, row.ReferenceId }).IsUnique();
            token.HasIndex(row => new { row.InstanceId, row.Subject });
            token.HasIndex(row => new { row.InstanceId, row.ApplicationId });
            token.HasIndex(row => row.AuthorizationId);
            token.HasIndex(row => row.ExpirationDate);
            token.Property(row => row.Bindings).HasJson(StorageJson.Default.IReadOnlyListCredentialBinding);
            token.Property(row => row.Properties).HasJson(StorageJson.Default.IReadOnlyDictionaryStringJsonElement);
            token.Ignore(row => row.StoredReferenceId);
        });
    }
}
