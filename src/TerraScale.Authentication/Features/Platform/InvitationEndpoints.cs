using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Invites an email address.</summary>
/// <param name="Email">The invitee's address.</param>
/// <param name="Role">An organization role, or with <paramref name="ProjectId"/> a project role.</param>
/// <param name="ProjectId">The project, for a project invitation.</param>
public sealed record CreateInvitationRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("project_id")] string? ProjectId = null);

/// <summary>Names an invitation by its token.</summary>
/// <param name="Token">The invitation token.</param>
public sealed record InvitationTokenRequest([property: JsonPropertyName("token")] string? Token);

/// <summary>Nominates the new owner.</summary>
/// <param name="UserId">A member who is not an owner.</param>
public sealed record StartTransferRequest([property: JsonPropertyName("user_id")] string? UserId);

/// <summary>An invitation.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Email">The invitee's address.</param>
/// <param name="Role">The role it gives.</param>
/// <param name="ProjectId">Its project, for a project invitation.</param>
/// <param name="InvitedBy">The inviting user.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="ExpiresAt">Expiry.</param>
/// <param name="Status"><c>pending</c>, <c>accepted</c>, <c>declined</c>, <c>revoked</c> or <c>expired</c>.</param>
public sealed record InvitationBody(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("invited_by")] string InvitedBy,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("status")] string Status);

/// <summary>A new invitation and its token, shown once.</summary>
/// <param name="Invitation">The invitation.</param>
/// <param name="Token">Its token; also emailed when an invitation page is configured.</param>
public sealed record InvitationCreatedBody(
    [property: JsonPropertyName("invitation")] InvitationBody Invitation,
    [property: JsonPropertyName("token")] string Token);

/// <summary>A page of invitations.</summary>
/// <param name="Items">The invitations.</param>
/// <param name="Next">The next page's <c>after</c>, or null.</param>
public sealed record InvitationPageBody(
    [property: JsonPropertyName("items")] IReadOnlyList<InvitationBody> Items,
    [property: JsonPropertyName("next")] string? Next);

/// <summary>An organization or project by id and name.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
public sealed record NamedBody(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

/// <summary>What a pending invitation offers.</summary>
/// <param name="Organization">The organization.</param>
/// <param name="Project">The project, for a project invitation.</param>
/// <param name="Email">The invitee's address.</param>
/// <param name="Role">The role it gives.</param>
/// <param name="ExpiresAt">Expiry.</param>
public sealed record InvitationLookupBody(
    [property: JsonPropertyName("organization")] NamedBody Organization,
    [property: JsonPropertyName("project")] NamedBody? Project,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>What the caller now holds.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="ProjectId">The project, for a project invitation.</param>
/// <param name="Role">The role.</param>
public sealed record AcceptedInvitationBody(
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("role")] string Role);

/// <summary>A pending ownership transfer.</summary>
/// <param name="FromUserId">The owner who offered it.</param>
/// <param name="ToUserId">The nominee.</param>
/// <param name="CreatedAt">When it was offered.</param>
/// <param name="ExpiresAt">Expiry.</param>
public sealed record OwnershipTransferBody(
    [property: JsonPropertyName("from_user_id")] string FromUserId,
    [property: JsonPropertyName("to_user_id")] string ToUserId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>An audit event.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Actor">The acting user.</param>
/// <param name="Action">What happened.</param>
/// <param name="Target">A user id, email, project id or key id.</param>
/// <param name="Detail">The new role, name or scopes.</param>
/// <param name="OccurredAt">When.</param>
public sealed record AuditEventBody(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt);

/// <summary>A page of audit events.</summary>
/// <param name="Items">The events, newest first.</param>
/// <param name="Next">The next page's <c>after</c>, or null.</param>
public sealed record AuditEventPageBody(
    [property: JsonPropertyName("items")] IReadOnlyList<AuditEventBody> Items,
    [property: JsonPropertyName("next")] string? Next);

/// <summary><c>POST /v1/terrascale/organizations/{organization}/invitations</c>.</summary>
public sealed class CreateInvitationEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post($"{Organization}/invitations");
        this.Document("create_invitation", "Invite an email address", "Without project_id: an organization role; needs admin, and owner to invite an owner. With project_id: a role on that project; needs the project's admin role. ts-auth emails a link to the dashboard's accept page, <dashboard origin>/invitations/<token>; the token is also answered once. Valid 7 days, single use. 409 invitation_pending while an unexpired invitation for the email and target is pending, 409 already_member when the address's user already has it. 429 rate_limited past 100 invitations per organization per hour. A full or stopping email queue answers 503 before creating a pending invitation; failed creates release their reserved queue capacity.", "Invitations", typeof(CreateInvitationRequest), StatusCodes.Status201Created, typeof(InvitationCreatedBody), conflict: true);
        Produces<Contracts.ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var body = await this.BodyAsync(PlatformApiJson.Default.CreateInvitationRequest, cancellationToken);
        var email = Users.NormalizeEmail(body.Email) ?? throw Invalid("email must be a valid email address.");
        if (body.ProjectId is not null && !PlatformDirectory.IsId(body.ProjectId))
        {
            throw Invalid("project_id must be a project id.");
        }

        if (body.Role is not { } role || !(body.ProjectId is null ? Roles.Organization : Roles.Project).Contains(role))
        {
            throw Invalid(body.ProjectId is null ? "role must be viewer, member, admin or owner." : "role must be viewer, member or admin for a project.");
        }

        var (invitation, token) = await invitations.CreateAsync(caller, this.RouteId("organization"), email, role, body.ProjectId,
            AuthEmails.Locale(this.HttpContext.Request.Headers.AcceptLanguage), cancellationToken);
        return Results.Json(new InvitationCreatedBody(this.Map(invitations, invitation), token), PlatformApiJson.Default.InvitationCreatedBody, statusCode: StatusCodes.Status201Created);
    }
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}/invitations</c>.</summary>
public sealed class ListInvitationsEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get($"{Organization}/invitations");
        this.Document("list_invitations", "List invitations", "Every invitation with its status, in creation order. Needs admin; a project admin lists its project's with project_id.", "Invitations", null, StatusCodes.Status200OK, typeof(InvitationPageBody), paged: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (limit, after) = this.Paging();
        var projectId = this.HttpContext.Request.Query.TryGetValue("project_id", out var value) ? value.ToString() : null;
        if (projectId is not null && !PlatformDirectory.IsId(projectId))
        {
            throw Invalid("project_id must be a project id.");
        }

        var page = await invitations.ListAsync(caller.Id, this.RouteId("organization"), projectId, limit, after, cancellationToken);
        return Results.Json(new InvitationPageBody([.. page.Items.Select(item => this.Map(invitations, item))], page.Next), PlatformApiJson.Default.InvitationPageBody);
    }
}

/// <summary><c>DELETE /v1/terrascale/organizations/{organization}/invitations/{invitation}</c>.</summary>
public sealed class RevokeInvitationEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete($"{Organization}/invitations/{{invitation}}");
        this.Document("revoke_invitation", "Revoke an invitation", "Needs admin, or the project's admin for a project invitation. Its status becomes revoked and its token stops working. 409 when it is no longer pending.", "Invitations", null, StatusCodes.Status204NoContent, null, conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await invitations.RevokeAsync(caller.Id, this.RouteId("organization"), this.HttpContext.GetRouteValue("invitation") as string ?? string.Empty, cancellationToken);
        return Results.NoContent();
    }
}

/// <summary><c>POST /v1/terrascale/invitations/lookup</c>.</summary>
public sealed class LookupInvitationEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/terrascale/invitations/lookup");
        this.Document("lookup_invitation", "Look up an invitation by token", "Any signed-in user holding the token. A token that is unknown, expired or no longer pending answers 404.", "Invitations", typeof(InvitationTokenRequest), StatusCodes.Status200OK, typeof(InvitationLookupBody), roleChecked: false);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var token = (await this.BodyAsync(PlatformApiJson.Default.InvitationTokenRequest, cancellationToken)).Token ?? throw Invalid("token is required.");
        var (invitation, organization, project) = await invitations.LookupAsync(token, cancellationToken);
        return Results.Json(new InvitationLookupBody(new(invitation.OrganizationId, organization), project is null ? null : new(invitation.ProjectId!, project),
            invitation.Email, invitation.Role, invitation.ExpiresAt), PlatformApiJson.Default.InvitationLookupBody);
    }
}

/// <summary><c>POST /v1/terrascale/invitations/accept</c>.</summary>
public sealed class AcceptInvitationEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/terrascale/invitations/accept");
        this.Document("accept_invitation", "Accept an invitation", "The caller's verified email must equal the invitation's, ignoring case (403 email_mismatch otherwise). 404 for an unknown token, 409 invitation_expired, 409 not_pending or already_member.", "Invitations", typeof(InvitationTokenRequest), StatusCodes.Status200OK, typeof(AcceptedInvitationBody), conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var token = (await this.BodyAsync(PlatformApiJson.Default.InvitationTokenRequest, cancellationToken)).Token ?? throw Invalid("token is required.");
        var invitation = await invitations.AcceptAsync(caller, token, cancellationToken);
        return Results.Json(new AcceptedInvitationBody(invitation.OrganizationId, invitation.ProjectId, invitation.Role), PlatformApiJson.Default.AcceptedInvitationBody);
    }
}

/// <summary><c>POST /v1/terrascale/invitations/decline</c>.</summary>
public sealed class DeclineInvitationEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/terrascale/invitations/decline");
        this.Document("decline_invitation", "Decline an invitation", "Same email rule as accept. The invitation's status becomes declined.", "Invitations", typeof(InvitationTokenRequest), StatusCodes.Status204NoContent, null, conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var token = (await this.BodyAsync(PlatformApiJson.Default.InvitationTokenRequest, cancellationToken)).Token ?? throw Invalid("token is required.");
        await invitations.DeclineAsync(caller, token, cancellationToken);
        return Results.NoContent();
    }
}

/// <summary><c>POST /v1/terrascale/organizations/{organization}/ownership-transfer</c>.</summary>
public sealed class StartOwnershipTransferEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post($"{Organization}/ownership-transfer");
        this.Document("start_ownership_transfer", "Offer ownership to a member", "Needs owner. The nominee is a member who is not an owner. Replaces a pending transfer; valid 7 days.", "Ownership transfer", typeof(StartTransferRequest), StatusCodes.Status201Created, typeof(OwnershipTransferBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var nominee = (await this.BodyAsync(PlatformApiJson.Default.StartTransferRequest, cancellationToken)).UserId;
        if (!PlatformDirectory.IsId(nominee))
        {
            throw Invalid("user_id must be a user id.");
        }

        var transfer = await platform.StartTransferAsync(caller.Id, this.RouteId("organization"), nominee!, cancellationToken);
        return Results.Json(Map(transfer), PlatformApiJson.Default.OwnershipTransferBody, statusCode: StatusCodes.Status201Created);
    }

    internal static OwnershipTransferBody Map(OwnershipTransferRecord transfer) => new(transfer.FromUserId, transfer.ToUserId, transfer.CreatedAt, transfer.ExpiresAt);
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}/ownership-transfer</c>.</summary>
public sealed class GetOwnershipTransferEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get($"{Organization}/ownership-transfer");
        this.Document("get_ownership_transfer", "Get the pending transfer", "Needs any organization role (a collaborator gets 403). 404 when none is pending.", "Ownership transfer", null, StatusCodes.Status200OK, typeof(OwnershipTransferBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        Results.Json(StartOwnershipTransferEndpoint.Map(await platform.TransferAsync(caller.Id, this.RouteId("organization"), cancellationToken)), PlatformApiJson.Default.OwnershipTransferBody);
}

/// <summary><c>DELETE /v1/terrascale/organizations/{organization}/ownership-transfer</c>.</summary>
public sealed class CancelOwnershipTransferEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete($"{Organization}/ownership-transfer");
        this.Document("cancel_ownership_transfer", "Cancel the pending transfer", "An owner or the nominee.", "Ownership transfer", null, StatusCodes.Status204NoContent, null);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await platform.CancelTransferAsync(caller.Id, this.RouteId("organization"), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary><c>POST /v1/terrascale/organizations/{organization}/ownership-transfer/confirm</c>.</summary>
public sealed class ConfirmOwnershipTransferEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post($"{Organization}/ownership-transfer/confirm");
        this.Document("confirm_ownership_transfer", "Confirm a transfer", "Only the nominee. The nominee becomes an owner and the owner who started it an admin.", "Ownership transfer", null, StatusCodes.Status200OK, typeof(MemberBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (member, email) = await platform.ConfirmTransferAsync(caller.Id, this.RouteId("organization"), cancellationToken);
        return Results.Json(Map(member, email), PlatformApiJson.Default.MemberBody);
    }
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}/audit-events</c>.</summary>
public sealed class ListAuditEventsEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get($"{Organization}/audit-events");
        this.Document("list_audit_events", "List audit events", "Needs admin. Newest first; kept at least 90 days.", "Audit", null, StatusCodes.Status200OK, typeof(AuditEventPageBody), paged: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (limit, after) = this.Paging();
        var page = await platform.AuditEventsAsync(caller.Id, this.RouteId("organization"), limit, after, cancellationToken);
        return Results.Json(new AuditEventPageBody([.. page.Items.Select(item => new AuditEventBody(item.Id, item.Actor, item.Action, item.Target, item.Detail, item.OccurredAt))], page.Next),
            PlatformApiJson.Default.AuditEventPageBody);
    }
}
