using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext
{
    public DbSet<ClosureJob> ClosureJobs => this.Set<ClosureJob>();

    public DbSet<ClosureCancellation> ClosureCancellations => this.Set<ClosureCancellation>();

    public DbSet<ClosureCommit> ClosureCommits => this.Set<ClosureCommit>();

    public DbSet<DeletionJob> DeletionJobs => this.Set<DeletionJob>();

    public DbSet<DeletionCursorRecord> DeletionCursors => this.Set<DeletionCursorRecord>();

    public DbSet<AccountDeletionReceipt> AccountDeletionReceipts => this.Set<AccountDeletionReceipt>();

    public DbSet<SigningKey> SigningKeys => this.Set<SigningKey>();

    public DbSet<RateLimitRecord> RateLimits => this.Set<RateLimitRecord>();

    public DbSet<EmailBudgetRecord> EmailBudgets => this.Set<EmailBudgetRecord>();

    public DbSet<EmailReservationRecord> EmailReservations => this.Set<EmailReservationRecord>();

    public DbSet<EmailDeliveryRecord> EmailDeliveries => this.Set<EmailDeliveryRecord>();

    public DbSet<SupportEmailDelivery> SupportDeliveries => this.Set<SupportEmailDelivery>();

    public DbSet<EmailConfig> EmailConfigs => this.Set<EmailConfig>();

    public DbSet<MailRow> MailOutbox => this.Set<MailRow>();

    public DbSet<StorageProbe> StorageProbes => this.Set<StorageProbe>();

    private static void ConfigureOperations(ModelBuilder model)
    {
        model.Entity<ClosureJob>(job =>
        {
            job.ToTable("closure_jobs", table =>
            {
                table.HasCheckConstraint("ck_closure_jobs_scope", "scope IN ('project', 'organization', 'account')");
                table.HasCheckConstraint("ck_closure_jobs_version", "project_closure_version IN (1, 2)");
            });
            job.HasKey(row => new { row.InstanceId, row.Scope, row.SubjectId });
            job.HasIndex(row => row.NextAttemptAt);
            job.HasIndex(row => row.AccountId);
            Versioned(job);
        });

        model.Entity<ClosureCancellation>(mark =>
        {
            mark.ToTable("closure_cancellations");
            mark.HasKey(row => row.Generation);
        });

        model.Entity<ClosureCommit>(mark =>
        {
            mark.ToTable("closure_commits");
            mark.HasKey(row => row.Generation);
        });

        model.Entity<DeletionJob>(job =>
        {
            job.ToTable("deletion_jobs", table => table.HasCheckConstraint("ck_deletion_jobs_scope", "scope IN ('project', 'organization', 'account')"));
            job.HasKey(row => row.Id);
            job.HasIndex(row => row.NextAttemptAt);
            Versioned(job);
        });

        model.Entity<DeletionCursorRecord>(cursor =>
        {
            cursor.ToTable("deletion_cursors");
            cursor.HasKey(row => row.Name);
            Versioned(cursor);
        });

        model.Entity<AccountDeletionReceipt>(receipt =>
        {
            receipt.ToTable("account_deletion_receipts");
            receipt.HasKey(row => new { row.InstanceId, row.TokenHash });
            receipt.Property(row => row.OrganizationIds).HasJson(StorageJson.Default.IReadOnlyListString);
        });

        model.Entity<SigningKey>(key =>
        {
            key.ToTable("signing_keys", table => table.HasCheckConstraint("ck_signing_keys_algorithm", "algorithm = 'ES256'"));
            key.HasKey(row => new { row.InstanceId, row.Id });
            Versioned(key);
        });

        model.Entity<RateLimitRecord>(limit =>
        {
            limit.ToTable("rate_limits", table => table.HasCheckConstraint("ck_rate_limits_counts", "window >= 0 AND used >= 0"));
            limit.HasKey(row => new { row.InstanceId, row.Key });
            Versioned(limit);
        });

        model.Entity<EmailBudgetRecord>(budget =>
        {
            budget.ToTable("email_budgets");
            budget.HasKey(row => row.Key);
            Versioned(budget);
        });

        model.Entity<EmailReservationRecord>(reservation =>
        {
            reservation.ToTable("email_reservations");
            reservation.HasKey(row => row.Id);
            reservation.HasIndex(row => row.At);
            reservation.HasIndex(row => row.AttemptId).IsUnique();
        });

        model.Entity<EmailDeliveryRecord>(delivery =>
        {
            delivery.ToTable("email_deliveries", table =>
                table.HasCheckConstraint("ck_email_deliveries_status", "status IN ('pending', 'sending', 'accepted', 'rejected', 'unknown')"));
            delivery.HasKey(row => new { row.Kind, row.Key, row.Recipient });
            Versioned(delivery);
        });

        model.Entity<SupportEmailDelivery>(delivery =>
        {
            delivery.ToTable("support_deliveries");
            delivery.HasKey(row => row.RequestId);
            Versioned(delivery);
        });

        model.Entity<EmailConfig>(config =>
        {
            config.ToTable("email_configs", table => table.HasCheckConstraint("ck_email_configs_version", "version > 0"));
            config.HasKey(row => new { row.InstanceId, row.Version });
            config.Property(row => row.Actor).HasJson(EmailJson.Default.EmailActor);
            config.Property(row => row.Branding).HasJson(EmailJson.Default.EmailBranding);
            config.Property(row => row.Templates).HasJson(EmailJson.Default.IReadOnlyListEmailTemplateOverride);
        });

        model.Entity<StorageProbe>(probe =>
        {
            probe.ToTable("storage_probes");
            probe.HasKey(row => row.Id);
            Versioned(probe);
        });

        model.Entity<MailRow>(row =>
        {
            row.ToTable("mail_outbox", table =>
                table.HasCheckConstraint("ck_mail_outbox_state", "state IN ('queued', 'ready', 'sending', 'sent', 'rejected', 'failed', 'suppressed', 'unknown')"));
            row.HasKey(item => item.Id);
            row.HasIndex(item => new { item.State, item.LeaseUntil });
            row.HasIndex(item => item.CreatedAt);
            Versioned(row);
        });
    }
}
