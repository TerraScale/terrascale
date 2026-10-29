using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Management;

/// <summary>Real SDK persistence, concurrent bootstrap and email-code access to the seeded organization.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PlatformBootstrapTests(TerraBaseFixture fixture)
{
    private static readonly string[] ProjectIds = [PlatformBootstrap.AuthProjectId, PlatformBootstrap.DashboardProjectId];
    private static readonly string[] ProjectNames = ["ts-auth", "ts-dashboard"];

    [Test]
    public async Task Concurrent_bootstraps_seed_one_owner_and_exactly_two_projects()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var store = new AuthStore(target.Database, TimeProvider.System);
        var users = new Users(store, TimeProvider.System);
        var directory = new PlatformDirectory(store, users, Credentials(store, users), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformDirectory>.Instance);
        var first = new PlatformBootstrap(store, users, directory, TimeProvider.System, AuthTestServer.BootstrapOwnerEmail);
        var second = new PlatformBootstrap(store, users, directory, TimeProvider.System, AuthTestServer.BootstrapOwnerEmail);
        await Task.WhenAll(first.EnsureAsync(CancellationToken.None), second.EnsureAsync(CancellationToken.None));
        var before = await SnapshotAsync(target);
        await first.EnsureAsync(CancellationToken.None);
        await Assert.That(await SnapshotAsync(target)).IsEqualTo(before);
        await Assert.That(await target.CountAsync(AuthStore.UserCollection)).IsEqualTo(1);
        await Assert.That(await target.CountAsync(AuthStore.OrganizationCollection)).IsEqualTo(1);
        await Assert.That(await target.CountAsync(AuthStore.ProjectCollection)).IsEqualTo(2);
        await Assert.That(await target.CountAsync(AuthStore.MembershipCollection)).IsEqualTo(1);
        await Assert.That(await target.CountAsync(AuthStore.ProjectMemberCollection)).IsEqualTo(2);
        var owner = await users.FindByEmailAsync(PlatformDirectory.Instance, AuthTestServer.BootstrapOwnerEmail);
        await Assert.That(owner!.PasswordHash).IsNull();
        await Assert.That((await directory.OrganizationAccessAsync(owner.Id, PlatformBootstrap.OrganizationId, CancellationToken.None)).Role).IsEqualTo(Roles.Owner);
    }

    [Test]
    public async Task Restart_bootstrap_does_not_restore_a_revoked_owner_membership()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var services = oidc.Server.App.Services;
        var owner = await services.GetRequiredService<Users>().FindByEmailAsync(PlatformDirectory.Instance, AuthTestServer.BootstrapOwnerEmail);
        var store = services.GetRequiredService<AuthStore>();
        var key = $"{PlatformBootstrap.OrganizationId}/{owner!.Id}";
        var member = await store.GetAsync(AuthStore.MembershipCollection, PlatformDirectory.Instance, key);
        await Assert.That(await store.CompareAndDeleteAsync(AuthStore.MembershipCollection, PlatformDirectory.Instance, key, member!.Version)).IsTrue();
        await services.GetRequiredService<PlatformBootstrap>().EnsureAsync(CancellationToken.None);
        await Assert.That(await store.GetAsync(AuthStore.MembershipCollection, PlatformDirectory.Instance, key)).IsNull();
    }

    [Test]
    public async Task A_conflicting_project_id_aborts_the_whole_topology_without_overwriting()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var store = new AuthStore(target.Database, TimeProvider.System);
        var users = new Users(store, TimeProvider.System);
        var directory = new PlatformDirectory(store, users, Credentials(store, users), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformDirectory>.Instance);
        var conflict = TerraBaseFixture.Json("{\"instanceId\":\"terrascale\",\"name\":\"another-project\"}");
        await target.PutAsync(AuthStore.ProjectCollection, PlatformDirectory.Instance, PlatformBootstrap.DashboardProjectId, conflict);
        var before = await target.ReadAsync(AuthStore.ProjectCollection, PlatformDirectory.Instance, PlatformBootstrap.DashboardProjectId);
        var bootstrap = new PlatformBootstrap(store, users, directory, TimeProvider.System, AuthTestServer.BootstrapOwnerEmail);
        await Assert.That(() => bootstrap.EnsureAsync(CancellationToken.None)).Throws<InvalidOperationException>();
        await Assert.That(await target.CountAsync(AuthStore.OrganizationCollection)).IsEqualTo(0);
        await Assert.That(await target.CountAsync(AuthStore.ProjectCollection)).IsEqualTo(1);
        var preserved = await target.ReadAsync(AuthStore.ProjectCollection, PlatformDirectory.Instance, PlatformBootstrap.DashboardProjectId);
        await Assert.That(preserved!.Value.GetRawText()).IsEqualTo(before!.Value.GetRawText());
    }

    private static async Task<string> SnapshotAsync(SharedStateTarget target)
    {
        var records = new List<string>();
        foreach (var collection in new[] { AuthStore.UserCollection, AuthStore.OrganizationCollection, AuthStore.ProjectCollection, AuthStore.MembershipCollection, AuthStore.ProjectMemberCollection, AuthStore.AuditEventCollection, AuthStore.UniqueCollection })
        {
            records.AddRange((await target.Database.ListDocumentsAsync(collection)).Items.Select(document => document.Document.GetRawText()));
        }

        return string.Join('\n', records.Order(StringComparer.Ordinal));
    }

    private static TotpCredentials Credentials(AuthStore store, Users users)
    {
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        return new TotpCredentials(store, users, new AttemptLimiter(new TerraBaseDocumentStore(store), TimeProvider.System, options), options, TimeProvider.System);
    }
}
