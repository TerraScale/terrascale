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
    public async Task AnUnknownOutcomeKeepsItsSlotUntilTsAuthListsIt()
    {
        await using var scenario = await CapacityScenario.StartAsync();
        // The dashboard sees a failure while ts-auth has not committed yet; only the reservation holds the slot.
        scenario.Platform.DeferredEmail = "in-flight@example.test";
        using (var failed = await scenario.InviteAsync(0, scenario.Platform.DeferredEmail))
            await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using (var blocked = await scenario.InviteAsync(1, "while-in-flight@example.test"))
            await Assert.That(blocked.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        scenario.Platform.CommitDeferred.TrySetResult();
        await scenario.Platform.DeferredCommitted.WaitAsync(TimeSpan.FromSeconds(10));
        using (var after = await scenario.InviteAsync(1, "after-commit@example.test"))
            await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await scenario.PendingAsync()).IsEqualTo(4);
    }

    // Round-3 review probes, verbatim: ts-auth has no idempotency key or cancellation for invitation creation,
    // so neither elapsed time nor another replica's clock ends an unknown outcome.
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
        scenario.OtherClock.Advance(TimeSpan.FromMinutes(16));
        using var replacement = await scenario.InviteAsync(1, "skewed-replacement@example.test");
        scenario.Platform.CommitDeferred.TrySetResult();
        await scenario.Platform.DeferredCommitted.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = await scenario.PendingAsync();
        Console.WriteLine($"CLOCK-SKEW replacement={(int)replacement.StatusCode}, pending={pending}, occupied={pending + 1}, real elapsed<15min");
        await Assert.That(replacement.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(pending).IsEqualTo(4);
    }

    // Round-3 review probe, verbatim: an earlier accepted invitation for the same email is not this request's.
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

    // ts-auth decides when an invitation expires; a replica clock ahead of it must not free pending slots.
    [Test]
    public async Task AReplicaClockCannotExpireTsAuthsPendingInvitations()
    {
        await using var scenario = await CapacityScenario.StartAsync(separateClocks: true);
        using (var last = await scenario.InviteAsync(0, "last-slot@example.test"))
            await Assert.That(last.StatusCode).IsEqualTo(HttpStatusCode.OK);
        // ts-auth lists every pending invitation as expiring in ten minutes; the second replica's clock is 16 ahead.
        scenario.Platform.ListedExpiry = TimeSpan.FromMinutes(10);
        scenario.OtherClock.Advance(TimeSpan.FromMinutes(16));
        using (var skewed = await scenario.InviteAsync(1, "skewed-clock@example.test"))
            await Assert.That(skewed.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because("ts-auth still lists the invitations as pending");
        await Assert.That(await scenario.PendingAsync()).IsEqualTo(4);
    }

    // Recovery for an unknown outcome: the same email is invited again on the slot it already holds. Here the
    // retry's invitation is revoked before the retry records its id, and another decision ends the original's
    // reservation on that invitation; the retry's reservation then stands for the original, which may still commit.
    [Test]
    public async Task ARetryReusesTheUnknownSlotWhichOutlivesTheRetrysInvitation()
    {
        await using var scenario = await CapacityScenario.StartAsync();
        scenario.Platform.DeferredEmail = "retried@example.test";
        using (var failed = await scenario.InviteAsync(0, scenario.Platform.DeferredEmail))
            await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        scenario.Platform.DeferredEmail = "";
        scenario.Platform.PauseAfterCreateEmail = "retried@example.test";
        var retry = scenario.InviteAsync(1, "retried@example.test");
        await scenario.Platform.CreatedPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scenario.RevokeAsync("retried@example.test");
        using (var during = await scenario.InviteAsync(0, "during-retry@example.test"))
            await Assert.That(during.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        scenario.Platform.ContinueCreate.TrySetResult();
        using (var result = await retry) await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("the retry uses its email's slot");
        using (var afterRevoke = await scenario.InviteAsync(0, "after-revoke@example.test"))
            await Assert.That(afterRevoke.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because("the original may still commit");
        scenario.Platform.CommitDeferred.TrySetResult();
        await scenario.Platform.DeferredCommitted.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(scenario.Platform.DeferredRefused).IsFalse();
        using (var overflow = await scenario.InviteAsync(1, "overflow@example.test"))
            await Assert.That(overflow.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
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
        public JourneyClock OtherClock { get; } = new();
        public IReadOnlyList<string> Pending { get; private set; } = [];

        public static async Task<CapacityScenario> StartAsync(int replicas = 2, bool separateClocks = false)
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
                        builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(separateClocks && index == 1 ? scenario.OtherClock : scenario.Clock));
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

        public async Task RevokeAsync(string email)
        {
            var invitation = (await Platform.ListInvitationsAsync(owner, organizationId, default)).Single(invitation => invitation.Email == email && invitation.Status == "pending");
            await Platform.RevokeInvitationAsync(owner, organizationId, invitation.Id, default);
        }

        public async ValueTask DisposeAsync()
        {
            if (Platform is not null) { foreach (var gate in Platform.Release) gate.TrySetResult(); Platform.CommitDeferred.TrySetResult(); Platform.ContinueCreate.TrySetResult(); }
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

    // The in-memory ts-auth platform with gates after invitation-list reads and after a committed invitation,
    // a response lost after ts-auth committed, and a commit deferred past the dashboard's failure.
    private sealed class ControlledPlatform(ITsAuthPlatform inner) : ITsAuthPlatform
    {
        private int reads;
        public TaskCompletionSource[] Arrived { get; private set; } = [];
        public TaskCompletionSource[] Release { get; private set; } = [];
        public string LostResponseEmail { get; set; } = "";
        public string DeferredEmail { get; set; } = "";
        public TaskCompletionSource CommitDeferred { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DeferredCommitted { get; private set; } = Task.CompletedTask;
        public bool DeferredRefused { get; private set; }
        public TimeSpan? ListedExpiry { get; set; }
        public string PauseAfterCreateEmail { get; set; } = "";
        public TaskCompletionSource CreatedPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueCreate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            return ListedExpiry is { } expiry ? [.. result.Select(invitation => invitation with { ExpiresAt = DateTimeOffset.UtcNow + expiry })] : result;
        }

        public async Task<TerraScaleAuthPlatformCreatedInvitation> CreateInvitationAsync(SessionRecord session, string organizationId, string email, string role, CancellationToken cancellationToken)
        {
            if (email == DeferredEmail)
            {
                DeferredCommitted = Task.Run(async () =>
                {
                    await CommitDeferred.Task;
                    try { await inner.CreateInvitationAsync(session, organizationId, email, role, default); }
                    catch (AccountAccessException) { DeferredRefused = true; }
                });
                throw new HttpRequestException("Synthetic timeout before ts-auth committed the invitation.");
            }
            var created = await inner.CreateInvitationAsync(session, organizationId, email, role, cancellationToken);
            if (email == LostResponseEmail) throw new HttpRequestException("Synthetic response loss after ts-auth committed the invitation.");
            if (email == PauseAfterCreateEmail) { CreatedPaused.TrySetResult(); await ContinueCreate.Task.WaitAsync(cancellationToken); }
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
