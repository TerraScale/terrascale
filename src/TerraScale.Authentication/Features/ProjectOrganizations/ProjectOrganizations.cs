using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public readonly union OrganizationCreated(ProjectOrganizationBody, Validation, Conflict);

public readonly union OrganizationSaved(ProjectOrganizationBody, NotFound, Validation, Conflict);

public readonly union OrganizationDeleted(Done, NotFound, Conflict);

public readonly union MembershipPageListed(ProjectMembershipPage, NotFound);

public readonly union OrganizationMember(ProjectOrganizationMembership, NotFound, Validation, Conflict);

public readonly union OrganizationMemberChanged(ProjectOrganizationMembership, Done, NotFound, Validation, Conflict);

public sealed partial class ProjectOrganizationDirectory(AuthData data, WebhookOutbox outbox, TimeProvider clock, SignInMail mail, AuthOptions options, IHttpClientFactory clients, ILogger<ProjectOrganizationDirectory> logger)
{
    public const string Changed = "The organization changed or a unique value is already in use; read it again.";

    public static ProjectOrganizationBody Body(ProjectOrganizationRecord org) => new(org.Id, org.Name, org.Slug, org.ImageUrl,
        org.PublicMetadata, org.PrivateMetadata, org.MaxMembers, org.DomainJoin, org.CreatedAt);

    internal async Task<Versioned<ProjectOrganizationRecord>?> ReadAsync(string project, string org, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.ProjectOrganizations.Where(row => row.ProjectId == project && (row.Id == org || row.Slug == org))
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);

    internal static NotFound NoSuchOrganization() => new("No such resource.", "not_found");

    public async Task<ProjectOrganizationPage> ListAsync(string project, int limit, string? after, CancellationToken cancellationToken)
    {
        var cursor = PlatformDirectory.Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.ProjectOrganizations.Where(row => row.ProjectId == project && string.Compare(row.Id, cursor) > 0)
            .OrderBy(row => row.Id).Take(limit + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return new([.. rows.Take(limit).Select(Body)], rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task<OrganizationCreated> CreateAsync(string project, ProjectOrganizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await data.ReadAsync((db, token) => db.Users.Where(row => row.InstanceId == project && row.Id == request.AdminUserId && !row.Disabled)
            .SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new Validation("user_id must identify an active user in this project.");
        }

        var validated = Validate(new(project, AuthIds.New(), "", "", null, EmptyMetadata(), EmptyMetadata(), null, "off", 1, 1, clock.GetUtcNow()), request);
        if (validated is Validation invalidOrg)
        {
            return invalidOrg;
        }

        var org = validated switch
        {
            ProjectOrganizationRecord record => record,
            Validation => throw new UnreachableException(),
        };

        var member = new ProjectOrganizationMembership(project, org.Id, user.Id, "admin", clock.GetUtcNow());
        return await data.SaveAsync([Change.Assert(user), Change.Insert(org), Change.Insert(member), this.Event(org, "organization.created"),
            this.Event(org, "organizationMembership.created", user.Id, "admin")], cancellationToken).ConfigureAwait(false)
            ? new OrganizationCreated(Body(org))
            : Refused();
    }

    public async Task<OrganizationSaved> UpdateAsync(string project, string id, ProjectOrganizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return NoSuchOrganization();
        }

        var validated = Validate(current.Value, request);
        if (validated is Validation invalid)
        {
            return invalid;
        }

        var updated = validated switch
        {
            ProjectOrganizationRecord record => record,
            Validation => throw new UnreachableException(),
        };

        return await data.SaveAsync([Change.Update(current, updated), this.Event(updated, "organization.updated")], cancellationToken).ConfigureAwait(false)
            ? new OrganizationSaved(Body(updated))
            : Refused();
    }

    public async Task<OrganizationDeleted> DeleteAsync(string project, string id, CancellationToken cancellationToken)
    {
        var current = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return NoSuchOrganization();
        }

        var members = await data.ReadAsync((db, token) => db.ProjectOrganizationMemberships
            .Where(row => row.ProjectId == project && row.OrganizationId == current.Value.Id).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        var invitations = await data.ReadAsync((db, token) => db.ProjectOrganizationInvitations
            .Where(row => row.ProjectId == project && row.OrganizationId == current.Value.Id).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return await data.SaveAsync([Change.Delete(current), this.Event(current.Value, "organization.deleted"),
            .. members.Select(member => this.Event(current.Value, "organizationMembership.deleted", member.UserId, member.Role)),
            .. invitations.Select(invite => this.Event(current.Value, "organizationInvitation.revoked", invite.Id))], cancellationToken).ConfigureAwait(false)
            ? new Done()
            : Refused();
    }

    public async Task<MembershipPageListed> MembersAsync(string project, string id, int limit, string? after, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return NoSuchOrganization();
        }

        var cursor = PlatformDirectory.Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.ProjectOrganizationMemberships
            .Where(row => row.ProjectId == project && row.OrganizationId == org.Value.Id && string.Compare(row.UserId, cursor) > 0)
            .OrderBy(row => row.UserId).Take(limit + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return new ProjectMembershipPage([.. rows.Take(limit)], rows.Count > limit ? rows[limit - 1].UserId : null);
    }

    public async Task<OrganizationMember> AddAsync(string project, string id, string? userId, string? role, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return NoSuchOrganization();
        }

        var user = await data.ReadAsync((db, token) => db.Users.Where(row => row.InstanceId == project && row.Id == userId && !row.Disabled)
            .SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new Validation("user_id must identify an active user in this project.");
        }

        if (Role(role) is not { } named)
        {
            return new Validation("role must be admin or member.");
        }

        if (RefusesMembership(org.Value, named) is { } refused)
        {
            return refused;
        }

        var updated = AddMember(org.Value, named);
        var member = new ProjectOrganizationMembership(project, org.Value.Id, user.Id, named, clock.GetUtcNow());
        return await data.SaveAsync([Change.Assert(user), Change.Update(org, updated), Change.Insert(member),
            this.Event(updated, "organizationMembership.created", user.Id, named),
            .. await this.ClearDismissalAsync(project, org.Value.Id, user.Id, cancellationToken).ConfigureAwait(false)], cancellationToken).ConfigureAwait(false)
            ? new OrganizationMember(member)
            : Refused();
    }

    public async Task<OrganizationMemberChanged> ChangeMemberAsync(string project, string id, string userId, string? role, bool remove, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return NoSuchOrganization();
        }

        var member = await this.MemberAsync(project, org.Value.Id, userId, cancellationToken).ConfigureAwait(false);
        if (member is null)
        {
            return NoSuchOrganization();
        }

        string? nextRole = null;
        if (!remove)
        {
            if (Role(role) is not { } named)
            {
                return new Validation("role must be admin or member.");
            }

            nextRole = named;
        }

        var admins = org.Value.Admins - (member.Value.Role == "admin" ? 1 : 0) + (nextRole == "admin" ? 1 : 0);
        if (admins == 0)
        {
            return new Conflict("An organization must keep at least one admin.", "last_admin");
        }

        var updated = member.Value with { Role = nextRole ?? member.Value.Role };
        return await data.SaveAsync([Change.Update(org, org.Value with { Admins = admins, Members = org.Value.Members - (remove ? 1 : 0) }),
            remove ? Change.Delete(member) : Change.Update(member, updated),
            this.Event(org.Value, remove ? "organizationMembership.deleted" : "organizationMembership.updated", userId, nextRole),
            .. remove ? await this.ClearDismissalAsync(project, org.Value.Id, userId, cancellationToken).ConfigureAwait(false) : []], cancellationToken).ConfigureAwait(false)
            ? remove ? new Done() : updated
            : Refused();
    }

    private async Task<IReadOnlyList<Change>> ClearDismissalAsync(string project, string org, string userId, CancellationToken cancellationToken)
    {
        var dismissal = await data.ReadAsync((db, token) => db.ProjectOrganizationDismissals
            .Where(row => row.ProjectId == project && row.OrganizationId == org && row.UserId == userId)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        return dismissal is null ? [] : [Change.Delete(dismissal)];
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
                                             select new OrganizationClaims(org.Id, org.Slug, member.Role)).ToListAsync(token), cancellationToken).ConfigureAwait(false);

    internal static Conflict? RefusesMembership(ProjectOrganizationRecord org, string role)
    {
        if (org.Admins == 0 && role != "admin")
        {
            return new Conflict("The first member of an empty organization must be an admin.", "organization_requires_admin");
        }

        return org.MaxMembers is { } maximum && org.Members >= maximum
            ? new Conflict("The organization's member limit was reached.", "organization_full")
            : null;
    }

    internal static ProjectOrganizationRecord AddMember(ProjectOrganizationRecord org, string role) =>
        org with { Members = checked(org.Members + 1), Admins = org.Admins + (role == "admin" ? 1 : 0) };

    internal static string? Role(string? role) => role is "admin" or "member" ? role : null;

    internal static Conflict Refused() => new(Changed, "version_conflict");

    internal static Validation InvalidRequest(string message) => new(message);

    private static Valid<ProjectOrganizationRecord> Validate(ProjectOrganizationRecord org, ProjectOrganizationRequest request)
    {
        var name = PlatformDirectory.Name(request.Name);
        if (name is null)
        {
            return new Validation("name must be 1 to 128 characters.");
        }

        var slug = request.Slug;
        if (slug is not { Length: >= 3 and <= 63 } || !char.IsAsciiLetterOrDigit(slug[0]) || !char.IsAsciiLetterOrDigit(slug[^1])
            || slug.Any(c => !char.IsAsciiDigit(c) && !char.IsAsciiLetterLower(c) && c != '-'))
        {
            return new Validation("slug must be 3 to 63 lowercase letters, digits or hyphens, beginning and ending with a letter or digit.");
        }

        if (request.ImageUrl is { } image && (!Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.Scheme != "https" || image.Length > 2048 || uri.UserInfo.Length > 0))
        {
            return new Validation("image_url must be an HTTPS URL of at most 2048 characters.");
        }

        if (request.MaxMembers is { } maximum && maximum < org.Members || request.DomainJoin is not ("off" or "suggest" or "auto"))
        {
            return new Validation("max_members must accommodate existing members; domain_join must be off, suggest or auto.");
        }

        var publicMetadata = Metadata(request.PublicMetadata);
        if (publicMetadata is null)
        {
            return new Validation("Each metadata field must be a JSON object of at most 8192 UTF-8 bytes.");
        }

        var privateMetadata = Metadata(request.PrivateMetadata);
        if (privateMetadata is null)
        {
            return new Validation("Each metadata field must be a JSON object of at most 8192 UTF-8 bytes.");
        }

        return org with
        {
            Name = name,
            Slug = slug,
            ImageUrl = request.ImageUrl,
            PublicMetadata = publicMetadata.Value,
            PrivateMetadata = privateMetadata.Value,
            MaxMembers = request.MaxMembers,
            DomainJoin = request.DomainJoin
        };
    }

    private static JsonElement EmptyMetadata()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement? Metadata(JsonElement? value)
    {
        if (value is null)
        {
            return EmptyMetadata();
        }

        return value.Value.ValueKind == JsonValueKind.Object && Encoding.UTF8.GetByteCount(value.Value.GetRawText()) <= 8192 ? value.Value.Clone() : null;
    }

    internal Change Event(ProjectOrganizationRecord org, string type, string? user = null, string? role = null) => Change.Insert(outbox.NewEvent(org.ProjectId, type, user ?? org.Id, organizationId: org.Id, organizationRole: role));
}
