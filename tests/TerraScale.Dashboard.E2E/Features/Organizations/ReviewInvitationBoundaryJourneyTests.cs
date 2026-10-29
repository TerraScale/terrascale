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
public sealed class ReviewInvitationBoundaryJourneyTests
{
    [Test]
    public async Task AnAmbiguousInvitationCannotCommitAfterExpiryAndReplacement()
    {
        await using var scenario = await CapacityScenario.StartAsync();
        scenario.Platform.DeferredEmail = "late-original@example.test";
        using (var failed = await scenario.InviteAsync(0, scenario.Platform.DeferredEmail))
            await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        scenario.Clock.Advance(TimeSpan.FromMinutes(20));
        using var replacement = await scenario.InviteAsync(1, "replacement@example.test");
        scenario.Platform.CommitDeferred.TrySetResult();
        await scenario.Platform.DeferredCommitted.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = await scenario.PendingAsync();
        Console.WriteLine($"LATE-COMMIT replacement={(int)replacement.StatusCode}, pending={pending}, occupied={pending + 1}, limit=5");
        await Assert.That(replacement.StatusCode).IsEqualTo(HttpStatusCode.Conflict)
            .Because("elapsed time and an absent list entry do not terminate the original operation");
        await Assert.That(pending).IsEqualTo(4);
    }

    [Test]
    public async Task AReplicaClockCannotExpireAnotherReplicasAmbiguousOperation()
    {
        await using var scenario = await CapacityScenario.StartAsync(separateClocks: true);
        scenario.Platform.DeferredEmail = "skewed-original@example.test";
        using (var failed = await scenario.InviteAsync(0, scenario.Platform.DeferredEmail))
            await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        scenario.Clock.Advance(TimeSpan.FromMinutes(14));
        scenario.OtherClock.Advance(TimeSpan.FromMinutes(16));
        using var replacement = await scenario.InviteAsync(1, "skewed-replacement@example.test");
        scenario.Platform.CommitDeferred.TrySetResult();
        await scenario.Platform.DeferredCommitted.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = await scenario.PendingAsync();
        Console.WriteLine($"CLOCK-SKEW replacement={(int)replacement.StatusCode}, pending={pending}, occupied={pending + 1}, original-clock elapsed=14min, replica-skew=2min");
        await Assert.That(replacement.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(pending).IsEqualTo(4);
    }

    [Test]
    public async Task ADirectTsAuthMemberAfterTheReadCannotDoubleBookTheSlot()
    {
        await using var scenario = await CapacityScenario.StartAsync();
        scenario.Platform.Arm(1);
        var stale = scenario.InviteAsync(1, "dashboard-after-direct@example.test");
        await scenario.Platform.Arrived[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        scenario.Platform.PauseNextMembers = true;
        scenario.Platform.Release[0].TrySetResult();
        await scenario.Platform.MembersArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scenario.AddDirectMemberAsync("direct-member@example.test");
        scenario.Platform.MembersRelease.TrySetResult();
        using var result = await stale;
        var occupied = await scenario.OccupiedAsync();
        Console.WriteLine($"DIRECT-TOCTOU dashboard={(int)result.StatusCode}, occupied={occupied}, limit=5");
        await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(occupied).IsEqualTo(5);
    }

    [Test]
    public async Task CaseAndWhitespaceCannotProduceADuplicateSlot()
    {
        await using var scenario = await CapacityScenario.StartAsync();
        using (var admitted = await scenario.InviteAsync(0, "  Mixed.Case@Example.Test  "))
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using (var duplicate = await scenario.InviteAsync(1, "mixed.case@example.test"))
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await scenario.PendingAsync()).IsEqualTo(4);
    }

    [Test]
    public async Task PlusAddressesAndUnicodeFormsFollowTsAuthDistinctIdentity()
    {
        await using var scenario = await CapacityScenario.StartAsync(planId: "pro");
        foreach (var (email, expected) in new[] { ("user+tag@example.com", HttpStatusCode.OK), ("\u00e9@example.test", HttpStatusCode.OK), ("e\u0301@example.test", HttpStatusCode.UnprocessableEntity) })
        {
            using var invitation = await scenario.InviteAsync(0, email);
            await Assert.That(invitation.StatusCode).IsEqualTo(expected).Because(email);
        }
        await Assert.That(await scenario.PendingAsync()).IsEqualTo(5);
    }

    [Test]
    public async Task ADowngradeStillCountsAnUnresolvedReservation()
    {
        await using var scenario = await CapacityScenario.StartAsync(planId: "pro");
        scenario.Platform.DeferredEmail = "reserved-before-downgrade@example.test";
        using (var failed = await scenario.InviteAsync(0, scenario.Platform.DeferredEmail))
            await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await scenario.SetPlanAsync(OrganizationRecord.StarterPlanId);
        using var blocked = await scenario.InviteAsync(1, "after-downgrade@example.test");
        await Assert.That(blocked.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task APlanChangedDuringDecisionIsReadAgainAfterCasFailure()
    {
        await using var scenario = await CapacityScenario.StartAsync(planId: "pro");
        using (var fill = await scenario.InviteAsync(0, "fifth-slot@example.test"))
            await Assert.That(fill.StatusCode).IsEqualTo(HttpStatusCode.OK);
        scenario.Platform.Arm(1);
        var stale = scenario.InviteAsync(1, "after-concurrent-downgrade@example.test");
        await scenario.Platform.Arrived[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scenario.SetPlanAsync(OrganizationRecord.StarterPlanId);
        scenario.Platform.Release[0].TrySetResult();
        using var result = await stale;
        await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task ADefiniteRefusalReleasesOnlyItsOwnReservation()
    {
        await using var scenario = await CapacityScenario.StartAsync();
        scenario.Platform.RefusedEmail = "refused@example.test";
        using (var refused = await scenario.InviteAsync(0, scenario.Platform.RefusedEmail))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        using (var admitted = await scenario.InviteAsync(1, "after-refusal@example.test"))
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await scenario.PendingAsync()).IsEqualTo(4);
    }

    [Test]
    public async Task AnOlderAcceptedInvitationIsNotProofThatTheNewOperationCommitted()
    {
        await using var scenario = await CapacityScenario.StartAsync();
        await scenario.Platform.AcceptAsync(scenario.Pending[0]);
        await scenario.RemoveDirectMemberAsync("existing-0@example.test");
        await scenario.AddDirectInvitationAsync("restore-four-occupied@example.test");
        scenario.Platform.DeferredEmail = "existing-0@example.test";
        using (var failed = await scenario.InviteAsync(0, scenario.Platform.DeferredEmail))
            await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var replacement = await scenario.InviteAsync(1, "while-reinviting@example.test");
        scenario.Platform.CommitDeferred.TrySetResult();
        await scenario.Platform.DeferredCommitted.WaitAsync(TimeSpan.FromSeconds(10));
        var occupied = await scenario.OccupiedAsync();
        Console.WriteLine($"OLDER-ACCEPTED replacement={(int)replacement.StatusCode}, occupied={occupied}, limit=5, no clock advance");
        await Assert.That(replacement.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(occupied).IsEqualTo(5);
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
        public JourneyClock OtherClock { get; } = new();
        private IDashboardState sharedState = null!;
        public IReadOnlyList<string> Pending { get; private set; } = [];

        public static async Task<CapacityScenario> StartAsync(int replicas = 2, bool separateClocks = false, string? planId = null)
        {
            var scenario = new CapacityScenario();
            try
            {
                await scenario.environment.InitializeAsync();
                IDashboardState? state = null;
                for (var index = 0; index < replicas; index++)
                {
                    var host = scenario.environment.CreateTestHost(Root, 0, index == 0 ? "http://127.0.0.1" : scenario.hosts[0].Urls.Single(), builder =>
                    {
                        var inner = (ITsAuthPlatform)builder.Services.Last(service => service.ServiceType == typeof(ITsAuthPlatform)).ImplementationInstance!;
                        state ??= (IDashboardState)builder.Services.Last(service => service.ServiceType == typeof(IDashboardState)).ImplementationInstance!;
                        scenario.Platform ??= new(inner);
                        builder.Services.Replace(ServiceDescriptor.Singleton<ITsAuthPlatform>(scenario.Platform));
                        builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(separateClocks && index == 1 ? scenario.OtherClock : scenario.Clock));
                    });
                    scenario.hosts.Add(host);
                    await host.StartAsync();
                }
                foreach (var host in scenario.hosts)
                {
                    var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
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
                settings.PlanId = planId ?? OrganizationRecord.StarterPlanId;
                scenario.sharedState = state!;
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

        public async Task SetPlanAsync(string plan)
        {
            var document = (await sharedState.GetAsync(AccountService.OrganizationsCollection, organizationId, default))!;
            var settings = AccountService.Read<OrganizationRecord>(document);
            settings.PlanId = plan;
            await sharedState.PutAsync(AccountService.OrganizationsCollection, organizationId, JsonSerializer.SerializeToElement(settings), TerraBasePrecondition.Matches(document.Version), default);
        }

        public async Task AddDirectMemberAsync(string email)
        {
            var created = await Platform.CreateInvitationAsync(owner, organizationId, email, "member", default);
            await Platform.AcceptAsync(created.Token);
        }

        public Task AddDirectInvitationAsync(string email) => Platform.CreateInvitationAsync(owner, organizationId, email, "member", default);

        public async Task RemoveDirectMemberAsync(string email)
        {
            var members = await Platform.ListMembersAsync(owner, organizationId, default);
            await Platform.RemoveMemberAsync(owner, organizationId, members.Single(member => member.Email == email).UserId, default);
        }

        public async Task<int> OccupiedAsync()
        {
            var members = await Platform.ListMembersAsync(owner, organizationId, default);
            var invitations = await Platform.ListInvitationsAsync(owner, organizationId, default);
            return members.Select(member => member.Email).Union(invitations.Where(invitation => invitation.Status == "pending").Select(invitation => invitation.Email), StringComparer.OrdinalIgnoreCase).Count();
        }

        public async ValueTask DisposeAsync()
        {
            if (Platform is not null) { Platform.MembersRelease.TrySetResult(); foreach (var gate in Platform.Release) gate.TrySetResult(); Platform.CommitDeferred.TrySetResult(); }
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
        public string RefusedEmail { get; set; } = "";
        public bool PauseNextMembers { get; set; }
        public TaskCompletionSource MembersArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MembersRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DeferredEmail { get; set; } = "";
        public TaskCompletionSource CommitDeferred { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DeferredCommitted { get; private set; } = Task.CompletedTask;

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

        public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformInvitation>> ListProjectInvitationsAsync(SessionRecord session,
            string organizationId, string projectId, string? after, CancellationToken cancellationToken) =>
            inner.ListProjectInvitationsAsync(session, organizationId, projectId, after, cancellationToken);

        public async Task<IReadOnlyList<TerraScaleAuthPlatformInvitation>> ListInvitationsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        {
            var result = await inner.ListInvitationsAsync(session, organizationId, cancellationToken);
            var index = Interlocked.Increment(ref reads) - 1;
            if (index < Arrived.Length) { Arrived[index].TrySetResult(); await Release[index].Task.WaitAsync(cancellationToken); }
            return result;
        }

        public async Task<TerraScaleAuthPlatformCreatedInvitation> CreateInvitationAsync(SessionRecord session, string organizationId, string email, string role, CancellationToken cancellationToken)
        {
            if (email == RefusedEmail) throw new AccountAccessException("Synthetic definite refusal.", 422);
            if (email == DeferredEmail)
            {
                DeferredCommitted = Task.Run(async () => { await CommitDeferred.Task; await inner.CreateInvitationAsync(session, organizationId, email, role, default); });
                throw new HttpRequestException("Synthetic timeout before ts-auth committed the invitation.");
            }
            var created = await inner.CreateInvitationAsync(session, organizationId, email, role, cancellationToken);
            if (email == LostResponseEmail) throw new HttpRequestException("Synthetic response loss after ts-auth committed the invitation.");
            return created;
        }

        public Task<IReadOnlyList<TerraScaleAuthPlatformOrganization>> ListOrganizationsAsync(SessionRecord session, CancellationToken cancellationToken) => inner.ListOrganizationsAsync(session, cancellationToken);
        public Task<TerraScaleAuthPlatformOrganization> CreateOrganizationAsync(SessionRecord session, string name, CancellationToken cancellationToken) => inner.CreateOrganizationAsync(session, name, cancellationToken);
        public Task RenameOrganizationAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken) => inner.RenameOrganizationAsync(session, organizationId, name, cancellationToken);
        public Task LeaveOrganizationAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.LeaveOrganizationAsync(session, organizationId, cancellationToken);
        public Task SignOutEverywhereAsync(SessionRecord session, CancellationToken cancellationToken) => inner.SignOutEverywhereAsync(session, cancellationToken);
        public Task DeleteOrganizationAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.DeleteOrganizationAsync(session, organizationId, cancellationToken);
        public async Task<IReadOnlyList<TerraScaleAuthPlatformMember>> ListMembersAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        {
            var members = await inner.ListMembersAsync(session, organizationId, cancellationToken);
            if (PauseNextMembers)
            {
                PauseNextMembers = false;
                MembersArrived.TrySetResult();
                await MembersRelease.Task.WaitAsync(cancellationToken);
            }
            return members;
        }
        public Task ChangeMemberRoleAsync(SessionRecord session, string organizationId, string userId, string role, CancellationToken cancellationToken) => inner.ChangeMemberRoleAsync(session, organizationId, userId, role, cancellationToken);
        public Task RemoveMemberAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken) => inner.RemoveMemberAsync(session, organizationId, userId, cancellationToken);
        public Task<TerraScaleAuthPlatformProjectMember> SetProjectMemberRoleAsync(SessionRecord session, string organizationId, string projectId, string userId, string role, CancellationToken cancellationToken) => inner.SetProjectMemberRoleAsync(session, organizationId, projectId, userId, role, cancellationToken);
        public Task RemoveProjectMemberAsync(SessionRecord session, string organizationId, string projectId, string userId, CancellationToken cancellationToken) => inner.RemoveProjectMemberAsync(session, organizationId, projectId, userId, cancellationToken);
        public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>> ListProjectMembersAsync(SessionRecord session, string organizationId, string projectId, string? after, CancellationToken cancellationToken) => inner.ListProjectMembersAsync(session, organizationId, projectId, after, cancellationToken);
        public Task<IReadOnlyList<TerraScaleAuthPlatformProject>> ListProjectsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.ListProjectsAsync(session, organizationId, cancellationToken);
        public Task<TerraScaleAuthPlatformProject> CreateProjectAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken) => inner.CreateProjectAsync(session, organizationId, name, cancellationToken);
        public Task RenameProjectAsync(SessionRecord session, string organizationId, string projectId, string name, CancellationToken cancellationToken) => inner.RenameProjectAsync(session, organizationId, projectId, name, cancellationToken);
        public Task<bool> DeleteProjectAsync(SessionRecord session, string organizationId, string projectId, CancellationToken cancellationToken) => inner.DeleteProjectAsync(session, organizationId, projectId, cancellationToken);
        public Task RevokeInvitationAsync(SessionRecord session, string organizationId, string invitationId, CancellationToken cancellationToken) => inner.RevokeInvitationAsync(session, organizationId, invitationId, cancellationToken);
        public Task<TerraScaleAuthPlatformInvitationLookup?> LookupInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken) => inner.LookupInvitationAsync(session, token, cancellationToken);
        public Task<TerraScaleAuthPlatformAcceptedInvitation> AcceptInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken) => inner.AcceptInvitationAsync(session, token, cancellationToken);
        public Task DeclineInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken) => inner.DeclineInvitationAsync(session, token, cancellationToken);
        public Task<TerraScaleAuthPlatformOwnershipTransfer?> GetOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.GetOwnershipTransferAsync(session, organizationId, cancellationToken);
        public Task StartOwnershipTransferAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken) => inner.StartOwnershipTransferAsync(session, organizationId, userId, cancellationToken);
        public Task ConfirmOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.ConfirmOwnershipTransferAsync(session, organizationId, cancellationToken);
        public Task CancelOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.CancelOwnershipTransferAsync(session, organizationId, cancellationToken);
        public Task<IReadOnlyList<TerraScaleAuthPlatformAuditEvent>> ListAuditEventsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) => inner.ListAuditEventsAsync(session, organizationId, cancellationToken);
        public Task<bool> DeleteAccountAsync(SessionRecord session, string receipt, string authorization, CancellationToken cancellationToken) => inner.DeleteAccountAsync(session, receipt, authorization, cancellationToken);
        public Task<bool> AccountDeletionCompletedAsync(string receipt, CancellationToken cancellationToken) => inner.AccountDeletionCompletedAsync(receipt, cancellationToken);
        public Task<bool> AccountDeletionOrganizationCompletedAsync(string receipt, string organizationId, CancellationToken cancellationToken) => inner.AccountDeletionOrganizationCompletedAsync(receipt, organizationId, cancellationToken);
    }
}
