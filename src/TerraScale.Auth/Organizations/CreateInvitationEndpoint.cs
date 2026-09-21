namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Organizations.OrganizationSupport;

internal sealed class CreateInvitationEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/organizations/{orgId}/invitations", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string orgId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, AuthOptions options, IEmailSender emailSender, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(httpRequest, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
        if (organization is null)
            return ApiResults.NotFound("organization_not_found", "no such organization");
        var body = await httpRequest.ReadFromJsonAsync<CreateInvitationRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Email))
            return ApiResults.BadRequest("invalid_request", "email and role are required");
        if (!SessionService.IsValidEmail(body.Email))
            return ApiResults.BadRequest("invalid_email", "email is not a valid address");
        if (!IsValidRole(body.Role))
            return ApiResults.BadRequest("invalid_role", "role must be 1-64 characters");

        var now = Now();
        var invitation = new Invitation
        {
            Id = "inv_" + SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            OrgId = organization.Id,
            Email = SessionService.NormalizeEmail(body.Email),
            Role = body.Role!.Trim(),
            Status = "pending",
            ExpiresAt = now + (long)InvitationLifetime.TotalSeconds,
            CreatedAt = now,
        };
        await store.CreateInvitationAsync(invitation, ct);

        var secret = SecureTokens.RandomToken(48);
        // VerificationToken has no metadata field, so the invitation id rides in UserId
        // (it is string?); the "invitation" purpose scopes that interpretation.
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "vtok_" + SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            UserId = invitation.Id,
            Purpose = "invitation",
            TokenKey = SecureTokens.Sha256Hex(secret),
            ExpiresAt = invitation.ExpiresAt,
            CreatedAt = now,
        }, ct);

        await AuditAsync(store, context.Instance.Id, "invitation.created", context.ApiKeyId, new Dictionary<string, object>
        {
            ["organization_id"] = organization.Id,
            ["email"] = invitation.Email,
        }, ct);

        var acceptanceUrl = $"{Origin(options)}/ui/{slug}/invitations/{invitation.Id}/accept?token={secret}";
        await emailSender.SendAsync(invitation.Email, $"You're invited to join {organization.Name}", $"Accept your invitation to join {organization.Name}: {acceptanceUrl}", ct);

        return Results.Ok(Render(invitation));
    }

    /// <summary>How long an invitation stays acceptable after it is issued.</summary>
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);
}
