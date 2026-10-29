using System.Collections.Concurrent;
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

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    // Starter allows five members including pending invitations. Each journey starts with the
    // owner and three pending invitations, so exactly one more invitation fits. Requests pause
    // inside ts-auth reads to replay the races from review round 2 of #13 deterministically.
    public sealed class ReviewRoundFourCapacityJourneyTests
    {
        [Test]
        public async Task CaseAndWhitespaceCannotProduceADuplicateSlot()
        {
            await using var scenario = await CapacityScenario.StartAsync();
            using (var admitted = await scenario.InviteAsync(0, "  Mixed.Case@Example.Test  "))
            {
                await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            using (var duplicate = await scenario.InviteAsync(1, "mixed.case@example.test"))
            {
                await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            }

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
            {
                await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            }

            await scenario.SetPlanAsync(OrganizationRecord.StarterPlanId);
            using var blocked = await scenario.InviteAsync(1, "after-downgrade@example.test");
            await Assert.That(blocked.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        }

        [Test]
        public async Task APlanChangedDuringDecisionIsReadAgainAfterCasFailure()
        {
            await using var scenario = await CapacityScenario.StartAsync(planId: "pro");
            using (var fill = await scenario.InviteAsync(0, "fifth-slot@example.test"))
            {
                await Assert.That(fill.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            scenario.Platform.Arm(1);
            var stale = scenario.BeginInvite(1, "after-concurrent-downgrade@example.test");
            await scenario.Platform.Arrived[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
            await scenario.SetPlanAsync(OrganizationRecord.StarterPlanId);
            scenario.Platform.Release[0].TrySetResult();
            using var result = await stale.WaitAsync(CapacityScenario.RequestBudget);
            await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        }

        [Test]
        public async Task ADefiniteRefusalReleasesOnlyItsOwnReservation()
        {
            await using var scenario = await CapacityScenario.StartAsync();
            scenario.Platform.RefusedEmail = "refused@example.test";
            using (var refused = await scenario.InviteAsync(0, scenario.Platform.RefusedEmail))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            }

            using (var admitted = await scenario.InviteAsync(1, "after-refusal@example.test"))
            {
                await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            await Assert.That(await scenario.PendingAsync()).IsEqualTo(4);
        }

        [Test]
        public async Task ADirectTsAuthMemberAfterTheReadCannotDoubleBookTheSlot()
        {
            await using var scenario = await CapacityScenario.StartAsync();
            scenario.Platform.PauseMembersAfterNextInvitationRead();
            var stale = scenario.BeginInvite(1, "dashboard-after-direct@example.test");
            await scenario.Platform.MembersArrived.Task.WaitAsync(CapacityScenario.RequestBudget);
            await scenario.AddDirectMemberAsync("direct-member@example.test");
            scenario.Platform.MembersRelease.TrySetResult();
            using var result = await stale.WaitAsync(CapacityScenario.RequestBudget);
            var occupied = await scenario.OccupiedAsync();
            Console.WriteLine($"DIRECT-TOCTOU dashboard={(int)result.StatusCode}, occupied={occupied}, limit=5");
            await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(occupied).IsEqualTo(5);
        }

        // ts-auth Status() derives expired from the listing replica's clock; it does not close the stored pending
        // invitation. An accept already validated, or a slower provider clock, can still commit that invitation.
        [Test]
        public async Task AProviderClockExpiryIsNotATerminalInvitationDecision()
        {
            await using var scenario = await CapacityScenario.StartAsync();
            const string email = "clock-expired@example.test";
            using (var original = await scenario.InviteAsync(0, email))
            {
                await Assert.That(original.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            scenario.Platform.ReportedExpiredEmail = email;
            using var replacement = await scenario.InviteAsync(1, "replacement-after-provider-expiry@example.test");
            // The accept validated while the stored invitation was pending; listing expired wrote no version.
            await scenario.Platform.AcceptAsync(scenario.Platform.Tokens[email]);
            scenario.Platform.ReportedExpiredEmail = "";
            var occupied = await scenario.OccupiedAsync();
            Console.WriteLine($"PROVIDER-EXPIRY replacement={(int)replacement.StatusCode}, occupied={occupied}, limit=5");
            await Assert.That(replacement.StatusCode).IsEqualTo(HttpStatusCode.Conflict)
                .Because("a derived expired status is not a stored terminal decision");
            await Assert.That(occupied).IsEqualTo(5);
        }

        private sealed class CapacityScenario : IAsyncDisposable
        {
            private readonly DemoEnvironment environment = new();
            private readonly List<WebApplication> hosts = [];
            private readonly List<(HttpClient Client, string Csrf)> clients = [];
            private readonly List<HttpClient> pausable = [];
            private IDashboardState sharedState = null!;
            private SessionRecord owner = null!;
            private string organizationId = "";
            public ControlledPlatform Platform { get; private set; } = null!;
            public JourneyClock Clock { get; } = new();
            public JourneyClock OtherClock { get; } = new();
            public IReadOnlyList<string> Pending { get; private set; } = [];
            // One budget per request; waiting for a request to reach its gate never gives up sooner than the request would.
            public static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(20);

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
                    // One sign-in serves every replica: they share the session store and keys, cookies ignore the port,
                    // and the form token is the double-submit cookie, so setup stays three light requests at any replica count.
                    var cookies = new CookieContainer();
                    var clients = scenario.hosts.Select(host => new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = cookies })
                    { BaseAddress = new(host.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)), Timeout = RequestBudget }).ToArray();
                    using (var login = await HttpJourney.LoginAsync(clients[0]))
                    {
                        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                    }

                    var csrf = await HttpJourney.CsrfAsync(clients[0]);
                    scenario.clients.AddRange(clients.Select(client => (client, csrf)));
                    scenario.pausable.AddRange(clients.Select(client => new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = cookies })
                    { BaseAddress = client.BaseAddress, Timeout = Timeout.InfiniteTimeSpan }));
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
                    {
                        pending.Add((await scenario.Platform.CreateInvitationAsync(scenario.owner, scenario.organizationId, "existing-" + index + "@example.test", "member", default)).Token);
                    }

                    scenario.Pending = pending;
                    return scenario;
                }
                catch { await scenario.DisposeAsync(); throw; }
            }

            public Task<HttpResponseMessage> InviteAsync(int replica, string email)
            {
                return HttpJourney.PostFormAsync(clients[replica].Client,
                "/api/v1/access/invitations?tab=invitations", new() { ["_csrf"] = clients[replica].Csrf, ["email"] = email, ["role"] = "member" });
            }

            // A request the journey pauses at a gate: the pause is the journey's, so the caller applies RequestBudget
            // once it releases the gate (arrival at the gate is bounded by the same budget).
            public Task<HttpResponseMessage> BeginInvite(int replica, string email)
            {
                return HttpJourney.PostFormAsync(pausable[replica],
                "/api/v1/access/invitations?tab=invitations", new() { ["_csrf"] = clients[replica].Csrf, ["email"] = email, ["role"] = "member" });
            }

            public async Task<int> PendingAsync()
            {
                return (await Platform.ListInvitationsAsync(owner, organizationId, default)).Count(invitation => invitation.Status == "pending");
            }

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

            public Task AddDirectInvitationAsync(string email)
            {
                return Platform.CreateInvitationAsync(owner, organizationId, email, "member", default);
            }

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
                if (Platform is not null) { foreach (var gate in Platform.Release) { gate.TrySetResult(); } Platform.CommitDeferred.TrySetResult(); Platform.ContinueCreate.TrySetResult(); Platform.MembersRelease.TrySetResult(); }
                foreach (var (client, _) in clients)
                {
                    client.Dispose();
                }

                foreach (var client in pausable)
                {
                    client.Dispose();
                }

                foreach (var host in hosts) { await host.StopAsync(); await host.DisposeAsync(); }
            }

            private static string Root { get; } = FindRoot();
            private static string FindRoot()
            {
                for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "TerraScale.slnx")))
                    {
                        return directory.FullName;
                    }
                }

                throw new DirectoryNotFoundException("The E2E executable must run from the dashboard checkout.");
            }
        }

        private sealed class JourneyClock : TimeProvider
        {
            private long offset;
            public override DateTimeOffset GetUtcNow()
            {
                return DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offset));
            }

            public void Advance(TimeSpan delta)
            {
                Interlocked.Add(ref offset, delta.Ticks);
            }
        }

        // The in-memory ts-auth platform with gates after invitation-list reads and after a committed invitation,
        // a response lost after ts-auth committed, and a commit deferred past the dashboard's failure.
        private sealed class ControlledPlatform(ITsAuthPlatform inner) : ITsAuthPlatform
        {
            private int reads;
            private int pauseMembers;
            public TaskCompletionSource[] Arrived { get; private set; } = [];
            public TaskCompletionSource[] Release { get; private set; } = [];
            public string RefusedEmail { get; set; } = "";
            public string ReportedExpiredEmail { get; set; } = "";
            public string LostResponseEmail { get; set; } = "";
            public string DeferredEmail { get; set; } = "";
            public TaskCompletionSource CommitDeferred { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task DeferredCommitted { get; private set; } = Task.CompletedTask;
            public bool DeferredRefused { get; private set; }
            public TimeSpan? ListedExpiry { get; set; }
            public ConcurrentDictionary<string, string> Tokens { get; } = new();
            public TaskCompletionSource MembersArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource MembersRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            // Pauses the member read that follows the next invitation read, after both have answered.
            public void PauseMembersAfterNextInvitationRead()
            {
                Volatile.Write(ref pauseMembers, 1);
            }

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

            public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformInvitation>> ListProjectInvitationsAsync(SessionRecord session,
                string organizationId, string projectId, string? after, CancellationToken cancellationToken)
            {
                return inner.ListProjectInvitationsAsync(session, organizationId, projectId, after, cancellationToken);
            }

            public async Task<IReadOnlyList<TerraScaleAuthPlatformInvitation>> ListInvitationsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
            {
                var result = await inner.ListInvitationsAsync(session, organizationId, cancellationToken);
                Interlocked.CompareExchange(ref pauseMembers, 2, 1);
                var index = Interlocked.Increment(ref reads) - 1;
                if (index < Arrived.Length) { Arrived[index].TrySetResult(); await Release[index].Task.WaitAsync(cancellationToken); }
                result = result.Select(invitation => invitation.Email == ReportedExpiredEmail ? invitation with { Status = "expired", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) } : invitation).ToArray();
                return ListedExpiry is { } expiry ? [.. result.Select(invitation => invitation with { ExpiresAt = DateTimeOffset.UtcNow + expiry })] : result;
            }

            public async Task<TerraScaleAuthPlatformCreatedInvitation> CreateInvitationAsync(SessionRecord session, string organizationId, string email, string role, CancellationToken cancellationToken)
            {
                if (email == RefusedEmail)
                {
                    throw new AccountAccessException("Synthetic definite refusal.", 422);
                }

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
                Tokens[email] = created.Token;
                if (email == LostResponseEmail)
                {
                    throw new HttpRequestException("Synthetic response loss after ts-auth committed the invitation.");
                }

                if (email == PauseAfterCreateEmail) { CreatedPaused.TrySetResult(); await ContinueCreate.Task.WaitAsync(cancellationToken); }
                return created;
            }

            public Task<IReadOnlyList<TerraScaleAuthPlatformOrganization>> ListOrganizationsAsync(SessionRecord session, CancellationToken cancellationToken)
            {
                return inner.ListOrganizationsAsync(session, cancellationToken);
            }

            public Task<TerraScaleAuthPlatformOrganization> CreateOrganizationAsync(SessionRecord session, string name, CancellationToken cancellationToken)
            {
                return inner.CreateOrganizationAsync(session, name, cancellationToken);
            }

            public Task RenameOrganizationAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken)
            {
                return inner.RenameOrganizationAsync(session, organizationId, name, cancellationToken);
            }

            public Task LeaveOrganizationAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
            {
                return inner.LeaveOrganizationAsync(session, organizationId, cancellationToken);
            }

            public Task SignOutEverywhereAsync(SessionRecord session, CancellationToken cancellationToken)
            {
                return inner.SignOutEverywhereAsync(session, cancellationToken);
            }

            public Task DeleteOrganizationAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
            {
                return inner.DeleteOrganizationAsync(session, organizationId, cancellationToken);
            }

            public async Task<IReadOnlyList<TerraScaleAuthPlatformMember>> ListMembersAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
            {
                var members = await inner.ListMembersAsync(session, organizationId, cancellationToken);
                if (Interlocked.CompareExchange(ref pauseMembers, 0, 2) == 2) { MembersArrived.TrySetResult(); await MembersRelease.Task.WaitAsync(cancellationToken); }
                return members;
            }
            public Task ChangeMemberRoleAsync(SessionRecord session, string organizationId, string userId, string role, CancellationToken cancellationToken)
            {
                return inner.ChangeMemberRoleAsync(session, organizationId, userId, role, cancellationToken);
            }

            public Task RemoveMemberAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken)
            {
                return inner.RemoveMemberAsync(session, organizationId, userId, cancellationToken);
            }

            public Task<TerraScaleAuthPlatformProjectMember> SetProjectMemberRoleAsync(SessionRecord session, string organizationId, string projectId, string userId, string role, CancellationToken cancellationToken)
            {
                return inner.SetProjectMemberRoleAsync(session, organizationId, projectId, userId, role, cancellationToken);
            }

            public Task RemoveProjectMemberAsync(SessionRecord session, string organizationId, string projectId, string userId, CancellationToken cancellationToken)
            {
                return inner.RemoveProjectMemberAsync(session, organizationId, projectId, userId, cancellationToken);
            }

            public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>> ListProjectMembersAsync(SessionRecord session, string organizationId, string projectId, string? after, CancellationToken cancellationToken)
            {
                return inner.ListProjectMembersAsync(session, organizationId, projectId, after, cancellationToken);
            }

            public Task<IReadOnlyList<TerraScaleAuthPlatformProject>> ListProjectsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
            {
                return inner.ListProjectsAsync(session, organizationId, cancellationToken);
            }

            public Task<TerraScaleAuthPlatformProject> CreateProjectAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken)
            {
                return inner.CreateProjectAsync(session, organizationId, name, cancellationToken);
            }

            public Task RenameProjectAsync(SessionRecord session, string organizationId, string projectId, string name, CancellationToken cancellationToken)
            {
                return inner.RenameProjectAsync(session, organizationId, projectId, name, cancellationToken);
            }

            public Task<bool> DeleteProjectAsync(SessionRecord session, string organizationId, string projectId, CancellationToken cancellationToken)
            {
                return inner.DeleteProjectAsync(session, organizationId, projectId, cancellationToken);
            }

            public Task RevokeInvitationAsync(SessionRecord session, string organizationId, string invitationId, CancellationToken cancellationToken)
            {
                return inner.RevokeInvitationAsync(session, organizationId, invitationId, cancellationToken);
            }

            public Task<TerraScaleAuthPlatformInvitationLookup?> LookupInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken)
            {
                return inner.LookupInvitationAsync(session, token, cancellationToken);
            }

            public Task<TerraScaleAuthPlatformAcceptedInvitation> AcceptInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken)
            {
                return inner.AcceptInvitationAsync(session, token, cancellationToken);
            }

            public Task DeclineInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken)
            {
                return inner.DeclineInvitationAsync(session, token, cancellationToken);
            }

            public Task<TerraScaleAuthPlatformOwnershipTransfer?> GetOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
            {
                return inner.GetOwnershipTransferAsync(session, organizationId, cancellationToken);
            }

            public Task StartOwnershipTransferAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken)
            {
                return inner.StartOwnershipTransferAsync(session, organizationId, userId, cancellationToken);
            }

            public Task ConfirmOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
            {
                return inner.ConfirmOwnershipTransferAsync(session, organizationId, cancellationToken);
            }

            public Task CancelOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
            {
                return inner.CancelOwnershipTransferAsync(session, organizationId, cancellationToken);
            }

            public Task<IReadOnlyList<TerraScaleAuthPlatformAuditEvent>> ListAuditEventsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
            {
                return inner.ListAuditEventsAsync(session, organizationId, cancellationToken);
            }

            public Task<bool> DeleteAccountAsync(SessionRecord session, string receipt, string authorization, CancellationToken cancellationToken)
            {
                return inner.DeleteAccountAsync(session, receipt, authorization, cancellationToken);
            }

            public Task<bool> AccountDeletionCompletedAsync(string receipt, CancellationToken cancellationToken)
            {
                return inner.AccountDeletionCompletedAsync(receipt, cancellationToken);
            }

            public Task<bool> AccountDeletionOrganizationCompletedAsync(string receipt, string organizationId, CancellationToken cancellationToken)
            {
                return inner.AccountDeletionOrganizationCompletedAsync(receipt, organizationId, cancellationToken);
            }
        }
    }
}
