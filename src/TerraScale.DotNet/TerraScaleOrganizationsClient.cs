using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Client for organization, membership, and invitation endpoints.
/// </summary>
public sealed class TerraScaleOrganizationsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleOrganizationsClient(TerraScaleClient client) => this.client = client;

    /// <summary>
    /// Lists organizations visible to the current caller.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleOrganization>> ListAsync(
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleOrganization>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.OrganizationsList(skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Creates an organization.
    /// </summary>
    public Task<TerraScaleOrganization> CreateAsync(
        TerraScaleCreateOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        return client.SendAsync<TerraScaleOrganization>(
            HttpMethod.Post,
            TerraScaleApiRoutes.OrganizationsCreate(),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Gets an organization by id.
    /// </summary>
    public Task<TerraScaleOrganization> GetAsync(
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(organizationId, nameof(organizationId));
        return client.SendAsync<TerraScaleOrganization>(
            HttpMethod.Get,
            TerraScaleApiRoutes.OrganizationGet(organizationId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Updates an organization's display name.
    /// </summary>
    public Task<TerraScaleOrganization> UpdateAsync(
        string organizationId,
        TerraScaleUpdateOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateId(organizationId, nameof(organizationId));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        return client.SendAsync<TerraScaleOrganization>(
            HttpMethod.Patch,
            TerraScaleApiRoutes.OrganizationUpdate(organizationId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Requests permanent organization deletion after exact-name confirmation.
    /// </summary>
    public Task<TerraScaleOrganization> RequestDeletionAsync(
        string organizationId,
        TerraScaleRequestOrganizationDeletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateId(organizationId, nameof(organizationId));
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Confirm)
        {
            throw new ArgumentException("Organization deletion requires confirm=true.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.OrganizationName);
        return client.SendAsync<TerraScaleOrganization>(
            HttpMethod.Post,
            TerraScaleApiRoutes.OrganizationDeletionRequest(organizationId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Removes the current caller from an organization.
    /// </summary>
    public Task LeaveAsync(
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(organizationId, nameof(organizationId));
        return client.SendNonRetryableNoContentAsync(
            HttpMethod.Post,
            TerraScaleApiRoutes.OrganizationLeave(organizationId),
            cancellationToken);
    }

    /// <summary>
    /// Lists organization members.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleOrganizationMember>> ListMembersAsync(
        string organizationId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateId(organizationId, nameof(organizationId));
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleOrganizationMember>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.OrganizationMembersList(organizationId, skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Updates an organization member's role.
    /// </summary>
    public Task<TerraScaleOrganizationMember> UpdateMemberRoleAsync(
        string organizationId,
        string userId,
        TerraScaleUpdateOrganizationMemberRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateId(organizationId, nameof(organizationId));
        ValidateId(userId, nameof(userId));
        ArgumentNullException.ThrowIfNull(request);
        ValidateRole(request.Role, nameof(request));
        return client.SendAsync<TerraScaleOrganizationMember>(
            HttpMethod.Patch,
            TerraScaleApiRoutes.OrganizationMemberUpdate(organizationId, userId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Removes a member from an organization after explicit confirmation.
    /// </summary>
    public Task RemoveMemberAsync(
        string organizationId,
        string userId,
        TerraScaleRemoveOrganizationMemberRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ValidateId(organizationId, nameof(organizationId));
        ValidateId(userId, nameof(userId));
        request ??= new TerraScaleRemoveOrganizationMemberRequest(true);
        if (!request.Confirm)
        {
            throw new ArgumentException("Organization member removal requires confirm=true.", nameof(request));
        }

        return client.SendNonRetryableNoContentAsync(
            HttpMethod.Post,
            TerraScaleApiRoutes.OrganizationMemberRemove(organizationId, userId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Lists organization invitations.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleOrganizationInvitation>> ListInvitationsAsync(
        string organizationId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateId(organizationId, nameof(organizationId));
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleOrganizationInvitation>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.OrganizationInvitationsList(organizationId, skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Creates an organization invitation.
    /// </summary>
    public Task<TerraScaleOrganizationInvitation> CreateInvitationAsync(
        string organizationId,
        TerraScaleCreateOrganizationInvitationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateId(organizationId, nameof(organizationId));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Email);
        if (request.ProjectId is not null)
        {
            ValidateId(request.ProjectId, nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResponseUrlTemplate);
        return client.SendAsync<TerraScaleOrganizationInvitation>(
            HttpMethod.Post,
            TerraScaleApiRoutes.OrganizationInvitationCreate(organizationId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Accepts an organization invitation addressed to the current caller.
    /// </summary>
    public Task<TerraScaleOrganizationInvitation> AcceptInvitationAsync(
        string organizationId,
        string invitationId,
        CancellationToken cancellationToken = default) =>
        SendInvitationActionAsync(
            organizationId,
            invitationId,
            TerraScaleApiRoutes.OrganizationInvitationAccept,
            cancellationToken);

    /// <summary>
    /// Declines an organization invitation addressed to the current caller.
    /// </summary>
    public Task<TerraScaleOrganizationInvitation> DeclineInvitationAsync(
        string organizationId,
        string invitationId,
        CancellationToken cancellationToken = default) =>
        SendInvitationActionAsync(
            organizationId,
            invitationId,
            TerraScaleApiRoutes.OrganizationInvitationDecline,
            cancellationToken);

    /// <summary>
    /// Resends a pending organization invitation.
    /// </summary>
    public Task<TerraScaleOrganizationInvitation> ResendInvitationAsync(
        string organizationId,
        string invitationId,
        CancellationToken cancellationToken = default) =>
        SendInvitationActionAsync(
            organizationId,
            invitationId,
            TerraScaleApiRoutes.OrganizationInvitationResend,
            cancellationToken);

    /// <summary>
    /// Cancels a pending organization invitation.
    /// </summary>
    public Task<TerraScaleOrganizationInvitation> CancelInvitationAsync(
        string organizationId,
        string invitationId,
        CancellationToken cancellationToken = default) =>
        SendInvitationActionAsync(
            organizationId,
            invitationId,
            TerraScaleApiRoutes.OrganizationInvitationCancel,
            cancellationToken);

    private Task<TerraScaleOrganizationInvitation> SendInvitationActionAsync(
        string organizationId,
        string invitationId,
        Func<string, string, TerraScaleBoundRoute> route,
        CancellationToken cancellationToken)
    {
        ValidateId(organizationId, nameof(organizationId));
        ValidateId(invitationId, nameof(invitationId));
        return client.SendAsync<TerraScaleOrganizationInvitation>(
            HttpMethod.Post,
            route(organizationId, invitationId),
            body: null,
            cancellationToken);
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identifier is required.", parameterName);
        }
    }

    private static void ValidateRole(string role, string parameterName)
    {
        if (role is not (TerraScaleOrganizationMemberRoles.Member or TerraScaleOrganizationMemberRoles.Owner))
        {
            throw new ArgumentException(
                $"Role must be {TerraScaleOrganizationMemberRoles.Member} or {TerraScaleOrganizationMemberRoles.Owner}.",
                parameterName);
        }
    }
}

/// <summary>
/// Canonical organization member roles.
/// </summary>
public static class TerraScaleOrganizationMemberRoles
{
    /// <summary>A standard organization member.</summary>
    public const string Member = "Member";

    /// <summary>An organization owner.</summary>
    public const string Owner = "OrganizationOwner";
}

/// <summary>Request to create an organization.</summary>
public sealed record TerraScaleCreateOrganizationRequest(string Name);

/// <summary>Request to update an organization.</summary>
public sealed record TerraScaleUpdateOrganizationRequest(string Name);

/// <summary>Exact-name confirmation for organization deletion.</summary>
public sealed record TerraScaleRequestOrganizationDeletionRequest(bool Confirm, string OrganizationName);

/// <summary>Request to update an organization member's role.</summary>
public sealed record TerraScaleUpdateOrganizationMemberRoleRequest(string Role);

/// <summary>Explicit confirmation for organization member removal.</summary>
public sealed record TerraScaleRemoveOrganizationMemberRequest(bool Confirm);

/// <summary>Request to invite a user to an organization and optional project.</summary>
public sealed record TerraScaleCreateOrganizationInvitationRequest(
    string Email,
    string ResponseUrlTemplate,
    string? ProjectId = null);

/// <summary>Organization metadata returned by public organization endpoints.</summary>
public sealed record TerraScaleOrganization(
    [property: JsonRequired] string OrganizationId,
    [property: JsonRequired] string Name,
    [property: JsonRequired] IReadOnlyList<string> OwnerUserIds,
    [property: JsonRequired] string? CreatedByUserId,
    [property: JsonRequired] string Status,
    [property: JsonRequired] DateTimeOffset? DeletionRequestedAt,
    [property: JsonRequired] string? DeletionRequestedByUserId,
    [property: JsonRequired] DateTimeOffset CreatedAt) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (OrganizationId is null || Name is null || OwnerUserIds is null || Status is null)
        {
            throw new JsonException("TerraScale organization response contains a null required field.");
        }
    }
}

/// <summary>An organization member returned by the public API.</summary>
public sealed record TerraScaleOrganizationMember(
    [property: JsonRequired] string UserId,
    [property: JsonRequired] string Email,
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] string Role,
    [property: JsonRequired] DateTimeOffset JoinedAt) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (UserId is null || Email is null || DisplayName is null || Role is null)
        {
            throw new JsonException("TerraScale organization member response contains a null required field.");
        }
    }
}

/// <summary>An organization invitation returned by the public API.</summary>
public sealed record TerraScaleOrganizationInvitation(
    [property: JsonRequired] string InvitationId,
    [property: JsonRequired] string OrganizationId,
    string? ProjectId,
    [property: JsonRequired] string Email,
    [property: JsonRequired] string Role,
    [property: JsonRequired] string Status,
    string? InvitedByEmail,
    [property: JsonRequired] DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt,
    string? AcceptedByUserId,
    DateTimeOffset? DeclinedAt,
    DateTimeOffset? CanceledAt,
    [property: JsonRequired] DateTimeOffset LastSentAt,
    [property: JsonRequired] int SentCount,
    DateTimeOffset? CreatedAt) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (InvitationId is null || OrganizationId is null || Email is null || Role is null || Status is null)
        {
            throw new JsonException("TerraScale organization invitation response contains a null required field.");
        }
    }
}
