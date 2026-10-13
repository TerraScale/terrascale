using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

// Starter allows five members including pending invitations. Each journey starts with the
// owner and three pending invitations, so exactly one more invitation fits. Requests pause
// inside ts-auth reads to replay the races from review round 2 of #13 deterministically.
public sealed class InvitationCapacityJourneyTests
{
    [Test]
    public async Task ConcurrentInvitationsOnFourReplicasFillTheLastSlotOnce()
    {
        await using var scenario = await CapacityScenario.StartAsync(replicas: 4);
        var invites = Enumerable.Range(0, 8).Select(index => scenario.InviteAsync(index % 4, "stampede-" + index + "@example.test")).ToArray();
        var responses = await Task.WhenAll(invites);
        try { await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.OK)).IsEqualTo(1); }
        finally { foreach (var response in responses) response.Dispose(); }
        await Assert.That(await scenario.PendingAsync()).IsEqualTo(4);
    }

    [Test]
    public async Task ALostSuccessfulResponseKeepsItsSlotForAnOlderSnapshot()
    {
        await using var scenario = await CapacityScenario.StartAsync();
        scenario.Platform.Arm(1);
        var stale = scenario.InviteAsync(1, "older-snapshot@example.test");
        await scenario.Platform.Arrived[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        scenario.Platform.LostResponseEmail = "committed-response-lost@example.test";
        using (var lost = await scenario.InviteAsync(0, scenario.Platform.LostResponseEmail))
            await Assert.That(lost.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        scenario.Platform.Release[0].TrySetResult();
        using (var result = await stale) await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await scenario.PendingAsync()).IsEqualTo(4);
    }

    [Test]
    public async Task ADuplicateEmailLoserNeverFreesTheWinnersSlot()
    {
        await using var scenario = await CapacityScenario.StartAsync(replicas: 3);
        scenario.Platform.Arm(3);
        var stale = scenario.InviteAsync(2, "older-snapshot@example.test");
        await scenario.Platform.Arrived[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        var first = scenario.InviteAsync(0, "same@example.test");
        await scenario.Platform.Arrived[1].Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = scenario.InviteAsync(1, "same@example.test");
        await scenario.Platform.Arrived[2].Task.WaitAsync(TimeSpan.FromSeconds(10));
        scenario.Platform.Release[1].TrySetResult();
        scenario.Platform.Release[2].TrySetResult();
        var pair = await Task.WhenAll(first, second);
        try { await Assert.That(pair.Count(response => response.StatusCode == HttpStatusCode.OK)).IsEqualTo(1); }
        finally { foreach (var response in pair) response.Dispose(); }
        scenario.Platform.Release[0].TrySetResult();
        using (var result = await stale) await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await scenario.PendingAsync()).IsEqualTo(4);
    }

    [Test]
    public async Task AnOldReservationOrSnapshotNeverAdmitsAnotherInvitation()
    {
        await using var scenario = await CapacityScenario.StartAsync();
        scenario.Platform.Arm(1);
        var stale = scenario.InviteAsync(1, "older-snapshot@example.test");
        await scenario.Platform.Arrived[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var committed = await scenario.InviteAsync(0, "committed-before-expiry@example.test"))
            await Assert.That(committed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        scenario.Clock.Advance(TimeSpan.FromMinutes(20));
        scenario.Platform.Release[0].TrySetResult();
        using (var result = await stale) await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await scenario.PendingAsync()).IsEqualTo(4);
    }

    [Test]
    public async Task AnInvitationAcceptedDuringTheDecisionCountsOnce()
    {
        await using var scenario = await CapacityScenario.StartAsync();
        var invitee = scenario.Pending[0];
        scenario.Platform.Arm(1);
        var admitted = scenario.InviteAsync(1, "admitted@example.test");
        await scenario.Platform.Arrived[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The invitee becomes a member between the dashboard's invitation and member reads.
        await scenario.Platform.AcceptAsync(invitee);
        scenario.Platform.Release[0].TrySetResult();
        using (var result = await admitted) await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("the accepted invitee is one slot, not two");
        using (var overflow = await scenario.InviteAsync(0, "overflow@example.test"))
            await Assert.That(overflow.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because("the accepted invitee is one slot, not zero");
    }

    private sealed class CapacityScenario : IAsyncDisposable
    {
        private readonly DemoEnvironment environment = new();
        private readonly List<WebApplication> hosts = [];
        private readonly List<(HttpClient Client, string Csrf)> clients = [];
        private SessionRecord owner = null!;
        private string organizationId = "";
        public ControlledPlatform Platform { get; private set; } = null!;
        public JourneyClock Clock { get; } = new();
        public IReadOnlyList<string> Pending { get; private set; } = [];

        public static async Task<CapacityScenario> StartAsync(int replicas = 2)
        {
            var scenario = new CapacityScenario();
            try
            {
                await scenario.environment.InitializeAsync();
                IDashboardState? state = null;
                for (var index = 0; index < replicas; index++)
                {
                    var host = scenario.environment.CreateHost(Root, 0, index == 0 ? "http://127.0.0.1" : scenario.hosts[0].Urls.Single(), builder =>
                    {
                        var inner = (ITsAuthPlatform)builder.Services.Last(service => service.ServiceType == typeof(ITsAuthPlatform)).ImplementationInstance!;
                        state ??= (IDashboardState)builder.Services.Last(service => service.ServiceType == typeof(IDashboardState)).ImplementationInstance!;
                        scenario.Platform ??= new(inner);
                        builder.Services.Replace(ServiceDescriptor.Singleton<ITsAuthPlatform>(scenario.Platform));
                        builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(scenario.Clock));
                    });
                    scenario.hosts.Add(host);
                    await host.StartAsync();
                }
                foreach (var host in scenario.hosts)
                {
                    var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
                    { BaseAddress = new(host.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)), Timeout = TimeSpan.FromSeconds(20) };
                    using (var login = await HttpJourney.LoginAsync(client)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                    scenario.clients.Add((client, await HttpJourney.CsrfAsync(client, "/organization?tab=invitations")));
                }
                using var me = JsonDocument.Parse(await scenario.clients[0].Client.GetStringAsync("/api/v1/auth/me"));
                scenario.organizationId = me.RootElement.GetProperty("organizationId").GetString()!;
                scenario.owner = new("test-user", "Test User", "user@example.com", "", null, DateTimeOffset.UtcNow.AddHours(1));
                // Starter's member allowance; the plan is dashboard billing state.
                var stored = (await state!.GetAsync(AccountService.OrganizationsCollection, scenario.organizationId, default))!;
                var settings = AccountService.Read<OrganizationRecord>(stored);
                settings.PlanId = OrganizationRecord.StarterPlanId;
                await state.PutAsync(AccountService.OrganizationsCollection, scenario.organizationId, JsonSerializer.SerializeToElement(settings), TerraBasePrecondition.Matches(stored.Version), default);
                var pending = new List<string>();
                for (var index = 0; index < 3; index++)
                    pending.Add((await scenario.Platform.CreateInvitationAsync(scenario.owner, scenario.organizationId, "existing-" + index + "@example.test", "member", default)).Token);
                scenario.Pending = pending;
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }

        public Task<HttpResponseMessage> InviteAsync(int replica, string email) => HttpJourney.PostFormAsync(clients[replica].Client,
            "/api/v1/access/invitations?tab=invitations", new() { ["_csrf"] = clients[replica].Csrf, ["email"] = email, ["role"] = "member" });

        public async Task<int> PendingAsync() =>
            (await Platform.ListInvitationsAsync(owner, organizationId, default)).Count(invitation => invitation.Status == "pending");

        public async ValueTask DisposeAsync()
        {
            if (Platform is not null) foreach (var gate in Platform.Release) gate.TrySetResult();
            foreach (var (client, _) in clients) client.Dispose();
            foreach (var host in hosts) { await host.StopAsync(); await host.DisposeAsync(); }
        }

        private static string Root { get; } = FindRoot();
        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "TerraScale.Dashboard.slnx"))) return directory.FullName;
            throw new DirectoryNotFoundException("The E2E executable must run from the dashboard checkout.");
        }
    }

    private sealed class JourneyClock : TimeProvider
    {
        private long offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offset));
        public void Advance(TimeSpan delta) => Interlocked.Add(ref offset, delta.Ticks);
    }

    // The in-memory ts-auth platform with gates after invitation-list reads and a response that
    // is lost after ts-auth committed the invitation.
    private sealed class ControlledPlatform(ITsAuthPlatform inner) : ITsAuthPlatform
    {
        private int reads;
        public TaskCompletionSource[] Arrived { get; private set; } = [];
        public TaskCompletionSource[] Release { get; private set; } = [];
        public string LostResponseEmail { get; set; } = "";

        public void Arm(int count)
        {
            Arrived = Enumerable.Range(0, count).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            Release = Enumerable.Range(0, count).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            Interlocked.Exchange(ref reads, 0);
        }

        public async Task AcceptAsync(string token)
        {
            var lookup = await inner.LookupInvitationAsync(Invitee, token, default) ?? throw new InvalidOperationException("Unknown fixture invitation.");
            await inner.AcceptInvitationAsync(Invitee with { Email = lookup.Email, UserId = new TerraScaleIdGenerator().NewId() }, token, default);
        }
        private static SessionRecord Invitee => new("invitee", "Invitee", "", "", null, DateTimeOffset.UtcNow.AddHours(1));

        public async Task<IReadOnlyList<TerraScaleAuthPlatformInvitation>> ListInvitationsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        {
            var result = await inner.ListInvitationsAsync(session, organizationId, cancellationToken);
            var index = Interlocked.Increment(ref reads) - 1;
            if (index < Arrived.Length) { Arrived[index].TrySetResult(); await Release[index].Task.WaitAsync(cancellationToken); }
            return result;
        }

        public async Task<TerraScaleAuthPlatformCreatedInvitation> CreateInvitationAsync(SessionRecord session, string organizationId, string email, string role, CancellationToken cancellationToken)
        {
            var created = await inner.CreateInvitationAsync(session, organizationId, email, role, cancellationToken);
            if (email == LostResponseEmail) throw new HttpRequestException("Synthetic response loss after ts-auth committed the invitation.");
            return created;
        }

        public Task<IReadOnlyList<TerraScaleAuthPlatformOrganization>> ListOrganizationsAsync(SessionRecord session, CancellationToken cancellationToken) => inner.ListOrganizationsAsync(session, cancellationToken);
        public Task<TerraScaleAuthPlatformOrganization> CreateOrganizationAsync(SessionRecord session, string name, CancellationToken cancellationToken) => inner.CreateOrganizationAsync(session, name, cancellationToken);
        public Task RenameOrganizationAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken) => inner.RenameOrganizationAsync(session, organizationId, name, cancellationToken);
        public Task LeaveOrganizationAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.LeaveOrganizationAsync(session, organizationId, cancellationToken);
        public Task<IReadOnlyList<TerraScaleAuthPlatformMember>> ListMembersAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.ListMembersAsync(session, organizationId, cancellationToken);
        public Task ChangeMemberRoleAsync(SessionRecord session, string organizationId, string userId, string role, CancellationToken cancellationToken) => inner.ChangeMemberRoleAsync(session, organizationId, userId, role, cancellationToken);
        public Task RemoveMemberAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken) => inner.RemoveMemberAsync(session, organizationId, userId, cancellationToken);
        public Task<IReadOnlyList<TerraScaleAuthPlatformProject>> ListProjectsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.ListProjectsAsync(session, organizationId, cancellationToken);
        public Task<TerraScaleAuthPlatformProject> CreateProjectAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken) => inner.CreateProjectAsync(session, organizationId, name, cancellationToken);
        public Task RenameProjectAsync(SessionRecord session, string organizationId, string projectId, string name, CancellationToken cancellationToken) => inner.RenameProjectAsync(session, organizationId, projectId, name, cancellationToken);
        public Task DeleteProjectAsync(SessionRecord session, string organizationId, string projectId, CancellationToken cancellationToken) => inner.DeleteProjectAsync(session, organizationId, projectId, cancellationToken);
        public Task RevokeInvitationAsync(SessionRecord session, string organizationId, string invitationId, CancellationToken cancellationToken) => inner.RevokeInvitationAsync(session, organizationId, invitationId, cancellationToken);
        public Task<TerraScaleAuthPlatformInvitationLookup?> LookupInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken) => inner.LookupInvitationAsync(session, token, cancellationToken);
        public Task<TerraScaleAuthPlatformAcceptedInvitation> AcceptInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken) => inner.AcceptInvitationAsync(session, token, cancellationToken);
        public Task DeclineInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken) => inner.DeclineInvitationAsync(session, token, cancellationToken);
        public Task<TerraScaleAuthPlatformOwnershipTransfer?> GetOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.GetOwnershipTransferAsync(session, organizationId, cancellationToken);
        public Task StartOwnershipTransferAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken) => inner.StartOwnershipTransferAsync(session, organizationId, userId, cancellationToken);
        public Task ConfirmOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.ConfirmOwnershipTransferAsync(session, organizationId, cancellationToken);
        public Task CancelOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.CancelOwnershipTransferAsync(session, organizationId, cancellationToken);
        public Task<IReadOnlyList<TerraScaleAuthPlatformAuditEvent>> ListAuditEventsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.ListAuditEventsAsync(session, organizationId, cancellationToken);
    }
}
