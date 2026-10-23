namespace TerraScale.Dashboard.Features.Accounts;

public sealed record AccountContextView(AccountContextUser User, AccountContextSelection Selection, IReadOnlyList<AccountContextOrganization> Organizations)
{
    public static AccountContextView From(AccountContext context) => new(new(context.UserId, context.Account.Name, context.Account.Email),
        new(context.Organization?.Id, context.Project?.Id), context.Organizations.Select(org => new AccountContextOrganization(org.Id, org.Name,
            org.Kind, org.Slug, org.Status, org.OwnerUserId, org.PlanId, org.BillingStatus, org.PaymentMethodValidated,
            org.Archived, org.DeletionRequested, org.PendingOwnerUserId, org.PendingOwnerExpiresAt, Date(org.CreatedAt),
            org.Members.ToDictionary(item => item.Key, item => new AccountContextMember(item.Value.UserId, item.Value.Name, item.Value.Email,
                item.Value.Role, item.Value.Status, Date(item.Value.CreatedAt), item.Value.JoinedOn)),
            org.Invitations.ToDictionary(item => item.Key, item => new AccountContextInvitation(item.Value.Id, item.Value.Email, item.Value.Role,
                item.Value.Status, Date(item.Value.CreatedAt), Date(item.Value.ExpiresAt))),
            org.Projects.ToDictionary(item => item.Key, item => Project(item.Value)))).ToArray());

    private static AccountContextProject Project(ProjectRecord project) => new(project.Id, project.OrganizationId, project.Name, project.Slug,
        project.Region, project.BackupRegion, project.Regions.ToArray(), project.Status, project.IsDefault,
        Limits(project.OperationLimits), Date(project.CreatedAt), project.Databases.ToDictionary(item => item.Key, item =>
            new AccountContextDatabase(item.Value.Id, item.Value.ProjectId, item.Value.Name, item.Value.Region, item.Value.Regions.ToArray(),
                item.Value.Status, Limits(item.Value.OperationLimits), Date(item.Value.CreatedAt))));
    private static AccountContextLimits Limits(OperationLimits limits) => new(limits.ReadLimit, limits.WriteLimit, limits.ReplicationLimit);
    private static DateTimeOffset? Date(DateTimeOffset value) => value == DateTimeOffset.MinValue ? null : value;
}

public sealed record AccountContextUser(string Id, string Name, string Email);
public sealed record AccountContextSelection(string? OrganizationId, string? ProjectId);
public sealed record AccountContextOrganization(string Id, string Name, string Kind, string Slug, string Status, string OwnerUserId,
    string PlanId, string BillingStatus, bool PaymentMethodValidated, bool Archived,
    bool DeletionRequested, string PendingOwnerUserId, DateTimeOffset? PendingOwnerExpiresAt, DateTimeOffset? CreatedAt,
    IReadOnlyDictionary<string, AccountContextMember> Members, IReadOnlyDictionary<string, AccountContextInvitation> Invitations,
    IReadOnlyDictionary<string, AccountContextProject> Projects);
public sealed record AccountContextMember(string UserId, string Name, string Email, string Role, string Status, DateTimeOffset? CreatedAt, DateOnly? JoinedOn);
public sealed record AccountContextInvitation(string Id, string Email, string Role, string Status, DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record AccountContextProject(string Id, string OrganizationId, string Name, string Slug, string Region, string BackupRegion,
    IReadOnlyList<string> Regions, string Status, bool IsDefault, AccountContextLimits OperationLimits,
    DateTimeOffset? CreatedAt, IReadOnlyDictionary<string, AccountContextDatabase> Databases);
public sealed record AccountContextDatabase(string Id, string ProjectId, string Name, string Region, IReadOnlyList<string> Regions,
    string Status, AccountContextLimits OperationLimits, DateTimeOffset? CreatedAt);
public sealed record AccountContextLimits(long ReadLimit, long WriteLimit, long ReplicationLimit);
