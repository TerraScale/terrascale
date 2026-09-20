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

/// <summary>Organizations feature endpoints: organizations, memberships with roles, and email invitations.</summary>
public static class OrganizationEndpoints
{
    /// <summary>How long an invitation stays acceptable after it is issued.</summary>
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    /// <summary>Maps Organizations endpoints.</summary>
    public static void Map(WebApplication app)
    {
        // ---- Organizations ------------------------------------------------------------

        app.MapPost("/v1/{slug}/organizations", async (string slug, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var body = await httpRequest.ReadFromJsonAsync<CreateOrganizationRequest>(ct);
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return ApiResults.BadRequest("invalid_request", "name is required");
            var name = body.Name.Trim();

            var orgSlug = string.IsNullOrWhiteSpace(body.Slug) ? DeriveSlug(name) : body.Slug.Trim().ToLowerInvariant();
            if (orgSlug is null)
                return ApiResults.BadRequest("invalid_slug", "a slug cannot be derived from this name; provide an explicit slug");
            if (orgSlug.Length is < 3 or > 48 || orgSlug.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                return ApiResults.BadRequest("invalid_slug", "slug must be 3-48 lowercase url-safe characters");

            var organization = new Organization
            {
                Id = "org_" + SecureTokens.RandomToken(20),
                InstanceId = context.Instance.Id,
                Name = name,
                SlugKey = $"{context.Instance.Id}:{orgSlug}",
                CreatedAt = Now(),
            };
            // Check-then-act (as instance creation does): the slugKey unique index is the
            // hard guarantee, this catches the common case with a precise error either way.
            if ((await store.ListOrganizationsAsync(context.Instance.Id, ct)).Any(o => o.SlugKey == organization.SlugKey))
                return ApiResults.Conflict("organization_exists", "an organization with this slug already exists");
            try
            {
                await store.CreateOrganizationAsync(organization, ct);
            }
            catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                return ApiResults.Conflict("organization_exists", "an organization with this slug already exists");
            }
            await AuditAsync(store, context.Instance.Id, "organization.created", context.ApiKeyId, new Dictionary<string, object>
            {
                ["organization_id"] = organization.Id,
            }, ct);
            return Results.Ok(Render(organization, context.Instance.Id));
        });

        app.MapGet("/v1/{slug}/organizations", async (string slug, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var organizations = await store.ListOrganizationsAsync(context.Instance.Id, ct);
            return Results.Ok(organizations.Select(org => Render(org, context.Instance.Id)).ToList());
        });

        app.MapGet("/v1/{slug}/organizations/{orgId}", async (string slug, string orgId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
            return organization is null
                ? ApiResults.NotFound("organization_not_found", "no such organization")
                : Results.Ok(Render(organization, context.Instance.Id));
        });

        app.MapPatch("/v1/{slug}/organizations/{orgId}", async (string slug, string orgId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
            if (organization is null)
                return ApiResults.NotFound("organization_not_found", "no such organization");
            var body = await httpRequest.ReadFromJsonAsync<UpdateOrganizationRequest>(ct);
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return ApiResults.BadRequest("invalid_request", "name is required");

            organization = organization with { Name = body.Name.Trim() };
            await store.UpsertOrganizationAsync(organization, ct);
            return Results.Ok(Render(organization, context.Instance.Id));
        });

        app.MapDelete("/v1/{slug}/organizations/{orgId}", async (string slug, string orgId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, TerraScaleClient client, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
            if (organization is null)
                return ApiResults.NotFound("organization_not_found", "no such organization");

            await store.DeleteOrganizationAsync(organization.Id, ct);
            await AuditAsync(store, context.Instance.Id, "organization.deleted", context.ApiKeyId, new Dictionary<string, object>
            {
                ["organization_id"] = organization.Id,
            }, ct);
            return Results.NoContent();
        });

        // ---- Memberships ----------------------------------------------------------------

        app.MapPost("/v1/{slug}/organizations/{orgId}/memberships", async (string slug, string orgId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
            if (organization is null)
                return ApiResults.NotFound("organization_not_found", "no such organization");
            var body = await httpRequest.ReadFromJsonAsync<CreateMembershipRequest>(ct);
            if (body is null || string.IsNullOrWhiteSpace(body.UserId) || !IsValidRole(body.Role))
                return ApiResults.BadRequest("invalid_request", "user_id and role (1-64 characters) are required");

            var user = await store.GetUserAsync(body.UserId.Trim(), ct);
            if (user is null || user.InstanceId != context.Instance.Id || user.Deleted)
                return ApiResults.NotFound("user_not_found", "no such user in this instance");

            var membership = new OrgMembership
            {
                Id = "mem_" + SecureTokens.RandomToken(20),
                InstanceId = context.Instance.Id,
                OrgId = organization.Id,
                UserId = user.Id,
                MemberKey = $"{organization.Id}:{user.Id}",
                Role = body.Role!.Trim(),
                CreatedAt = Now(),
            };
            // Check-then-act ahead of the memberKey unique index (see organization creation).
            if ((await store.ListMembershipsByOrgAsync(organization.Id, ct)).Any(m => m.UserId == user.Id))
                return ApiResults.Conflict("already_a_member", "the user is already a member of this organization");
            try
            {
                await store.CreateMembershipAsync(membership, ct);
            }
            catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                return ApiResults.Conflict("already_a_member", "the user is already a member of this organization");
            }
            await AuditAsync(store, context.Instance.Id, "membership.created", context.ApiKeyId, new Dictionary<string, object>
            {
                ["organization_id"] = organization.Id,
                ["membership_id"] = membership.Id,
                ["user_id"] = membership.UserId,
                ["role"] = membership.Role,
            }, ct);
            return Results.Ok(Render(membership));
        });

        app.MapGet("/v1/{slug}/organizations/{orgId}/memberships", async (string slug, string orgId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
            if (organization is null)
                return ApiResults.NotFound("organization_not_found", "no such organization");

            var items = new List<object>();
            foreach (var membership in await store.ListMembershipsByOrgAsync(organization.Id, ct))
            {
                var user = await store.GetUserAsync(membership.UserId, ct);
                object? summary = user is null ? null : new { id = user.Id, email = user.Email, name = user.Name };
                items.Add(new { id = membership.Id, organization_id = membership.OrgId, user_id = membership.UserId, role = membership.Role, created_at = membership.CreatedAt, user = summary });
            }
            return Results.Ok(items);
        });

        app.MapPatch("/v1/{slug}/organizations/{orgId}/memberships/{membershipId}", async (string slug, string orgId, string membershipId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
            if (organization is null)
                return ApiResults.NotFound("organization_not_found", "no such organization");
            var membership = await FindMembershipAsync(store, context.Instance.Id, organization.Id, membershipId, ct);
            if (membership is null)
                return ApiResults.NotFound("membership_not_found", "no such membership in this organization");
            var body = await httpRequest.ReadFromJsonAsync<UpdateMembershipRequest>(ct);
            if (body is null || !IsValidRole(body.Role))
                return ApiResults.BadRequest("invalid_role", "role must be 1-64 characters");

            // One atomic write: the memberKey claim stays held the whole time (no
            // delete+re-create window where the membership or its claim is released).
            membership = membership with { Role = body.Role!.Trim() };
            await store.UpsertMembershipAsync(membership, ct);
            await AuditAsync(store, context.Instance.Id, "membership.updated", context.ApiKeyId, new Dictionary<string, object>
            {
                ["organization_id"] = organization.Id,
                ["membership_id"] = membership.Id,
                ["user_id"] = membership.UserId,
                ["role"] = membership.Role,
            }, ct);
            return Results.Ok(Render(membership));
        });

        app.MapDelete("/v1/{slug}/organizations/{orgId}/memberships/{membershipId}", async (string slug, string orgId, string membershipId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
            if (organization is null)
                return ApiResults.NotFound("organization_not_found", "no such organization");
            var membership = await FindMembershipAsync(store, context.Instance.Id, organization.Id, membershipId, ct);
            if (membership is null)
                return ApiResults.NotFound("membership_not_found", "no such membership in this organization");

            await store.DeleteMembershipAsync(membership.Id, ct);
            await AuditAsync(store, context.Instance.Id, "membership.deleted", context.ApiKeyId, new Dictionary<string, object>
            {
                ["organization_id"] = organization.Id,
                ["membership_id"] = membership.Id,
                ["user_id"] = membership.UserId,
            }, ct);
            return Results.NoContent();
        });

        // ---- User's organizations ----------------------------------------------------------

        app.MapGet("/v1/{slug}/users/{userId}/organizations", async (string slug, string userId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var user = await store.GetUserAsync(userId, ct);
            if (user is null || user.InstanceId != context.Instance.Id)
                return ApiResults.NotFound("user_not_found", "no such user in this instance");

            var items = new List<object>();
            foreach (var membership in (await store.ListMembershipsByUserAsync(user.Id, ct)).Where(m => m.InstanceId == context.Instance.Id))
            {
                var organization = await store.GetOrganizationAsync(membership.OrgId, ct);
                object? summary = organization is null ? null : Render(organization, context.Instance.Id);
                items.Add(new { id = membership.Id, role = membership.Role, created_at = membership.CreatedAt, organization = summary });
            }
            return Results.Ok(items);
        });

        // ---- Invitations -------------------------------------------------------------------

        app.MapPost("/v1/{slug}/organizations/{orgId}/invitations", async (string slug, string orgId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, AuthOptions options, IEmailSender emailSender, CancellationToken ct) =>
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
        });

        app.MapGet("/v1/{slug}/organizations/{orgId}/invitations", async (string slug, string orgId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
            if (organization is null)
                return ApiResults.NotFound("organization_not_found", "no such organization");

            var invitations = await store.ListInvitationsAsync(organization.Id, ct);
            return Results.Ok(invitations.Select(Render).ToList());
        });

        app.MapPost("/v1/{slug}/organizations/{orgId}/invitations/{invitationId}/revoke", async (string slug, string orgId, string invitationId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
        {
            var context = await resolver.ResolveAsync(httpRequest, ct);
            if (context is null || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
            if (organization is null)
                return ApiResults.NotFound("organization_not_found", "no such organization");
            var invitation = await store.GetInvitationAsync(invitationId, ct);
            if (invitation is null || invitation.InstanceId != context.Instance.Id || invitation.OrgId != organization.Id)
                return ApiResults.NotFound("invitation_not_found", "no such invitation for this organization");
            if (invitation.Status != "pending")
                return ApiResults.Conflict("invitation_not_pending", "only pending invitations can be revoked");

            // Atomic pending→revoked: an accept that lands first wins the transition, so a
            // revoke can never overwrite an accepted invitation (and vice versa below).
            var revoked = await store.TryTransitionInvitationAsync(invitation.Id, "pending", "revoked", ct);
            if (revoked is null)
                return ApiResults.Conflict("invitation_not_pending", "only pending invitations can be revoked");
            return Results.Ok(Render(revoked));
        });

        // ---- Hosted acceptance page (public) ------------------------------------------------

        app.MapGet("/ui/{slug}/invitations/{invitationId}/accept", async (string slug, string invitationId, string? token, HttpRequest httpRequest, AuthStore store, SessionService sessions, AuthOptions options, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return Ui.Page("Invitation not found", """<div class="error">This invitation link is not valid.</div>""", status: 404);

            var now = Now();
            var invitation = await store.GetInvitationAsync(invitationId, ct);
            if (invitation is null || invitation.InstanceId != instance.Id)
                return Ui.Page("Invitation not found", """<div class="error">This invitation link is not valid.</div>""", status: 404);

            // Token first, invitation state second: a replayed link must read as used, not
            // as expired/revoked (an accepted invitation still has a perfectly valid token shape).
            var verification = string.IsNullOrEmpty(token) ? null : await store.FindVerificationTokenAsync(SecureTokens.Sha256Hex(token), ct);
            if (verification is null || verification.Purpose != "invitation" || verification.InstanceId != instance.Id
                || verification.UserId != invitation.Id || verification.Consumed || verification.ExpiresAt <= now)
                return Ui.Page("Invalid link", """<div class="error">This acceptance link is invalid or has already been used.</div>""", status: 400);

            if (invitation.Status != "pending" || invitation.ExpiresAt <= now)
                return Ui.Page("Invitation no longer valid", """<div class="error">This invitation has expired or was revoked. Ask an administrator to send a new one.</div>""", status: 400);

            var organization = await store.GetOrganizationAsync(invitation.OrgId, ct);
            if (organization is null || organization.InstanceId != instance.Id)
                return Ui.Page("Organization unavailable", """<div class="error">The organization that invited you no longer exists.</div>""", status: 400);

            var origin = Origin(options);
            if (await store.FindUserByEmailAsync(instance.Id, invitation.Email, ct) is null)
                return Ui.Page("Create your account", $"""
                    <h1>Accept your invitation</h1>
                    <p>No account exists for <strong>{Ui.Esc(invitation.Email)}</strong> yet. Sign up with this email address first, then open this invitation link again.</p>
                    <p class="hint"><a href="{origin}/ui/{slug}/sign-up">Go to sign-up</a></p>
                    """);

            var cookie = httpRequest.Cookies[SessionCookies.Name];
            if (string.IsNullOrEmpty(cookie) || await sessions.ResolveAsync(instance.Id, cookie, ct) is not { } current)
                return Ui.Page("Sign in to accept", $"""
                    <h1>Accept your invitation</h1>
                    <p>You're invited to join <strong>{Ui.Esc(organization.Name)}</strong> as <strong>{Ui.Esc(invitation.Email)}</strong>.</p>
                    <p class="hint"><a href="{origin}/ui/{slug}/sign-in">Sign in</a> with that email address to accept.</p>
                    """);

            if (SessionService.NormalizeEmail(current.User.Email ?? "") != invitation.Email)
                return Ui.Page("Wrong account", $"""
                    <h1>Accept your invitation</h1>
                    <div class="error">You're signed in as <strong>{Ui.Esc(current.User.Email ?? current.User.Id)}</strong>, but this invitation is for <strong>{Ui.Esc(invitation.Email)}</strong>.</div>
                    <p class="hint"><a href="{origin}/ui/{slug}/sign-out">Sign out</a>, then sign in with the invited email address and reopen this link.</p>
                    """);

            var alreadyMember = (await store.ListMembershipsByOrgAsync(organization.Id, ct)).Any(m => m.UserId == current.User.Id);

            // Atomic single-use gate: the consume is an exact-version compare-and-set, so of
            // two concurrent accepts of one link only the first proceeds; the loser reads
            // the token as already used and nothing is created.
            var consumed = await store.ConsumeVerificationTokenAsync(
                verification.TokenKey,
                record => record.Purpose == "invitation" && record.InstanceId == instance.Id
                    && record.UserId == invitation.Id && !record.Consumed && record.ExpiresAt > now,
                ct);
            if (consumed is null)
                return Ui.Page("Invalid link", """<div class="error">This acceptance link is invalid or has already been used.</div>""", status: 400);

            // Atomic pending→accepted: a revoke that landed after the checks above wins here,
            // so a revoked invitation never creates a membership and is never reset to accepted.
            if (await store.TryTransitionInvitationAsync(invitation.Id, "pending", "accepted", ct) is null)
                return Ui.Page("Invitation no longer valid", """<div class="error">This invitation has expired or was revoked. Ask an administrator to send a new one.</div>""", status: 400);

            if (!alreadyMember)
            {
                try
                {
                    await store.CreateMembershipAsync(new OrgMembership
                    {
                        Id = "mem_" + SecureTokens.RandomToken(20),
                        InstanceId = instance.Id,
                        OrgId = organization.Id,
                        UserId = current.User.Id,
                        MemberKey = $"{organization.Id}:{current.User.Id}",
                        Role = invitation.Role,
                        CreatedAt = now,
                    }, ct);
                }
                catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                {
                    alreadyMember = true;
                }
            }

            await AuditAsync(store, instance.Id, "invitation.accepted", current.User.Id, new Dictionary<string, object>
            {
                ["organization_id"] = organization.Id,
                ["invitation_id"] = invitation.Id,
            }, ct);

            var message = alreadyMember ? $"You're already a member of {organization.Name}." : $"You've joined {organization.Name}!";
            return Ui.Page("Invitation accepted", $"""
                <h1>Invitation accepted</h1>
                <div class="success">{Ui.Esc(message)}</div>
                <p class="hint"><a href="{origin}/ui/{slug}/sign-in">Continue</a></p>
                """);
        });
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Public origin without a trailing slash (Uri.ToString() always appends one).</summary>
    private static string Origin(AuthOptions options) => options.PublicOrigin.ToString().TrimEnd('/');

    private static bool IsValidRole(string? role) =>
        !string.IsNullOrEmpty(role) && role.Trim().Length is >= 1 and <= 64;

    /// <summary>Lowercase slug from a display name: alphanumerics kept, everything else collapsed to dashes, trimmed to 48. Null when no valid 3-48 slug remains.</summary>
    private static string? DeriveSlug(string name)
    {
        var builder = new System.Text.StringBuilder();
        var pendingDash = false;
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0)
                    builder.Append('-');
                pendingDash = false;
                builder.Append(c);
            }
            else
            {
                pendingDash = true;
            }
        }
        var slug = builder.ToString();
        if (slug.Length > 48)
            slug = slug[..48].TrimEnd('-');
        return slug.Length is >= 3 and <= 48 ? slug : null;
    }

    private static async Task<Organization?> FindOrganizationAsync(AuthStore store, string instanceId, string orgId, CancellationToken ct)
    {
        var organization = await store.GetOrganizationAsync(orgId, ct);
        return organization is not null && organization.InstanceId == instanceId ? organization : null;
    }

    private static async Task<OrgMembership?> FindMembershipAsync(AuthStore store, string instanceId, string orgId, string membershipId, CancellationToken ct)
    {
        var membership = await store.GetMembershipAsync(membershipId, ct);
        return membership is not null && membership.InstanceId == instanceId && membership.OrgId == orgId ? membership : null;
    }

    private static Task AuditAsync(AuthStore store, string instanceId, string type, string? actorId, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = actorId,
            Data = data,
            CreatedAt = Now(),
        }, ct);

    private static object Render(Organization organization, string instanceId) =>
        new { id = organization.Id, name = organization.Name, slug = organization.SlugKey[(instanceId.Length + 1)..], created_at = organization.CreatedAt };

    private static object Render(OrgMembership membership) =>
        new { id = membership.Id, organization_id = membership.OrgId, user_id = membership.UserId, role = membership.Role, created_at = membership.CreatedAt };

    private static object Render(Invitation invitation) =>
        new { id = invitation.Id, organization_id = invitation.OrgId, email = invitation.Email, role = invitation.Role, status = invitation.Status, expires_at = invitation.ExpiresAt, created_at = invitation.CreatedAt };
}

/// <summary>Create organization request body.</summary>
internal sealed record CreateOrganizationRequest(string? Name, string? Slug);

/// <summary>Update organization request body.</summary>
internal sealed record UpdateOrganizationRequest(string? Name);

/// <summary>Create membership request body.</summary>
internal sealed record CreateMembershipRequest([property: System.Text.Json.Serialization.JsonPropertyName("user_id")] string? UserId, string? Role);

/// <summary>Update membership request body.</summary>
internal sealed record UpdateMembershipRequest(string? Role);

/// <summary>Create invitation request body.</summary>
internal sealed record CreateInvitationRequest(string? Email, string? Role);
