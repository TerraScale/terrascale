namespace TerraScale.Dashboard.Features.Accounts;

public sealed record AccountContextView(AccountContextUser User, AccountContextSelection Selection, IReadOnlyList<AccountContextOrganization> Organizations)
{
    public static AccountContextView From(AccountContext context) => new(new(context.UserId, context.Account.Name, context.Account.Email),
        new(context.Organization?.Id, context.Project?.Id), [.. context.Organizations.Select(org => Organization(context, org))]);

    private static AccountContextOrganization Organization(AccountContext context, OrganizationRecord org)
    {
        var member = org.ContainsMember(context.UserId);
        // Null means outside this caller's organization scope, rather than a negative billing or membership fact.
        return new(org.Id, org.Name, member ? org.Kind : null, org.Slug, org.Status, member ? org.OwnerUserId : null,
            member ? org.PlanId : null, member ? org.BillingStatus : null, member ? org.PaymentMethodValidated : null,
            member ? org.Archived : null, member ? org.DeletionRequested : null, member ? org.PendingOwnerUserId : null,
            member ? org.PendingOwnerExpiresAt : null, Date(org.CreatedAt),
            member ? org.Members.ToDictionary(item => item.Key, item => new AccountContextMember(item.Value.UserId, item.Value.Name, item.Value.Email,
                item.Value.Role, item.Value.Status, Date(item.Value.CreatedAt), item.Value.JoinedOn)) : null,
            member ? org.Invitations.ToDictionary(item => item.Key, item => new AccountContextInvitation(item.Value.Id, item.Value.Email, item.Value.Role,
                item.Value.Status, Date(item.Value.CreatedAt), Date(item.Value.ExpiresAt))) : null,
            org.Projects.ToDictionary(item => item.Key, item => Project(item.Value, member)), member ? org.RoleFor(context.UserId) : null);
    }

    private static AccountContextProject Project(ProjectRecord project, bool member) => new(project.Id, project.OrganizationId, project.Name, project.Slug,
        project.Region, project.BackupRegion, [.. project.Regions], project.Status, project.IsDefault,
        member ? Limits(project.OperationLimits) : null, Date(project.CreatedAt), project.Databases.ToDictionary(item => item.Key, item =>
            new AccountContextDatabase(item.Value.Id, item.Value.ProjectId, item.Value.Name, item.Value.Region, [.. item.Value.Regions],
                item.Value.Status, member ? Limits(item.Value.OperationLimits) : null, Date(item.Value.CreatedAt))), project.Role);
    private static AccountContextLimits Limits(OperationLimits limits) => new(limits.ReadLimit, limits.WriteLimit, limits.ReplicationLimit);
    private static DateTimeOffset? Date(DateTimeOffset value) => value == DateTimeOffset.MinValue ? null : value;
}

public sealed record AccountContextUser(string Id, string Name, string Email);
public sealed record AccountContextSelection(string? OrganizationId, string? ProjectId);
public sealed record AccountContextOrganization(string Id, string Name, string? Kind, string Slug, string Status, string? OwnerUserId,
    string? PlanId, string? BillingStatus, bool? PaymentMethodValidated, bool? Archived,
    bool? DeletionRequested, string? PendingOwnerUserId, DateTimeOffset? PendingOwnerExpiresAt, DateTimeOffset? CreatedAt,
    IReadOnlyDictionary<string, AccountContextMember>? Members, IReadOnlyDictionary<string, AccountContextInvitation>? Invitations,
    IReadOnlyDictionary<string, AccountContextProject> Projects, string? Role);
public sealed record AccountContextMember(string UserId, string Name, string Email, string Role, string Status, DateTimeOffset? CreatedAt, DateOnly? JoinedOn);
public sealed record AccountContextInvitation(string Id, string Email, string Role, string Status, DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record AccountContextProject(string Id, string OrganizationId, string Name, string Slug, string Region, string BackupRegion,
    IReadOnlyList<string> Regions, string Status, bool IsDefault, AccountContextLimits? OperationLimits,
    DateTimeOffset? CreatedAt, IReadOnlyDictionary<string, AccountContextDatabase> Databases, string? Role);
public sealed record AccountContextDatabase(string Id, string ProjectId, string Name, string Region, IReadOnlyList<string> Regions,
    string Status, AccountContextLimits? OperationLimits, DateTimeOffset? CreatedAt);
public sealed record AccountContextLimits(long ReadLimit, long WriteLimit, long ReplicationLimit);
