using Dapper;

namespace TerraScale.Dashboard.Features.Notifications;

public sealed record AllowanceAlertRow
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
}

public sealed record AlertRuleRow
{
    public string? OriginalVersion { get; set; }

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

    [DbValue(Ignore = true)]
    public List<AlertEventRow> Events { get; init; } = [];
}

public sealed record AlertEventRow
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
}
