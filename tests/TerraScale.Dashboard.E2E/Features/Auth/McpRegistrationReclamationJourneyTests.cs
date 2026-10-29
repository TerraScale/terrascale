using NUlid;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class McpRegistrationReclamationJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task PrivateSdkReclamationHonorsBoundaryAndPreservesCompletedLegacyAndOtherCollections()
        {
            await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
            var clock = new Clock(); scenario.RegistrationTime = clock;
            await Assert.That(await scenario.StartHostsAsync()).IsTrue();
            var state = scenario.RegistrationState;
            var boundary = Ulid.NewUlid().ToString();
            var completed = Ulid.NewUlid().ToString();
            var active = Ulid.NewUlid().ToString();
            var created = clock.Now.AddHours(-24).AddTicks(1);
            var fresh = McpRegistrations.Create(boundary, "Pending client", ["http://127.0.0.1/callback"], created);
            foreach (var row in new[] { fresh, fresh with { ClientId = active, CreatedAt = clock.Now, LastActivityAt = clock.Now },
                fresh with { ClientId = completed, FirstSignInCompletedAt = created } })
            {
                await state.PutAsync(McpOAuth.ClientCollection, row.ClientId, JsonSerializer.SerializeToElement(row), TerraBasePrecondition.Absent, default);
            }

            const string legacy = "mcp_legacy_retained";
            await state.PutAsync(McpOAuth.ClientCollection, legacy, JsonSerializer.SerializeToElement(new { ClientId = legacy }), TerraBasePrecondition.Absent, default);
            await state.PutAsync("dashboard_mcp_oauth_codes", boundary, JsonSerializer.SerializeToElement(new { ClientId = boundary }), TerraBasePrecondition.Absent, default);
            using var worker = scenario.RegistrationReclaimer();
            await worker.RunOnceAsync(default);
            await Assert.That(await state.GetAsync(McpOAuth.ClientCollection, boundary, default) is not null).IsTrue();
            clock.Now = clock.Now.AddTicks(1);
            await worker.RunOnceAsync(default);
            await Assert.That(await state.GetAsync(McpOAuth.ClientCollection, boundary, default) is null).IsTrue();
            await Assert.That(await McpRegistrations.ReadActiveAsync(state, clock, boundary, default) is null).IsTrue();
            await Assert.That(await McpRegistrations.CompleteSignInAsync(state, clock, boundary, default) is null).IsTrue();
            foreach (var id in new[] { active, completed, legacy })
            {
                await Assert.That(await state.GetAsync(McpOAuth.ClientCollection, id, default) is not null).IsTrue();
            }

            await Assert.That(await state.GetAsync("dashboard_mcp_oauth_codes", boundary, default) is not null).IsTrue();
            await Assert.That(scenario.ForeignCredentials).IsEqualTo(0);
        }

        [Test]
        public async Task FailedRowDoesNotStarveBoundedCursorTraversal()
        {
            await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
            var clock = new Clock(); scenario.RegistrationTime = clock;
            await Assert.That(await scenario.StartHostsAsync()).IsTrue();
            var state = scenario.RegistrationState;
            var ids = Enumerable.Range(0, 65).Select(_ => Ulid.NewUlid().ToString()).ToArray();
            foreach (var id in ids)
            {
                var row = McpRegistrations.Create(id, "Expired", ["http://127.0.0.1/callback"], clock.Now.AddDays(-2));
                await state.PutAsync(McpOAuth.ClientCollection, id, JsonSerializer.SerializeToElement(row), TerraBasePrecondition.Absent, default);
            }
            // Valid provider identity, malformed lifecycle: failure must preserve it without stopping a page.
            var malformed = Ulid.NewUlid().ToString();
            await state.PutAsync(McpOAuth.ClientCollection, malformed, JsonSerializer.SerializeToElement(new { ClientId = malformed }), TerraBasePrecondition.Absent, default);
            using var worker = scenario.RegistrationReclaimer();
            await worker.RunOnceAsync(default);
            var remaining = 0;
            foreach (var id in ids)
            {
                if (await state.GetAsync(McpOAuth.ClientCollection, id, default) is not null)
                {
                    remaining++;
                }
            }

            await Assert.That(remaining >= 33).IsTrue(); // Never drains beyond a 32-row page.
            await worker.RunOnceAsync(default);
            await worker.RunOnceAsync(default);
            foreach (var id in ids)
            {
                await Assert.That(await state.GetAsync(McpOAuth.ClientCollection, id, default) is null).IsTrue();
            }

            await Assert.That(await state.GetAsync(McpOAuth.ClientCollection, malformed, default) is not null).IsTrue();
        }

        [Test]
        public async Task CompletedSignInWinningTheDeleteRaceIsRetainedByProviderCas()
        {
            await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
            var clock = new Clock(); scenario.RegistrationTime = clock;
            await Assert.That(await scenario.StartHostsAsync()).IsTrue();
            var state = scenario.RegistrationState;
            var id = Ulid.NewUlid().ToString();
            var row = McpRegistrations.Create(id, "Race", ["http://127.0.0.1/callback"], clock.Now.AddDays(-2));
            await state.PutAsync(McpOAuth.ClientCollection, id, JsonSerializer.SerializeToElement(row), TerraBasePrecondition.Absent, default);
            using var controls = scenario.CreateProviderClient();
            using (var response = await controls.PostAsJsonAsync("/__test/control", new { command = "mode", value = "complete-mcp-before-delete" }))
            {
                response.EnsureSuccessStatusCode();
            }

            using var worker = scenario.RegistrationReclaimer();
            await worker.RunOnceAsync(default);
            var retained = (await state.GetAsync(McpOAuth.ClientCollection, id, default))!.Document.Deserialize<McpRegistration>()!;
            await Assert.That(retained.FirstSignInCompletedAt is not null).IsTrue();
            await Assert.That(await McpRegistrations.ReadActiveAsync(state, clock, id, default) is not null).IsTrue();
        }

        [Test]
        public async Task PublicRegistrationRemovedAfterDeadlineCannotAuthorizeOnAnotherReplica()
        {
            await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
            var clock = new Clock(); scenario.RegistrationTime = clock;
            await Assert.That(await scenario.StartHostsAsync()).IsTrue();
            using var browser = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            using var registration = await browser.PostAsJsonAsync("/oauth/register", new
            { client_name = "Unfinished public client", redirect_uris = new[] { "http://127.0.0.1/callback" } });
            await Assert.That(registration.StatusCode).IsEqualTo(System.Net.HttpStatusCode.Created);
            var id = (await registration.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
            var authorize = "/oauth/authorize?client_id=" + id + "&response_type=code&code_challenge_method=S256&code_challenge=" + new string('A', 43) + "&resource=https%3A%2F%2Fmcp.terrascale.tech%2F";
            using (var before = await replica.GetAsync(authorize))
            {
                await Assert.That(before.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
            }

            clock.Now = clock.Now.AddHours(24);
            using var worker = scenario.RegistrationReclaimer();
            await worker.RunOnceAsync(default);
            using var after = await replica.GetAsync(authorize);
            await Assert.That(after.StatusCode).IsEqualTo(System.Net.HttpStatusCode.BadRequest);
            await Assert.That(await scenario.RegistrationState.GetAsync(McpOAuth.ClientCollection, id, default) is null).IsTrue();
            // Retrying a callback after deletion cannot recreate the natural key.
            await Assert.That(await McpRegistrations.CompleteSignInAsync(scenario.RegistrationState, clock, id, default) is null).IsTrue();
            await Assert.That(scenario.ForeignCredentials).IsEqualTo(0);
        }

        [Test]
        [Arguments(true)]
        [Arguments(false)]
        public async Task ActualHostedCallbackAndReclamationPreserveTheWinningVersion(bool completionWins)
        {
            await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
            var clock = new Clock(); scenario.RegistrationTime = clock;
            await Assert.That(await scenario.StartHostsAsync()).IsTrue();
            using var browser = scenario.CreateClient();
            using var registered = await browser.PostAsJsonAsync("/oauth/register", new
            { client_name = "Callback race", redirect_uris = new[] { "http://127.0.0.1/callback" } });
            var id = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
            var state = scenario.RegistrationState;
            var stored = (await state.GetAsync(McpOAuth.ClientCollection, id, default))!;
            var created = clock.Now.AddHours(-24).AddTicks(1);
            var aged = stored.Document.Deserialize<McpRegistration>()! with { CreatedAt = created, LastActivityAt = created };
            await state.PutAsync(McpOAuth.ClientCollection, id, JsonSerializer.SerializeToElement(aged), TerraBasePrecondition.Matches(stored.Version), default);
            var path = "/oauth/authorize?client_id=" + id + "&response_type=code&code_challenge_method=S256&code_challenge=" + new string('A', 43) + "&resource=https%3A%2F%2Fmcp.terrascale.tech%2F";
            using (var signedUp = await HttpJourney.LoginAsync(browser, "race@example.test", "Test-race-password", signUp: true))
            {
                await Assert.That(signedUp.StatusCode).IsEqualTo(System.Net.HttpStatusCode.SeeOther);
            }

            var csrf = await HttpJourney.CsrfAsync(browser, path);
            using var allowed = await HttpJourney.PostConfirmedFormAsync(browser, "/oauth/authorize", new()
            { ["client_id"] = id, ["response_type"] = "code", ["code_challenge_method"] = "S256", ["code_challenge"] = new string('A', 43), ["resource"] = "https://mcp.terrascale.tech/", ["_csrf"] = csrf, ["decision"] = "allow" });
            var paused = scenario.PauseRegistrationCompletion();
            var callback = HttpJourney.HostedSignInAsync(browser, allowed, "race@example.test", "Test-race-password");
            await paused.WaitAsync(TimeSpan.FromSeconds(10));
            var credentials = (await state.ListAsync("auth_credentials", default)).Count;
            var codes = (await state.ListAsync(McpOAuth.CodeCollection, default)).Count;
            clock.Now = clock.Now.AddTicks(1);
            using var worker = scenario.RegistrationReclaimer();
            if (completionWins)
            {
                scenario.ReleaseRegistrationCompletion();
                using var result = await callback;
                await Assert.That(result.StatusCode).IsEqualTo(System.Net.HttpStatusCode.Redirect);
                await worker.RunOnceAsync(default);
                var retained = (await state.GetAsync(McpOAuth.ClientCollection, id, default))!.Document.Deserialize<McpRegistration>()!;
                await Assert.That(retained.FirstSignInCompletedAt is not null).IsTrue();
                await Assert.That((await state.ListAsync(McpOAuth.CodeCollection, default)).Count).IsEqualTo(codes + 1);
            }
            else
            {
                await worker.RunOnceAsync(default);
                scenario.ReleaseRegistrationCompletion();
                using var result = await callback;
                await Assert.That(result.StatusCode).IsEqualTo(System.Net.HttpStatusCode.BadRequest);
                await Assert.That(await state.GetAsync(McpOAuth.ClientCollection, id, default) is null).IsTrue();
                await Assert.That((await state.ListAsync("auth_credentials", default)).Count).IsEqualTo(credentials);
                await Assert.That((await state.ListAsync(McpOAuth.CodeCollection, default)).Count).IsEqualTo(codes);
            }
        }

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
            public override DateTimeOffset GetUtcNow()
            {
                return Now;
            }
        }
    }
}
