using System.Net;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class StepUpJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("/api/v1/dashboard/profile/delete")]
        [Arguments("/api/v1/dashboard/profile/access-tokens")]
        [Arguments("/api/v1/dashboard/credentials/key")]
        [Arguments("/api/v1/dashboard/credentials/rotate")]
        [Arguments("/api/v1/auth-management/clients")]
        public async Task ApiCallsWithoutProofRefuseBeforeIssuanceOrDeletion(string path)
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            using var response = await HttpJourney.PostFormAsync(scenario.Browser, path, await scenario.FormAsync());
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await scenario.AssertNoEffectsAsync();
        }

        [Test]
        public async Task UnapprovedDeletionCannotResumeFromThePendingIntent()
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            using var started = await scenario.BeginAsync("/api/v1/dashboard/profile/delete");
            await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(started.Headers.Location!.Query).IsEqualTo("");
            await Assert.That(started.Headers.Location.OriginalString).DoesNotContain("synthetic-backend-proof");
            using var callback = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            using var resume = await HttpJourney.PostFormAsync(scenario.Browser, "/api/v1/dashboard/profile/delete/continue", await scenario.FormAsync());
            await Assert.That(resume.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await scenario.AssertNoEffectsAsync();
        }

        [Test]
        public async Task LostConsumeAnswerRequiresNewProofBeforeAnyEffects()
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            using var started = await scenario.BeginAsync("/api/v1/dashboard/profile/delete");
            using var approved = await scenario.ApproveAsync(started);
            scenario.Gateway.LoseConsumeAnswer = true;
            using var callback = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await scenario.AssertNoEffectsAsync();
            using var replay = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            scenario.Gateway.LoseConsumeAnswer = false;
            using var restarted = await HttpJourney.PostConfirmedFormAsync(scenario.Browser, "/api/v1/dashboard/profile/delete", await scenario.FormAsync());
            await Assert.That(restarted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(restarted.Headers.Location!.OriginalString).IsEqualTo("/account-deleted");
        }

        [Test]
        public async Task MissingDeletionGrantCannotCreateAnAuthorizedOperation()
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            using var started = await scenario.BeginAsync("/api/v1/dashboard/profile/delete");
            using var approved = await scenario.ApproveAsync(started);
            scenario.Gateway.OmitDeletionAuthorization = true;
            using var callback = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await scenario.AssertNoEffectsAsync();
        }

        [Test]
        [Arguments("/api/v1/dashboard/profile/delete")]
        [Arguments("/api/v1/dashboard/profile/access-tokens")]
        [Arguments("/api/v1/dashboard/credentials/key")]
        [Arguments("/api/v1/dashboard/credentials/rotate")]
        [Arguments("/api/v1/auth-management/clients")]
        public async Task SwitchingAccountAfterApprovalRejectsTheOriginalIntent(string path)
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            using var started = await scenario.BeginAsync(path);
            using var approved = await scenario.ApproveAsync(started);
            using var logout = await HttpJourney.PostFormAsync(scenario.Browser, "/api/v1/auth/logout", await scenario.FormAsync());
            using var login = await HttpJourney.LoginAsync(scenario.Browser, "other@example.com", "test-password");
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var callback = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await scenario.AssertNoEffectsAsync();
        }

        [Test]
        public async Task ExpiredApprovalCannotResumeDeletion()
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            using var started = await scenario.BeginAsync("/api/v1/dashboard/profile/delete");
            using var approved = await scenario.ApproveAsync(started);
            scenario.Advance(TimeSpan.FromMinutes(6));
            using var callback = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await scenario.AssertNoEffectsAsync();
        }

        [Test]
        public async Task PatKeepsItsIndependentGrantButCannotIssueWithoutConsumedProof()
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            using var started = await scenario.BeginAsync("/api/v1/dashboard/profile/access-tokens");
            // Skipping hosted approval may reach OIDC; a fresh OIDC grant alone is insufficient.
            using var oidc = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(oidc.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            using var rejected = await HttpJourney.HostedSignInAsync(scenario.Browser, oidc);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await scenario.AssertNoEffectsAsync();
            var secret = await HttpJourney.CreatePatAsync(scenario.Browser);
            await Assert.That(secret).StartsWith("tspat_");
            using var replay = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }

        [Test]
        public async Task PatCallbackRevokesFreshGrantWhenApprovedWorkspaceMembershipWasRemoved()
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            var shared = await scenario.SelectSharedWorkspaceAsync();
            var activeBefore = scenario.Environment.Auth.ActiveGrants;
            var callback = await scenario.PatOidcCallbackAsync();
            await Assert.That(scenario.Environment.Auth.ActiveGrants).IsEqualTo(activeBefore + 1);
            bool removed;
            lock (scenario.Environment.Workspace.Gate)
            {
                removed = scenario.Environment.Workspace.Organizations[shared].Members.Remove("test-user");
            }

            await Assert.That(removed).IsTrue();
            using var refused = await scenario.Browser.GetAsync(callback);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("Workspace is unavailable.");
            await Assert.That(scenario.Environment.Auth.ActiveGrants).IsEqualTo(activeBefore);
            await Assert.That(scenario.Gateway.Consumed).IsEqualTo(0);
            await scenario.AssertNoEffectsAsync();
        }

        [Test]
        [Arguments("browser-read")]
        [Arguments("workspace-read")]
        [Arguments("workspace-cancel")]
        public async Task PatCallbackRevokesUnretainedGrantOnValidationDependencyOrCancellation(string failure)
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            var activeBefore = scenario.Environment.Auth.ActiveGrants;
            var callback = await scenario.PatOidcCallbackAsync();
            if (failure == "browser-read")
            {
                foreach (var (key, document) in scenario.Environment.State.Items.Where(pair => pair.Key.StartsWith("sessions/", StringComparison.Ordinal)))
                {
                    scenario.Environment.State.Items[key] = document with { State = "conflict" };
                }
            }
            else
            {
                scenario.Environment.Workspace.BeforeListMembers = () => failure == "workspace-cancel"
                ? Task.FromCanceled(new CancellationToken(canceled: true)) : Task.FromException(new IOException("Synthetic workspace read outage."));
            }

            using var refused = await scenario.Browser.GetAsync(callback);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(scenario.Environment.Auth.ActiveGrants).IsEqualTo(activeBefore);
            await scenario.AssertNoEffectsAsync();
        }

        [Test]
        public async Task PatCallbackKeepsRetainedCredentialWhenPreparingTheProfileViewFails()
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            var activeBefore = scenario.Environment.Auth.ActiveGrants;
            var callback = await scenario.PatOidcCallbackAsync();
            scenario.Environment.State.BeforeWriteAsync = (collection, _) =>
            {
                if (collection == AccessTokens.Collection)
                {
                    foreach (var (key, document) in scenario.Environment.State.Items.Where(pair => pair.Key.StartsWith("session_indexes/", StringComparison.Ordinal)))
                    {
                        scenario.Environment.State.Items[key] = document with { State = "conflict" };
                    }
                }

                return Task.CompletedTask;
            };
            using var refused = await scenario.Browser.GetAsync(callback);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(scenario.Gateway.Consumed).IsEqualTo(1);
            await Assert.That(scenario.Environment.Auth.ActiveGrants).IsEqualTo(activeBefore + 1);
            await Assert.That((await scenario.Environment.State.ListAsync("auth_credentials", default)).Count).IsEqualTo(1);
            await Assert.That((await scenario.Environment.State.ListAsync(AccessTokens.Collection, default)).Count).IsEqualTo(1);
            scenario.Environment.State.BeforeWriteAsync = null;
            foreach (var (key, document) in scenario.Environment.State.Items.Where(pair => pair.Key.StartsWith("session_indexes/", StringComparison.Ordinal)))
            {
                scenario.Environment.State.Items[key] = document with { State = "value" };
            }

            using var profile = await scenario.Browser.GetAsync("/profile?tab=access-tokens");
            await Assert.That(profile.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await profile.Content.ReadAsStringAsync()).Contains("Proof protected");
        }

        [Test]
        public async Task RevokedUnacceptedDeletionRequiresFreshConfirmationAndReceiptBeforeStripe()
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root, signIn: false);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            scenario.Email = "interrupted-delete-" + suffix + "@example.test";
            await Billing.BillingPlanJourneyTests.SignUpPaidAsync(scenario.Browser, scenario.Email, "E2E-" + suffix, suffix);
            // The operation is persisted before ts-auth is asked to delete; the interruption lands between them.
            scenario.Environment.Workspace.BeforeDeleteAccount = () => throw new IOException("Synthetic interruption after operation persistence.");
            using var started = await scenario.BeginAsync("/api/v1/dashboard/profile/delete");
            using var approved = await scenario.ApproveAsync(started);
            using var interrupted = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            scenario.Environment.Workspace.BeforeDeleteAccount = null;
            await Assert.That((await scenario.Environment.State.ListAsync("account_deletions", default)).Count).IsEqualTo(1);
            var receipt = scenario.Gateway.LastReceipt;
            await Assert.That(receipt).IsNotNull();
            await Assert.That(scenario.Cookies.GetCookies(scenario.Browser.BaseAddress!)["ts-dashboard-deletion"]).IsNotNull();
            scenario.Environment.Auth.ChangePassword(scenario.Email, "changed-password");
            using var signedIn = await HttpJourney.LoginAsync(scenario.Browser, scenario.Email, "changed-password");
            await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var continued = await HttpJourney.PostFormAsync(scenario.Browser, "/api/v1/dashboard/profile/delete/continue", await scenario.FormAsync());
            await Assert.That(continued.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await continued.Content.ReadAsStringAsync()).Contains("step_up_required");
            await Assert.That(scenario.Cookies.GetCookies(scenario.Browser.BaseAddress!)["ts-dashboard-deletion"]).IsNull();
            await scenario.AssertNoEffectsAsync();
            using var fresh = await HttpJourney.PostConfirmedFormAsync(scenario.Browser, "/api/v1/dashboard/profile/delete", await scenario.FormAsync());
            await Assert.That(fresh.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(fresh.Headers.Location!.OriginalString).IsEqualTo("/account-deleted");
            await Assert.That(scenario.Gateway.LastReceipt).IsNotEqualTo(receipt);
            await Assert.That(scenario.Environment.BillingPlans.CancellationCalls).IsEqualTo(1);
            await Assert.That(scenario.Environment.Workspace.DeletionCalls).IsEqualTo(1);
        }

        [Test]
        public async Task AcceptedDeletionReceiptResumesPendingThenCompletedWithoutRepeatingEffects()
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            // The operation is persisted before ts-auth is asked to delete; the interruption lands between them.
            scenario.Environment.Workspace.BeforeDeleteAccount = () => throw new IOException("Synthetic interruption after operation persistence.");
            using var started = await scenario.BeginAsync("/api/v1/dashboard/profile/delete");
            using var approved = await scenario.ApproveAsync(started);
            using var interrupted = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            scenario.Environment.Workspace.BeforeDeleteAccount = null;
            scenario.Environment.Workspace.SetDeletionStatus(scenario.Gateway.LastReceipt!, false);
            using var pending = await HttpJourney.PostFormAsync(scenario.Browser, "/api/v1/dashboard/profile/delete/continue", await scenario.FormAsync());
            await Assert.That(pending.Headers.Location!.OriginalString).IsEqualTo("/account-deletion");
            await Assert.That((await scenario.Environment.State.ListAsync("account_deletions", default)).Count).IsEqualTo(1);
            scenario.Environment.Workspace.SetDeletionStatus(scenario.Gateway.LastReceipt!, true);
            using var completed = await HttpJourney.PostFormAsync(scenario.Browser, "/api/v1/dashboard/profile/delete/continue", await scenario.FormAsync());
            await Assert.That(completed.Headers.Location!.OriginalString).IsEqualTo("/account-deleted");
            await Assert.That((await scenario.Environment.State.ListAsync("account_deletions", default)).Count).IsEqualTo(0);
            await Assert.That(scenario.Environment.BillingPlans.CancellationCalls).IsEqualTo(0);
            await Assert.That(scenario.Environment.Workspace.DeletionCalls).IsEqualTo(0);
        }

        [Test]
        [Arguments("/api/v1/dashboard/credentials/key", false)]
        [Arguments("/api/v1/dashboard/credentials/key", true)]
        [Arguments("/api/v1/dashboard/credentials/rotate", false)]
        [Arguments("/api/v1/dashboard/credentials/rotate", true)]
        [Arguments("/api/v1/auth-management/clients", false)]
        [Arguments("/api/v1/auth-management/clients", true)]
        public async Task CredentialProducersRequireApprovedProofAndCallbackCannotReplay(string path, bool approve)
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            if (path.EndsWith("rotate", StringComparison.Ordinal))
            {
                using var created = await HttpJourney.PostConfirmedFormAsync(scenario.Browser, "/api/v1/dashboard/credentials/key", await scenario.FormAsync());
                await Assert.That(created.IsSuccessStatusCode).IsTrue();
                scenario.CaptureEffects();
            }
            var form = await scenario.FormAsync();
            if (path.EndsWith("rotate", StringComparison.Ordinal))
            {
                form["keyId"] = scenario.Environment.ApiKeys.Issued().Single().Key.Id;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Accept.ParseAdd("text/html");
            using var started = await scenario.Browser.SendAsync(request);
            await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            if (approve)
            {
                using var approved = await scenario.ApproveAsync(started);
                await Assert.That(approved.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            }
            var consumed = scenario.Gateway.Consumed;
            using var result = await scenario.Browser.GetAsync("/auth/step-up/callback");
            if (approve)
            {
                await Assert.That(result.IsSuccessStatusCode || result.StatusCode == HttpStatusCode.SeeOther).IsTrue();
                await Assert.That(scenario.Gateway.Consumed).IsEqualTo(consumed + 1);
                scenario.CaptureEffects();
            }
            else
            {
                await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            }

            await scenario.AssertNoEffectsAsync();
            using var replay = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(scenario.Gateway.Consumed).IsEqualTo(consumed + (approve ? 1 : 0));
            await scenario.AssertNoEffectsAsync();
        }

        [Test]
        [Arguments("switch")]
        [Arguments("expiry")]
        [Arguments("lost-consume")]
        [Arguments("unapproved")]
        public async Task McpIndependentOidcCallbackRejectsInvalidProofWithoutLeavingGrants(string failure)
        {
            await using var scenario = await StepUpScenario.StartAsync(fixture.Root);
            var callback = await scenario.McpOidcCallbackAsync(failure);
            var activeBefore = scenario.Environment.Auth.ActiveGrants - 1; // the fresh, still-unexchanged OIDC grant
            if (failure == "expiry")
            {
                scenario.Advance(TimeSpan.FromMinutes(6));
            }

            scenario.Gateway.LoseConsumeAnswer = failure == "lost-consume";
            using var rejected = await scenario.Browser.GetAsync(callback);
            await Assert.That(rejected.StatusCode).IsEqualTo(failure == "lost-consume" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden);
            await Assert.That(scenario.Environment.Auth.ActiveGrants).IsEqualTo(activeBefore);
            await scenario.AssertNoEffectsAsync();
            using var replay = await scenario.Browser.GetAsync(callback);
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await scenario.AssertNoEffectsAsync();
        }
    }
}
