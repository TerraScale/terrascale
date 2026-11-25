using System.Text;
using System.Text.Json;
using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed partial class ProjectOrganizationDirectory(AuthData data, WebhookOutbox outbox, TimeProvider clock, SignInMail mail, AuthOptions options, IHttpClientFactory clients, ILogger<ProjectOrganizationDirectory> logger)
{
    private const int RetireBatch = 64;

    private const int RetireConflicts = 8;

    private enum RetireStep
    {
        Progressed,
        Conflicted,
        Finished,
    }

    public static ProjectOrganizationBody Body(ProjectOrganizationRecord org) => new(org.Id, org.Name, org.Slug, org.ImageUrl,
        org.PublicMetadata, org.PrivateMetadata, org.MaxMembers, org.DomainJoin, org.CreatedAt);

    public async Task<Versioned<ProjectOrganizationRecord>> ReadAsync(string project, string org, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectOrganizationRecord>(
            "SELECT * FROM project_organizations WHERE project_id = @project AND (id = @org OR slug = @org)", new { project, org }), cancellationToken))
            ?? throw PlatformDirectory.NotFound();

    public async Task<ProjectOrganizationPage> ListAsync(string project, int limit, string? after, CancellationToken cancellationToken)
    {
        var cursor = PlatformDirectory.Cursor(after);
        var take = limit + 1;
        var rows = (await data.ReadAsync(connection => connection.QueryAsync<ProjectOrganizationRecord>(
            "SELECT * FROM project_organizations WHERE project_id = @project AND id > @cursor ORDER BY id LIMIT @take", new { project, cursor, take }), cancellationToken)).AsList();
        return new([.. rows.Take(limit).Select(Body)], rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task<ProjectOrganizationBody> CreateAsync(string project, ProjectOrganizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await this.UserAsync(project, request.AdminUserId, cancellationToken);
        var org = Validate(new(project, AuthIds.New(), "", "", null, EmptyMetadata(), EmptyMetadata(), null, "off", 1, 1, clock.GetUtcNow()), request);
        var member = new ProjectOrganizationMembership(project, org.Id, user.Id, "admin", clock.GetUtcNow());
        await this.CommitAsync([Change.Assert(user), Change.Insert(org), Change.Insert(member), this.Event(org, "organization.created"),
            this.Event(org, "organizationMembership.created", user.Id, "admin")], cancellationToken);
        return Body(org);
    }

    public async Task<ProjectOrganizationBody> UpdateAsync(string project, string id, ProjectOrganizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await this.ReadAsync(project, id, cancellationToken);
        var updated = Validate(current.Value, request);
        await this.CommitAsync([Change.Update(current, updated), this.Event(updated, "organization.updated")], cancellationToken);
        return Body(updated);
    }

    public async Task DeleteAsync(string project, string id, CancellationToken cancellationToken)
    {
        var org = (await this.ReadAsync(project, id, cancellationToken)).Value.Id;
        for (var conflicts = 0; conflicts < RetireConflicts;)
        {
            var current = await this.ReadAsync(project, org, cancellationToken);
            var step = await this.RetireMembersAsync(current, cancellationToken) ?? await this.RetireInvitationsAsync(current, cancellationToken)
                ?? (await data.SaveAsync([Change.Delete(current), this.Event(current.Value, "organization.deleted")], cancellationToken) ? RetireStep.Finished : RetireStep.Conflicted);
            if (step == RetireStep.Finished)
            {
                return;
            }

            conflicts = step == RetireStep.Conflicted ? conflicts + 1 : 0;
        }

        throw Conflict("version_conflict", "The organization kept changing while it was being deleted; try again.");
    }

    private async Task<RetireStep?> RetireMembersAsync(Versioned<ProjectOrganizationRecord> current, CancellationToken cancellationToken)
    {
        var (project, org) = (current.Value.ProjectId, current.Value.Id);
        var members = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<ProjectOrganizationMembership>(
            "SELECT * FROM project_organization_memberships WHERE project_id = @project AND organization_id = @org ORDER BY user_id LIMIT @RetireBatch",
            new { project, org, RetireBatch }), cancellationToken));
        if (members.Count == 0)
        {
            return null;
        }

        var admins = members.Count(member => member.Value.Role == "admin");
        return await data.SaveAsync(
        [
            Change.Update(current, current.Value with { Members = current.Value.Members - members.Count, Admins = current.Value.Admins - admins }),
            .. members.Select(member => Change.Delete(member)),
            .. members.Select(member => this.Event(current.Value, "organizationMembership.deleted", member.Value.UserId, member.Value.Role)),
        ], cancellationToken) ? RetireStep.Progressed : RetireStep.Conflicted;
    }

    private async Task<RetireStep?> RetireInvitationsAsync(Versioned<ProjectOrganizationRecord> current, CancellationToken cancellationToken)
    {
        var (project, org) = (current.Value.ProjectId, current.Value.Id);
        var invitations = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<ProjectOrganizationInvitation>(
            "SELECT * FROM project_organization_invitations WHERE project_id = @project AND organization_id = @org ORDER BY id LIMIT @RetireBatch",
            new { project, org, RetireBatch }), cancellationToken));
        if (invitations.Count == 0)
        {
            return null;
        }

        return await data.SaveAsync(
        [
            Change.Update(current, current.Value),
            .. invitations.Select(invite => Change.Delete(invite)),
            .. invitations.Select(invite => this.Event(current.Value, "organizationInvitation.revoked", invite.Value.Id)),
        ], cancellationToken) ? RetireStep.Progressed : RetireStep.Conflicted;
    }

    public async Task<ProjectMembershipPage> MembersAsync(string project, string id, int limit, string? after, CancellationToken cancellationToken)
    {
        var org = (await this.ReadAsync(project, id, cancellationToken)).Value.Id;
        var cursor = PlatformDirectory.Cursor(after);
        var take = limit + 1;
        var rows = (await data.ReadAsync(connection => connection.QueryAsync<ProjectOrganizationMembership>(
            "SELECT * FROM project_organization_memberships WHERE project_id = @project AND organization_id = @org AND user_id > @cursor ORDER BY user_id LIMIT @take",
            new { project, org, cursor, take }), cancellationToken)).AsList();
        return new([.. rows.Take(limit)], rows.Count > limit ? rows[limit - 1].UserId : null);
    }

    public async Task<ProjectOrganizationMembership> AddAsync(string project, string id, string? userId, string? role, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken);
        var user = await this.UserAsync(project, userId, cancellationToken);
        var member = new ProjectOrganizationMembership(project, org.Value.Id, user.Id, Role(role), clock.GetUtcNow());
        var updated = AddMember(org.Value, member.Role);
        await this.CommitAsync([Change.Assert(user), Change.Update(org, updated), Change.Insert(member),
            this.Event(updated, "organizationMembership.created", user.Id, member.Role),
            .. await this.ClearDismissalAsync(project, org.Value.Id, user.Id, cancellationToken)], cancellationToken);
        return member;
    }

    public async Task<ProjectOrganizationMembership?> ChangeMemberAsync(string project, string id, string userId, string? role, bool remove, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken);
        var member = await this.MemberAsync(project, org.Value.Id, userId, cancellationToken) ?? throw PlatformDirectory.NotFound();
        var nextRole = remove ? null : Role(role);
        var admins = org.Value.Admins - (member.Value.Role == "admin" ? 1 : 0) + (nextRole == "admin" ? 1 : 0);
        if (admins == 0)
        {
            throw Conflict("last_admin", "An organization must keep at least one admin.");
        }

        var updated = member.Value with { Role = nextRole ?? member.Value.Role };
        await this.CommitAsync([Change.Update(org, org.Value with { Admins = admins, Members = org.Value.Members - (remove ? 1 : 0) }),
            remove ? Change.Delete(member) : Change.Update(member, updated),
            this.Event(org.Value, remove ? "organizationMembership.deleted" : "organizationMembership.updated", userId, nextRole),
            .. remove ? await this.ClearDismissalAsync(project, org.Value.Id, userId, cancellationToken) : []], cancellationToken);
        return remove ? null : updated;
    }

    private async Task<IReadOnlyList<Change>> ClearDismissalAsync(string project, string org, string userId, CancellationToken cancellationToken)
    {
        var dismissal = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectOrganizationDismissal>(
            "SELECT * FROM project_organization_dismissals WHERE project_id = @project AND organization_id = @org AND user_id = @userId", new { project, org, userId }),
            cancellationToken));
        return dismissal is null ? [] : [Change.Delete(dismissal)];
    }

    public async Task<Versioned<ProjectOrganizationMembership>?> MemberAsync(string project, string org, string userId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectOrganizationMembership>(
            "SELECT * FROM project_organization_memberships WHERE project_id = @project AND organization_id = @org AND user_id = @userId", new { project, org, userId }),
            cancellationToken));

    public Task<OrganizationClaims?> ClaimsAsync(string project, string org, string userId, CancellationToken cancellationToken) =>
        data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<OrganizationClaims>(
            """
            SELECT o.id, o.slug, m.role FROM project_organizations o
            JOIN project_organization_memberships m ON m.project_id = o.project_id AND m.organization_id = o.id
            WHERE o.project_id = @project AND (o.id = @org OR o.slug = @org) AND m.user_id = @userId
            """, new { project, org, userId }), cancellationToken);

    public async Task<IReadOnlyList<OrganizationClaims>> ChoicesAsync(string project, string userId, CancellationToken cancellationToken) =>
        (await data.ReadAsync(connection => connection.QueryAsync<OrganizationClaims>(
            """
            SELECT o.id, o.slug, m.role FROM project_organizations o
            JOIN project_organization_memberships m ON m.project_id = o.project_id AND m.organization_id = o.id
            WHERE o.project_id = @project AND m.user_id = @userId ORDER BY o.slug
            """, new { project, userId }), cancellationToken)).AsList();

    private async Task<UserRecord> UserAsync(string project, string? id, CancellationToken cancellationToken) =>
        await data.ReadUserAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM users WHERE instance_id = @project AND id = @id AND disabled = 0", new { project, id }), cancellationToken)
             ?? throw Invalid("user_id must identify an active user in this project.");

    private static ProjectOrganizationRecord Validate(ProjectOrganizationRecord org, ProjectOrganizationRequest request)
    {
        var name = PlatformDirectory.Name(request.Name) ?? throw Invalid(PlatformDirectory.NameRule);
        var slug = request.Slug;
        if (slug is not { Length: >= 3 and <= 63 } || !char.IsAsciiLetterOrDigit(slug[0]) || !char.IsAsciiLetterOrDigit(slug[^1])
            || slug.Any(c => !char.IsAsciiDigit(c) && !char.IsAsciiLetterLower(c) && c != '-'))
        {
            throw Invalid("slug must be 3 to 63 lowercase letters, digits or hyphens, beginning and ending with a letter or digit.");
        }

        if (request.ImageUrl is { } image && (!Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.Scheme != "https" || image.Length > 2048 || uri.UserInfo.Length > 0))
        {
            throw Invalid("image_url must be an HTTPS URL of at most 2048 characters.");
        }

        if (request.MaxMembers is { } maximum && maximum < org.Members || request.DomainJoin is not ("off" or "suggest" or "auto"))
        {
            throw Invalid("max_members must accommodate existing members; domain_join must be off, suggest or auto.");
        }

        return org with
        {
            Name = name,
            Slug = slug,
            ImageUrl = request.ImageUrl,
            PublicMetadata = Metadata(request.PublicMetadata),
            PrivateMetadata = Metadata(request.PrivateMetadata),
            MaxMembers = request.MaxMembers,
            DomainJoin = request.DomainJoin
        };
    }

    private static JsonElement EmptyMetadata()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement Metadata(JsonElement? value)
    {
        if (value is null)
        {
            return EmptyMetadata();
        }

        if (value.Value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.Value.GetRawText()) > 8192)
        {
            throw Invalid("Each metadata field must be a JSON object of at most 8192 UTF-8 bytes.");
        }

        return value.Value.Clone();
    }

    private static ProjectOrganizationRecord AddMember(ProjectOrganizationRecord org, string role)
    {
        if (org.Admins == 0 && role != "admin")
        {
            throw Conflict("organization_requires_admin", "The first member of an empty organization must be an admin.");
        }

        return org.MaxMembers is { } maximum && org.Members >= maximum
            ? throw Conflict("organization_full", "The organization's member limit was reached.")
            : org with { Members = checked(org.Members + 1), Admins = org.Admins + (role == "admin" ? 1 : 0) };
    }

    internal static string Role(string? role) => role is "admin" or "member" ? role : throw Invalid("role must be admin or member.");
    private static ApiException Invalid(string message) => new(400, "invalid_request", message);
    private static ApiException Conflict(string code, string message) => new(409, code, message);
    private Change Event(ProjectOrganizationRecord org, string type, string? user = null, string? role = null) => Change.Insert(outbox.NewEvent(org.ProjectId, type, user ?? org.Id, organizationId: org.Id, organizationRole: role));

    private async Task CommitAsync(IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        if (!await data.SaveAsync(changes, cancellationToken))
        {
            throw Conflict("version_conflict", "The organization changed or a unique value is already in use; read it again.");
        }
    }
}
