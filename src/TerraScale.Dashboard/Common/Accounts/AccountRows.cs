using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Common.Accounts;

public sealed record AccountRow
{
    public string? OriginalVersion { get; set; }

    public required string Id { get; init; }

    public required string SubjectHash { get; init; }

    public string? UserId { get; set; }

    public required string Name { get; set; }

    public required string Email { get; set; }

    public required string Phone { get; set; }

    public required string Locale { get; set; }

    public required bool OnboardingCompleted { get; set; }

    public required string OnboardingStep { get; set; }

    public required string OrganizationIds { get; set; }

    public required string PersonalOrganizationId { get; set; }

    public DateTime? PersonalClaimUntil { get; set; }

    public required string ActiveOrganizationId { get; set; }

    public required string ActiveProjectId { get; set; }

    public required DateTime CreatedAt { get; init; }

    public string? ErasureId { get; set; }

    public required string Version { get; set; }
}

public sealed record NotificationRow
{
    public required string AccountId { get; init; }

    public required string Id { get; init; }

    public required string OrganizationId { get; set; }

    public required string ProjectId { get; set; }

    public required string Type { get; set; }

    public required string BillingMonth { get; set; }

    public required string Title { get; set; }

    public required string Body { get; set; }

    public required string Severity { get; set; }

    public required string Status { get; set; }

    public required string Url { get; set; }

    public required DateTime CreatedAt { get; set; }
}

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(InternalBillingPolicy))]
[JsonSerializable(typeof(Dictionary<string, InternalBillingAlert>))]
internal sealed partial class AccountRowJson : JsonSerializerContext;
