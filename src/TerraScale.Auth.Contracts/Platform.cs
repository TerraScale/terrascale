namespace TerraScale.Auth.Contracts;

/// <summary>An organization of the platform tenant (collection <c>organizations</c>).</summary>
/// <param name="Id">UUIDv7.</param>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="Name">Display name; not unique.</param>
/// <param name="CreatedBy">The creating user.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Owners">Members with the owner role; every owner change writes it, so the last owner is never lost.</param>
/// <param name="Projects">Its projects; every project creation and deletion writes it.</param>
public sealed record OrganizationRecord(string Id, string InstanceId, string Name, string CreatedBy, DateTimeOffset CreatedAt, int Owners, int Projects);

/// <summary>
/// A user's place in an organization (collection <c>memberships</c>, addressed by organization and user): a member's
/// organization role, or a collaborator's record without one. Every write that gives or takes a project role also
/// rewrites or deletes this record, so a removal and a grant never both succeed on stale reads.
/// </summary>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="UserId">The member or collaborator.</param>
/// <param name="Role"><c>viewer</c>, <c>member</c>, <c>admin</c> or <c>owner</c>; null for a collaborator.</param>
/// <param name="JoinedAt">When the user joined.</param>
public sealed record MembershipRecord(string InstanceId, string OrganizationId, string UserId, string? Role, DateTimeOffset JoinedAt);

/// <summary>A project (collection <c>projects</c>); its id is also its end users' tenant.</summary>
/// <param name="Id">UUIDv7.</param>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="OrganizationId">The owning organization; never changes.</param>
/// <param name="Name">Display name, unique in the organization ignoring case.</param>
/// <param name="CreatedBy">The creating user.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record ProjectRecord(string Id, string InstanceId, string OrganizationId, string Name, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>An explicit project role (collection <c>project_members</c>, addressed by project and user).</summary>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="OrganizationId">The project's organization.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="UserId">The user; an organization member or a collaborator.</param>
/// <param name="Role"><c>viewer</c>, <c>member</c> or <c>admin</c>.</param>
/// <param name="GrantedAt">When the role was first granted.</param>
public sealed record ProjectMemberRecord(string InstanceId, string OrganizationId, string ProjectId, string UserId, string Role, DateTimeOffset GrantedAt);
