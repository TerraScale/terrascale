using System.Text.Json;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Seeds TerraScale identity in ts-auth's own database, independently of TerraBase startup.</summary>
public sealed class PlatformBootstrap(AuthStore store, Users users, PlatformDirectory directory, TimeProvider clock, string ownerEmail = PlatformBootstrap.OwnerEmail)
{
    /// <summary>The initial owner; email-code sign-in must still prove control of the mailbox.</summary>
    public const string OwnerEmail = "mariogk@terrascale.tech";

    /// <summary>The organization ID agreed with TerraBase's platform seed.</summary>
    public const string OrganizationId = "00000000000000000000000001";

    /// <summary>The project owning only the pre-created ts-auth database.</summary>
    public const string AuthProjectId = App.StorageProject;

    /// <summary>The project owning only the pre-created ts-dashboard database.</summary>
    public const string DashboardProjectId = "00000000000000000000000005";

    /// <summary>
    /// Creates the passwordless owner, then the organization, both projects, grants, name claims and audit in one
    /// create-only transaction. Concurrent starts read the winner; restarts never reset accounts or restore revoked roles.
    /// A conflicting fixed ID or incomplete topology fails closed without overwriting it.
    /// </summary>
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
            var operations = new List<StoreOperation>
            {
                AuthStore.Create(AuthStore.OrganizationCollection, instance, OrganizationId, JsonSerializer.SerializeToElement(organization, PlatformJson.Default.OrganizationRecord)),
                AuthStore.Create(AuthStore.MembershipCollection, instance, $"{OrganizationId}/{owner.Id}", JsonSerializer.SerializeToElement(member, PlatformJson.Default.MembershipRecord)),
            };
            foreach (var (id, name) in new[] { (AuthProjectId, App.StorageDatabase), (DashboardProjectId, "ts-dashboard") })
            {
                var project = new ProjectRecord(id, instance, OrganizationId, name, owner.Id, now);
                var grant = new ProjectMemberRecord(instance, OrganizationId, id, owner.Id, Roles.Admin, now);
                operations.Add(AuthStore.Claim(instance, "project-name", $"{OrganizationId}/{name.ToUpperInvariant()}", id));
                operations.Add(AuthStore.Create(AuthStore.ProjectCollection, instance, id, JsonSerializer.SerializeToElement(project, PlatformJson.Default.ProjectRecord)));
                operations.Add(AuthStore.Create(AuthStore.ProjectMemberCollection, instance, $"{id}/{owner.Id}", JsonSerializer.SerializeToElement(grant, PlatformJson.Default.ProjectMemberRecord)));
            }

            operations.Add(directory.Audit(OrganizationId, owner.Id, "platform.bootstrapped"));
            if (await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null)
            {
                return;
            }
        }

        throw new InvalidOperationException("Platform bootstrap could not commit its identity records.");
    }
}
