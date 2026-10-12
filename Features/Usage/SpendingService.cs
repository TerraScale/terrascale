using System.Globalization;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Usage;

public sealed record OperationLimitChange(long? ReadLimit = null, long? WriteLimit = null, long? ReplicationLimit = null)
{
    public bool Changed => ReadLimit is not null || WriteLimit is not null || ReplicationLimit is not null;
    public void Apply(OperationLimits limits)
    {
        if (ReadLimit is { } reads) limits.ReadLimit = reads;
        if (WriteLimit is { } writes) limits.WriteLimit = writes;
        if (ReplicationLimit is { } replication) limits.ReplicationLimit = replication;
    }
}
public sealed record SpendingChange(long? OrganizationCapCents = null, bool? PaidExcess = null,
    IReadOnlyDictionary<string, long>? ProjectCaps = null, IReadOnlyDictionary<string, long>? DatabaseCaps = null,
    IReadOnlyDictionary<string, OperationLimitChange>? ProjectOperations = null, IReadOnlyDictionary<string, OperationLimitChange>? DatabaseOperations = null)
{
    public bool Changed => OrganizationCapCents is not null || PaidExcess is not null || ProjectCaps?.Count > 0 || DatabaseCaps?.Count > 0 ||
        ProjectOperations?.Values.Any(value => value.Changed) == true || DatabaseOperations?.Values.Any(value => value.Changed) == true;
}
public sealed record SpendingDatabase(string Id, string Name, string Region, long CapCents, long EffectiveCapCents, OperationLimits Operations);
public sealed record SpendingProject(string Id, string Name, string Region, long CapCents, long EffectiveCapCents, OperationLimits Operations, IReadOnlyList<SpendingDatabase> Databases);
public sealed record SpendingSnapshot(string OrganizationId, long OrganizationCapCents, bool PaidExcess, IReadOnlyList<SpendingProject> Projects);

public sealed class SpendingService(AccountService accounts, TimeProvider time, IDashboardState state)
{
    public async Task<SpendingSnapshot> ReadAsync(AccountContext context, CancellationToken cancellationToken) => Snapshot((await FreshAsync(context, cancellationToken)).Organization!);

    public async Task SetAsync(AccountContext context, SpendingChange change, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        if (!context.CanManage) throw new AccountAccessException("Organization management is required.");
        Validate(change);
        if (!change.Changed) return;
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            if (!org.CanManage(context.UserId)) throw new AccountAccessException("Organization management is required.");
            if (change.OrganizationCapCents is { } cap) org.SpendCapCents = cap;
            // Internal billing is always report-only. A submitted ordinary
            // excess checkbox cannot turn this estimate into enforcement.
            if (change.PaidExcess is { } paid && org.InternalBilling is null) org.PaidExcess = paid;
            foreach (var item in change.ProjectCaps ?? new Dictionary<string, long>()) Project(org, item.Key).SpendCapCents = item.Value;
            foreach (var item in change.DatabaseCaps ?? new Dictionary<string, long>()) Database(org, item.Key).SpendCapCents = item.Value;
            foreach (var item in change.ProjectOperations ?? new Dictionary<string, OperationLimitChange>()) item.Value.Apply(Project(org, item.Key).OperationLimits);
            foreach (var item in change.DatabaseOperations ?? new Dictionary<string, OperationLimitChange>()) item.Value.Apply(Database(org, item.Key).OperationLimits);
            org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "usage.spending_controls_updated", org.Name, "usage control", time.GetUtcNow()));
        }, cancellationToken);
    }

    private async Task<AccountContext> FreshAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var fresh = await AccountScope.RefreshAsync(accounts, context, cancellationToken);
        await InternalBillingService.ValidateClaimsAsync(state, fresh.Organization!, cancellationToken);
        return fresh;
    }
    public static SpendingSnapshot Snapshot(OrganizationRecord org)
    {
        if (org.SpendCapCents < 0) throw new IOException("Stored spending cap is invalid.");
        org.InternalBilling?.Validate(org);
        var projects = org.Projects.Values.OrderBy(project => project.Name, StringComparer.Ordinal).Select(project =>
        {
            if (project.OrganizationId != org.Id) throw new IOException("Stored project scope is invalid.");
            return new SpendingProject(project.Id, project.Name, project.Region, CheckedCap(project.SpendCapCents), EffectiveCap(org.SpendCapCents, project.SpendCapCents), Copy(project.OperationLimits),
                project.Databases.Values.OrderBy(database => database.Name, StringComparer.Ordinal).Select(database =>
                {
                    if (database.ProjectId != project.Id) throw new IOException("Stored database scope is invalid.");
                    return new SpendingDatabase(database.Id, database.Name, database.Region, CheckedCap(database.SpendCapCents), EffectiveCap(org.SpendCapCents, project.SpendCapCents, database.SpendCapCents), Copy(database.OperationLimits));
                }).ToArray());
        }).ToArray();
        return new(org.Id, org.SpendCapCents, org.PaidExcess, projects);
    }
    public static long EffectiveCap(params long[] caps) => caps.Where(cap => cap > 0).DefaultIfEmpty().Min();
    public static string Dollars(long cents) => cents <= 0 ? "" : (cents / 100m).ToString("F2", CultureInfo.InvariantCulture);
    public static string CapLabel(long cents) => cents <= 0 ? "No limit" : "$" + Dollars(cents);
    private static long CheckedCap(long value) => value >= 0 ? value : throw new IOException("Stored spending cap is invalid.");
    private static OperationLimits Copy(OperationLimits? limits) => limits is not null && limits.ReadLimit >= 0 && limits.WriteLimit >= 0 && limits.ReplicationLimit >= 0
        ? new() { ReadLimit = limits.ReadLimit, WriteLimit = limits.WriteLimit, ReplicationLimit = limits.ReplicationLimit } : throw new IOException("Stored operation limits are invalid.");
    private static ProjectRecord Project(OrganizationRecord org, string id) => org.Projects.TryGetValue(id, out var project) && project.OrganizationId == org.Id ? project : throw new AccountAccessException("Project is unavailable.", 404);
    private static DatabaseRecord Database(OrganizationRecord org, string id)
    {
        foreach (var project in org.Projects.Values)
            if (project.OrganizationId == org.Id && project.Databases.TryGetValue(id, out var database) && database.ProjectId == project.Id) return database;
        throw new AccountAccessException("Database is unavailable.", 404);
    }
    private static void Validate(SpendingChange change)
    {
        if (change.OrganizationCapCents is < 0 || change.ProjectCaps?.Any(pair => pair.Key.Length == 0 || pair.Value < 0) == true || change.DatabaseCaps?.Any(pair => pair.Key.Length == 0 || pair.Value < 0) == true)
            throw new AccountAccessException("Spend caps must be zero or positive US cents.", 400);
        foreach (var pair in (change.ProjectOperations ?? new Dictionary<string, OperationLimitChange>()).Concat(change.DatabaseOperations ?? new Dictionary<string, OperationLimitChange>()))
            if (pair.Key.Length == 0 || pair.Value.ReadLimit is < 0 || pair.Value.WriteLimit is < 0 || pair.Value.ReplicationLimit is < 0)
                throw new AccountAccessException("Operation limits must be zero or positive whole numbers.", 400);
    }
}
