using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Features.Notifications;

public sealed class AllowanceAlertRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string BillingMonth { get; init; }

    public required string Metric { get; init; }

    public required int Threshold { get; init; }

    public required long Used { get; init; }

    public required long Allowance { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required string EmailStatus { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<AllowanceAlertRow>
    {
        public void Configure(EntityTypeBuilder<AllowanceAlertRow> builder)
        {
            builder.ToTable("allowance_alerts", table =>
            {
                table.HasCheckConstraint("ck_allowance_alerts_id", "length(id) = 26 AND length(organization_id) = 26");
                table.HasCheckConstraint("ck_allowance_alerts_billing_month", "length(billing_month) = 7 AND substr(billing_month, 5, 1) = '-'");
                table.HasCheckConstraint("ck_allowance_alerts_metric", "metric IN ('operations', 'storage', 'spend')");
                table.HasCheckConstraint("ck_allowance_alerts_threshold", "threshold IN (80, 100) AND (metric <> 'spend' OR threshold = 100)");
                table.HasCheckConstraint("ck_allowance_alerts_amounts", "used >= 0 AND allowance > 0");
                table.HasCheckConstraint("ck_allowance_alerts_email_status", "email_status IN ('pending', 'accepted', 'unknown', 'rejected', 'no_recipients')");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => new { row.OrganizationId, row.BillingMonth, row.Metric, row.Threshold }).IsUnique();
        }
    }
}

public sealed class AlertRuleRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string ProjectId { get; init; }

    public required int Slot { get; init; }

    public required string Name { get; init; }

    public required string Metric { get; init; }

    public required double Threshold { get; set; }

    public string? DatabaseId { get; init; }

    public required bool Email { get; init; }

    public string? WebhookUrl { get; init; }

    public string? WebhookNonce { get; init; }

    public string? WebhookCiphertext { get; init; }

    public string? WebhookTag { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required string State { get; set; }

    public DateTime? Since { get; set; }

    public string? AlertId { get; set; }

    public double? Value { get; set; }

    public required string Version { get; set; }

    public List<AlertEventRow> Events { get; init; } = [];

    public sealed class Configuration : IEntityTypeConfiguration<AlertRuleRow>
    {
        public void Configure(EntityTypeBuilder<AlertRuleRow> builder)
        {
            builder.ToTable("alert_rules", table =>
            {
                table.HasCheckConstraint("ck_alert_rules_id", "length(id) = 26 AND length(organization_id) = 26 AND length(project_id) = 26");
                table.HasCheckConstraint("ck_alert_rules_slot", $"slot >= 0 AND slot < {AlertRules.RuleLimit}");
                table.HasCheckConstraint("ck_alert_rules_name", "length(name) BETWEEN 1 AND 64");
                table.HasCheckConstraint("ck_alert_rules_metric", "metric IN ('p95_latency_ms', 'error_rate_percent', 'storage_gb', 'operation_units_percent')");
                table.HasCheckConstraint("ck_alert_rules_threshold", "threshold > 0 AND threshold <= 1e12");
                table.HasCheckConstraint("ck_alert_rules_database", "database_id IS NULL OR length(database_id) = 26");
                table.HasCheckConstraint("ck_alert_rules_webhook",
                    "(webhook_url IS NULL AND webhook_nonce IS NULL AND webhook_ciphertext IS NULL AND webhook_tag IS NULL) OR " +
                    "(webhook_url LIKE 'https://%' AND webhook_nonce IS NOT NULL AND webhook_ciphertext IS NOT NULL AND webhook_tag IS NOT NULL)");
                table.HasCheckConstraint("ck_alert_rules_state", "(state = 'ok' AND (since IS NULL) = (alert_id IS NULL)) OR (state = 'firing' AND since IS NOT NULL AND length(alert_id) = 26)");
                table.HasCheckConstraint("ck_alert_rules_version", "length(version) = 26");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => new { row.ProjectId, row.Slot }).IsUnique();
            builder.HasIndex(row => row.OrganizationId);
            builder.Property(row => row.Version).IsConcurrencyToken();
            builder.HasMany(row => row.Events).WithOne().HasForeignKey(row => row.RuleId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}

public sealed class AlertEventRow
{
    public required string Id { get; init; }

    public required string RuleId { get; init; }

    public required string AlertId { get; init; }

    public required string State { get; init; }

    public required double Value { get; init; }

    public string? DatabaseName { get; init; }

    public required DateTime At { get; init; }

    public required string EmailStatus { get; set; }

    public required string WebhookStatus { get; set; }

    public required int WebhookAttempts { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<AlertEventRow>
    {
        public void Configure(EntityTypeBuilder<AlertEventRow> builder)
        {
            builder.ToTable("alert_events", table =>
            {
                table.HasCheckConstraint("ck_alert_events_id", "length(id) = 26 AND length(rule_id) = 26 AND length(alert_id) = 26");
                table.HasCheckConstraint("ck_alert_events_state", "state IN ('firing', 'resolved')");
                table.HasCheckConstraint("ck_alert_events_email_status", "email_status IN ('pending', 'none', 'accepted', 'unknown', 'rejected', 'no_recipients')");
                table.HasCheckConstraint("ck_alert_events_webhook_status", "webhook_status IN ('pending', 'none', 'delivered', 'failed')");
                table.HasCheckConstraint("ck_alert_events_pending", "email_status = 'pending' OR webhook_status = 'pending'");
                table.HasCheckConstraint("ck_alert_events_webhook_attempts", $"webhook_attempts BETWEEN 0 AND {AlertRules.WebhookAttempts}");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => new { row.RuleId, row.Id });
        }
    }
}
