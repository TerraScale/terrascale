using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed partial class ProjectOrganizationDirectory(AuthData data, WebhookOutbox outbox, TimeProvider clock, SignInMail mail, AuthOptions options, IHttpClientFactory clients)
{
    public static ProjectOrganizationBody Body(ProjectOrganizationRecord org) => new(org.Id, org.Name, org.Slug, org.ImageUrl,
        org.PublicMetadata, org.PrivateMetadata, org.MaxMembers, org.DomainJoin, org.CreatedAt);

    public async Task<Versioned<ProjectOrganizationRecord>> ReadAsync(string project, string org, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.ProjectOrganizations.Where(row => row.ProjectId == project && (row.Id == org || row.Slug == org))
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();

    public async Task<ProjectOrganizationPage> ListAsync(string project, int limit, string? after, CancellationToken cancellationToken)
    {
        var cursor = PlatformDirectory.Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.ProjectOrganizations.Where(row => row.ProjectId == project && string.Compare(row.Id, cursor) > 0)
            .OrderBy(row => row.Id).Take(limit + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return new([.. rows.Take(limit).Select(Body)], rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task<ProjectOrganizationBody> CreateAsync(string project, ProjectOrganizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await this.UserAsync(project, request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var org = Validate(new(project, AuthIds.New(), "", "", null, EmptyMetadata(), EmptyMetadata(), null, "off", 1, 1, clock.GetUtcNow()), request);
        var member = new ProjectOrganizationMembership(project, org.Id, user.Id, "admin", clock.GetUtcNow());
        await this.CommitAsync([Change.Assert(user), Change.Insert(org), Change.Insert(member), this.Event(org, "organization.created"),
            this.Event(org, "organizationMembership.created", user.Id, "admin")], cancellationToken).ConfigureAwait(false);
        return Body(org);
    }

    public async Task<ProjectOrganizationBody> UpdateAsync(string project, string id, ProjectOrganizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        var updated = Validate(current.Value, request);
        await this.CommitAsync([Change.Update(current, updated), this.Event(updated, "organization.updated")], cancellationToken).ConfigureAwait(false);
        return Body(updated);
    }

    public async Task DeleteAsync(string project, string id, CancellationToken cancellationToken)
    {
        var current = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        await this.CommitAsync([Change.Delete(current), this.Event(current.Value, "organization.deleted")], cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectMembershipPage> MembersAsync(string project, string id, int limit, string? after, CancellationToken cancellationToken)
    {
        var org = (await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false)).Value.Id;
        var cursor = PlatformDirectory.Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.ProjectOrganizationMemberships
            .Where(row => row.ProjectId == project && row.OrganizationId == org && string.Compare(row.UserId, cursor) > 0)
            .OrderBy(row => row.UserId).Take(limit + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return new([.. rows.Take(limit)], rows.Count > limit ? rows[limit - 1].UserId : null);
    }

    public async Task<ProjectOrganizationMembership> AddAsync(string project, string id, string? userId, string? role, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        var user = await this.UserAsync(project, userId, cancellationToken).ConfigureAwait(false);
        var member = new ProjectOrganizationMembership(project, org.Value.Id, user.Id, Role(role), clock.GetUtcNow());
        var updated = AddMember(org.Value, member.Role);
        await this.CommitAsync([Change.Assert(user), Change.Update(org, updated), Change.Insert(member),
            this.Event(updated, "organizationMembership.created", user.Id, member.Role)], cancellationToken).ConfigureAwait(false);
        return member;
    }

    public async Task<ProjectOrganizationMembership?> ChangeMemberAsync(string project, string id, string userId, string? role, bool remove, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        var member = await this.MemberAsync(project, org.Value.Id, userId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        var nextRole = remove ? null : Role(role);
        var admins = org.Value.Admins - (member.Value.Role == "admin" ? 1 : 0) + (nextRole == "admin" ? 1 : 0);
        if (admins == 0)
        {
            throw Conflict("last_admin", "An organization must keep at least one admin.");
        }

        var updated = member.Value with { Role = nextRole ?? member.Value.Role };
        await this.CommitAsync([Change.Update(org, org.Value with { Admins = admins, Members = org.Value.Members - (remove ? 1 : 0) }),
            remove ? Change.Delete(member) : Change.Update(member, updated),
            this.Event(org.Value, remove ? "organizationMembership.deleted" : "organizationMembership.updated", userId, nextRole)], cancellationToken).ConfigureAwait(false);
        return remove ? null : updated;
    }

    public Task<Versioned<ProjectOrganizationMembership>?> MemberAsync(string project, string org, string userId, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.ProjectOrganizationMemberships.Where(row => row.ProjectId == project && row.OrganizationId == org && row.UserId == userId)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken);

    public Task<OrganizationClaims?> ClaimsAsync(string project, string org, string userId, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => (from organization in db.ProjectOrganizations
                                       join member in db.ProjectOrganizationMemberships on new { organization.ProjectId, OrganizationId = organization.Id } equals new { member.ProjectId, member.OrganizationId }
                                       where organization.ProjectId == project && (organization.Id == org || organization.Slug == org) && member.UserId == userId
                                       select new OrganizationClaims(organization.Id, organization.Slug, member.Role)).SingleOrDefaultAsync(token), cancellationToken);

    public async Task<IReadOnlyList<OrganizationClaims>> ChoicesAsync(string project, string userId, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => (from org in db.ProjectOrganizations
                                             join member in db.ProjectOrganizationMemberships on new { org.ProjectId, OrganizationId = org.Id } equals new { member.ProjectId, member.OrganizationId }
                                             where org.ProjectId == project && member.UserId == userId
                                             orderby org.Slug
                                             select new OrganizationClaims(org.Id, org.Slug, member.Role)).Take(101).ToListAsync(token), cancellationToken).ConfigureAwait(false);

    private async Task<UserRecord> UserAsync(string project, string? id, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.Users.Where(row => row.InstanceId == project && row.Id == id && !row.Disabled).SingleOrDefaultAsync(token), cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid("user_id must identify an active user in this project.");

    private static ProjectOrganizationRecord Validate(ProjectOrganizationRecord org, ProjectOrganizationRequest request)
    {
        var name = PlatformDirectory.Name(request.Name) ?? throw Invalid("name must be 1 to 128 characters.");
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

    private static ProjectOrganizationRecord AddMember(ProjectOrganizationRecord org, string role) => org.MaxMembers is { } maximum && org.Members >= maximum
        ? throw Conflict("organization_full", "The organization's member limit was reached.")
        : org with { Members = checked(org.Members + 1), Admins = org.Admins + (role == "admin" ? 1 : 0) };

    internal static string Role(string? role) => role is "admin" or "member" ? role : throw Invalid("role must be admin or member.");
    private static ApiException Invalid(string message) => new(400, "invalid_request", message);
    private static ApiException Conflict(string code, string message) => new(409, code, message);
    private Change Event(ProjectOrganizationRecord org, string type, string? user = null, string? role = null) => Change.Insert(outbox.NewEvent(org.ProjectId, type, user ?? org.Id, organizationId: org.Id, organizationRole: role));

    private async Task CommitAsync(IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        if (!await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
        {
            throw Conflict("version_conflict", "The organization changed or a unique value is already in use; read it again.");
        }
    }
}
