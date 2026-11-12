using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth;

public sealed record TsAuthOrganization(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("image_url")] string? ImageUrl,
    [property: JsonPropertyName("public_metadata")] JsonElement PublicMetadata,
    [property: JsonPropertyName("private_metadata")] JsonElement PrivateMetadata,
    [property: JsonPropertyName("max_members")] int? MaxMembers,
    [property: JsonPropertyName("domain_join")] string DomainJoin,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record TsAuthOrganizationRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("admin_user_id")] string? AdminUserId,
    [property: JsonPropertyName("image_url")] string? ImageUrl,
    [property: JsonPropertyName("public_metadata")] JsonElement? PublicMetadata,
    [property: JsonPropertyName("private_metadata")] JsonElement? PrivateMetadata,
    [property: JsonPropertyName("max_members")] int? MaxMembers,
    [property: JsonPropertyName("domain_join")] string DomainJoin);

public sealed record TsAuthOrganizationMembership(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record TsAuthOrganizationMembershipRequest(
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("role")] string Role);

public sealed record TsAuthOrganizationInvitation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record TsAuthOrganizationInvitationRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("inviter_name")] string? InviterName = null);

public sealed record TsAuthOrganizationDomain(
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("record_name")] string RecordName,
    [property: JsonPropertyName("record_value")] string RecordValue,
    [property: JsonPropertyName("verified_at")] DateTimeOffset? VerifiedAt);

public sealed record TsAuthOrganizationDomainRequest(
    [property: JsonPropertyName("domain")] string Domain);

public sealed record TsAuthOrganizationPage([property: JsonPropertyName("items")] IReadOnlyList<TsAuthOrganization> Items, [property: JsonPropertyName("next")] string? Next);

public sealed record TsAuthOrganizationMembershipPage([property: JsonPropertyName("items")] IReadOnlyList<TsAuthOrganizationMembership> Items, [property: JsonPropertyName("next")] string? Next);

public sealed record TsAuthOrganizationInvitationPage([property: JsonPropertyName("items")] IReadOnlyList<TsAuthOrganizationInvitation> Items, [property: JsonPropertyName("next")] string? Next);

public sealed record TsAuthOrganizationDomainPage([property: JsonPropertyName("items")] IReadOnlyList<TsAuthOrganizationDomain> Items);

