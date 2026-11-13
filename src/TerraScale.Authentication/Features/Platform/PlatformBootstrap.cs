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
        var owner = await users.FindByEmailAsync(instance, ownerEmail, cancellationToken).ConfigureAwait(false)
            ?? await users.CreateAsync(instance, ownerEmail, cancellationToken).ConfigureAwait(false)
            ?? await users.FindByEmailAsync(instance, ownerEmail, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Platform bootstrap owner could not be read after creation.");

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var existing = await directory.OrganizationAsync(OrganizationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var auth = await directory.ProjectAsync(AuthProjectId, cancellationToken).ConfigureAwait(false);
                var dashboard = await directory.ProjectAsync(DashboardProjectId, cancellationToken).ConfigureAwait(false);
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
            if (await data.SaveAsync(operations, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new InvalidOperationException("Platform bootstrap could not commit its identity records.");
    }
}
