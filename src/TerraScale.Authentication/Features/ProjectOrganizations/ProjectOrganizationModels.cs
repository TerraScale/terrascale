using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed record ProjectOrganizationRecord(string ProjectId, string Id, string Name, string Slug, string? ImageUrl,
    JsonElement PublicMetadata, JsonElement PrivateMetadata, int? MaxMembers, string DomainJoin, int Members, int Admins, DateTimeOffset CreatedAt);

public sealed record ProjectOrganizationMembership(string ProjectId, string OrganizationId, string UserId, string Role, DateTimeOffset CreatedAt);

public sealed record ProjectOrganizationInvitation(string ProjectId, string OrganizationId, string Id, string Email, string Role,
    string TokenHash, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record ProjectOrganizationDomain(string ProjectId, string OrganizationId, string Domain, string VerificationToken, DateTimeOffset? VerifiedAt, DateTimeOffset? CheckedAt = null, int FailedChecks = 0);

public sealed record ProjectOrganizationDismissal(string ProjectId, string OrganizationId, string UserId);

public sealed record ProjectOrganizationBody(string Id, string Name, string Slug, string? ImageUrl, JsonElement PublicMetadata,
    JsonElement PrivateMetadata, int? MaxMembers, string DomainJoin, DateTimeOffset CreatedAt);

public sealed record ProjectOrganizationPage(IReadOnlyList<ProjectOrganizationBody> Items, string? Next);

public sealed record ProjectOrganizationRequest(string? Name, string? Slug, string? AdminUserId, string? ImageUrl = null,
    JsonElement? PublicMetadata = null, JsonElement? PrivateMetadata = null, int? MaxMembers = null, string DomainJoin = "off");

public sealed record ProjectMembershipRequest(string? UserId, string? Role);

public sealed record ProjectMembershipPage(IReadOnlyList<ProjectOrganizationMembership> Items, string? Next);

public sealed record ProjectInvitationRequest(string? Email, string? Role);

public sealed record ProjectOrganizationInvitationBody(string Id, string Email, string Role, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record ProjectOrganizationInvitationPage(IReadOnlyList<ProjectOrganizationInvitationBody> Items, string? Next);

public sealed record ProjectDomainRequest(string? Domain);

public sealed record ProjectOrganizationDomainBody(string Domain, string RecordName, string RecordValue, DateTimeOffset? VerifiedAt);

public sealed record ProjectOrganizationDomainPage(IReadOnlyList<ProjectOrganizationDomainBody> Items);

public sealed record OrganizationClaims(string Id, string Slug, string Role);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProjectOrganizationRequest))]
[JsonSerializable(typeof(ProjectOrganizationBody))]
[JsonSerializable(typeof(ProjectOrganizationPage))]
[JsonSerializable(typeof(ProjectMembershipRequest))]
[JsonSerializable(typeof(ProjectOrganizationMembership))]
[JsonSerializable(typeof(ProjectMembershipPage))]
[JsonSerializable(typeof(ProjectInvitationRequest))]
[JsonSerializable(typeof(ProjectOrganizationInvitationBody))]
[JsonSerializable(typeof(ProjectOrganizationInvitationPage))]
[JsonSerializable(typeof(ProjectDomainRequest))]
[JsonSerializable(typeof(ProjectOrganizationDomainBody))]
[JsonSerializable(typeof(ProjectOrganizationDomainPage))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ProjectOrganizationsJson : JsonSerializerContext;
