using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Billing;

public sealed record SpendingDatabase(string Id, string Name, string Region);
public sealed record SpendingProject(string Id, string Name, string Region, IReadOnlyList<SpendingDatabase> Databases);
public sealed record SpendingSnapshot(string OrganizationId, long OrganizationCapCents, IReadOnlyList<SpendingProject> Projects);

public sealed class SpendingService(AccountService accounts, TimeProvider time, IDashboardState state)
{
    public async Task<SpendingSnapshot> ReadAsync(AccountContext context, CancellationToken cancellationToken) => Snapshot((await FreshAsync(context, cancellationToken)).Organization!);

    public async Task SetInternalCapAsync(AccountContext context, long capCents, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        if (context.Organization!.InternalBilling is null) throw new AccountAccessException("Internal billing is unavailable.", 404);
        if (!context.CanManage) throw new AccountAccessException("Organization management is required.");
        if (capCents < 0) throw new AccountAccessException("Report-only cap must be zero or positive US cents.", 400);
        if (context.Organization.SpendCapCents == capCents) return;
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            if (org.InternalBilling is null) throw new AccountAccessException("Internal billing is unavailable.", 404);
            if (!org.CanManage(context.UserId)) throw new AccountAccessException("Organization management is required.");
            org.SpendCapCents = capCents;
            org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "billing.internal_report_cap_updated", org.Name, "report-only cap", time.GetUtcNow()));
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
        if (org.InternalBilling is not null && org.SpendCapCents < 0) throw new IOException("Stored spending cap is invalid.");
        org.InternalBilling?.Validate(org);
        var projects = org.Projects.Values.OrderBy(project => project.Name, StringComparer.Ordinal).Select(project =>
        {
            if (project.OrganizationId != org.Id) throw new IOException("Stored project scope is invalid.");
            return new SpendingProject(project.Id, project.Name, project.Region,
                [.. project.Databases.Values.OrderBy(database => database.Name, StringComparer.Ordinal).Select(database =>
                {
                    if (database.ProjectId != project.Id) throw new IOException("Stored database scope is invalid.");
                    return new SpendingDatabase(database.Id, database.Name, database.Region);
                })]);
        }).ToArray();
        return new(org.Id, org.InternalBilling is null ? 0 : org.SpendCapCents, projects);
    }
}
