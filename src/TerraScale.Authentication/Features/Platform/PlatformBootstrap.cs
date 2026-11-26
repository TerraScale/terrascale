using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Storage;
using TerraScale.Contracts.ControlPlane;

namespace TerraScale.Authentication.Features.Platform;

public sealed class PlatformBootstrap(AuthData data, Users users, PlatformDirectory directory, TimeProvider clock)
{
    public const string OwnerEmail = PlatformIdentity.OwnerEmail;

    public const string OrganizationId = PlatformIdentity.OrganizationId;

    public const string AuthProjectId = PlatformIdentity.AuthProjectId;

    public const string DashboardProjectId = PlatformIdentity.DashboardProjectId;

    public const string PlatformProjectId = PlatformIdentity.PlatformProjectId;

    private const int Attempts = 4;

    private static readonly IReadOnlyList<(string Id, string Name)> ServiceProjects =
    [
        (AuthProjectId, PlatformIdentity.AuthDatabaseName),
        (DashboardProjectId, PlatformIdentity.DashboardDatabaseName),
        (PlatformProjectId, PlatformIdentity.PlatformDatabaseName),
    ];

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        var owner = await this.EnsureOwnerAsync(cancellationToken);
        await this.EnsureOrganizationAsync(owner, cancellationToken);
        await this.EnsureTenantAsync(owner, cancellationToken);
    }

    private async Task<UserRecord> EnsureOwnerAsync(CancellationToken cancellationToken)
    {
        var instance = PlatformDirectory.Instance;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var owner = await users.FindByEmailAsync(instance, OwnerEmail, cancellationToken)
                ?? await users.CreateAsync(instance, OwnerEmail, cancellationToken)
                ?? await users.FindByEmailAsync(instance, OwnerEmail, cancellationToken)
                ?? throw new InvalidOperationException("Platform bootstrap owner could not be read after creation.");
            if (!string.IsNullOrWhiteSpace(owner.FirstName) && !string.IsNullOrWhiteSpace(owner.LastName))
            {
                return owner;
            }

            var named = owner with
            {
                FirstName = string.IsNullOrWhiteSpace(owner.FirstName) ? PlatformIdentity.OwnerFirstName : owner.FirstName,
                LastName = string.IsNullOrWhiteSpace(owner.LastName) ? PlatformIdentity.OwnerLastName : owner.LastName,
            };
            if (await data.SaveAsync(Change.Update(named), cancellationToken))
            {
                return named;
            }
        }

        throw new InvalidOperationException("Platform bootstrap could not commit the owner's name.");
    }

    private async Task EnsureOrganizationAsync(UserRecord owner, CancellationToken cancellationToken)
    {
        var instance = PlatformDirectory.Instance;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var existing = await directory.OrganizationAsync(OrganizationId, cancellationToken);
            if (existing is not null && existing.Value.CreatedBy != owner.Id)
            {
                throw IdConflict();
            }

            var now = clock.GetUtcNow();
            List<Change> operations = [];
            var createdProjects = 0;
            foreach (var (id, name) in ServiceProjects)
            {
                var project = await directory.ProjectAsync(id, cancellationToken);
                if (project is null)
                {
                    operations.Add(Change.Insert(new ProjectRecord(id, instance, OrganizationId, name, owner.Id, now)));
                    createdProjects++;
                }
                else if (project.Value.OrganizationId != OrganizationId || project.Value.CreatedBy != owner.Id)
                {
                    throw IdConflict();
                }

                var grant = await directory.ProjectMemberAsync(id, owner.Id, cancellationToken);
                if (grant is null)
                {
                    operations.Add(Change.Insert(new ProjectMemberRecord(instance, OrganizationId, id, owner.Id, Roles.Admin, now)));
                }
                else if (grant.Value.Role != Roles.Admin)
                {
                    operations.Add(Change.Update(grant, grant.Value with { Role = Roles.Admin }));
                }
            }

            var membership = await directory.MembershipAsync(OrganizationId, owner.Id, cancellationToken);
            var addedOwner = 0;
            if (membership is null)
            {
                operations.Add(Change.Insert(new MembershipRecord(instance, OrganizationId, owner.Id, Roles.Owner, now)));
                addedOwner = 1;
            }
            else if (membership.Value.Role != Roles.Owner)
            {
                operations.Add(Change.Update(membership, membership.Value with { Role = Roles.Owner }));
                addedOwner = 1;
            }

            if (existing is not null && operations.Count == 0)
            {
                return;
            }

            operations.Add(existing is null
                ? Change.Insert(new OrganizationRecord(OrganizationId, instance, PlatformIdentity.OrganizationName, owner.Id, now, Owners: 1, Projects: createdProjects))
                : Change.Update(existing, existing.Value with
                {
                    Owners = existing.Value.Owners + addedOwner,
                    Projects = existing.Value.Projects + createdProjects,
                }));
            operations.Add(directory.Audit(OrganizationId, owner.Id, "platform.bootstrapped"));
            if (await data.SaveAsync(operations, cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException("Platform bootstrap could not commit its identity records.");
    }

    private static InvalidOperationException IdConflict() =>
        new("Platform bootstrap IDs conflict with existing identity records.");

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
