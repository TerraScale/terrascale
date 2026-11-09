using System.Text.Json.Serialization;

namespace TerraBase.Tenancy;

// QoS compares tiers, so they stay sorted by entitlement.
[JsonConverter(typeof(JsonStringEnumConverter<PlanTier>))]
public enum PlanTier
{
    [JsonStringEnumMemberName("hobby")]
    Hobby,

    [JsonStringEnumMemberName("starter")]
    Starter,

    [JsonStringEnumMemberName("pro")]
    Pro,

    [JsonStringEnumMemberName("team")]
    Team,

    [JsonStringEnumMemberName("enterprise")]
    Enterprise,
}

public sealed record TenantContext(string OrganizationId, PlanTier Plan, string? ProjectId = null, string? DatabaseId = null);
