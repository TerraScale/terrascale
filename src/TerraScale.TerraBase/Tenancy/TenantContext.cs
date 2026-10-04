using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Tenancy;

/// <summary>An organization's plan, stored on the organization. Quality-of-service work keys off it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PlanTier>))]
public enum PlanTier
{
    /// <summary>The default plan of every organization.</summary>
    [JsonStringEnumMemberName("hobby")]
    Hobby,

    /// <summary>Paid plan.</summary>
    [JsonStringEnumMemberName("pro")]
    Pro,

    /// <summary>Contracted plan.</summary>
    [JsonStringEnumMemberName("enterprise")]
    Enterprise,
}

/// <summary>
/// The tenant a request acts for: an organization (the tenant, which owns billing) and its plan and, when the
/// request addresses them, one of its projects and one database of that project. It is resolved once per
/// request, from the API key on the data plane or from the dashboard credential and the acting user's
/// membership on the Management API, and every engine call takes one: storage is addressed by tenant,
/// never by a database id alone.
/// </summary>
/// <param name="OrganizationId">The organization, a lowercase hyphenated UUID.</param>
/// <param name="Plan">The organization's plan.</param>
/// <param name="ProjectId">A project of the organization, when the request addresses one.</param>
/// <param name="DatabaseId">A database of <paramref name="ProjectId"/>, when the request addresses one.</param>
public sealed record TenantContext(string OrganizationId, PlanTier Plan, string? ProjectId = null, string? DatabaseId = null);
