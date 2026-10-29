using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using Scenario = TerraScale.Dashboard.E2E.DirectAuthDeletionScenario;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
    public sealed class AccountDeletionPendingJourneyTests
    {
        [Test]
        public async Task PendingReceiptExplainsRegionalConfirmationAndStopsAutomaticChecksAtTheLimit()
        {
            await using var scenario = await StartAsync();
            using var client = await PendingClientAsync(scenario);
            var calls = WireStatus(scenario, HttpStatusCode.Accepted);
            using var pending = await client.GetAsync("/account-deletion");
            await Assert.That(pending.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            var page = await pending.Content.ReadAsStringAsync();
            await Assert.That(page).Contains("every storage region").And.Contains("does not identify a specific waiting step or region")
                .And.Contains("Check deletion status").And.Contains("aria-live=\"polite\"").And.Contains("setTimeout")
                .And.DoesNotContain("/delete/continue").And.DoesNotContain("SJC");
            await Assert.That(pending.Headers.CacheControl!.NoStore).IsTrue();
            var csrf = await CsrfAsync(client, "/account-deletion?retry=19");
            using var last = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete/status", new() { ["_csrf"] = csrf, ["_retry"] = "19" });
            await Assert.That(last.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That(await last.Content.ReadAsStringAsync()).Contains("Automatic checks have paused").And.DoesNotContain("setTimeout");
            await Assert.That(calls.All(method => method == "GET")).IsTrue();
            await Assert.That((await scenario.State.ListAsync("account_deletions", default)).Count).IsEqualTo(1);
        }

        [Test]
        [Arguments(401)]
        [Arguments(403)]
        [Arguments(404)]
        [Arguments(409)]
        public async Task PermanentReceiptErrorsRetainTheOperationAndNeverRetryAdmission(int status)
        {
            await using var scenario = await StartAsync();
            using var client = await PendingClientAsync(scenario);
            _ = WireStatus(scenario, HttpStatusCode.Accepted);
            var csrf = await CsrfAsync(client, "/account-deletion");
            var calls = WireStatus(scenario, (HttpStatusCode)status);
            using var attention = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete/status", new() { ["_csrf"] = csrf });
            await Assert.That(attention.StatusCode).IsEqualTo((HttpStatusCode)status);
            await Assert.That(await attention.Content.ReadAsStringAsync()).Contains("Automatic checks have stopped")
                .And.Contains("Sign in to review deletion").And.Contains("receipt has been retained")
                .And.DoesNotContain("setTimeout").And.DoesNotContain("/delete/continue");
            await Assert.That((await scenario.State.ListAsync("account_deletions", default)).Count).IsEqualTo(1);
            await Assert.That(calls.Count).IsEqualTo(1);
            await Assert.That(calls.All(method => method == "GET")).IsTrue();
        }

        [Test]
        public async Task ConfirmedReceiptUsesExistingCleanupAndMissingCsrfPerformsNoStatusCall()
        {
            await using var scenario = await StartAsync();
            using var client = await PendingClientAsync(scenario);
            var calls = WireStatus(scenario, HttpStatusCode.NoContent);
            using (var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete/status", new()))
            {
                await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            }

            await Assert.That(calls.Count).IsEqualTo(0);
            var csrf = await CsrfAsync(client, "/account-deletion");
            await Assert.That((await scenario.State.ListAsync("account_deletions", default)).Count).IsEqualTo(1);
            using var finished = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete/status", new() { ["_csrf"] = csrf });
            await Assert.That(finished.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(finished.Headers.Location!.OriginalString).IsEqualTo("/account-deleted");
            await Assert.That((await scenario.State.ListAsync("account_deletions", default)).Count).IsEqualTo(0);
            await Assert.That(calls.All(method => method == "GET")).IsTrue();
        }

        [Test]
        public async Task SlowStatusReadObservesItsBudgetAndRendersRecoverableProgress()
        {
            await using var scenario = await StartAsync();
            using var client = await PendingClientAsync(scenario);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            scenario.OverrideForwardAsync = async (_, token) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cancelled.TrySetResult(token.IsCancellationRequested); }
                return null;
            };
            client.Timeout = TimeSpan.FromSeconds(20);
            using var response = await client.GetAsync("/account-deletion");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1))).IsTrue();
            await Assert.That(await response.Content.ReadAsStringAsync()).Contains("temporarily unavailable").And.Contains("setTimeout");
            await Assert.That((await scenario.State.ListAsync("account_deletions", default)).Count).IsEqualTo(1);
        }

        [Test]
        public async Task RevokedSessionFollowsItsDeletionToCompletionInsteadOfSignIn()
        {
            await using var scenario = await StartAsync();
            using var client = await PendingClientAsync(scenario);
            var calls = WireStatus(scenario, HttpStatusCode.NoContent);
            using (var profile = await client.GetAsync("/profile?tab=danger"))
            {
                await Assert.That(profile.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
                await Assert.That(profile.Headers.Location!.OriginalString).IsEqualTo("/account-deletion")
                    .Because("ts-auth revoked the session when it accepted the deletion (QA newuser:27).");
            }
            var csrf = await CsrfAsync(client, "/account-deletion");
            using var finished = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete/status", new() { ["_csrf"] = csrf });
            await Assert.That(finished.Headers.Location!.OriginalString).IsEqualTo("/account-deleted");
            using (var after = await client.GetAsync("/profile?tab=danger"))
            {
                await Assert.That(after.Headers.Location!.OriginalString).StartsWith("/login?returnTo=")
                    .Because("Completion drops the recovery cookie, so later visits sign in normally.");
            }

            await Assert.That(calls.All(method => method == "GET")).IsTrue();
        }

        private static Task<Scenario> StartAsync()
        {
            return Scenario.StartReceiptStatusAsync();
        }

        private static async Task<string> CsrfAsync(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            var token = Regex.Match(await response.Content.ReadAsStringAsync(), "name=\"_csrf\" value=\"([^\"]+)\"").Groups[1].Value;
            await Assert.That(token.Length).IsGreaterThan(0);
            return WebUtility.HtmlDecode(token);
        }

        private static async Task<HttpClient> PendingClientAsync(Scenario scenario, string? cookie = null)
        {
            cookie ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var receipt = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            await scenario.StoreDeletionOperationAsync(scenario.User, cookie, receipt);
            return scenario.Client(cookie);
        }

        [Test]
        [Category("Slow")]
        public async Task PendingAndAttentionViewsHaveAccessibleManualContinuationAndBusyFeedback()
        {
            await using var scenario = await StartAsync();
            var cookie = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            using var client = await PendingClientAsync(scenario, cookie);
            _ = WireStatus(scenario, HttpStatusCode.Accepted);
            var evidence = Path.Combine(new DashboardE2EFixture().Root, "test-results", "account-deletion-pending", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(evidence);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1280, Height = 900 }, RecordVideoDir = evidence });
            await context.AddCookiesAsync([new() { Name = "ts-dashboard-deletion", Value = cookie, Url = client.BaseAddress!.ToString() }]);
            var page = await context.NewPageAsync();
            await page.GotoAsync(new Uri(client.BaseAddress!, "/account-deletion?retry=5").ToString());
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Account deletion is pending" })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("every storage region");
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "pending.png"), FullPage = true });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scenario.OverrideForwardAsync = async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":{"code":"unauthorized","message":"Synthetic refusal"}}""", Encoding.UTF8, "application/json") };
            };
            var button = page.GetByRole(AriaRole.Button, new() { Name = "Check deletion status" });
            await page.EvaluateAsync("""
            () => window.addEventListener('submit', event => {
                sessionStorage.setItem('pending-check-busy', event.submitter.getAttribute('aria-busy') ?? 'missing');
            }, {once: true})
            """);
            var submit = button.ClickAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { release.TrySetResult(); }
            await submit;
            await Assert.That(await page.EvaluateAsync<string>("() => sessionStorage.getItem('pending-check-busy')")).IsEqualTo("true");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Deletion status needs attention" })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Sign in to review deletion" })).ToBeVisibleAsync();
            await Expect(page.Locator("form")).Not.ToHaveAttributeAsync("data-init", new Regex("setTimeout"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "attention.png"), FullPage = true });
        }

        private static List<string> WireStatus(Scenario scenario, HttpStatusCode status)
        {
            var calls = new List<string>();
            scenario.OverrideForwardAsync = (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath != "/v1/terrascale/account/deletion")
                {
                    throw new InvalidOperationException("Pending UI must only call the receipt status API.");
                }

                calls.Add(request.Method.Method);
                return Task.FromResult<HttpResponseMessage?>(new(status)
                {
                    Content = new StringContent("""{"error":{"code":"deletion_refused","message":"Synthetic receipt refusal"}}""", Encoding.UTF8, "application/json")
                });
            };
            return calls;
        }
    }
}
