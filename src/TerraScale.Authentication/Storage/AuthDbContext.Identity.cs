using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext
{
    public DbSet<UserRecord> Users => this.Set<UserRecord>();

    public DbSet<ProviderLinkRecord> ProviderLinks => this.Set<ProviderLinkRecord>();

    public DbSet<SessionRecord> Sessions => this.Set<SessionRecord>();

    public DbSet<PasscodeRecord> Passcodes => this.Set<PasscodeRecord>();

    public DbSet<TotpCredentialRecord> MfaCredentials => this.Set<TotpCredentialRecord>();

    public DbSet<PasskeyCredentialEntry> Passkeys => this.Set<PasskeyCredentialEntry>();

    public DbSet<CredentialClaimRecord> CredentialClaims => this.Set<CredentialClaimRecord>();

    public DbSet<ChallengeRecord> Challenges => this.Set<ChallengeRecord>();

    internal DbSet<StepUpRecord> StepUpProofs => this.Set<StepUpRecord>();

    internal DbSet<StepUpDeletionGrant> StepUpDeletionGrants => this.Set<StepUpDeletionGrant>();

    internal DbSet<EmailChangeRecord> EmailChanges => this.Set<EmailChangeRecord>();

    private static void ConfigureIdentity(ModelBuilder model)
    {
        model.Entity<UserRecord>(user =>
        {
            user.ToTable("users", table =>
            {
                table.HasCheckConstraint("ck_users_auth_epoch", "auth_epoch >= 0");
                table.HasCheckConstraint("ck_users_email", "length(email) BETWEEN 3 AND 254 AND email = lower(email)");
                table.HasCheckConstraint("ck_users_platform_role", "platform_role IS NULL OR platform_role = 'admin'");
            });
            user.HasKey(row => row.Id);
            user.Property(row => row.Id).HasMaxLength(26);
            user.Property(row => row.InstanceId).HasMaxLength(26);
            user.Property(row => row.Email).HasMaxLength(254);
            user.Property(row => row.AuthStamp).IsRequired().HasMaxLength(26);
            user.Property(row => row.Version).IsRequired().HasMaxLength(26).IsConcurrencyToken();
            user.HasIndex(row => new { row.InstanceId, row.Email }).IsUnique();
            user.Ignore(row => row.LinkedProviders);
            user.Ignore(row => row.AuthenticationBindings);
            user.Ignore(row => row.StepUpBindings);
            user.Ignore(row => row.MultiFactor);
            user.Ignore(row => row.MfaMethod);
        });

        model.Entity<ProviderLinkRecord>(link =>
        {
            link.ToTable("provider_links");
            link.HasKey(row => new { row.InstanceId, row.Provider, row.Issuer, row.ClientId, row.Subject });
            link.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
            link.HasIndex(row => row.UserId);
            link.Ignore(row => row.Identity);
            Versioned(link);
        });

        model.Entity<SessionRecord>(session =>
        {
            session.ToTable("sessions", table => table.HasCheckConstraint("ck_sessions_token_hash", "length(token_hash) = 64"));
            session.HasKey(row => row.Id);
            session.HasIndex(row => row.TokenHash).IsUnique();
            session.HasIndex(row => new { row.InstanceId, row.Id });
            session.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
            session.HasIndex(row => row.UserId);
            session.HasIndex(row => row.ExpiresAt);
            session.Property(row => row.Bindings).HasJson(StorageJson.Default.IReadOnlyListCredentialBinding);
            Versioned(session);
        });

        model.Entity<PasscodeRecord>(passcode =>
        {
            passcode.ToTable("passcodes", table =>
            {
                table.HasCheckConstraint("ck_passcodes_failed_attempts", "failed_attempts >= 0");
                table.HasCheckConstraint("ck_passcodes_purpose", "purpose IS NULL OR purpose IN ('password-reset', 'sign-up', 'step-up')");
            });
            passcode.HasKey(row => new { row.InstanceId, row.Key });
            passcode.HasIndex(row => new { row.InstanceId, row.UserId });
            passcode.HasIndex(row => row.WindowStartedAt);
            passcode.Property(row => row.IssuanceBindings).HasJson(StorageJson.Default.IReadOnlyListCredentialBinding);
            passcode.Ignore(row => row.Binding);
            passcode.Ignore(row => row.AuthenticatedUser);
            Versioned(passcode);
        });

        model.Entity<TotpCredentialRecord>(credential =>
        {
            credential.ToTable("mfa_credentials", table =>
                table.HasCheckConstraint("ck_mfa_credentials_steps", "enrollment_epoch >= 0 AND last_accepted_step >= -1"));
            credential.HasKey(row => row.UserId);
            credential.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
            credential.Property(row => row.RecoveryHashes).HasJson(StorageJson.Default.IReadOnlyListString);
            Versioned(credential);
        });

        model.Entity<PasskeyCredentialEntry>(passkey =>
        {
            passkey.ToTable("passkeys");
            passkey.HasKey(row => new { row.InstanceId, row.CredentialId });
            passkey.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
            passkey.HasIndex(row => row.UserId);
            passkey.Property(row => row.CredentialId).HasMaxLength(1400);
            passkey.Property(row => row.Transports).HasJson(StorageJson.Default.IReadOnlyListString);
            Versioned(passkey);
        });

        model.Entity<CredentialClaimRecord>(claim =>
        {
            claim.ToTable("credential_claims");
            claim.HasKey(row => new { row.InstanceId, row.Kind, row.Key });
            claim.HasIndex(row => new { row.InstanceId, row.OwnerId });
            Versioned(claim);
        });

        model.Entity<ChallengeRecord>(challenge =>
        {
            challenge.ToTable("challenges", table => table.HasCheckConstraint("ck_challenges_token_hash", "length(token_hash) = 64"));
            challenge.HasKey(row => new { row.InstanceId, row.Purpose, row.TokenHash });
            challenge.HasIndex(row => row.ExpiresAt);
            Versioned(challenge);
        });

        model.Entity<StepUpRecord>(proof =>
        {
            proof.ToTable("step_up_proofs");
            proof.HasKey(row => row.Id);
            proof.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
            proof.HasIndex(row => row.UserId);
            proof.HasIndex(row => row.ExpiresAt);
            proof.Property(row => row.Bindings).HasJson(StorageJson.Default.IReadOnlyListCredentialBinding);
            Versioned(proof);
        });

        model.Entity<StepUpDeletionGrant>(grant =>
        {
            grant.ToTable("step_up_deletion_grants");
            grant.HasKey(row => new { row.InstanceId, row.ReceiptHash });
            grant.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
            grant.HasIndex(row => row.UserId);
            grant.HasIndex(row => row.ExpiresAt);
            grant.Property(row => row.Bindings).HasJson(StorageJson.Default.IReadOnlyListCredentialBinding);
            Versioned(grant);
        });

        model.Entity<EmailChangeRecord>(change =>
        {
            change.ToTable("email_changes", table => table.HasCheckConstraint("ck_email_changes_failed_attempts", "failed_attempts >= 0"));
            change.HasKey(row => row.UserId);
            change.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
            change.HasIndex(row => row.ExpiresAt);
            change.Property(row => row.Bindings).HasJson(StorageJson.Default.IReadOnlyListCredentialBinding);
            Versioned(change);
        });
    }
}
