using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;

namespace TerraScale.Authentication.Features.Platform;

public sealed record CreateInvitationRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("project_id")] string? ProjectId = null);

public sealed record InvitationTokenRequest([property: JsonPropertyName("token")] string? Token);

public sealed record StartTransferRequest([property: JsonPropertyName("user_id")] string? UserId);

public sealed record InvitationBody(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("invited_by")] string InvitedBy,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("status")] string Status);

public sealed record InvitationCreatedBody(
    [property: JsonPropertyName("invitation")] InvitationBody Invitation,
    [property: JsonPropertyName("token")] string Token);

public sealed record InvitationPageBody(
    [property: JsonPropertyName("items")] IReadOnlyList<InvitationBody> Items,
    [property: JsonPropertyName("next")] string? Next);

public sealed record NamedBody(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record InvitationLookupBody(
    [property: JsonPropertyName("organization")] NamedBody Organization,
    [property: JsonPropertyName("project")] NamedBody? Project,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record AcceptedInvitationBody(
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("role")] string Role);

public sealed record OwnershipTransferBody(
    [property: JsonPropertyName("from_user_id")] string FromUserId,
    [property: JsonPropertyName("to_user_id")] string ToUserId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record AuditEventBody(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt);

public sealed record AuditEventPageBody(
    [property: JsonPropertyName("items")] IReadOnlyList<AuditEventBody> Items,
    [property: JsonPropertyName("next")] string? Next);

public sealed class CreateInvitationEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Post($"{Organization}/invitations");
        this.Describe("create_invitation", "Invite an email address", "Without project_id: an organization role; needs admin, and owner to invite an owner. With project_id: a role on that project; needs the project's admin role. ts-auth emails a link to the dashboard's accept page, <dashboard origin>/invitations/<token>; the token is also answered once. Valid 7 days, single use. 409 invitation_pending while an unexpired invitation for the email and target is pending, 409 already_member when the address's user already has it. 429 rate_limited with Retry-After past 100 invitations per organization per hour or 50 per inviting user per day, or when the organization's share of invitation email capacity is used up; nothing is created. A full or stopping email queue answers 503 before creating a pending invitation; failed creates release their reserved queue capacity.", "Invitations", typeof(CreateInvitationRequest), StatusCodes.Status201Created, typeof(InvitationCreatedBody), conflict: true);
        Produces<Contracts.ErrorBody>(StatusCodes.Status429TooManyRequests);
        ProducesHeader(StatusCodes.Status429TooManyRequests, "Retry-After", "Minimum delay in whole seconds before the binding limit admits another invitation.", JsonSchemaType.Integer);
    }

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
        return TypedResults.Json(new InvitationCreatedBody(this.Map(invitations, invitation), token), PlatformApiJson.Default.InvitationCreatedBody, statusCode: StatusCodes.Status201Created);
    }
}

public sealed class ListInvitationsEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Get($"{Organization}/invitations");
        this.Describe("list_invitations", "List invitations", "Every invitation with its status, in creation order. Needs admin; a project admin lists its project's with project_id.", "Invitations", null, StatusCodes.Status200OK, typeof(InvitationPageBody), paged: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (limit, after) = this.Paging();
        var projectId = this.HttpContext.Request.Query.TryGetValue("project_id", out var value) ? value.ToString() : null;
        if (projectId is not null && !PlatformDirectory.IsId(projectId))
        {
            throw Invalid("project_id must be a project id.");
        }

        var page = await invitations.ListAsync(caller.Id, this.RouteId("organization"), projectId, limit, after, cancellationToken);
        return TypedResults.Json(new InvitationPageBody([.. page.Items.Select(item => this.Map(invitations, item))], page.Next), PlatformApiJson.Default.InvitationPageBody);
    }
}

public sealed class RevokeInvitationEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Delete($"{Organization}/invitations/{{invitation}}");
        this.Describe("revoke_invitation", "Revoke an invitation", "Needs admin, or the project's admin for a project invitation. Its status becomes revoked and its token stops working. 409 when it is no longer pending.", "Invitations", null, StatusCodes.Status204NoContent, null, conflict: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await invitations.RevokeAsync(caller.Id, this.RouteId("organization"), this.HttpContext.GetRouteValue("invitation") as string ?? string.Empty, cancellationToken);
        return TypedResults.NoContent();
    }
}

public sealed class LookupInvitationEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Post("/v1/terrascale/invitations/lookup");
        this.Describe("lookup_invitation", "Look up an invitation by token", "Any signed-in user holding the token. A token that is unknown, expired or no longer pending answers 404.", "Invitations", typeof(InvitationTokenRequest), StatusCodes.Status200OK, typeof(InvitationLookupBody));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var token = (await this.BodyAsync(PlatformApiJson.Default.InvitationTokenRequest, cancellationToken)).Token ?? throw Invalid("token is required.");
        var (invitation, organization, project) = await invitations.LookupAsync(token, cancellationToken);
        return TypedResults.Json(new InvitationLookupBody(new(invitation.OrganizationId, organization), project is null ? null : new(invitation.ProjectId!, project),
            invitation.Email, invitation.Role, invitation.ExpiresAt), PlatformApiJson.Default.InvitationLookupBody);
    }
}

public sealed class AcceptInvitationEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Post("/v1/terrascale/invitations/accept");
        this.Describe("accept_invitation", "Accept an invitation", "The caller's verified email must equal the invitation's, ignoring case (403 email_mismatch otherwise). 404 for an unknown token, 409 invitation_expired, 409 not_pending or already_member.", "Invitations", typeof(InvitationTokenRequest), StatusCodes.Status200OK, typeof(AcceptedInvitationBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var token = (await this.BodyAsync(PlatformApiJson.Default.InvitationTokenRequest, cancellationToken)).Token ?? throw Invalid("token is required.");
        var invitation = await invitations.AcceptAsync(caller, token, cancellationToken);
        return TypedResults.Json(new AcceptedInvitationBody(invitation.OrganizationId, invitation.ProjectId, invitation.Role), PlatformApiJson.Default.AcceptedInvitationBody);
    }
}

public sealed class DeclineInvitationEndpoint(Invitations invitations, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Post("/v1/terrascale/invitations/decline");
        this.Describe("decline_invitation", "Decline an invitation", "Same email rule as accept. The invitation's status becomes declined.", "Invitations", typeof(InvitationTokenRequest), StatusCodes.Status204NoContent, null, conflict: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var token = (await this.BodyAsync(PlatformApiJson.Default.InvitationTokenRequest, cancellationToken)).Token ?? throw Invalid("token is required.");
        await invitations.DeclineAsync(caller, token, cancellationToken);
        return TypedResults.NoContent();
    }
}

public sealed class StartOwnershipTransferEndpoint(PlatformDirectory platform, PlatformAuthentication authentication, StepUpProofs proofs) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Post($"{Organization}/ownership-transfer");
        this.Describe("start_ownership_transfer", "Offer ownership to a member", "Needs owner and a fresh start_ownership_transfer/ts-auth X-TerraScale-Step-Up-Proof. The nominee is a member who is not an owner. Replaces a pending transfer; valid 7 days.", "Ownership transfer", typeof(StartTransferRequest), StatusCodes.Status201Created, typeof(OwnershipTransferBody));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var nominee = (await this.BodyAsync(PlatformApiJson.Default.StartTransferRequest, cancellationToken)).UserId;
        if (!PlatformDirectory.IsId(nominee))
        {
            throw Invalid("user_id must be a user id.");
        }

        return await proofs.ConsumeAsync(caller, StepUpProofs.Proof(this.HttpContext.Request),
            "start_ownership_transfer", "ts-auth", null, cancellationToken, this.RouteId("organization")) switch
        {
            StepUpConsumption consumption => await this.StartTransferAsync(consumption.Caller, nominee!, cancellationToken),
            AuthForbidden failure => ErrorResults.ToResult(failure),
        };
    }

    private async Task<IResult> StartTransferAsync(UserRecord caller, string nominee, CancellationToken cancellationToken)
    {
        var transfer = await platform.StartTransferAsync(caller.Id, this.RouteId("organization"), nominee, cancellationToken);
        return TypedResults.Json(Map(transfer), PlatformApiJson.Default.OwnershipTransferBody, statusCode: StatusCodes.Status201Created);
    }

    internal static OwnershipTransferBody Map(OwnershipTransferRecord transfer) => new(transfer.FromUserId, transfer.ToUserId, transfer.CreatedAt, transfer.ExpiresAt);
}

public sealed class GetOwnershipTransferEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Get($"{Organization}/ownership-transfer");
        this.Describe("get_ownership_transfer", "Get the pending transfer", "Needs any organization role (a collaborator gets 403). 404 when none is pending.", "Ownership transfer", null, StatusCodes.Status200OK, typeof(OwnershipTransferBody));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        TypedResults.Json(StartOwnershipTransferEndpoint.Map(await platform.TransferAsync(caller.Id, this.RouteId("organization"), cancellationToken)), PlatformApiJson.Default.OwnershipTransferBody);
}

public sealed class CancelOwnershipTransferEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Delete($"{Organization}/ownership-transfer");
        this.Describe("cancel_ownership_transfer", "Cancel the pending transfer", "An owner or the nominee.", "Ownership transfer", null, StatusCodes.Status204NoContent, null);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await platform.CancelTransferAsync(caller.Id, this.RouteId("organization"), cancellationToken);
        return TypedResults.NoContent();
    }
}

public sealed class ConfirmOwnershipTransferEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Post($"{Organization}/ownership-transfer/confirm");
        this.Describe("confirm_ownership_transfer", "Confirm a transfer", "Only the nominee. The nominee becomes an owner and the owner who started it an admin.", "Ownership transfer", null, StatusCodes.Status200OK, typeof(MemberBody));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (member, email) = await platform.ConfirmTransferAsync(caller.Id, this.RouteId("organization"), cancellationToken);
        return TypedResults.Json(Map(member, email), PlatformApiJson.Default.MemberBody);
    }
}

public sealed class ListAuditEventsEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Get($"{Organization}/audit-events");
        this.Describe("list_audit_events", "List audit events", "Needs admin. Newest first; kept at least 90 days.", "Audit", null, StatusCodes.Status200OK, typeof(AuditEventPageBody), paged: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (limit, after) = this.Paging();
        var page = await platform.AuditEventsAsync(caller.Id, this.RouteId("organization"), limit, after, cancellationToken);
        return TypedResults.Json(new AuditEventPageBody([.. page.Items.Select(item => new AuditEventBody(item.Id, item.Actor, item.Action, item.Target, item.Detail, item.OccurredAt))], page.Next),
            PlatformApiJson.Default.AuditEventPageBody);
    }
}
