using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>
/// Project and account closure against a real TerraBase over ts-auth's peer transport and the ts-dotnet SDK
/// (docs/terrabase/project-closure.md): no TerraBase answer other than acceptance removes an auth root.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectClosureTests(TerraBaseFixture fixture)
{
    private static readonly string[] DocumentsRead = ["documents:read"];

    [Test]
    public async Task A_project_is_removed_only_after_terrabase_accepts_and_a_lost_answer_is_retried_with_the_same_closure()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false);
        var (owner, token) = await oidc.SignInUserAsync("owner@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var first = await oidc.CreateProjectAsync(token, organization, "First");
        var second = await oidc.CreateProjectAsync(token, organization, "Second");
        var terraBase = fixture.Server.Management();

        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{first}", token, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{organization}/projects/{first}", token)).IsEqualTo((404, "not_found"));
        await FencedAsync(first, organization);

        // The intent is committed and TerraBase accepted, then ts-auth lost the answer before removing the root: the retry
        // presents the same stored closure.
        var intent = await oidc.Server.App.Services.GetRequiredService<PlatformDirectory>().RequestProjectClosureAsync(owner.Id, organization, second, null, CancellationToken.None);
        await Assert.That(intent.Value.ProjectClosureVersion).IsEqualTo(1);
        await Assert.That((await terraBase.CloseProjectAsync(second, organization, intent.Value.Id)).Accepted).IsTrue();
        await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{organization}/projects/{second}", token);
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{second}", token, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{organization}/projects/{second}", token)).IsEqualTo((404, "not_found"));
        await Assert.That(await JobAsync(oidc, "project", second)).IsNull();
    }

    [Test]
    public async Task Legacy_acceptance_keeps_the_project_until_current_erasure_evidence_arrives()
    {
        using var legacy = new LegacyClosureEvidence { InnerHandler = TerraBaseFixture.NewHandler() };
        using var transport = new HttpClient(legacy);
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, services: services =>
            services.AddKeyedSingleton(App.StorageAuthority, (_, _) => new TerraBaseManagementClient(transport,
                new TerraBaseManagementClientOptions { BaseAddress = fixture.Server.InternalAddress })));
        var (_, token) = await oidc.SignInUserAsync("owner@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Retained");
        var route = $"{Organizations}/{organization}/projects/{project}";
        await oidc.OkAsync(HttpMethod.Delete, route, token, status: HttpStatusCode.Accepted);
        await oidc.OkAsync(HttpMethod.Get, route, token);
        await Assert.That((await JobAsync(oidc, "project", project))!.FailureCode).IsEqualTo("terrabase_evidence_pending");
        legacy.OmitEvidence = false;
        await oidc.OkAsync(HttpMethod.Delete, route, token, status: HttpStatusCode.NoContent);
        await Assert.That(await JobAsync(oidc, "project", project)).IsNull();
    }

    [Test]
    [Arguments(HttpStatusCode.BadRequest, false)]
    [Arguments(HttpStatusCode.NotFound, false)]
    [Arguments(HttpStatusCode.NotImplemented, false)]
    [Arguments(HttpStatusCode.BadRequest, true)]
    [Arguments(HttpStatusCode.NotFound, true)]
    [Arguments(HttpStatusCode.NotImplemented, true)]
    public async Task Unsupported_erasure_uses_the_legacy_path_and_resumes_the_same_account_intent_after_upgrade(HttpStatusCode unsupported, bool nonempty)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        using var legacy = new LegacyErasureContract(unsupported, nonempty) { InnerHandler = TerraBaseFixture.NewHandler() };
        using var transport = new HttpClient(legacy);
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, clock: clock, services: services =>
            services.AddKeyedSingleton(App.StorageAuthority, (_, _) => new TerraBaseManagementClient(transport,
                new TerraBaseManagementClientOptions { BaseAddress = fixture.Server.InternalAddress })));
        var services = oidc.Server.App.Services;
        var worker = services.GetRequiredService<ClosureWorker>();
        var (owner, token) = await oidc.SignInUserAsync("owner@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Retained");
        await oidc.OkAsync(HttpMethod.Delete, "/v1/terrascale/account", token, status: HttpStatusCode.Accepted);
        await Assert.That(legacy.Versions is [2, 1]).IsTrue();
        var pending = (await JobAsync(oidc, "project", project))!;
        await Assert.That(pending.ProjectClosureVersion).IsEqualTo(2);
        await Assert.That(pending.FailureCode).IsEqualTo(nonempty ? "terrabase_erasure_unsupported" : "terrabase_evidence_pending");
        await Assert.That(await services.GetRequiredService<Users>().GetAsync("terrascale", owner.Id)).IsNotNull();
        await Assert.That(await JobAsync(oidc, "account", owner.Id)).IsNotNull();
        var directory = services.GetRequiredService<PlatformDirectory>();
        await Assert.That(await directory.ProjectAsync(project, CancellationToken.None)).IsNotNull();

        // No second submit: the same durable driver resumes against the upgraded real engine.
        legacy.Upgraded = true;
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        await worker.RunOnceAsync();
        await Assert.That(await services.GetRequiredService<Users>().GetAsync("terrascale", owner.Id)).IsNull();
        await Assert.That(await directory.ProjectAsync(project, CancellationToken.None)).IsNull();
        await Assert.That(await JobAsync(oidc, "account", owner.Id)).IsNull();
    }

    [Test]
    public async Task A_legacy_fallback_conflict_cannot_cancel_a_v2_fence_whose_answer_was_lost()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        using var legacy = new LegacyErasureContract(HttpStatusCode.BadRequest, false)
        {
            InnerHandler = TerraBaseFixture.NewHandler(),
            LoseFirstErasureAnswer = true,
        };
        using var transport = new HttpClient(legacy);
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, clock: clock, services: services =>
            services.AddKeyedSingleton(App.StorageAuthority, (_, _) => new TerraBaseManagementClient(transport,
                new TerraBaseManagementClientOptions { BaseAddress = fixture.Server.InternalAddress })));
        var services = oidc.Server.App.Services;
        var worker = services.GetRequiredService<ClosureWorker>();
        var (owner, token) = await oidc.SignInUserAsync("owner@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Retained");
        await oidc.OkAsync(HttpMethod.Delete, "/v1/terrascale/account", token, status: HttpStatusCode.Accepted);
        await Assert.That(legacy.WroteFence).IsTrue();
        var original = (await JobAsync(oidc, "project", project))!;
        await Assert.That(original.FenceSeen).IsFalse();

        // Put the account first in the due snapshot. Its retry reschedules the child before this same batch reaches it.
        var account = (await services.GetRequiredService<PlatformDirectory>().ClosureAsync("account", owner.Id, CancellationToken.None))!;
        await Assert.That(await services.GetRequiredService<AuthStore>().ReplaceAsync(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key("account", owner.Id),
            JsonSerializer.SerializeToElement(account.Value with { NextAttemptAt = clock.UtcNow.AddSeconds(1) }, ClosureJson.Default.ClosureJob), account.Version)).IsNotNull();

        // The old engine's fallback now meets the real v2 fence, with no recorded fence evidence in auth.
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        await worker.RunOnceAsync();
        await Assert.That(legacy.Versions is [2, 2, 1]).IsTrue();
        var pending = (await JobAsync(oidc, "project", project))!;
        await Assert.That(pending.Same(original)).IsTrue();
        await Assert.That(pending.FailureCode).IsEqualTo("terrabase_erasure_unsupported");
        await Assert.That(pending.FenceSeen).IsFalse();
        await Assert.That(await JobAsync(oidc, "account", owner.Id)).IsNotNull();
        await Assert.That(await services.GetRequiredService<Users>().GetAsync("terrascale", owner.Id)).IsNotNull();

        legacy.Upgraded = true;
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        await worker.RunOnceAsync();
        await Assert.That(await services.GetRequiredService<Users>().GetAsync("terrascale", owner.Id)).IsNull();
        await Assert.That(await services.GetRequiredService<PlatformDirectory>().ProjectAsync(project, CancellationToken.None)).IsNull();
        await Assert.That(await JobAsync(oidc, "account", owner.Id)).IsNull();
    }

    private sealed class LegacyErasureContract(HttpStatusCode unsupported, bool nonempty) : DelegatingHandler
    {
        public bool Upgraded { get; set; }
        public bool LoseFirstErasureAnswer { get; set; }
        public bool WroteFence { get; private set; }
        public List<int> Versions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/closure", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                var version = body.GetProperty("version").GetInt32();
                this.Versions.Add(version);
                if (version == 2 && this.LoseFirstErasureAnswer)
                {
                    this.LoseFirstErasureAnswer = false;
                    using var accepted = await base.SendAsync(request, cancellationToken);
                    this.WroteFence = accepted.IsSuccessStatusCode;
                    return new(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("""{"error":{"code":"temporarily_unavailable","message":"answer lost"}}""", Encoding.UTF8, "application/json"),
                    };
                }

                if (!this.Upgraded && (version == 2 || nonempty))
                {
                    // Only the old wire failures are injected; auth persists in real TerraBase and successful closures
                    // run through its real v1/v2 endpoint. A refusal writes no fence.
                    return new(version == 2 ? unsupported : HttpStatusCode.Conflict)
                    {
                        Content = new StringContent(version == 2
                            ? """{"error":{"code":"invalid_request","message":"version must be 1"}}"""
                            : """{"error":{"code":"not_empty","message":"delete what it contains first"}}""", Encoding.UTF8, "application/json"),
                    };
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class LegacyClosureEvidence : DelegatingHandler
    {
        public bool OmitEvidence { get; set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (this.OmitEvidence && request.RequestUri!.AbsolutePath.EndsWith("/closure", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
                body.Remove("evidenceVersion");
                response.Content.Dispose();
                response.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            return response;
        }
    }

    [Test]
    [Arguments("closure")]
    [Arguments("binding")]
    public async Task A_terrabase_refusal_changes_nothing_and_keeps_every_right_of_the_project(string refusal)
    {
        await using var enrollment = await ProjectEnrollment.StartAsync(TestContext.Current!.Execution.CancellationToken);
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false,
            services: services => services.AddKeyedSingleton(App.StorageAuthority, (_, _) => enrollment.Server.Management()));
        var (_, token) = await oidc.SignInUserAsync("owner@example.test");
        var (guest, _) = await oidc.SignInUserAsync("guest@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Kept");
        if (refusal == "binding")
        {
            await enrollment.EnrollAsync(project);
        }
        else
        {
            await enrollment.Server.Management().CloseProjectAsync(project, organization, AuthStore.NewId());
        }

        await Assert.That(await oidc.FailAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{project}", token))
            .IsEqualTo((409, refusal == "binding" ? "binding_conflict" : "closure_conflict"));
        await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{organization}/projects/{project}", token);
        await Assert.That(await JobAsync(oidc, "project", project)).IsNull();
        // The intent's fence is gone with it: the project takes new roles again.
        await Assert.That(await oidc.GrantAsync(project, guest.Id, "viewer")).IsTrue();
    }

    // A wrong listener (404 unknown_route), a wrong peer credential (401) or no TerraBase at all proves nothing: the intent
    // stays, fences the project, and a restarted replica with the right transport finishes it.
    [Test]
    [Arguments("listener")]
    [Arguments("credential")]
    [Arguments("unreachable")]
    public async Task An_unproven_answer_keeps_the_project_and_its_intent_until_a_restart_finishes_it(string fault)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, services: services => Misroute(services, fault));
        var (_, token) = await oidc.SignInUserAsync("owner@example.test");
        var (guest, _) = await oidc.SignInUserAsync("guest@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Pending");

        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{project}", token, status: HttpStatusCode.Accepted);
        await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{organization}/projects/{project}", token);
        var job = await JobAsync(oidc, "project", project);
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.FailureCode).IsNotNull();
        // The pending intent refuses new access to the project.
        await Assert.That(async () => await oidc.GrantAsync(project, guest.Id, "viewer")).Throws<ApiException>();
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<AuthStore>().GetAsync(AuthStore.ProjectMemberCollection, "terrascale", $"{project}/{guest.Id}")).IsNull();
        // Repeating the request resumes the same intent and still proves nothing.
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{project}", token, status: HttpStatusCode.Accepted);

        await oidc.Server.DisposeAsync();
        // Restarted past the one-minute retry delay the failed attempt recorded.
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(2) };
        await using var restarted = await oidc.StartReplicaAsync(runClosureWorker: false, clock: clock);
        await SettleAsync(restarted, clock, async () => await restarted.App.Services.GetRequiredService<PlatformDirectory>().ClosureAsync("project", project, CancellationToken.None) is null);
        await Assert.That(await restarted.App.Services.GetRequiredService<PlatformDirectory>().ProjectAsync(project, CancellationToken.None)).IsNull();
        await Assert.That(await restarted.App.Services.GetRequiredService<PlatformDirectory>().ClosureAsync("project", project, CancellationToken.None)).IsNull();
        await FencedAsync(project, organization);
    }

    // The account intent is committed before TerraBase is asked; its organization fence keeps a sole organization sole.
    [Test]
    public async Task An_account_closure_is_durable_before_terrabase_and_resumes_after_a_restart()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, services: services => Misroute(services, "unreachable"));
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, _) = await oidc.SignInUserAsync("bob@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Solo");

        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{project}", token, status: HttpStatusCode.Accepted);
        var directoryBeforeRestart = oidc.Server.App.Services.GetRequiredService<PlatformDirectory>();
        var pending = (await directoryBeforeRestart.ClosureAsync("project", project, CancellationToken.None))!;
        await Assert.That(pending.Value.ProjectClosureVersion).IsEqualTo(1);
        await Assert.That(async () => await directoryBeforeRestart.RequestProjectClosureAsync(alice.Id, organization, project, alice.Id, CancellationToken.None)).Throws<ApiException>();
        await Assert.That((await JobAsync(oidc, "project", project))!.ProjectClosureVersion).IsEqualTo(1);

        var receipt = Receipt();
        await Assert.That(await ReceiptCallAsync(oidc.Client, receipt, HttpMethod.Delete, token: token, oidc: oidc)).IsEqualTo((202, (string?)null));
        await Assert.That(await ReceiptCallAsync(oidc.Client, receipt)).IsEqualTo((202, (string?)null));
        var attached = Receipt();
        await Assert.That(await ReceiptCallAsync(oidc.Client, attached, HttpMethod.Delete, token: token, oidc: oidc)).IsEqualTo((202, (string?)null));
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        await Assert.That(await users.GetAsync("terrascale", alice.Id)).IsNotNull();
        await Assert.That(await JobAsync(oidc, "account", alice.Id)).IsNotNull();
        await Assert.That(await JobAsync(oidc, "organization", organization)).IsNotNull();
        await Assert.That((await JobAsync(oidc, "project", project))!.ProjectClosureVersion).IsEqualTo(2);
        var adopted = (await JobAsync(oidc, "project", project))!;
        await Assert.That(adopted.Id).IsEqualTo(pending.Value.Id);
        await Assert.That(adopted.Generation).IsNotEqualTo(pending.Value.Generation);
        await Assert.That(adopted.Same(pending.Value)).IsFalse();
        await Assert.That(adopted.AccountId).IsEqualTo(alice.Id);
        await Assert.That(await directoryBeforeRestart.CancelClosureAsync(pending, CancellationToken.None)).IsFalse();
        var liveClosures = ActivatorUtilities.CreateInstance<Closures>(oidc.Server.App.Services, fixture.Server.Management());
        await Assert.That(await liveClosures.AdvanceAsync(pending, CancellationToken.None)).IsFalse();
        await Assert.That((await JobAsync(oidc, "project", project))!.Same(adopted)).IsTrue();
        await Assert.That(await directoryBeforeRestart.ProjectAsync(project, CancellationToken.None)).IsNotNull();
        // Nobody joins the closing organization, so the sole-owner decision cannot go stale.
        await Assert.That(async () => await oidc.AddMemberAsync(organization, bob.Id, "member")).Throws<ApiException>();
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<AuthStore>().GetAsync(AuthStore.MembershipCollection, "terrascale", $"{organization}/{bob.Id}")).IsNull();

        await oidc.Server.DisposeAsync();
        // Restarted past the one-minute retry delay the failed attempt recorded.
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(2) };
        using var completion = new HeldClosureCompletion(AuthStore.NaturalKeyId("terrascale", AuthStore.ClosureJobCollection, ClosureJob.Key("account", alice.Id)))
        {
            InnerHandler = TerraBaseFixture.NewHandler(),
        };
        await using var restarted = await oidc.StartReplicaAsync(runClosureWorker: false, clock: clock, services: services => services.AddSingleton(new HttpClient(completion)));
        var directory = restarted.App.Services.GetRequiredService<PlatformDirectory>();
        async Task<bool> DoneAsync() => await directory.ClosureAsync("account", alice.Id, CancellationToken.None) is null;
        var settling = SettleAsync(restarted, clock, DoneAsync);
        try
        {
            // Hold the real terminal DELETE before TerraBase commits it. The user is already gone, but the durable
            // account job still exists: user absence cannot be this fixture's completion condition.
            await completion.Entered.Task.WaitAsync(TestContext.Current!.Execution.CancellationToken);
            await Assert.That(await restarted.App.Services.GetRequiredService<Users>().GetAsync("terrascale", alice.Id)).IsNull();
            await Assert.That(await directory.ClosureAsync("account", alice.Id, CancellationToken.None)).IsNotNull();
            await Assert.That(await DoneAsync()).IsFalse();
            await Assert.That(await ReceiptCallAsync(restarted.Client, receipt)).IsEqualTo((204, (string?)null));
            await Assert.That(await ReceiptCallAsync(restarted.Client, attached, organization: organization)).IsEqualTo((204, (string?)null));
            using var unauthorized = await restarted.Client.SendAsync(BearerRequest(HttpMethod.Get, Organizations, token));
            await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        finally
        {
            completion.Release.TrySetResult();
            await settling.WaitAsync(TestContext.Current!.Execution.CancellationToken);
        }
        await Assert.That(await restarted.App.Services.GetRequiredService<Users>().GetAsync("terrascale", alice.Id)).IsNull();
        await Assert.That(await directory.OrganizationAsync(organization, CancellationToken.None)).IsNull();
        await Assert.That(await directory.ProjectAsync(project, CancellationToken.None)).IsNull();
        foreach (var (scope, subject) in new[] { ("account", alice.Id), ("organization", organization), ("project", project) })
        {
            await Assert.That(await directory.ClosureAsync(scope, subject, CancellationToken.None)).IsNull().Because(scope);
        }

        await FencedAsync(project, organization);
        clock.UtcNow = clock.UtcNow.AddDays(16);
        await Assert.That(await ReceiptCallAsync(restarted.Client, receipt)).IsEqualTo((404, "not_found"));
    }

    // The other owner of a shared organization leaves while the account closure waits: resuming finds the caller the last
    // owner and ends the account intent. The project closure it had started (pending, maybe fenced in TerraBase) is not
    // rolled back but detached and finished on its own; the account, its organizations, memberships and untouched
    // projects keep every right, and no intent is left behind.
    [Test]
    public async Task A_late_last_owner_ends_the_account_intent_finishes_started_closures_and_keeps_everything_else()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, services: services => Misroute(services, "unreachable"));
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var (carol, _) = await oidc.SignInUserAsync("carol@example.test");
        var solo = await oidc.CreateOrganizationAsync(token, "Solo");
        var started = await oidc.CreateProjectAsync(token, solo, "Solo project");
        var shared = await oidc.CreateOrganizationAsync(token, "Shared");
        var untouched = await oidc.CreateProjectAsync(token, shared, "Shared project");
        await oidc.AddMemberAsync(shared, bob.Id, "owner");
        await oidc.AddMemberAsync(shared, carol.Id, "member");

        var receipt = Receipt();
        await Assert.That(await ReceiptCallAsync(oidc.Client, receipt, HttpMethod.Delete, token: token, oidc: oidc)).IsEqualTo((202, (string?)null));
        var startedJob = (await JobAsync(oidc, "project", started))!;
        await Assert.That(startedJob.AccountId).IsEqualTo(alice.Id);
        await Assert.That(startedJob.ProjectClosureVersion).IsEqualTo(2);
        await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{shared}/leave", bobToken, status: HttpStatusCode.NoContent);

        await oidc.Server.DisposeAsync();
        // Restarted past the one-minute retry delay the failed attempt recorded.
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(2) };
        await using var restarted = await oidc.StartReplicaAsync(runClosureWorker: false, clock: clock);
        var directory = restarted.App.Services.GetRequiredService<PlatformDirectory>();
        await SettleAsync(restarted, clock, async () => await directory.ClosureAsync("account", alice.Id, CancellationToken.None) is null);
        await Assert.That(await directory.ClosureAsync("account", alice.Id, CancellationToken.None)).IsNull();
        await Assert.That((await directory.ClosureAsync("project", started, CancellationToken.None))?.Value.AccountId).IsNull();
        await SettleAsync(restarted, clock, async () => await directory.ProjectAsync(started, CancellationToken.None) is null);

        await Assert.That(await restarted.App.Services.GetRequiredService<Users>().GetAsync("terrascale", alice.Id)).IsNotNull();
        await Assert.That(await directory.OrganizationAsync(solo, CancellationToken.None)).IsNotNull();
        await Assert.That(await directory.ProjectAsync(started, CancellationToken.None)).IsNull();
        await FencedAsync(started, solo);
        // Detaching the child cannot downgrade the authorized erasure: the real permanent fence retains version 2.
        var downgraded = await Assert.That(async () => await fixture.Server.Management().CloseProjectAsync(started, solo, startedJob.Id)).Throws<TerraBaseConflictException>();
        await Assert.That(downgraded!.ErrorCode).IsEqualTo("closure_conflict");
        foreach (var (scope, subject) in new[] { ("organization", solo), ("project", started), ("project", untouched) })
        {
            await Assert.That(await directory.ClosureAsync(scope, subject, CancellationToken.None)).IsNull().Because(subject);
        }

        await Assert.That(await directory.ProjectAsync(untouched, CancellationToken.None)).IsNotNull();
        await Assert.That((await directory.ProjectAccessAsync(alice.Id, shared, untouched, CancellationToken.None)).Role).IsEqualTo("admin");
        await Assert.That((await directory.ProjectAccessAsync(carol.Id, shared, untouched, CancellationToken.None)).Role).IsNotNull();
        // The fences went with the intent: both organizations take members and roles again.
        await Assert.That(await directory.AddMemberAsync(solo, carol.Id, "member", CancellationToken.None)).IsTrue();
        await Assert.That(await directory.GrantAsync(untouched, bob.Id, "viewer", CancellationToken.None)).IsTrue();
        await Assert.That(await ReceiptCallAsync(restarted.Client, receipt)).IsEqualTo((409, "deletion_refused"));
        using var signedIn = await oidc.PostFormAsync("/ui/terrascale/sign-in", new() { ["email"] = alice.Email }, client: restarted.Client);
        var code = await oidc.Email.NextCodeAsync(alice.Email);
        using var verified = await oidc.PostFormAsync("/ui/terrascale/sign-in/verify", new() { ["email"] = alice.Email, ["code"] = code }, client: restarted.Client);
        await Assert.That(OidcHarness.SessionCookie(verified)).IsNotNull();
    }

    // One project closure is accepted and removed, the next is refused: the account intent ends, the accepted removal
    // stands, and the refused and not-yet-started projects keep every right with no intent left.
    [Test]
    [Arguments("closure")]
    [Arguments("binding")]
    public async Task A_refusal_after_an_accepted_project_closure_ends_the_account_intent_without_stranding_anything(string refusal)
    {
        await using var enrollment = await ProjectEnrollment.StartAsync(TestContext.Current!.Execution.CancellationToken);
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false,
            services: services => services.AddKeyedSingleton(App.StorageAuthority, (_, _) => enrollment.Server.Management()));
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var (guest, _) = await oidc.SignInUserAsync("guest@example.test");
        var solo = await oidc.CreateOrganizationAsync(token, "Solo");
        string[] created = [await oidc.CreateProjectAsync(token, solo, "One"), await oidc.CreateProjectAsync(token, solo, "Two"), await oidc.CreateProjectAsync(token, solo, "Three")];
        // The closure visits an organization's projects in storage id order.
        var ordered = created.OrderBy(id => AuthStore.NaturalKeyId("terrascale", AuthStore.ProjectCollection, id), StringComparer.Ordinal).ToArray();
        var (first, refused, later) = (ordered[0], ordered[1], ordered[2]);
        if (refusal == "binding")
        {
            await enrollment.EnrollAsync(refused);
        }
        else
        {
            await enrollment.Server.Management().CloseProjectAsync(refused, solo, AuthStore.NewId());
        }

        await Assert.That(await oidc.FailAsync(HttpMethod.Delete, "/v1/terrascale/account", token)).IsEqualTo((409, refusal == "binding" ? "binding_conflict" : "closure_conflict"));
        var directory = oidc.Server.App.Services.GetRequiredService<PlatformDirectory>();
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<Users>().GetAsync("terrascale", alice.Id)).IsNotNull();
        await Assert.That(await directory.ProjectAsync(first, CancellationToken.None)).IsNull();
        foreach (var project in new[] { refused, later })
        {
            await Assert.That(await directory.ProjectAsync(project, CancellationToken.None)).IsNotNull().Because(project);
            await Assert.That(await oidc.GrantAsync(project, guest.Id, "viewer")).IsTrue().Because(project);
        }

        foreach (var (scope, subject) in new[] { ("account", alice.Id), ("organization", solo), ("project", first), ("project", refused), ("project", later) })
        {
            await Assert.That(await directory.ClosureAsync(scope, subject, CancellationToken.None)).IsNull().Because(subject);
        }

        await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{solo}", token);
    }

    // QA round 4 (sites cleanup): an accepted account deletion closes its sole organizations, and their project keys kept
    // minting tokens until every region confirmed. Keys stop at acceptance: any pending organization or project closure.
    [Test]
    [Arguments("organization")]
    [Arguments("project")]
    public async Task A_key_mints_nothing_once_its_organization_or_project_has_a_pending_closure(string scope)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, clock: clock);
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token, "Solo");
        var project = await oidc.CreateProjectAsync(token, organization, "Keys");
        var key = (await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/projects/{project}/api-keys", token,
            new { name = "backend", scopes = DocumentsRead }, HttpStatusCode.Created)).GetProperty("key").GetString()!;
        using (var before = await oidc.ApiKeyTokenAsync(key))
        {
            await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var subject = scope == "organization" ? organization : project;
        var (_, intent) = ClosureJob.New("terrascale", scope, subject, organization, alice.Id, clock, alice.Id);
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<AuthStore>().TransactAsync([intent])).IsNull();
        using var after = await oidc.ApiKeyTokenAsync(key);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await OidcHarness.JsonAsync(after)).GetProperty("error").GetString()).IsEqualTo("invalid_client");
    }

    // Every deferral is a remote-durable write: a pending intent backs off (2 s doubling, at most a minute) instead of being
    // rewritten on every two-second pass while its account intent drives it.
    [Test]
    public async Task An_account_driven_intent_backs_off_to_a_minute_between_deferrals()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, clock: clock);
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token, "Solo");
        var project = await oidc.CreateProjectAsync(token, organization, "Pending");
        var services = oidc.Server.App.Services;
        var (store, worker) = (services.GetRequiredService<AuthStore>(), services.GetRequiredService<ClosureWorker>());

        // The account intent is never due, so only its project intent is visited, and deferred each time it is.
        var (accountJob, _) = ClosureJob.New("terrascale", "account", alice.Id, null, alice.Id, clock);
        var (_, child) = ClosureJob.New("terrascale", "project", project, organization, alice.Id, clock, alice.Id);
        var account = AuthStore.Create(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key("account", alice.Id),
            JsonSerializer.SerializeToElement(accountJob with { NextAttemptAt = clock.UtcNow.AddDays(1) }, ClosureJson.Default.ClosureJob));
        await Assert.That(await store.TransactAsync([account, child])).IsNull();

        List<double> delays = [];
        for (var pass = 0; pass < 7; pass++)
        {
            await worker.RunOnceAsync();
            var pending = (await JobAsync(oidc, "project", project))!;
            await Assert.That(pending.FailureCode).IsEqualTo("account_driven");
            delays.Add((pending.NextAttemptAt - clock.UtcNow).TotalSeconds);
            clock.UtcNow = pending.NextAttemptAt;
        }

        await Assert.That(string.Join(",", delays)).IsEqualTo("2,4,8,16,32,60,60");

        // An intent still pending after about an hour at the cap is parked: every 15 minutes, never dropped.
        var directory = services.GetRequiredService<PlatformDirectory>();
        var stuck = (await directory.ClosureAsync("project", project, CancellationToken.None))!;
        await Assert.That(await store.ReplaceAsync(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key("project", project),
            JsonSerializer.SerializeToElement(stuck.Value with { Attempts = Closures.ParkAfter - 1 }, ClosureJson.Default.ClosureJob), stuck.Version)).IsNotNull();
        for (var pass = 0; pass < 2; pass++)
        {
            await worker.RunOnceAsync();
            var parked = (await JobAsync(oidc, "project", project))!;
            await Assert.That(parked.NextAttemptAt - clock.UtcNow).IsEqualTo(TimeSpan.FromMinutes(15));
            clock.UtcNow = parked.NextAttemptAt;
        }
    }

    // Review R2 P2-2: a step that read the organizations before a concurrent cancellation can no longer create an
    // account-owned project intent (its transaction requires the account intent); one created just before is recovered by
    // the background pass, as is a stale organization fence. While the account intent exists they stay its own.
    [Test]
    public async Task Account_owned_intents_without_their_account_intent_are_never_created_and_always_recovered()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, clock: clock);
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, _) = await oidc.SignInUserAsync("bob@example.test");
        var organization = await oidc.CreateOrganizationAsync(token, "Solo");
        var orphan = await oidc.CreateProjectAsync(token, organization, "Orphan");
        var guarded = await oidc.CreateProjectAsync(token, organization, "Guarded");
        var services = oidc.Server.App.Services;
        var (store, directory, worker) = (services.GetRequiredService<AuthStore>(), services.GetRequiredService<PlatformDirectory>(), services.GetRequiredService<ClosureWorker>());

        // The account intent, its organization fence and one project intent exist, as a step left them.
        // The account intent is not due yet, so only its children are visited.
        var (accountJob, _) = ClosureJob.New("terrascale", "account", alice.Id, null, alice.Id, clock);
        var (_, fence) = ClosureJob.New("terrascale", "organization", organization, organization, alice.Id, clock, alice.Id);
        var (_, child) = ClosureJob.New("terrascale", "project", orphan, organization, alice.Id, clock, alice.Id);
        var account = AuthStore.Create(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key("account", alice.Id),
            JsonSerializer.SerializeToElement(accountJob with { NextAttemptAt = clock.UtcNow.AddHours(1) }, ClosureJson.Default.ClosureJob));
        await Assert.That(await store.TransactAsync([account, fence, child])).IsNull();
        await worker.RunOnceAsync();
        await Assert.That((await directory.ClosureAsync("project", orphan, CancellationToken.None))!.Value.AccountId).IsEqualTo(alice.Id);
        await Assert.That(await directory.ProjectAsync(orphan, CancellationToken.None)).IsNotNull();

        // A concurrent cancellation removed only the account intent.
        var accountIntent = (await directory.ClosureAsync("account", alice.Id, CancellationToken.None))!;
        await Assert.That(await directory.CancelClosureAsync(accountIntent, CancellationToken.None)).IsTrue();
        await Assert.That(async () => await directory.RequestProjectClosureAsync(alice.Id, organization, guarded, alice.Id, CancellationToken.None)).Throws<ApiException>();
        await Assert.That(await directory.ClosureAsync("project", guarded, CancellationToken.None)).IsNull();
        await Assert.That(async () => await oidc.AddMemberAsync(organization, bob.Id, "member")).Throws<ApiException>();

        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        await worker.RunOnceAsync();
        await worker.RunOnceAsync();
        await Assert.That(await directory.ClosureAsync("organization", organization, CancellationToken.None)).IsNull();
        await Assert.That(await directory.ClosureAsync("project", orphan, CancellationToken.None)).IsNull();
        await Assert.That(await directory.ProjectAsync(orphan, CancellationToken.None)).IsNull();
        await Assert.That(await directory.ProjectAsync(guarded, CancellationToken.None)).IsNotNull();
        await Assert.That(await oidc.AddMemberAsync(organization, bob.Id, "member")).IsTrue();
    }

    // Review R2 P3-1 and P3-4: a refusal cancels the intent only at a version that never showed the fence. A refusal read
    // on a stale version is re-read; once a concurrent pass recorded the fence, it stays pending and nothing is cancelled.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_refusal_cancels_only_an_intent_that_never_showed_the_fence(bool fenceSeenMeanwhile)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false);
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token, "Org");
        var project = await oidc.CreateProjectAsync(token, organization, "Refused");
        // TerraBase holds another closure for this id, so it refuses this one (closure_conflict).
        await fixture.Server.Management().CloseProjectAsync(project, organization, AuthStore.NewId());
        var services = oidc.Server.App.Services;
        var (directory, closures) = (services.GetRequiredService<PlatformDirectory>(), services.GetRequiredService<Closures>());

        var stale = await directory.RequestProjectClosureAsync(alice.Id, organization, project, null, CancellationToken.None);
        // A concurrent pass moved the intent on (and maybe saw TerraBase's fence) after this caller read it.
        await closures.DeferAsync(stale, "terrabase_pending", CancellationToken.None, fenceSeen: fenceSeenMeanwhile);

        if (fenceSeenMeanwhile)
        {
            await Assert.That(await closures.AdvanceAsync(stale, CancellationToken.None)).IsFalse();
            var kept = (await directory.ClosureAsync("project", project, CancellationToken.None))!;
            await Assert.That(kept.Value.FenceSeen).IsTrue();
            await Assert.That(kept.Value.FailureCode).IsEqualTo("terrabase_refused_after_fence");
            // Later refusals keep it pending too.
            await Assert.That(await closures.AdvanceAsync(kept, CancellationToken.None)).IsFalse();
            await Assert.That(await directory.ClosureAsync("project", project, CancellationToken.None)).IsNotNull();
        }
        else
        {
            await Assert.That(async () => await closures.AdvanceAsync(stale, CancellationToken.None)).Throws<ApiException>();
            await Assert.That(await directory.ClosureAsync("project", project, CancellationToken.None)).IsNull();
        }

        await Assert.That(await directory.ProjectAsync(project, CancellationToken.None)).IsNotNull();
    }

    // Review R2 P3-2: once the account closure began removing things, a late last_owner (another owner left a shared
    // organization meanwhile) pauses it instead of cancelling a half-removed account; it finishes once resolved.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_existing_account_intent_handles_late_last_owner_through_its_refusal_path(bool committed)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, clock: clock);
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, _) = await oidc.SignInUserAsync("bob@example.test");
        var shared = await oidc.CreateOrganizationAsync(token, "Shared");
        await oidc.AddMemberAsync(shared, bob.Id, "member");
        var services = oidc.Server.App.Services;
        var (store, directory, worker) = (services.GetRequiredService<AuthStore>(), services.GetRequiredService<PlatformDirectory>(), services.GetRequiredService<ClosureWorker>());
        // A retry meets an existing intent after alice became the last owner with a member.
        var (job, _) = ClosureJob.New("terrascale", "account", alice.Id, null, alice.Id, clock);
        await store.CreateAsync(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key("account", alice.Id),
            JsonSerializer.SerializeToElement(job with { CommittedAt = committed ? clock.GetUtcNow() : null }, ClosureJson.Default.ClosureJob));

        var receipt = Receipt();
        await Assert.That(await ReceiptCallAsync(oidc.Client, receipt, HttpMethod.Delete, token: token, oidc: oidc)).IsEqualTo((409, "last_owner"));
        if (!committed)
        {
            await Assert.That(await directory.ClosureAsync("account", alice.Id, CancellationToken.None)).IsNull();
            await Assert.That(await ReceiptCallAsync(oidc.Client, receipt)).IsEqualTo((409, "deletion_refused"));
            await Assert.That(await services.GetRequiredService<Users>().GetAsync("terrascale", alice.Id)).IsNotNull();
            return;
        }

        var paused = (await directory.ClosureAsync("account", alice.Id, CancellationToken.None))!;
        await Assert.That(paused.Value.FailureCode).IsEqualTo("last_owner");
        await Assert.That(paused.Value.CommittedAt).IsNotNull();
        await Assert.That(await services.GetRequiredService<Users>().GetAsync("terrascale", alice.Id)).IsNotNull();

        await directory.ChangeRoleAsync(alice.Id, shared, bob.Id, "owner", CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        await worker.RunOnceAsync();
        await Assert.That(await services.GetRequiredService<Users>().GetAsync("terrascale", alice.Id)).IsNull();
        await Assert.That(await directory.ClosureAsync("account", alice.Id, CancellationToken.None)).IsNull();
        await Assert.That(await directory.OrganizationAsync(shared, CancellationToken.None)).IsNotNull();
    }

    // Review R3-A: a refusal ends the account intent by deleting the parent first. A concurrent pass that deferred the
    // parent (stale version) is read again and the delete retried; one that committed it pauses it instead, leaving the
    // children alone. Two passes, made deterministic by handing the refusing pass the stale parent.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_refusal_deletes_the_account_intent_first_or_pauses_it_when_a_concurrent_pass_committed(bool committedMeanwhile)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, clock: clock);
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, _) = await oidc.SignInUserAsync("bob@example.test");
        // alice is the last owner of an organization with another member: every pass refuses with last_owner.
        var shared = await oidc.CreateOrganizationAsync(token, "Shared");
        await oidc.AddMemberAsync(shared, bob.Id, "member");
        var solo = await oidc.CreateOrganizationAsync(token, "Solo");
        var project = await oidc.CreateProjectAsync(token, solo, "Solo project");
        var services = oidc.Server.App.Services;
        var (store, directory, closures, accounts) = (services.GetRequiredService<AuthStore>(), services.GetRequiredService<PlatformDirectory>(),
            services.GetRequiredService<Closures>(), services.GetRequiredService<AccountDeletion>());
        var (accountJob, _) = ClosureJob.New("terrascale", "account", alice.Id, null, alice.Id, clock);
        var (_, fence) = ClosureJob.New("terrascale", "organization", solo, solo, alice.Id, clock, alice.Id);
        var (_, child) = ClosureJob.New("terrascale", "project", project, solo, alice.Id, clock, alice.Id);
        await Assert.That(await store.TransactAsync([
            AuthStore.Create(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key("account", alice.Id),
                JsonSerializer.SerializeToElement(accountJob with { NextAttemptAt = clock.UtcNow.AddHours(1) }, ClosureJson.Default.ClosureJob)),
            fence, child])).IsNull();
        var stale = (await directory.ClosureAsync("account", alice.Id, CancellationToken.None))!;

        // The other pass moves the parent on first.
        if (committedMeanwhile)
        {
            await store.CompareAndPutAsync(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key("account", alice.Id),
                JsonSerializer.SerializeToElement(stale.Value with { CommittedAt = clock.UtcNow }, ClosureJson.Default.ClosureJob), stale.Version);
        }
        else
        {
            await closures.DeferAsync(stale, "project_closure_pending", CancellationToken.None);
        }

        var refused = await Assert.That(async () => await accounts.AdvanceAsync(stale, CancellationToken.None)).Throws<ApiException>();
        await Assert.That(refused!.Code).IsEqualTo("last_owner");
        var parent = await directory.ClosureAsync("account", alice.Id, CancellationToken.None);
        if (committedMeanwhile)
        {
            // Paused, not cancelled: the parent and its children stay as they were.
            await Assert.That(parent!.Value.CommittedAt).IsNotNull();
            await Assert.That(parent.Value.FailureCode).IsEqualTo("last_owner");
            await Assert.That((await directory.ClosureAsync("project", project, CancellationToken.None))!.Value.AccountId).IsEqualTo(alice.Id);
            await Assert.That(await directory.ClosureAsync("organization", solo, CancellationToken.None)).IsNotNull();
        }
        else
        {
            // Gone first, then its children: the project closure goes on alone and the organization fence is removed.
            await Assert.That(parent).IsNull();
            await Assert.That((await directory.ClosureAsync("project", project, CancellationToken.None))!.Value.AccountId).IsNull();
            await Assert.That(await directory.ClosureAsync("organization", solo, CancellationToken.None)).IsNull();
        }

        await Assert.That(await services.GetRequiredService<Users>().GetAsync("terrascale", alice.Id)).IsNotNull();
    }

    // Review R3 P3(1) and P3(3): an intent re-created for the same project (same closure id, new generation) is not the one
    // a stale pass read. That pass neither removes the root nor cleans its roles, and reports the closure as pending.
    [Test]
    public async Task A_stale_pass_never_removes_or_cleans_a_project_whose_intent_was_recreated()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false);
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var (guest, _) = await oidc.SignInUserAsync("guest@example.test");
        var organization = await oidc.CreateOrganizationAsync(token, "Org");
        var project = await oidc.CreateProjectAsync(token, organization, "Recreated");
        await Assert.That(await oidc.GrantAsync(project, guest.Id, "viewer")).IsTrue();
        var services = oidc.Server.App.Services;
        var (directory, closures) = (services.GetRequiredService<PlatformDirectory>(), services.GetRequiredService<Closures>());

        var stale = await directory.RequestProjectClosureAsync(alice.Id, organization, project, null, CancellationToken.None);
        await Assert.That(await directory.CancelClosureAsync(stale, CancellationToken.None)).IsTrue();
        var recreated = await directory.RequestProjectClosureAsync(alice.Id, organization, project, null, CancellationToken.None);
        await Assert.That(recreated.Value.Id).IsEqualTo(stale.Value.Id);
        await Assert.That(recreated.Value.Generation).IsNotEqualTo(stale.Value.Generation);

        await Assert.That(await directory.RemoveClosedProjectAsync(stale, CancellationToken.None)).IsFalse();
        // TerraBase accepts the closure id, but the stale pass is not this intent's: pending, nothing removed.
        await Assert.That(await closures.AdvanceAsync(stale, CancellationToken.None)).IsFalse();
        await Assert.That(await directory.ProjectAsync(project, CancellationToken.None)).IsNotNull();
        await Assert.That(await services.GetRequiredService<AuthStore>().GetAsync(AuthStore.ProjectMemberCollection, "terrascale", $"{project}/{guest.Id}")).IsNotNull();
        await Assert.That((await directory.ClosureAsync("project", project, CancellationToken.None))!.Value.Same(recreated.Value)).IsTrue();

        // The current intent finishes the closure.
        await Assert.That(await closures.AdvanceAsync(recreated, CancellationToken.None)).IsTrue();
        await Assert.That(await directory.ProjectAsync(project, CancellationToken.None)).IsNull();
    }

    // Review R3 P3(4): a pending closure refuses writes that add access but lets revocations through: lowering or removing
    // a project role while a project closes, and an owner demoting a member whose account is closing.
    [Test]
    public async Task A_pending_closure_refuses_new_access_but_lets_access_be_taken_away()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, services: services => Misroute(services, "unreachable"));
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var (carol, _) = await oidc.SignInUserAsync("carol@example.test");
        var (dave, _) = await oidc.SignInUserAsync("dave@example.test");
        var organization = await oidc.CreateOrganizationAsync(token, "Org");
        await oidc.AddMemberAsync(organization, bob.Id, "admin");
        await oidc.AddMemberAsync(organization, dave.Id, "member");
        var project = await oidc.CreateProjectAsync(token, organization, "Closing");
        await Assert.That(await oidc.GrantAsync(project, carol.Id, "admin")).IsTrue();
        var directory = oidc.Server.App.Services.GetRequiredService<PlatformDirectory>();

        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{project}", token, status: HttpStatusCode.Accepted);
        await Assert.That(async () => await directory.SetProjectRoleAsync(alice.Id, organization, project, dave.Id, "admin", CancellationToken.None)).Throws<ApiException>();
        await Assert.That((await directory.SetProjectRoleAsync(alice.Id, organization, project, carol.Id, "viewer", CancellationToken.None)).Member.Role).IsEqualTo("viewer");
        await directory.RemoveProjectRoleAsync(alice.Id, organization, project, carol.Id, CancellationToken.None);

        // bob's account closure is pending (his own organization's project waits for an unreachable TerraBase); alice can
        // still demote him in the organization they share, but not promote him back.
        var bobs = await oidc.CreateOrganizationAsync(bobToken, "Bob's");
        await oidc.CreateProjectAsync(bobToken, bobs, "Bob's project");
        await oidc.OkAsync(HttpMethod.Delete, "/v1/terrascale/account", bobToken, status: HttpStatusCode.Accepted);
        await Assert.That((await directory.ChangeRoleAsync(alice.Id, organization, bob.Id, "member", CancellationToken.None)).Member.Role).IsEqualTo("member");
        await Assert.That(async () => await directory.ChangeRoleAsync(alice.Id, organization, bob.Id, "admin", CancellationToken.None)).Throws<ApiException>();
    }

    // COS history audit: closure ids are opaque random ULIDs, never derived from the subject, so TerraBase's permanent
    // SYSTEM fence holds no subject hash. A project draws its id once and every later intent (after a refusal included)
    // reuses it, so a fence TerraBase already holds is still recognized; account and organization intents draw their own.
    [Test]
    public async Task Closure_ids_are_random_persisted_once_per_project_and_reused_by_later_intents()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, services: services => Misroute(services, "unreachable"));
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token, "Org");
        var project = await oidc.CreateProjectAsync(token, organization, "Project");
        var directory = oidc.Server.App.Services.GetRequiredService<PlatformDirectory>();

        var first = await directory.RequestProjectClosureAsync(alice.Id, organization, project, null, CancellationToken.None);
        await Assert.That(TerraScaleIds.IsValid(first.Value.Id)).IsTrue();
        await Assert.That(first.Value.Id).IsNotEqualTo(AuthStore.NaturalKeyId("terrascale", "closure-project", project));
        await Assert.That((await directory.ProjectAsync(project, CancellationToken.None))!.Value.ClosureId).IsEqualTo(first.Value.Id);
        // A retry of the same intent and a later intent after a refusal both present the stored id.
        await Assert.That((await directory.RequestProjectClosureAsync(alice.Id, organization, project, null, CancellationToken.None)).Value.Same(first.Value)).IsTrue();
        await Assert.That(await directory.CancelClosureAsync(first, CancellationToken.None)).IsTrue();
        var later = await directory.RequestProjectClosureAsync(alice.Id, organization, project, null, CancellationToken.None);
        await Assert.That(later.Value.Id).IsEqualTo(first.Value.Id);
        await Assert.That(later.Value.Generation).IsNotEqualTo(first.Value.Generation);

        var other = await oidc.CreateProjectAsync(token, organization, "Other");
        await Assert.That((await directory.RequestProjectClosureAsync(alice.Id, organization, other, null, CancellationToken.None)).Value.Id).IsNotEqualTo(first.Value.Id);

        // An account intent and its organization fence draw random ids too.
        await oidc.OkAsync(HttpMethod.Delete, "/v1/terrascale/account", token, status: HttpStatusCode.Accepted);
        foreach (var (scope, subject) in new[] { ("account", alice.Id), ("organization", organization) })
        {
            var job = (await directory.ClosureAsync(scope, subject, CancellationToken.None))!.Value;
            await Assert.That(TerraScaleIds.IsValid(job.Id)).IsTrue().Because(scope);
            await Assert.That(job.Id).IsNotEqualTo(AuthStore.NaturalKeyId("terrascale", "closure-" + scope, subject)).Because(scope);
        }
    }

    // Review R4 P3-1: TerraBase showed the fence, but a concurrent refusal had already cancelled the intent. Nothing is in
    // progress, so the answer is 503, not a 202 that promises background work; a retry presents the stored closure id,
    // which TerraBase recognizes, and finishes.
    [Test]
    public async Task A_fence_answer_after_the_intent_vanished_promises_nothing_and_a_retry_finishes()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false);
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token, "Org");
        var project = await oidc.CreateProjectAsync(token, organization, "Vanished");
        var services = oidc.Server.App.Services;
        var (directory, closures) = (services.GetRequiredService<PlatformDirectory>(), services.GetRequiredService<Closures>());

        var stale = await directory.RequestProjectClosureAsync(alice.Id, organization, project, null, CancellationToken.None);
        await Assert.That(await directory.CancelClosureAsync(stale, CancellationToken.None)).IsTrue();
        var unavailable = await Assert.That(async () => await closures.AdvanceAsync(stale, CancellationToken.None)).Throws<ApiException>();
        await Assert.That(unavailable!.Status).IsEqualTo(503);
        await Assert.That(await directory.ProjectAsync(project, CancellationToken.None)).IsNotNull();
        await Assert.That(await directory.ClosureAsync("project", project, CancellationToken.None)).IsNull();

        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{project}", token, status: HttpStatusCode.NoContent);
        await Assert.That(await directory.ProjectAsync(project, CancellationToken.None)).IsNull();
    }

    // Review R4 P3-3: cleaning up a removed account intent's leftovers never touches the intents of a newer account
    // closure for the same user: detaching and fence removal are guarded, in their transaction, by its absence.
    [Test]
    public async Task Leftover_cleanup_leaves_a_newer_account_closures_intents_alone()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, clock: clock);
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var solo = await oidc.CreateOrganizationAsync(token, "Solo");
        var project = await oidc.CreateProjectAsync(token, solo, "Solo project");
        var services = oidc.Server.App.Services;
        var (store, directory, closures) = (services.GetRequiredService<AuthStore>(), services.GetRequiredService<PlatformDirectory>(), services.GetRequiredService<Closures>());
        // A newer account closure (not due yet) owns these intents.
        var (accountJob, _) = ClosureJob.New("terrascale", "account", alice.Id, null, alice.Id, clock);
        var (_, fence) = ClosureJob.New("terrascale", "organization", solo, solo, alice.Id, clock, alice.Id);
        var (_, child) = ClosureJob.New("terrascale", "project", project, solo, alice.Id, clock, alice.Id);
        await Assert.That(await store.TransactAsync([
            AuthStore.Create(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key("account", alice.Id),
                JsonSerializer.SerializeToElement(accountJob with { NextAttemptAt = clock.UtcNow.AddHours(1) }, ClosureJson.Default.ClosureJob)),
            fence, child])).IsNull();
        var projectIntent = (await directory.ClosureAsync("project", project, CancellationToken.None))!;
        var organizationIntent = (await directory.ClosureAsync("organization", solo, CancellationToken.None))!;

        await Assert.That(await closures.DetachAsync(projectIntent, CancellationToken.None)).IsNull();
        await Assert.That(await directory.CancelClosureAsync(organizationIntent, CancellationToken.None, unlessAccount: alice.Id)).IsFalse();
        await Assert.That((await directory.ClosureAsync("project", project, CancellationToken.None))!.Value.AccountId).IsEqualTo(alice.Id);
        await Assert.That(await directory.ClosureAsync("organization", solo, CancellationToken.None)).IsNotNull();

        // Once no account intent exists, the same cleanup goes through.
        await Assert.That(await directory.CancelClosureAsync((await directory.ClosureAsync("account", alice.Id, CancellationToken.None))!, CancellationToken.None)).IsTrue();
        await Assert.That((await closures.DetachAsync(projectIntent, CancellationToken.None))!.Value.AccountId).IsNull();
        await Assert.That(await directory.CancelClosureAsync(organizationIntent, CancellationToken.None, unlessAccount: alice.Id)).IsTrue();
    }

    private sealed class HeldClosureCompletion(string id) : DelegatingHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete
                && request.RequestUri!.AbsolutePath.EndsWith("/collections/closure_jobs/documents/by-key", StringComparison.Ordinal)
                && request.RequestUri.Query == "?id=" + id)
            {
                this.Entered.TrySetResult();
                await this.Release.Task.WaitAsync(cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Test]
    public async Task Receipts_require_authentication_to_bind_and_never_certify_unknown_or_malformed_requests()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, services: services => Misroute(services, "listener"), clock: clock);
        const string password = "correct horse battery";
        var alice = (await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync("terrascale", "alice@example.test", Passwords.Hash(password), CancellationToken.None))!;
        var session = await oidc.SignInAsync(alice.Email);
        var (verifier, challenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
        var token = (await OidcHarness.JsonAsync(redeemed)).GetProperty("access_token").GetString()!;
        var organization = await oidc.CreateOrganizationAsync(token);
        await oidc.CreateProjectAsync(token, organization, "Pending receipt");
        var receipt = Receipt();
        await Assert.That(await ReceiptCallAsync(oidc.Client, receipt, HttpMethod.Delete)).IsEqualTo((401, "unauthorized"));
        await Assert.That(await ReceiptCallAsync(oidc.Client, receipt)).IsEqualTo((404, "not_found"));
        foreach (var malformed in new[] { "", receipt + "=", receipt[..42] + "B", "!" + receipt[1..], receipt + "," + receipt })
        {
            await Assert.That(await ReceiptCallAsync(oidc.Client, malformed)).IsEqualTo((400, "invalid_request"));
            await Assert.That(await ReceiptCallAsync(oidc.Client, malformed, HttpMethod.Delete, token: token, oidc: oidc)).IsEqualTo((400, "invalid_request"));
        }

        await Assert.That(await ReceiptCallAsync(oidc.Client, receipt, HttpMethod.Delete, token: token, oidc: oidc)).IsEqualTo((202, (string?)null));
        await Assert.That(await ReceiptCallAsync(oidc.Client, receipt, organization: "invalid")).IsEqualTo((400, "invalid_request"));
        await Assert.That(await ReceiptCallAsync(oidc.Client, receipt, organization: AuthStore.NewId())).IsEqualTo((404, "not_found"));
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(receipt)));
        var stored = await store.GetAsync(AuthStore.AccountDeletionReceiptCollection, "terrascale", hash);
        await Assert.That(stored!.Document.GetRawText()).DoesNotContain(receipt);
        using var passwordAttempt = await oidc.PostFormAsync("/ui/terrascale/sign-in/password", new() { ["email"] = alice.Email, ["password"] = password });
        await Assert.That(passwordAttempt.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(passwordAttempt)).IsNull();
        clock.Advance(HostedAuth.MailCooldown);
        using var requested = await oidc.PostFormAsync("/ui/terrascale/sign-in", new() { ["email"] = alice.Email });
        var code = await oidc.Email.NextCodeAsync(alice.Email);
        using var verified = await oidc.PostFormAsync("/ui/terrascale/sign-in/verify", new() { ["email"] = alice.Email, ["code"] = code });
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(verified)).IsNull();
        // An authenticated different subject cannot adopt an existing capability, even with a fresh proof.
        var (_, otherToken) = await oidc.SignInUserAsync("other@example.test");
        await Assert.That(await ReceiptCallAsync(oidc.Client, receipt, HttpMethod.Delete, token: otherToken, oidc: oidc)).IsEqualTo((403, "step_up_required"));
        // Losing a root without Users.DeleteAsync's atomic fence must never certify completion.
        var root = (await store.GetAsync(AuthStore.UserCollection, "terrascale", alice.Id))!;
        await Assert.That(await store.CompareAndDeleteAsync(AuthStore.UserCollection, "terrascale", alice.Id, root.Version)).IsTrue();
        var directory = oidc.Server.App.Services.GetRequiredService<PlatformDirectory>();
        var job = (await directory.ClosureAsync("account", alice.Id, CancellationToken.None))!;
        await Assert.That(async () => await oidc.Server.App.Services.GetRequiredService<AccountDeletion>().AdvanceAsync(job, CancellationToken.None)).Throws<ApiException>();
        await Assert.That(await ReceiptCallAsync(oidc.Client, receipt)).IsEqualTo((409, "deletion_refused"));
    }

    private static string Receipt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpRequestMessage BearerRequest(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<(int, string?)> ReceiptCallAsync(HttpClient client, string receipt, HttpMethod? method = null, string? token = null, string? organization = null, OidcHarness? oidc = null)
    {
        var path = "/v1/terrascale/account" + (method == HttpMethod.Delete ? "" : "/deletion") + (organization is null ? "" : "?organization=" + organization);
        using var request = BearerRequest(method ?? HttpMethod.Get, path, token);
        request.Headers.TryAddWithoutValidation("X-TerraScale-Deletion-Receipt", receipt);
        if (oidc is not null)
        {
            await oidc.AttachStepUpAsync(request, token);
        }
        using var response = await client.SendAsync(request);
        return ((int)response.StatusCode, response.IsSuccessStatusCode ? null : await OidcHarness.EnvelopeCodeAsync(response));
    }

    // Only explicit passes run on these hosts. Advance past retry delays between bounded recovery passes;
    // each test checks the resulting persisted state.
    private static async Task SettleAsync(AuthTestServer server, StoreTestClock clock, Func<Task<bool>> done)
    {
        var worker = server.App.Services.GetRequiredService<ClosureWorker>();
        for (var pass = 0; pass < 5 && !await done(); pass++)
        {
            await worker.RunOnceAsync();
            clock.UtcNow = clock.UtcNow.AddMinutes(2);
        }
    }

    private void Misroute(IServiceCollection services, string fault) =>
        services.AddKeyedSingleton(App.StorageAuthority, (_, _) => fault switch
        {
            // The public listener does not serve the peer operation.
            "listener" => new TerraBaseManagementClient(fixture.Server.Http, new TerraBaseManagementClientOptions { BaseAddress = fixture.Server.ClientAddress }),
            // The internal listener without the peer credential.
            "credential" => new TerraBaseManagementClient(TerraBaseFixture.NewHttpClient(), new TerraBaseManagementClientOptions { BaseAddress = fixture.Server.InternalAddress }),
            _ => new TerraBaseManagementClient(TerraBaseFixture.NewHttpClient(), new TerraBaseManagementClientOptions { BaseAddress = new Uri("http://127.0.0.1:1") }),
        });

    // TerraBase holds a permanent fence for the project under some closure id: any other one is refused.
    private async Task FencedAsync(string project, string organization)
    {
        var refused = await Assert.That(async () => await fixture.Server.Management().CloseProjectAsync(project, organization, AuthStore.NewId())).Throws<TerraBaseConflictException>();
        await Assert.That(refused!.ErrorCode).IsEqualTo("closure_conflict");
    }

    private static async Task<ClosureJob?> JobAsync(OidcHarness oidc, string scope, string subject) =>
        (await oidc.Server.App.Services.GetRequiredService<AuthStore>().GetAsync(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key(scope, subject)))
            ?.Document.Deserialize(ClosureJson.Default.ClosureJob);
}
