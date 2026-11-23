using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Platform;

public sealed class PlatformBootstrap(AuthData data, Users users, PlatformDirectory directory, TimeProvider clock, string ownerEmail = PlatformBootstrap.OwnerEmail)
{
    public const string OwnerEmail = "mariogk@terrascale.tech";

    public const string OrganizationId = "00000000000000000000000001";

    public const string AuthProjectId = App.StorageProject;

    public const string DashboardProjectId = "00000000000000000000000005";

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        var instance = PlatformDirectory.Instance;
        var owner = await users.FindByEmailAsync(instance, ownerEmail, cancellationToken)
            ?? await users.CreateAsync(instance, ownerEmail, cancellationToken)
            ?? await users.FindByEmailAsync(instance, ownerEmail, cancellationToken)
            ?? throw new InvalidOperationException("Platform bootstrap owner could not be read after creation.");
        await this.EnsureOrganizationAsync(owner, cancellationToken);
        await this.EnsureTenantAsync(owner, cancellationToken);
    }

    private async Task EnsureOrganizationAsync(UserRecord owner, CancellationToken cancellationToken)
    {
        var instance = PlatformDirectory.Instance;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var existing = await directory.OrganizationAsync(OrganizationId, cancellationToken);
            if (existing is not null)
            {
                var auth = await directory.ProjectAsync(AuthProjectId, cancellationToken);
                var dashboard = await directory.ProjectAsync(DashboardProjectId, cancellationToken);
                if (existing.Value.CreatedBy != owner.Id
                    || auth?.Value.OrganizationId != OrganizationId || auth.Value.CreatedBy != owner.Id
                    || dashboard?.Value.OrganizationId != OrganizationId || dashboard.Value.CreatedBy != owner.Id)
                {
                    throw new InvalidOperationException("Platform bootstrap IDs conflict with existing identity records or projects are missing.");
                }

                return;
            }

            var now = clock.GetUtcNow();
            var organization = new OrganizationRecord(OrganizationId, instance, "TerraScale", owner.Id, now, Owners: 1, Projects: 2);
            var member = new MembershipRecord(instance, OrganizationId, owner.Id, Roles.Owner, now);
            var operations = new List<Change> { Change.Insert(organization), Change.Insert(member) };
            foreach (var (id, name) in new[] { (AuthProjectId, App.StorageDatabase), (DashboardProjectId, "ts-dashboard") })
            {
                var project = new ProjectRecord(id, instance, OrganizationId, name, owner.Id, now);
                var grant = new ProjectMemberRecord(instance, OrganizationId, id, owner.Id, Roles.Admin, now);
                operations.Add(Change.Insert(project));
                operations.Add(Change.Insert(grant));
            }

            operations.Add(directory.Audit(OrganizationId, owner.Id, "platform.bootstrapped"));
            if (await data.SaveAsync(operations, cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException("Platform bootstrap could not commit its identity records.");
    }

    private async Task EnsureTenantAsync(UserRecord owner, CancellationToken cancellationToken)
    {
        if (await this.TenantExistsAsync(cancellationToken))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var organization = new OrganizationRecord(PlatformTenant.Id, PlatformDirectory.Instance, PlatformTenant.Name, owner.Id, now, Owners: 0, Projects: 1);
        var project = new ProjectRecord(PlatformTenant.Id, PlatformDirectory.Instance, PlatformTenant.Id, PlatformTenant.Name, owner.Id, now);
        if (!await data.SaveAsync([Change.Insert(organization), Change.Insert(project)], cancellationToken)
            && !await this.TenantExistsAsync(cancellationToken))
        {
            throw new InvalidOperationException("Platform bootstrap could not commit the platform tenant row.");
        }
    }

    private async Task<bool> TenantExistsAsync(CancellationToken cancellationToken) =>
        await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<string>(
            "SELECT organization_id FROM projects WHERE id = @tenant", new { tenant = PlatformTenant.Id }), cancellationToken) switch
        {
            null => false,
            PlatformTenant.Id => true,
            _ => throw new InvalidOperationException("The platform tenant project id belongs to another organization."),
        };
}
